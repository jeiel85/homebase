package io.github.jeiel85.homebase.net

import io.github.jeiel85.homebase.clip.ClipNotifier
import io.github.jeiel85.homebase.clip.ClipShare
import io.github.jeiel85.homebase.data.PcEntry
import io.github.jeiel85.homebase.data.PcStore
import io.github.jeiel85.homebase.push.PushEndpointsUpdate
import io.github.jeiel85.homebase.push.WebPush
import org.json.JSONObject
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNotNull
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
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

/**
 * DECISIONS M-62: clipboard text between this phone and a PC, sealed with the key the two agreed by ECDH
 * (`POST /api/v1/clip/key`). Derives the fixture key from the phone's side of `clip-key.v1.json` and opens
 * the fixtures the .NET ProtocolGoldenTests seal with it (fixed nonces), so the two implementations of the
 * agreement, the key derivation and AES-GCM can't drift apart.
 */
class ClipShareTest {

    private fun fixture(name: String) = File("../../docs/mobile-protocol/$name").readText(Charsets.UTF_8)

    /** RFC 8291 Appendix A: the user agent's key plays this phone, the application server's key the PC. */
    private val uaPublic = WebPush.unb64url("BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4")
    private val uaPrivate = WebPush.unb64url("q1dXpw3UpT5VOmu_cf_v6ih07Aems3njxI-JWgLcM94")

    private val clipText = "회의 링크: https://meet.example.com/abc-defg-hij\n비밀번호는 없습니다 \"그대로\" 붙여 넣으세요"
    private val pushText = "택배 송장번호 6081-2345-6789"

    private fun phonePair(): KeyPair {
        val pub = WebPush.publicKey(uaPublic)
        val priv = KeyFactory.getInstance("EC").generatePrivate(ECPrivateKeySpec(BigInteger(1, uaPrivate), pub.params)) as ECPrivateKey
        return KeyPair(pub, priv)
    }

    /** The fixture key as this phone derives it: its private key with the PC's public key from the fixture. */
    private fun fixtureKey(): ByteArray {
        val o = JSONObject(fixture("clip-key.v1.json"))
        val answer = JSONObject().put("schema", 1).put("pub", o.getString("pcPublic")).toString()
        return ClipKeyAgreement(phonePair()).finish(answer)!!
    }

    private fun instant(ts: String) = OffsetDateTime.parse(ts).toInstant()

    @Test
    fun the_phone_derives_the_key_the_pc_agreed() {
        val o = JSONObject(fixture("clip-key.v1.json"))
        val agreement = ClipKeyAgreement(phonePair())
        // The phone's public key goes out as 0x04 || X || Y, the fixture's device key.
        assertEquals(o.getString("devicePublic"), WebPush.b64url(agreement.devicePublic))
        assertEquals(o.getString("devicePublic"), agreement.requestJson().getString("pub"))

        val key = agreement.finish(JSONObject().put("schema", 1).put("pub", o.getString("pcPublic")).toString())!!
        assertEquals(o.getString("key"), WebPush.b64url(key))
        assertEquals(ClipCrypto.KEY_BYTES, key.size)
    }

    @Test
    fun an_answer_that_is_not_a_p256_point_agrees_nothing() {
        val agreement = ClipKeyAgreement(phonePair())
        val notOnCurve = ByteArray(65).also { it[0] = 4; it[64] = 1 }
        assertNull(agreement.finish(JSONObject().put("schema", 1).put("pub", WebPush.b64url(notOnCurve)).toString()))
        assertNull(agreement.finish(JSONObject().put("schema", 1).put("pub", "AAEC").toString()))
        assertNull(agreement.finish(JSONObject().put("schema", 2).put("pub", WebPush.b64url(uaPublic)).toString()))
        assertNull(agreement.finish("not json"))
        assertNull(agreement.finish(null))
    }

    @Test
    fun both_sides_agree_and_two_fresh_agreements_differ() {
        val pc = WebPush.generateKeyPair()
        val pcPublic = WebPush.uncompressed(pc.public as java.security.interfaces.ECPublicKey)
        val answer = JSONObject().put("schema", 1).put("pub", WebPush.b64url(pcPublic)).toString()

        val first = ClipKeyAgreement()
        val phoneKey = first.finish(answer)!!
        // The PC's side: its private key with the phone's public key, the device's key first in the info.
        val pcKey = ClipCrypto.agreedKey(pc.private, first.devicePublic, first.devicePublic, pcPublic)
        assertArrayEquals(pcKey, phoneKey)

        // A fresh pair per exchange: the next agreement with the very same PC key is another key.
        assertFalse(phoneKey.contentEquals(ClipKeyAgreement().finish(answer)!!))
    }

