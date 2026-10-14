package ru.ivanvorontsov.sklejka.core

/** Kind of a split piece, detected from its file name. */
enum class PartKind(val title: String, val joinable: Boolean) {
    NUMBERED("Части .001 / .002 (HJSplit, Total Commander, 7-Zip)", true),
    SEVEN_Z("Разрезанный архив 7z (.7z.001)", true),
    ZIP_RAW("Разрезанный архив zip (.zip.001)", true),
    PART_N("Части .part1 / .part01", true),
    EXT_N("Части файл.расширение.1", true),
    UNDERSCORE("Части файл_1.расширение", true),
    GSPLIT("Части GSplit (.gsd)", true),
    UNIX("Части split (.aa / .ab)", true),
    ZIP_SPAN("Многотомный zip (.z01 … .zip)", false),
    RAR_VOLUMES("Многотомный rar (.part1.rar)", false),
}

data class PartName(val group: String, val outputName: String, val index: Int, val kind: PartKind)

/** A concrete piece: a document plus the parsed name. [id] is an opaque handle (Uri string on Android). */
data class Part(val id: String, val name: String, val size: Long, val parsed: PartName)

data class PartGroup(
    val outputName: String,
    val kind: PartKind,
    val parts: List<Part>,
) {
    val totalSize: Long get() = parts.sumOf { it.size }
    val indices: List<Int> get() = parts.map { it.parsed.index }
    /** Indexes that should exist between the first expected index and the highest one, but don't. */
    val missing: List<Int> get() {
        if (parts.isEmpty()) return emptyList()
        val have = indices.toSet()
        val start = if (0 in have) 0 else 1
        val end = indices.max()
        return (start..end).filter { it !in have }
    }
    val duplicates: List<Int> get() = indices.groupBy { it }.filter { it.value.size > 1 }.keys.sorted()
    /** All parts except the last are normally the same size; returns odd-sized ones. */
    val oddSized: List<Part> get() {
        if (parts.size < 3) return emptyList()
        val body = parts.dropLast(1)
        val common = body.groupingBy { it.size }.eachCount().maxByOrNull { it.value }?.key ?: return emptyList()
        val odd = body.filter { it.size != common }.toMutableList()
        if (parts.last().size > common) odd += parts.last()
        return odd
    }
}

object PartDetector {
    private val rarVol = Regex("""^(.+)\.part(\d+)\.rar$""", RegexOption.IGNORE_CASE)
    private val sevenZ = Regex("""^(.+\.7z)\.(\d{3,})$""", RegexOption.IGNORE_CASE)
    private val zipRaw = Regex("""^(.+\.zip)\.(\d{3,})$""", RegexOption.IGNORE_CASE)
    private val numbered = Regex("""^(.+)\.(\d{3,})$""")
    private val partN = Regex("""^(.+)\.part(\d+)$""", RegexOption.IGNORE_CASE)
    private val zipSpan = Regex("""^(.+)\.z(\d{2,})$""", RegexOption.IGNORE_CASE)
    private val extN = Regex("""^(.+\.[^.\d][^.]*)\.(\d{1,2})$""")
    private val gsplit = Regex("""^(.*?)[ ._-]?(\d+)\.gsd$""", RegexOption.IGNORE_CASE)
    private val underscore = Regex("""^(.+)_(\d+)(\.[A-Za-z0-9]{1,6})?$""")
    private val unix = Regex("""^(.+\.[^.]+)\.([a-z]{2})$""")

