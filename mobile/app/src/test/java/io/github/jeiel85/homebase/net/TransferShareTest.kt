package io.github.jeiel85.homebase.net

import io.github.jeiel85.homebase.push.WebPush
import io.github.jeiel85.homebase.share.FileShare
import io.github.jeiel85.homebase.share.Uploader
import org.json.JSONObject
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.File
import java.math.BigInteger
import java.security.KeyFactory
import java.security.KeyPair
import java.security.MessageDigest
import java.security.interfaces.ECPrivateKey
import java.security.spec.ECPrivateKeySpec

/**
 * DECISIONS M-64: files from this phone to a PC. Opens `transfer.v1.json`, which the .NET ProtocolGoldenTests seal
 * with the clipboard key of `clip-key.v1.json` and fixed nonces, so the transfer key derivation, the positional
 * chunk nonces and the sealed metadata and hashes can't drift apart between the two implementations.
 */
class TransferShareTest {

    private fun fixture(name: String) = File("../../docs/mobile-protocol/$name").readText(Charsets.UTF_8)

    /** The clipboard key of the fixtures, as this phone derives it (RFC 8291 Appendix A user-agent key, see ClipShareTest). */
    private fun clipKey(): ByteArray {
        val uaPublic = WebPush.unb64url("BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4")
        val uaPrivate = WebPush.unb64url("q1dXpw3UpT5VOmu_cf_v6ih07Aems3njxI-JWgLcM94")
        val pub = WebPush.publicKey(uaPublic)
        val priv = KeyFactory.getInstance("EC").generatePrivate(ECPrivateKeySpec(BigInteger(1, uaPrivate), pub.params)) as ECPrivateKey
        val answer = JSONObject().put("schema", 1).put("pub", JSONObject(fixture("clip-key.v1.json")).getString("pcPublic")).toString()
        return ClipKeyAgreement(KeyPair(pub, priv)).finish(answer)!!
    }

    private val transfer get() = JSONObject(fixture("transfer.v1.json"))

    private fun envelope(o: JSONObject) = ClipEnvelope(o.getString("id"), o.getString("nonce"), o.getString("data"))

    private fun chunk(i: Int) = transfer.getJSONArray("chunks").getJSONObject(i)

    private fun sha256(bytes: ByteArray) = Uploader.hex(MessageDigest.getInstance("SHA-256").digest(bytes))

    @Test
    fun both_chunks_open_at_their_positions_with_the_transfer_key() {
        val id = transfer.getString("transferId")
        val key = TransferCrypto.transferKey(clipKey(), id)
        for (i in 0..1) {
            val c = chunk(i)
            val opened = TransferCrypto.openChunk(key, c.getInt("f"), c.getLong("n"), WebPush.unb64url(c.getString("data")))
            assertArrayEquals(WebPush.unb64url(c.getString("plain")), opened)
        }
        // The PNG signature is the second file's whole content.
        assertArrayEquals(byteArrayOf(0x89.toByte(), 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A), WebPush.unb64url(chunk(1).getString("plain")))
    }

    @Test
    fun a_chunk_moved_to_another_position_does_not_open() {
        val key = TransferCrypto.transferKey(clipKey(), transfer.getString("transferId"))
        val second = WebPush.unb64url(chunk(1).getString("data"))
        assertNull(TransferCrypto.openChunk(key, 0, 0, second))
        assertNull(TransferCrypto.openChunk(key, 1, 1, second))
        // Another transfer's key doesn't open it either.
        assertNull(TransferCrypto.openChunk(TransferCrypto.transferKey(clipKey(), "ffffffffffffffffffffffffffffffff"), 1, 0, second))
    }

    @Test
    fun sealing_a_chunk_here_gives_the_pcs_bytes() {
        val key = TransferCrypto.transferKey(clipKey(), transfer.getString("transferId"))
        for (i in 0..1) {
            val c = chunk(i)
            val plain = WebPush.unb64url(c.getString("plain"))
            assertEquals(c.getString("data"), WebPush.b64url(TransferCrypto.sealChunk(key, c.getInt("f"), c.getLong("n"), plain)))
        }
        // From the middle of a buffer, as the uploader seals it.
        val plain = WebPush.unb64url(chunk(1).getString("plain"))
        val buffer = ByteArray(32).also { plain.copyInto(it, destinationOffset = 5) }
        assertEquals(chunk(1).getString("data"), WebPush.b64url(TransferCrypto.sealChunk(key, 1, 0, buffer, 5, plain.size)))
    }