    @Test
    fun the_pcs_envelope_opens_with_the_agreed_key() {
        val envelope = ClipEnvelope.parse(fixture("clip.v1.json"))!!
        assertEquals("7d3c9e1f2a4b4c5d8e6f0a1b2c3d4e5f", envelope.id)

        val content = envelope.open(fixtureKey())!!
        assertEquals(clipText, content.text)
        assertEquals(clipText.length, content.chars)
        assertEquals(Instant.parse("2026-11-19T04:00:00Z"), instant(content.ts))
        assertFalse(content.sensitive)
    }

    @Test
    fun the_pcs_push_opens_with_the_agreed_key() {
        val push = ClipPush.parse(fixture("push-clip.v1.json"))!!
        assertEquals("6f1c0d2e9b8a4c7d", push.pcId)
        assertEquals("1a2b3c4d5e6f40718293a4b5c6d7e8f9", push.id)

        val content = push.open(fixtureKey())!!
        assertEquals(pushText, content.text)
        assertEquals(pushText.length, content.chars)
        assertEquals(Instant.parse("2026-11-19T04:05:00Z"), instant(content.ts))
        assertTrue(content.sensitive)
    }

    /**
     * The key used before the review (HKDF over the token's SHA-256) is what the Cloudflare edge could
     * compute from the bearer token it sees. It must not open anything any more.
     */
    @Test
    fun a_key_derived_from_the_token_opens_nothing() {
        fun hmac(key: ByteArray, data: ByteArray) = Mac.getInstance("HmacSHA256").apply { init(SecretKeySpec(key, "HmacSHA256")) }.doFinal(data)
        val ikm = MessageDigest.getInstance("SHA-256").digest("fixture-token".toByteArray(Charsets.UTF_8))
        val tokenKey = hmac(hmac(ByteArray(32), ikm), "homebase clip v1".toByteArray(Charsets.UTF_8) + byteArrayOf(1)).copyOf(32)

        assertNull(ClipEnvelope.parse(fixture("clip.v1.json"))!!.open(tokenKey))
        assertNull(ClipPush.parse(fixture("push-clip.v1.json"))!!.open(tokenKey))
    }

    @Test
    fun the_push_kind_routes_it_and_older_apps_find_no_title() {
        val plain = fixture("push-clip.v1.json")
        assertEquals(ClipProtocol.PUSH_KIND, PushEndpointsUpdate.kindOf(plain))
        assertFalse(JSONObject(plain).has("title"))
        assertNull(ClipPush.parse(fixture("push-endpoints.v1.json")))
        assertNull(ClipPush.parse("not json"))
    }

    @Test
    fun another_key_opens_nothing() {
        val other = ClipKeyAgreement().finish(JSONObject().put("schema", 1).put("pub", JSONObject(fixture("clip-key.v1.json")).getString("pcPublic")).toString())!!
        assertNull(ClipEnvelope.parse(fixture("clip.v1.json"))!!.open(other))
        assertNull(ClipPush.parse(fixture("push-clip.v1.json"))!!.open(other))
    }

    @Test
    fun an_altered_message_opens_nothing() {
        val key = fixtureKey()
        val envelope = ClipEnvelope.parse(fixture("clip.v1.json"))!!
        val flipped = String(envelope.data.toCharArray().also { it[10] = if (it[10] == 'A') 'B' else 'A' })
        assertNull(envelope.copy(data = flipped).open(key))
        assertNull(envelope.copy(nonce = "AAEC").open(key))
        assertNull(envelope.copy(id = "not-an-id").open(key))
    }

    @Test
    fun sealed_on_the_phone_opens_again() {
        val key = fixtureKey()
        val content = ClipContent.of("안녕 PC\n두 줄", sensitive = true, nowMillis = 1_763_524_800_000L)
        val envelope = ClipEnvelope.seal(key, ClipProtocol.newId(), content)

        assertTrue(ClipProtocol.isValidId(envelope.id))
        assertEquals(content, envelope.open(key))
        // A fresh nonce per message: the same text never seals the same twice.
        assertNotEquals(envelope.data, ClipEnvelope.seal(key, envelope.id, content).data)
    }

