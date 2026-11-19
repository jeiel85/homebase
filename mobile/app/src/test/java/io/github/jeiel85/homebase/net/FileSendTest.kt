package io.github.jeiel85.homebase.net

import io.github.jeiel85.homebase.push.PushEndpointsUpdate
import io.github.jeiel85.homebase.push.WebPush
import io.github.jeiel85.homebase.share.ReceivedStore
import io.github.jeiel85.homebase.share.Uploader
import org.json.JSONObject
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import java.math.BigInteger
import java.security.KeyFactory
import java.security.KeyPair
import java.security.MessageDigest
import java.security.interfaces.ECPrivateKey
import java.security.spec.ECPrivateKeySpec
import java.time.Instant
import java.time.OffsetDateTime

/**
 * DECISIONS M-65: files a PC sends to this phone. Opens `push-share.v1.json` and `transfer-offer.v1.json`, which the
 * .NET ProtocolGoldenTests seal with the clipboard key of `clip-key.v1.json`, and the chunks of `transfer.v1.json`
 * (a fetched chunk is sealed exactly like an uploaded one).
 */
class FileSendTest {

    private fun fixture(name: String) = File("../../docs/mobile-protocol/$name").readText(Charsets.UTF_8)

    /** The clipboard key of the fixtures, as this phone derives it (RFC 8291 Appendix A user-agent key). */
    private fun clipKey(): ByteArray {
        val uaPublic = WebPush.unb64url("BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4")
        val uaPrivate = WebPush.unb64url("q1dXpw3UpT5VOmu_cf_v6ih07Aems3njxI-JWgLcM94")
        val pub = WebPush.publicKey(uaPublic)
        val priv = KeyFactory.getInstance("EC").generatePrivate(ECPrivateKeySpec(BigInteger(1, uaPrivate), pub.params)) as ECPrivateKey
        val answer = JSONObject().put("schema", 1).put("pub", JSONObject(fixture("clip-key.v1.json")).getString("pcPublic")).toString()
        return ClipKeyAgreement(KeyPair(pub, priv)).finish(answer)!!
    }

    /** The two files of `transfer.v1.json`, whole (each is one chunk). */
    private fun plains(): List<ByteArray> {
        val chunks = JSONObject(fixture("transfer.v1.json")).getJSONArray("chunks")
        return (0 until chunks.length()).map { WebPush.unb64url(chunks.getJSONObject(it).getString("plain")) }
    }

    private fun sha256(bytes: ByteArray) = Uploader.hex(MessageDigest.getInstance("SHA-256").digest(bytes))

    private fun offerEnvelope(): ClipEnvelope = ClipEnvelope.parse(fixture("transfer-offer.v1.json"))!!

    @Test
    fun the_share_push_opens_with_the_clipboard_key() {
        val push = ClipPush.parse(fixture("push-share.v1.json"), FileSendProtocol.PUSH_KIND)!!
        assertEquals("6f1c0d2e9b8a4c7d", push.pcId)
        assertEquals("0c1d2e3f405162738495a6b7c8d9eafb", push.id) // the transfer id

        val notice = ShareNotice.open(clipKey(), push)!!
        assertEquals(2, notice.count)
        assertEquals(plains().sumOf { it.size.toLong() }, notice.totalBytes)
        assertEquals("회의록 11월.txt", notice.name)
        assertEquals(Instant.parse("2026-11-19T07:00:00Z"), OffsetDateTime.parse(notice.ts).toInstant())
    }

    @Test
    fun the_share_push_is_told_apart_from_a_clip_and_older_apps_find_no_title() {
        val plain = fixture("push-share.v1.json")
        assertEquals(FileSendProtocol.PUSH_KIND, PushEndpointsUpdate.kindOf(plain))
        assertNull(ClipPush.parse(plain)) // not a clipboard push
        assertNull(ClipPush.parse(fixture("push-clip.v1.json"), FileSendProtocol.PUSH_KIND))
        assertFalse(JSONObject(plain).has("title"))
    }

    @Test
    fun another_key_opens_neither_the_push_nor_the_offer() {
        val other = ClipKeyAgreement().finish(
            JSONObject().put("schema", 1).put("pub", JSONObject(fixture("clip-key.v1.json")).getString("pcPublic")).toString(),
        )!!
        assertNull(ShareNotice.open(other, ClipPush.parse(fixture("push-share.v1.json"), FileSendProtocol.PUSH_KIND)!!))
        assertNull(TransferOffer.open(other, offerEnvelope()))
    }

    @Test
    fun the_offer_lists_the_files_with_their_hashes() {
        val offer = TransferOffer.open(clipKey(), offerEnvelope())!!
        val (first, second) = plains()
        assertEquals(
            listOf(
                OfferFile("회의록 11월.txt", first.size.toLong(), "text/plain", sha256(first)),
                OfferFile("screen.png", second.size.toLong(), "image/png", sha256(second)),
            ),
            offer.files,
        )
        assertEquals(first.size.toLong() + second.size, offer.totalBytes)
        assertEquals(Instant.parse("2026-11-19T07:00:00Z"), OffsetDateTime.parse(offer.ts).toInstant())
    }