    @Test
    fun the_nonce_is_file_then_chunk_big_endian() {
        assertArrayEquals(byteArrayOf(0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1, 2), TransferCrypto.nonce(1, 0x0102))
    }

    @Test
    fun the_metadata_opens_with_the_clipboard_key() {
        val files = TransferMessages.openMeta(clipKey(), envelope(transfer.getJSONObject("meta")))!!
        val first = WebPush.unb64url(chunk(0).getString("plain"))
        assertEquals(
            listOf(TransferFile("회의록 11월.txt", first.size.toLong(), "text/plain"), TransferFile("screen.png", 8, "image/png")),
            files,
        )
    }

    @Test
    fun the_hashes_open_and_match_the_files() {
        val hashes = TransferMessages.openHashes(clipKey(), envelope(transfer.getJSONObject("hashes")))!!
        assertEquals(listOf(sha256(WebPush.unb64url(chunk(0).getString("plain"))), sha256(WebPush.unb64url(chunk(1).getString("plain")))), hashes)
    }

    @Test
    fun metadata_and_hashes_sealed_here_open_again() {
        val key = clipKey()
        val id = ClipProtocol.newId()
        val files = listOf(TransferFile("사진.jpg", 3_000_000, "image/jpeg"), TransferFile("notes", 0))
        val meta = TransferMessages.sealMeta(key, id, files)
        assertEquals(id, meta.id)
        assertEquals(files, TransferMessages.openMeta(key, meta))
        assertEquals(listOf("ab".repeat(32)), TransferMessages.openHashes(key, TransferMessages.sealHashes(key, id, listOf("ab".repeat(32)))))
        // A file without a type leaves "mime" out, like the PC.
        assertFalse(files[1].toJson().has("mime"))
    }

    @Test
    fun chunk_counts_and_lengths() {
        val c = TransferProtocol.CHUNK_BYTES.toLong()
        assertEquals(0L, TransferProtocol.chunkCount(0))
        assertEquals(1L, TransferProtocol.chunkCount(1))
        assertEquals(1L, TransferProtocol.chunkCount(c))
        assertEquals(2L, TransferProtocol.chunkCount(c + 1))
        assertEquals(3L, TransferProtocol.chunkCount(3 * c))
        assertEquals(0, TransferProtocol.chunkLength(0, 0)) // an empty file has no chunk
        assertEquals(TransferProtocol.CHUNK_BYTES, TransferProtocol.chunkLength(3 * c, 2)) // exact multiple: last is full
        assertEquals(1, TransferProtocol.chunkLength(c + 1, 1)) // remainder
        assertEquals(0, TransferProtocol.chunkLength(c + 1, 2)) // out of range
        assertEquals(10, TransferProtocol.chunkLength(10, 0))
    }

    @Test
    fun chunk_ranges_round_trip() {
        assertEquals("0-3,7", ChunkRanges.format(listOf<Long>(3, 0, 2, 1, 7, 2)))
        assertEquals("0-127,130,132-140", ChunkRanges.format((0L..127L) + 130L + (132L..140L)))
        assertEquals("", ChunkRanges.format(emptyList()))
        assertEquals("5", ChunkRanges.format(listOf<Long>(5)))
        assertEquals(listOf<Long>(0, 1, 2, 3, 7), ChunkRanges.parse("0-3,7"))
        assertEquals(emptyList<Long>(), ChunkRanges.parse(""))
        assertEquals(emptyList<Long>(), ChunkRanges.parse(null))
        val many = (0L..300L).filter { it % 7 != 3L }
        assertEquals(many, ChunkRanges.parse(ChunkRanges.format(many)))
        // Not the format: nothing is guessed.
        assertNull(ChunkRanges.parse("3-1"))
        assertNull(ChunkRanges.parse("a"))
        assertNull(ChunkRanges.parse("1,,2"))
        assertNull(ChunkRanges.parse(" 1"))
        assertNull(ChunkRanges.parse("-1"))
    }