    @Test
    fun the_fixtures_content_sealed_with_its_nonce_opens_to_the_same_content() {
        val key = fixtureKey()
        val fromPc = ClipEnvelope.parse(fixture("clip.v1.json"))!!
        val content = fromPc.open(key)!!
        val nonce = ByteArray(ClipCrypto.NONCE_BYTES) { it.toByte() }
        val sealed = ClipEnvelope.seal(key, fromPc.id, content, nonce)

        assertEquals(fromPc.nonce, sealed.nonce)
        assertEquals(content, sealed.open(key))
    }

    @Test
    fun the_wire_form_is_what_the_pc_reads() {
        val key = fixtureKey()
        val o = ClipEnvelope.seal(key, "7d3c9e1f2a4b4c5d8e6f0a1b2c3d4e5f", ClipContent.of("x", sensitive = false)).toJson()
        assertEquals(setOf("schema", "id", "nonce", "data"), o.keys().asSequence().toSet())
        assertEquals(1, o.getInt("schema"))

        val inner = ClipContent.of("x", sensitive = false).toJson()
        assertFalse(inner.has("sensitive")) // left out unless set, like the PC
        assertTrue(ClipContent.of("x", sensitive = true).toJson().getBoolean("sensitive"))
        // Instant.toString always has seconds, which the PC's parser needs.
        assertEquals("2025-11-19T04:00:00Z", ClipContent.of("x", false, 1_763_524_800_000L).ts)
    }

    @Test
    fun the_pcs_answers_are_told_apart_by_their_error() {
        fun body(error: String) = JSONObject().put("schema", 1).put("error", error).put("message", "m").toString()
        assertEquals(ClipSendResult.Sent, ClipSendResult.of(200, "{\"schema\":1,\"accepted\":true}"))
        assertEquals(ClipSendResult.KeyNeeded, ClipSendResult.of(409, body("clip_key_needed")))
        assertEquals(ClipSendResult.NoUserSession, ClipSendResult.of(409, body("no_user_session")))
        assertEquals(ClipSendResult.ClipboardBusy, ClipSendResult.of(503, body("clipboard_busy")))
        assertEquals(ClipSendResult.Rejected, ClipSendResult.of(400, body("invalid_clip")))
        assertEquals(ClipSendResult.Unsupported, ClipSendResult.of(404, body("not_found")))
        assertEquals(ClipSendResult.Failed("m"), ClipSendResult.of(503, body("busy")))
    }

    @Test
    fun stored_keys_round_trip_and_bad_ones_read_as_none() {
        val key = fixtureKey()
        assertArrayEquals(key, ClipCrypto.decodeKey(ClipCrypto.encodeKey(key)))
        assertNull(ClipCrypto.decodeKey(null))
        assertNull(ClipCrypto.decodeKey(""))
        assertNull(ClipCrypto.decodeKey("AAEC")) // not 32 bytes
        assertNull(ClipCrypto.decodeKey("%%%"))
    }

    @Test
    fun a_new_pairing_does_not_inherit_the_old_clipboard_key() {
        val old = PcEntry("pc1", "HOME-PC", emptyList(), "dev-old", "token-old", alias = "거실", clipKey = "old-key")
        val repaired = PcStore.upserted(listOf(old), PcEntry("pc1", "HOME-PC", emptyList(), "dev-new", "token-new")).single()
        assertEquals("거실", repaired.alias) // the alias stays, as before
        assertNull(repaired.clipKey) // the PC dropped that key with the old pairing
    }

    @Test
    fun text_out_of_bounds_is_not_sent_nor_taken() {
        assertNotNull(ClipShare.problem(""))
        assertNotNull(ClipShare.problem(null))
        assertNotNull(ClipShare.problem("a".repeat(ClipProtocol.MAX_CHARS + 1)))
        assertNull(ClipShare.problem("a".repeat(ClipProtocol.MAX_CHARS)))

        val key = fixtureKey()
        val tooLong = ClipEnvelope.seal(key, ClipProtocol.newId(), ClipContent.of("a".repeat(ClipProtocol.MAX_CHARS + 1), false))
        assertNull(tooLong.open(key))
    }

    @Test
    fun a_push_without_text_still_opens_so_the_phone_can_fetch_it() {
        val key = fixtureKey()
        val offer = ClipContent(null, 12_345, "2026-11-19T04:05:00+00:00")
        val sealed = ClipEnvelope.seal(key, "1a2b3c4d5e6f40718293a4b5c6d7e8f9", offer)
        val push = ClipPush("6f1c0d2e9b8a4c7d", sealed.id, sealed.nonce, sealed.data)

        val opened = push.open(key)!!
        assertNull(opened.text)
        assertEquals(12_345, opened.chars)
        // As an envelope (the GET answer) it must carry text.
        assertNull(sealed.open(key))
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
