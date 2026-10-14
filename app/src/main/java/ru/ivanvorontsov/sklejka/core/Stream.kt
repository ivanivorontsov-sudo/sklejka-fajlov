package ru.ivanvorontsov.sklejka.core

import java.io.InputStream
import java.io.OutputStream
import java.security.MessageDigest
import java.util.zip.CRC32

class Cancelled : Exception("Отменено")

/** CRC32 + MD5 + SHA-256 computed in one pass. */
class MultiHash {
    private val crc = CRC32()
    private val md5 = MessageDigest.getInstance("MD5")
    private val sha = MessageDigest.getInstance("SHA-256")
    fun update(b: ByteArray, off: Int, len: Int) { crc.update(b, off, len); md5.update(b, off, len); sha.update(b, off, len) }
    fun result() = Hashes(
        "%08X".format(crc.value),
        md5.digest().toHex(),
        sha.digest().toHex(),
    )
}

data class Hashes(val crc32: String, val md5: String, val sha256: String)

fun ByteArray.toHex() = joinToString("") { "%02x".format(it) }

fun interface Progress { fun onProgress(done: Long, total: Long, partIndex: Int) }

object Streamer {
    const val BUF = 1 shl 20 // 1 MiB

    /**
     * Concatenate [inputs] into [out], hashing as it goes. Memory use is one buffer.
     * [skip] bytes of the combined stream are assumed already written (resume): they are
     * hashed from [already] (the existing output) instead of being re-written.
     */
    fun join(
        inputs: List<() -> InputStream>,
        sizes: List<Long>,
        out: OutputStream,
        cancelled: () -> Boolean = { false },
        progress: Progress = Progress { _, _, _ -> },
        skip: Long = 0,
        already: (() -> InputStream)? = null,
    ): Hashes {
        val total = sizes.sum()
        val buf = ByteArray(BUF)
        val h = MultiHash()
        var done = 0L
        if (skip > 0) {
            requireNotNull(already).invoke().use { s ->
                var left = skip
                while (left > 0) {
                    val n = s.read(buf, 0, minOf(buf.size.toLong(), left).toInt())
                    if (n < 0) throw IllegalStateException("Готовая часть файла короче, чем записано")
                    h.update(buf, 0, n); left -= n; done += n
                    if (cancelled()) throw Cancelled()
                }
            }
            progress.onProgress(done, total, -1)
        }
        var offset = 0L
        for ((i, open) in inputs.withIndex()) {
            val size = sizes[i]
            val partEnd = offset + size
            if (partEnd <= skip) { offset = partEnd; continue }
            open().use { s ->
                var toSkip = (skip - offset).coerceAtLeast(0)
                while (toSkip > 0) { val k = s.skip(toSkip); if (k <= 0) { if (s.read() < 0) break; toSkip-- } else toSkip -= k }
                while (true) {
                    if (cancelled()) throw Cancelled()
                    val n = s.read(buf)
                    if (n < 0) break
                    out.write(buf, 0, n); h.update(buf, 0, n); done += n
                    progress.onProgress(done, total, i)
                }
            }
            offset = partEnd
        }
        out.flush()
        return h.result()
    }

    /** Split [input] into parts of [partSize] bytes; [newPart] opens part number i (1-based). */
    fun split(
        input: InputStream, total: Long, partSize: Long,
        newPart: (Int) -> OutputStream,
        cancelled: () -> Boolean = { false },
        progress: Progress = Progress { _, _, _ -> },
    ): Pair<Int, Hashes> {
        require(partSize > 0)
        val buf = ByteArray(BUF); val h = MultiHash()
        var idx = 0; var done = 0L
        input.use { s ->
            var eof = false
            while (!eof) {
                idx++
                var left = partSize
                newPart(idx).use { o ->
                    while (left > 0) {
                        if (cancelled()) throw Cancelled()
                        val n = s.read(buf, 0, minOf(buf.size.toLong(), left).toInt())
                        if (n < 0) { eof = true; break }
                        o.write(buf, 0, n); h.update(buf, 0, n); left -= n; done += n
                        progress.onProgress(done, total, idx - 1)
                    }
                }
                if (!eof && total in 0..done) eof = true
            }
        }
        return idx to h.result()
    }