    @Test
    fun the_status_says_which_chunks_arrived() {
        val s = TransferStatus.parse("""{"schema":1,"id":"0c1d2e3f405162738495a6b7c8d9eafb","state":"receiving","chunkBytes":4194304,"have":["0-2","",""]}""")!!
        assertEquals(setOf<Long>(0, 1, 2), s.arrived(0))
        assertEquals(emptySet<Long>(), s.arrived(1))
        assertEquals(emptySet<Long>(), s.arrived(5)) // a file the PC didn't list
        assertEquals("receiving", s.state)
        assertNull(TransferStatus.parse("""{"schema":2,"id":"x","have":[]}"""))
        assertNull(TransferStatus.parse("not json"))
    }

    @Test
    fun the_limits_are_said_before_anything_is_sent() {
        val small = TransferFile("a.txt", 10)
        assertNull(TransferProtocol.problem(listOf(small)))
        assertNotNull(TransferProtocol.problem(emptyList()))
        assertNotNull(TransferProtocol.problem(List(21) { small }))
        assertNotNull(TransferProtocol.problem(listOf(TransferFile("big", TransferProtocol.MAX_FILE_BYTES + 1))))
        assertNotNull(TransferProtocol.problem(List(3) { TransferFile("1.5g", 1_600_000_000L) })) // 4.8 GB together
        assertNull(TransferProtocol.problem(listOf(TransferFile("2g", TransferProtocol.MAX_FILE_BYTES))))
    }

    @Test
    fun what_is_sent_is_said_briefly() {
        val photos = List(3) { TransferFile("p$it.jpg", 4_133_334, "image/jpeg") }
        assertEquals("사진 3장 · 12.4MB", FileShare.summary(photos))
        assertEquals("동영상 2개", FileShare.what(List(2) { TransferFile("v.mp4", 1, "video/mp4") }))
        assertEquals("사진·동영상 2개", FileShare.what(listOf(TransferFile("p.jpg", 1, "image/jpeg"), TransferFile("v.mp4", 1, "video/mp4"))))
        assertEquals("파일 2개", FileShare.what(listOf(TransferFile("p.jpg", 1, "image/jpeg"), TransferFile("a.pdf", 1, "application/pdf"))))
        assertEquals("report.pdf", FileShare.what(listOf(TransferFile("report.pdf", 1, "application/pdf"))))
        assertEquals("820KB", FileShare.bytes(820_000))
        assertEquals("1.25GB", FileShare.bytes(1_250_000_000))
        assertEquals("4.1/12.4 MB", FileShare.progress(4_100_000, 12_400_000))
    }

    @Test
    fun the_object_particle_follows_the_last_sound() {
        assertEquals("을", FileShare.objectParticle("사진 3장"))
        assertEquals("를", FileShare.objectParticle("파일 2개"))
        assertEquals("을", FileShare.objectParticle("파일 3"))
        assertEquals("를", FileShare.objectParticle("파일 2"))
        assertEquals("을(를)", FileShare.objectParticle("report.pdf"))
    }

    @Test
    fun each_transfer_has_its_own_job() {
        assertEquals(0x0c_000000 + 0x0c1d2, FileShare.jobId("0c1d2e3f405162738495a6b7c8d9eafb"))
        assertTrue(FileShare.jobId("fffff000000000000000000000000000") > 0x0b_0002)
    }

    @Test
    fun a_file_is_read_in_whole_chunks_and_a_short_one_is_noticed() {
        val buffer = ByteArray(8)
        assertTrue(Uploader.readFully(ByteArrayInputStream(byteArrayOf(1, 2, 3, 4, 5)), buffer, 5, "x"))
        assertFalse(Uploader.readFully(ByteArrayInputStream(byteArrayOf(1, 2, 3)), buffer, 5, "x"))
        assertEquals("00ff10", Uploader.hex(byteArrayOf(0, -1, 16)))
    }
}
