package io.github.jeiel85.homebase.push

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import java.math.BigInteger
import java.security.GeneralSecurityException
import java.security.KeyFactory
import java.security.interfaces.ECPrivateKey
import java.security.interfaces.ECPublicKey
import java.security.spec.ECPrivateKeySpec
import org.json.JSONObject

/**
 * RFC 8291 Appendix A, the same vector the .NET WebPushEncryptionTests use, plus the PC-encrypted
 * fixture `push-message.v1.aes128gcm.txt` so both implementations are checked against each other.
 */
class WebPushTest {

    private val uaPublic = WebPush.unb64url("BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4")
    private val uaPrivate = WebPush.unb64url("q1dXpw3UpT5VOmu_cf_v6ih07Aems3njxI-JWgLcM94")
    private val auth = WebPush.unb64url("BTBZMqHH6r4Tts7J_aSIgg")
    private val header = "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8"
    private val ciphertext = "8pfeW0KbunFT06SuDKoJH9Ql87S1QUrdirN6GcG7sFz1y1sqLgVi1VhjVkHsUoEsbI_0LpXMuGvnzQ"

    private fun privateKey(): ECPrivateKey {
        val params = (WebPush.publicKey(uaPublic) as ECPublicKey).params
        return KeyFactory.getInstance("EC").generatePrivate(ECPrivateKeySpec(BigInteger(1, uaPrivate), params)) as ECPrivateKey
    }

    private fun body() = WebPush.unb64url(header) + WebPush.unb64url(ciphertext)

    private fun fixture(name: String) = File("../../docs/mobile-protocol/$name").readText(Charsets.UTF_8)

    @Test
    fun decrypts_the_rfc_8291_appendix_a_message() {
        val plain = WebPush.decrypt(body(), privateKey(), uaPublic, auth)
        assertEquals("When I grow up, I want to be a watermelon", plain.toString(Charsets.UTF_8))
    }

    @Test
    fun decrypts_what_the_pc_encrypted_and_it_parses_as_the_push_fixture() {
        val encrypted = WebPush.unb64url(fixture("push-message.v1.aes128gcm.txt").trim())
        val plain = WebPush.decrypt(encrypted, privateKey(), uaPublic, auth).toString(Charsets.UTF_8)

        assertEquals(PushMessage.parse(fixture("push-message.v1.json")), PushMessage.parse(plain))
    }

    @Test(expected = GeneralSecurityException::class)
    fun a_flipped_bit_fails_the_tag() {
        val b = body()
        b[b.size - 1] = (b[b.size - 1].toInt() xor 1).toByte()
        WebPush.decrypt(b, privateKey(), uaPublic, auth)
    }

    @Test(expected = GeneralSecurityException::class)
    fun a_wrong_auth_secret_fails() {
        WebPush.decrypt(body(), privateKey(), uaPublic, ByteArray(16))
    }

    @Test(expected = GeneralSecurityException::class)
    fun a_sender_key_off_the_curve_is_refused_before_ecdh() {
        val b = body()
        b[85] = (b[85].toInt() xor 1).toByte() // last byte of the sender's Y coordinate
        WebPush.decrypt(b, privateKey(), uaPublic, auth)
    }

    @Test
    fun generated_keys_export_as_65_byte_points_that_import_back() {
        val pair = WebPush.generateKeyPair()
        val raw = WebPush.uncompressed(pair.public as ECPublicKey)
        assertEquals(65, raw.size)
        assertEquals(4, raw[0].toInt())
        assertArrayEquals(raw, WebPush.uncompressed(WebPush.publicKey(raw)))
        assertEquals(87, WebPush.b64url(raw).length)
    }
}

class PushProtocolTest {

    private fun fixture(name: String) = File("../../docs/mobile-protocol/$name").readText(Charsets.UTF_8)

    @Test
    fun push_message_fixture_parses_with_every_field() {
        val m = PushMessage.parse(fixture("push-message.v1.json"))
        assertEquals("6f1c0d2e9b8a4c7d", m.pcId)
        assertEquals("HOME-PC", m.pcName)
        assertTrue(m.isToast)
        assertEquals("KakaoTalk", m.source)
        assertEquals(3, m.count)
        assertEquals("엄마: 저녁 먹었니?\n팀장: 회의 10분 뒤", m.body)
        assertEquals("info", m.priority)
    }

    @Test(expected = io.github.jeiel85.homebase.net.ProtocolException::class)
    fun another_schema_is_rejected() {
        PushMessage.parse(fixture("push-message.v1.json").replaceFirst("\"schema\": 1", "\"schema\": 2"))
    }

    @Test
    fun a_missing_body_is_null() {
        val o = JSONObject(fixture("push-message.v1.json")).apply { remove("body") }
        assertNull(PushMessage.parse(o.toString()).body)
    }

    @Test
    fun subscription_serializes_exactly_like_the_fixture() {
        val expected = JSONObject(fixture("push-subscription.v1.json"))
        val actual = PushSubscription(
            expected.getString("endpoint"), expected.getString("p256dh"), expected.getString("auth"),
            // Out of order on purpose: the wire form is sorted, like the PC's normalized copy.
            listOf("event_log", "advisor"),
        ).toJson()
        assertEquals(expected.toMap(), actual.toMap())
        // The fixture's keys are the Appendix A phone key: a real P-256 point, a 16-byte secret.
        WebPush.publicKey(WebPush.unb64url(actual.getString("p256dh")))
        assertEquals(16, WebPush.unb64url(actual.getString("auth")).size)
    }

    @Test
    fun subscription_response_fixture_parses() {
        val r = PushSubscriptionResponse.parse(fixture("push-subscription-response.v1.json"))
        assertEquals("a1b2c3d4e5f6", r.deviceId)
        assertTrue(r.registered)
    }

    @Test
    fun channels_are_per_pc_and_source_and_stable() {
        val a = PushNotifier.channelId("pc1", "KakaoTalk")
        assertEquals(a, PushNotifier.channelId("pc1", "kakaotalk"))
        assertTrue(a != PushNotifier.channelId("pc2", "KakaoTalk"))
        assertTrue(a != PushNotifier.channelId("pc1", "Outlook"))
        val toast = PushMessage.parse(fixture("push-message.v1.json"))
        assertEquals("KakaoTalk (Windows 알림)", PushNotifier.channelName(toast))
        assertEquals("프로세스 감시", PushNotifier.channelName(toast.copy(category = "process_watch", source = "process_watch")))
    }
}