    fun partCount(total: Long, partSize: Long) = if (total <= 0) 1 else ((total + partSize - 1) / partSize).toInt()
}

/** Checksum side-files: Total Commander .crc, .sfv, .md5, .sha256. */
object ChecksumFiles {
    data class Expect(val fileName: String?, val size: Long?, val crc32: String?, val md5: String?, val sha256: String?)

    fun isChecksumFile(name: String) = name.lowercase().let { it.endsWith(".crc") || it.endsWith(".sfv") || it.endsWith(".md5") || it.endsWith(".sha256") }

    /** Parse; when the file lists several entries, the one matching [target] (case-insensitive) wins. */
    fun parse(name: String, text: String, target: String): Expect? {
        val lower = name.lowercase()
        val lines = text.lineSequence().map { it.trim() }.filter { it.isNotEmpty() && !it.startsWith(";") && !it.startsWith("#") }.toList()
        return when {
            lower.endsWith(".crc") -> {
                val kv = lines.mapNotNull { l -> l.indexOf('=').takeIf { it > 0 }?.let { l.substring(0, it).trim().lowercase() to l.substring(it + 1).trim() } }.toMap()
                if (kv.isEmpty()) null else Expect(kv["filename"], kv["size"]?.toLongOrNull(), kv["crc32"]?.uppercase()?.padStart(8, '0'), null, null)
            }
            lower.endsWith(".sfv") -> {
                val e = lines.mapNotNull { l -> Regex("""^(.+?)\s+([0-9A-Fa-f]{8})$""").matchEntire(l) }
                pick(e.map { it.groupValues[1].trim() to it.groupValues[2] }, target)?.let { Expect(it.first, null, it.second.uppercase(), null, null) }
            }
            else -> {
                val hexLen = if (lower.endsWith(".md5")) 32 else 64
                val e = lines.mapNotNull { l -> Regex("""^([0-9A-Fa-f]{$hexLen})\s+\*?(.+)$""").matchEntire(l) }
                    .map { it.groupValues[2].trim() to it.groupValues[1].lowercase() }
                    .ifEmpty { lines.filter { Regex("^[0-9A-Fa-f]{$hexLen}$").matches(it) }.map { "" to it.lowercase() } }
                pick(e, target)?.let { if (hexLen == 32) Expect(it.first, null, null, it.second, null) else Expect(it.first, null, null, null, it.second) }
            }
        }
    }

    private fun pick(e: List<Pair<String, String>>, target: String) =
        e.firstOrNull { it.first.substringAfterLast('/').substringAfterLast('\\').equals(target, true) } ?: e.singleOrNull()

    /** Returns list of (label, ok) comparisons. */
    fun verify(exp: Expect, h: Hashes, size: Long): List<Pair<String, Boolean>> = buildList {
        exp.size?.let { add("Размер" to (it == size)) }
        exp.crc32?.let { add("CRC32" to it.equals(h.crc32, true)) }
        exp.md5?.let { add("MD5" to it.equals(h.md5, true)) }
        exp.sha256?.let { add("SHA-256" to it.equals(h.sha256, true)) }
    }

    /** Total Commander-compatible .crc text. */
    fun tcCrc(fileName: String, size: Long, crc: String) = "filename=$fileName\r\nsize=$size\r\ncrc32=$crc\r\n"
}

object Fmt {
    fun size(b: Long): String {
        if (b < 1024) return "$b Б"
        val u = listOf("КБ", "МБ", "ГБ", "ТБ"); var v = b / 1024.0; var i = 0
        while (v >= 1024 && i < u.size - 1) { v /= 1024; i++ }
        return String.format(java.util.Locale("ru"), if (v < 10) "%.2f %s" else "%.1f %s", v, u[i])
    }
}
