package io.github.jeiel85.homebase.net

import io.github.jeiel85.homebase.clip.ClipNotifier
import io.github.jeiel85.homebase.clip.ClipShare
import io.github.jeiel85.homebase.data.PcEntry
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import java.time.Instant
import java.time.OffsetDateTime

/**
 * DECISIONS M-62: clipboard text between this phone and a PC, sealed with a key from this phone's token
 * for that PC. Opens the fixtures the .NET ProtocolGoldenTests seal (fixed nonces), so the two
 * implementations of the key derivation and AES-GCM can't drift apart.
 */
class ClipShareTest {

    private fun fixture(name: String) = File("../../docs/mobile-protocol/$name").readText(Charsets.UTF_8)

    /** The token `ProtocolGoldenTests.EndpointsFixtureToken` sealed the fixtures for. */
    private val token = "fixture-token"

    private val clipText = "회의 링크: https://meet.example.com/abc-defg-hij\n비밀번호는 없습니다 \"그대로\" 붙여 넣으세요"
    private val pushText = "택배 송장번호 6081-2345-6789"

    private fun instant(ts: String) = OffsetDateTime.parse(ts).toInstant()

    @Test
    fun the_pcs_envelope_opens_with_this_phones_token() {
        val envelope = ClipEnvelope.parse(fixture("clip.v1.json"))!!
        assertEquals("7d3c9e1f2a4b4c5d8e6f0a1b2c3d4e5f", envelope.id)

        val content = envelope.open(token)!!
        assertEquals(clipText, content.text)
        assertEquals(clipText.length, content.chars)
        assertEquals(Instant.parse("2026-11-19T04:00:00Z"), instant(content.ts))
        assertFalse(content.sensitive)
    }

    @Test
    fun the_pcs_push_opens_with_this_phones_token_for_that_pc() {
        val push = ClipPush.parse(fixture("push-clip.v1.json"))!!
        assertEquals("6f1c0d2e9b8a4c7d", push.pcId)
        assertEquals("1a2b3c4d5e6f40718293a4b5c6d7e8f9", push.id)

        val content = push.open(token)!!
        assertEquals(pushText, content.text)
        assertEquals(pushText.length, content.chars)
        assertEquals(Instant.parse("2026-11-19T04:05:00Z"), instant(content.ts))
        assertTrue(content.sensitive)
    }

    @Test
    fun the_push_kind_routes_it_and_older_apps_find_no_title() {
        val plain = fixture("push-clip.v1.json")
        assertEquals(ClipProtocol.PUSH_KIND, io.github.jeiel85.homebase.push.PushEndpointsUpdate.kindOf(plain))
        assertFalse(JSONObject(plain).has("title"))
        assertNull(ClipPush.parse(fixture("push-endpoints.v1.json")))
        assertNull(ClipPush.parse("not json"))
    }

    @Test
    fun another_token_opens_nothing() {
        assertNull(ClipEnvelope.parse(fixture("clip.v1.json"))!!.open("another-token"))
        assertNull(ClipPush.parse(fixture("push-clip.v1.json"))!!.open("another-token"))
    }

    @Test
    fun an_altered_message_opens_nothing() {
        val envelope = ClipEnvelope.parse(fixture("clip.v1.json"))!!
        val flipped = String(envelope.data.toCharArray().also { it[10] = if (it[10] == 'A') 'B' else 'A' })
        assertNull(envelope.copy(data = flipped).open(token))
        assertNull(envelope.copy(nonce = "AAEC").open(token))
        assertNull(envelope.copy(id = "not-an-id").open(token))
    }

    @Test
    fun sealed_on_the_phone_opens_again() {
        val content = ClipContent.of("안녕 PC\n두 줄", sensitive = true, nowMillis = 1_763_524_800_000L)
        val envelope = ClipEnvelope.seal(token, ClipProtocol.newId(), content)

        assertTrue(ClipProtocol.isValidId(envelope.id))
        assertEquals(content, envelope.open(token))
        assertNull(envelope.open("another-token"))
        // A fresh nonce per message: the same text never seals the same twice.
        assertNotEquals(envelope.data, ClipEnvelope.seal(token, envelope.id, content).data)
    }

