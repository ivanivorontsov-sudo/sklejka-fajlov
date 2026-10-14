package ru.ivanvorontsov.sklejka.core

import org.junit.Assert.*
import org.junit.Test

class PartDetectorTest {
    private fun g(vararg names: String) = PartDetector.group(names.map { Triple(it, it, 10L) })

    @Test fun numbered001() {
        val p = PartDetector.parse("Фильм.mkv.001")!!
        assertEquals("Фильм.mkv", p.outputName); assertEquals(1, p.index); assertEquals(PartKind.NUMBERED, p.kind)
    }
    @Test fun archives() {
        assertEquals(PartKind.SEVEN_Z, PartDetector.parse("a.7z.002")!!.kind)
        assertEquals(PartKind.ZIP_RAW, PartDetector.parse("a.ZIP.010")!!.kind)
        val r = PartDetector.parse("backup.part03.rar")!!
        assertEquals(PartKind.RAR_VOLUMES, r.kind); assertEquals(3, r.index); assertEquals("backup.rar", r.outputName)
        assertFalse(r.kind.joinable)
    }
    @Test fun otherStyles() {
        assertEquals(12, PartDetector.parse("x.iso.part12")!!.index)
        assertEquals("doc.pdf", PartDetector.parse("doc.pdf.2")!!.outputName)
        assertEquals("song.mp3", PartDetector.parse("song_3.mp3")!!.outputName)
        assertEquals(2, PartDetector.parse("Disk2.gsd")!!.index)
        assertEquals(2, PartDetector.parse("big.tar.ab")!!.index)
        assertNull(PartDetector.parse("photo.jpg"))
    }
    @Test fun naturalSortAcrossWidths() {
        val names = (1..12).map { "v.mp4.part$it" }.shuffled()
        val grp = g(*names.toTypedArray()).single()
        assertEquals((1..12).toList(), grp.indices)
        assertTrue(grp.missing.isEmpty())
    }
    @Test fun naturalOrderComparator() {
        val l = listOf("a10", "a2", "A1", "a02b").sortedWith(NaturalOrder)
        assertEquals(listOf("A1", "a2", "a02b", "a10"), l)
    }
    @Test fun missingAndDuplicates() {
        val grp = PartDetector.group(listOf(
            Triple("1", "f.bin.001", 5L), Triple("2", "f.bin.003", 5L), Triple("3", "f.bin.003", 5L), Triple("4", "f.bin.005", 2L)
        )).single()
        assertEquals(listOf(2, 4), grp.missing)
        assertEquals(listOf(3), grp.duplicates)
    }
    @Test fun oddSize() {
        val grp = PartDetector.group(listOf(Triple("1", "f.001", 10L), Triple("2", "f.002", 7L), Triple("3", "f.003", 10L), Triple("4", "f.004", 3L))).single()
        assertEquals(listOf("f.002"), grp.oddSized.map { it.name })
    }
    @Test fun separateGroupsAndNoise() {
        val gs = g("a.001", "a.002", "b.7z.001", "b.7z.002", "readme.txt", "a.crc")
        assertEquals(listOf("a", "b.7z"), gs.map { it.outputName })
    }
    @Test fun zipSpanAttachesFinalZip() {
        val grp = g("arc.z01", "arc.z02", "arc.zip").single()
        assertEquals(listOf("arc.z01", "arc.z02", "arc.zip"), grp.parts.map { it.name })
        assertEquals(PartKind.ZIP_SPAN, grp.kind)
    }
    @Test fun weakPatternsNeedTwo() {
        assertTrue(g("holiday_2024.jpg").isEmpty())
        assertEquals(1, g("clip_1.mp4", "clip_2.mp4").size)
    }
    @Test fun checksumFiles() {
        val e = ChecksumFiles.parse("f.crc", "filename=f.bin\r\nsize=12\r\ncrc32=0ab\r\n", "f.bin")!!
        assertEquals(12L, e.size); assertEquals("000000AB", e.crc32)
        assertEquals("DEADBEEF", ChecksumFiles.parse("x.sfv", "; c\nf.bin DEADBEEF\nother 00000000", "f.bin")!!.crc32)
        assertEquals("d41d8cd98f00b204e9800998ecf8427e", ChecksumFiles.parse("x.md5", "D41D8CD98F00B204E9800998ECF8427E *f.bin", "f.bin")!!.md5)
    }
}