    @Test
    fun an_altered_offer_opens_nothing() {
        val e = offerEnvelope()
        val flipped = String(e.data.toCharArray().also { it[12] = if (it[12] == 'A') 'B' else 'A' })
        assertNull(TransferOffer.open(clipKey(), e.copy(data = flipped)))
        assertNull(TransferOffer.open(clipKey(), e.copy(id = "not-an-id")))
    }

    @Test
    fun a_fetched_chunk_opens_like_an_uploaded_one() {
        val transfer = JSONObject(fixture("transfer.v1.json"))
        val key = TransferCrypto.transferKey(clipKey(), transfer.getString("transferId"))
        val chunks = transfer.getJSONArray("chunks")
        for (i in 0 until chunks.length()) {
            val c = chunks.getJSONObject(i)
            val opened = TransferCrypto.openChunk(key, c.getInt("f"), c.getLong("n"), WebPush.unb64url(c.getString("data")))!!
            assertArrayEquals(WebPush.unb64url(c.getString("plain")), opened)
        }
        // The hashes the offer gives are what those chunks make.
        val offer = TransferOffer.open(clipKey(), offerEnvelope())!!
        assertEquals(offer.files.map { it.sha256 }, plains().map { sha256(it) })
    }

    @Test
    fun files_come_without_asking_except_large_ones_over_mobile_data() {
        // Not metered: any size (docs/SHARE_CONVENIENCE.md §9.3).
        assertTrue(FileSendProtocol.autoReceive(0, metered = false))
        assertTrue(FileSendProtocol.autoReceive(25_000_001, metered = false))
        assertTrue(FileSendProtocol.autoReceive(2_000_000_000, metered = false))
        // Metered: up to 25 MB, beyond that only with "모바일 데이터에서도 자동으로 받기".
        assertTrue(FileSendProtocol.autoReceive(1_000, metered = true))
        assertTrue(FileSendProtocol.autoReceive(25_000_000, metered = true))
        assertFalse(FileSendProtocol.autoReceive(25_000_001, metered = true))
        assertTrue(FileSendProtocol.autoReceive(25_000_001, metered = true, onMetered = true))
        // Nonsense is never fetched by itself.
        assertFalse(FileSendProtocol.autoReceive(-1, metered = false))
        assertFalse(FileSendProtocol.autoReceive(-1, metered = true, onMetered = true))
    }

    @Test
    fun a_large_fetch_that_started_by_itself_waits_for_wifi_unless_allowed() {
        assertFalse(FileSendProtocol.unmeteredOnly(25_000_000, onMetered = false))
        assertTrue(FileSendProtocol.unmeteredOnly(25_000_001, onMetered = false))
        assertFalse(FileSendProtocol.unmeteredOnly(25_000_001, onMetered = true))
    }

    @Test
    fun a_notice_older_than_a_day_is_dropped() {
        val now = Instant.parse("2026-11-20T08:00:00Z").toEpochMilli()
        assertFalse(FileSendProtocol.isExpired("2026-11-19T09:00:00+00:00", now))
        assertTrue(FileSendProtocol.isExpired("2026-11-19T07:00:00+00:00", now))
        assertFalse(FileSendProtocol.isExpired("garbled", now))
    }

    @Test
    fun names_from_a_pc_are_made_safe() {
        assertEquals("name.txt", ReceivedNames.clean("../../evil/name.txt"))
        assertEquals("y.png", ReceivedNames.clean("C:\\x\\y.png"))
        assertEquals("atxt.exe", ReceivedNames.clean("a\u202Etxt.exe")) // the override that fakes "exe.txt"
        assertEquals("ab.txt", ReceivedNames.clean("a\u0000\u001Fb.txt"))
        assertEquals("abc.txt", ReceivedNames.clean("a<b>c?.txt"))
        assertEquals("hidden", ReceivedNames.clean("  ..hidden. "))
        assertEquals("file", ReceivedNames.clean(""))
        assertEquals("file", ReceivedNames.clean(null))
        assertEquals("file-2", ReceivedNames.clean("/", "file-2"))
        assertEquals("회의록 11월.txt", ReceivedNames.clean("회의록 11월.txt"))
        val long = ReceivedNames.clean("가".repeat(300) + ".jpg")
        assertEquals(ReceivedNames.MAX_CHARS, long.length)
        assertTrue(long.endsWith(".jpg"))
    }

    @Test
    fun a_name_already_taken_gets_a_number() {
        val taken = setOf("a.jpg", "a (2).jpg", "notes")
        assertEquals("a (3).jpg", ReceivedStore.unique("a.jpg") { it in taken })
        assertEquals("notes (2)", ReceivedStore.unique("notes") { it in taken })
        assertEquals("b.jpg", ReceivedStore.unique("b.jpg") { it in taken })
    }

    @Test
    fun the_phone_says_it_fetches_files() {
        assertEquals("share", FileSendProtocol.FEATURE)
        assertTrue(io.github.jeiel85.homebase.push.PushSubscription.FEATURES.containsAll(listOf("clip", "share")))
    }
}
