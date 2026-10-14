package ru.ivanvorontsov.sklejka.core

import org.junit.Assert.*
import org.junit.Test
import java.io.ByteArrayOutputStream
import java.io.File
import java.security.MessageDigest
import kotlin.random.Random

class StreamerTest {
    private fun sha(b: ByteArray) = MessageDigest.getInstance("SHA-256").digest(b).toHex()

    @Test fun splitThenJoinIsByteExact() {
        val dir = java.nio.file.Files.createTempDirectory("sklejka").toFile()
        try {
            val data = Random(42).nextBytes(5 * 1024 * 1024 + 12345) // > buffer, odd tail
            val src = File(dir, "big.bin").apply { writeBytes(data) }
            val partSize = 1_300_000L
            val (n, hs) = Streamer.split(src.inputStream(), src.length(), partSize, { i -> File(dir, "big.bin.%03d".format(i)).outputStream() })
            assertEquals(Streamer.partCount(data.size.toLong(), partSize), n)
            val files = dir.listFiles()!!.filter { it.name != "big.bin" }.shuffled()
            val grp = PartDetector.group(files.map { Triple(it.path, it.name, it.length()) }).single()
            assertTrue(grp.missing.isEmpty())
            val out = File(dir, "joined.bin")
            val h = out.outputStream().use { o ->
                Streamer.join(grp.parts.map { p -> { File(p.id).inputStream() } }, grp.parts.map { it.size }, o)
            }
            assertEquals(sha(data), sha(out.readBytes()))
            assertEquals(sha(data), h.sha256)
            assertEquals(hs, h)
            val crc = java.util.zip.CRC32().apply { update(data) }.value
            assertEquals("%08X".format(crc), h.crc32)
        } finally { dir.deleteRecursively() }
    }

    @Test fun resumeContinuesExactly() {
        val parts = listOf(Random(1).nextBytes(3_000_000), Random(2).nextBytes(2_500_001), Random(3).nextBytes(17))
        val whole = parts.reduce { a, b -> a + b }
        val skip = 4_100_000L
        val out = ByteArrayOutputStream(); out.write(whole, 0, skip.toInt())
        val prefix = out.toByteArray()
        val h = Streamer.join(parts.map { p -> { p.inputStream() } }, parts.map { it.size.toLong() }, out, skip = skip, already = { prefix.inputStream() })
        assertArrayEquals(whole, out.toByteArray())
        assertEquals(sha(whole), h.sha256)
    }

    @Test(expected = Cancelled::class) fun cancelStops() {
        val p = Random(5).nextBytes(3_000_000)
        Streamer.join(listOf({ p.inputStream() }), listOf(p.size.toLong()), ByteArrayOutputStream(), cancelled = { true })
    }

    @Test fun sizes() { assertEquals("1.00 КБ".replace('.', ','), Fmt.size(1024).replace('.', ',')) }
}