    @Test
    fun the_fixtures_content_sealed_with_its_nonce_opens_to_the_same_content() {
        val fromPc = ClipEnvelope.parse(fixture("clip.v1.json"))!!
        val content = fromPc.open(token)!!
        val nonce = ByteArray(ClipCrypto.NONCE_BYTES) { it.toByte() }
        val sealed = ClipEnvelope.seal(token, fromPc.id, content, nonce)

        assertEquals(fromPc.nonce, sealed.nonce)
        assertEquals(content, sealed.open(token))
    }

    @Test
    fun the_wire_form_is_what_the_pc_reads() {
        val o = ClipEnvelope.seal(token, "7d3c9e1f2a4b4c5d8e6f0a1b2c3d4e5f", ClipContent.of("x", sensitive = false)).toJson()
        assertEquals(setOf("schema", "id", "nonce", "data"), o.keys().asSequence().toSet())
        assertEquals(1, o.getInt("schema"))

        val inner = ClipContent.of("x", sensitive = false).toJson()
        assertFalse(inner.has("sensitive")) // left out unless set, like the PC
        assertTrue(ClipContent.of("x", sensitive = true).toJson().getBoolean("sensitive"))
        // Instant.toString always has seconds, which the PC's parser needs.
        assertEquals("2025-11-19T04:00:00Z", ClipContent.of("x", false, 1_763_524_800_000L).ts)
    }

    @Test
    fun text_out_of_bounds_is_not_sent_nor_taken() {
        assertNotNull(ClipShare.problem(""))
        assertNotNull(ClipShare.problem(null))
        assertNotNull(ClipShare.problem("a".repeat(ClipProtocol.MAX_CHARS + 1)))
        assertNull(ClipShare.problem("a".repeat(ClipProtocol.MAX_CHARS)))

        val tooLong = ClipEnvelope.seal(token, ClipProtocol.newId(), ClipContent.of("a".repeat(ClipProtocol.MAX_CHARS + 1), false))
        assertNull(tooLong.open(token))
    }

    @Test
    fun a_push_without_text_still_opens_so_the_phone_can_fetch_it() {
        val offer = ClipContent(null, 12_345, "2026-11-19T04:05:00+00:00")
        val sealed = ClipEnvelope.seal(token, "1a2b3c4d5e6f40718293a4b5c6d7e8f9", offer)
        val push = ClipPush("6f1c0d2e9b8a4c7d", sealed.id, sealed.nonce, sealed.data)

        val opened = push.open(token)!!
        assertNull(opened.text)
        assertEquals(12_345, opened.chars)
        // As an envelope (the GET answer) it must carry text.
        assertNull(sealed.open(token))
    }

    @Test
    fun stale_pushes_do_not_reach_the_clipboard() {
        val now = Instant.parse("2026-11-19T05:00:00Z").toEpochMilli()
        assertFalse(ClipShare.isStale("2026-11-19T04:45:00+00:00", now))
        assertTrue(ClipShare.isStale("2026-11-19T04:00:00+00:00", now))
        assertFalse(ClipShare.isStale("garbled", now))
    }

    @Test
    fun the_last_used_pc_comes_first_and_the_tile_sends_there() {
        fun pc(id: String) = PcEntry(id, id.uppercase(), emptyList(), "dev-$id", "token-$id")
        val a = pc("a")
        val b = pc("b")
        val c = pc("c")

        assertEquals(listOf(b, a, c), ClipShare.ordered(listOf(a, b, c), "b"))
        assertEquals(listOf(a, b, c), ClipShare.ordered(listOf(a, b, c), "gone"))
        assertEquals(b, ClipShare.autoTarget(listOf(a, b, c), "b"))
        assertNull(ClipShare.autoTarget(listOf(a, b, c), null)) // ask
        assertEquals(a, ClipShare.autoTarget(listOf(a), null)) // the only PC
    }

    @Test
    fun the_notification_preview_is_one_short_line() {
        assertEquals("a b c", ClipNotifier.preview("  a\n b\t\tc  "))
        assertEquals(121, ClipNotifier.preview("x".repeat(500)).length)
    }
}