    /** Parse a single file name. Returns null if it doesn't look like a piece. */
    fun parse(name: String): PartName? {
        rarVol.matchEntire(name)?.let { m ->
            val b = m.groupValues[1]; return PartName("rar:${b.lowercase()}", "$b.rar", m.groupValues[2].toInt(), PartKind.RAR_VOLUMES)
        }
        sevenZ.matchEntire(name)?.let { m -> return num(m, PartKind.SEVEN_Z) }
        zipRaw.matchEntire(name)?.let { m -> return num(m, PartKind.ZIP_RAW) }
        numbered.matchEntire(name)?.let { m -> return num(m, PartKind.NUMBERED) }
        partN.matchEntire(name)?.let { m -> return num(m, PartKind.PART_N) }
        zipSpan.matchEntire(name)?.let { m ->
            val b = m.groupValues[1]; return PartName("zipspan:${b.lowercase()}", "$b.zip", m.groupValues[2].toInt(), PartKind.ZIP_SPAN)
        }
        extN.matchEntire(name)?.let { m -> return num(m, PartKind.EXT_N) }
        gsplit.matchEntire(name)?.let { m ->
            val b = m.groupValues[1].ifEmpty { "gsplit" }
            return PartName("gsd:${b.lowercase()}", b, m.groupValues[2].toInt(), PartKind.GSPLIT)
        }
        underscore.matchEntire(name)?.let { m ->
            val b = m.groupValues[1]; val ext = m.groupValues[3]
            return PartName("us:${(b + ext).lowercase()}", b + ext, m.groupValues[2].toInt(), PartKind.UNDERSCORE)
        }
        unix.matchEntire(name)?.let { m ->
            val s = m.groupValues[2]; val idx = (s[0] - 'a') * 26 + (s[1] - 'a') + 1
            return PartName("unix:${m.groupValues[1].lowercase()}", m.groupValues[1], idx, PartKind.UNIX)
        }
        return null
    }

    private fun num(m: MatchResult, k: PartKind): PartName {
        val b = m.groupValues[1]
        return PartName("${k.name}:${b.lowercase()}", b, m.groupValues[2].toInt(), k)
    }

    /**
     * Group arbitrary documents (name, size, id) into sets of pieces.
     * The final volume of a spanned zip (name.zip) is attached to its .z01 set as the last part.
     * Weak patterns (underscore, unix split) need at least two members to count.
     */
    fun group(files: List<Triple<String, String, Long>>): List<PartGroup> {
        val parts = files.mapNotNull { (id, name, size) -> parse(name)?.let { Part(id, name, size, it) } }.toMutableList()
        val byGroup = parts.groupBy { it.parsed.group }.toMutableMap()
        // spanned zip: attach the closing .zip
        for ((key, list) in byGroup.toMap()) {
            if (list.first().parsed.kind != PartKind.ZIP_SPAN) continue
            val zipName = list.first().parsed.outputName
            val last = files.firstOrNull { it.second.equals(zipName, ignoreCase = true) } ?: continue
            val idx = list.maxOf { it.parsed.index } + 1
            byGroup[key] = list + Part(last.first, last.second, last.third, PartName(key, zipName, idx, PartKind.ZIP_SPAN))
        }
        return byGroup.values
            .filter { l -> val k = l.first().parsed.kind; (k != PartKind.UNDERSCORE && k != PartKind.UNIX) || l.size >= 2 }
            .map { l -> PartGroup(l.first().parsed.outputName, l.first().parsed.kind, sortParts(l)) }
            .sortedWith { a, b -> NaturalOrder.compare(a.outputName, b.outputName) }
    }

    fun sortParts(list: List<Part>): List<Part> =
        list.sortedWith(compareBy<Part> { it.parsed.index }.thenComparator { a, b -> NaturalOrder.compare(a.name, b.name) })
}

/** "file2" < "file10", case-insensitive, digit runs compared numerically. */
object NaturalOrder : Comparator<String> {
    override fun compare(a: String, b: String): Int {
        var i = 0; var j = 0
        while (i < a.length && j < b.length) {
            val ca = a[i]; val cb = b[j]
            if (ca.isDigit() && cb.isDigit()) {
                var ei = i; while (ei < a.length && a[ei].isDigit()) ei++
                var ej = j; while (ej < b.length && b[ej].isDigit()) ej++
                val na = a.substring(i, ei).trimStart('0'); val nb = b.substring(j, ej).trimStart('0')
                if (na.length != nb.length) return na.length - nb.length
                val c = na.compareTo(nb); if (c != 0) return c
                i = ei; j = ej
            } else {
                val c = ca.lowercaseChar().compareTo(cb.lowercaseChar()); if (c != 0) return c
                i++; j++
            }
        }
        return (a.length - i) - (b.length - j)
    }
}
