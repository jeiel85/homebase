package io.github.jeiel85.homebase.net

import io.github.jeiel85.homebase.push.WebPush
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import java.math.BigInteger
import java.security.KeyFactory
import java.security.KeyPair
import java.security.interfaces.ECPrivateKey
import java.security.spec.ECPrivateKeySpec
import java.time.Instant
import java.time.OffsetDateTime

/**
 * DECISIONS M-69: remote commands. Reads `commands.v1.json`, which the .NET ProtocolGoldenTests write: the list a PC
 * answers, a request and that request sealed with the clipboard key of `clip-key.v1.json` (fixed nonce), and the PC's
 * answers — so the two sides can't drift apart on the list, the sealed request or the results.
 */
class RemoteCommandsTest {

    private fun fixture(name: String) = File("../../docs/mobile-protocol/$name").readText(Charsets.UTF_8)

    private fun commands(): JSONObject = JSONObject(fixture("commands.v1.json"))

    /** The clipboard key of the fixtures, as this phone derives it (RFC 8291 Appendix A user-agent key). */
    private fun clipKey(): ByteArray {
        val uaPublic = WebPush.unb64url("BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4")
        val uaPrivate = WebPush.unb64url("q1dXpw3UpT5VOmu_cf_v6ih07Aems3njxI-JWgLcM94")
        val pub = WebPush.publicKey(uaPublic)
        val priv = KeyFactory.getInstance("EC").generatePrivate(ECPrivateKeySpec(BigInteger(1, uaPrivate), pub.params)) as ECPrivateKey
        val answer = JSONObject().put("schema", 1).put("pub", JSONObject(fixture("clip-key.v1.json")).getString("pcPublic")).toString()
        return ClipKeyAgreement(KeyPair(pub, priv)).finish(answer)!!
    }

    private fun instant(ts: String): Instant = OffsetDateTime.parse(ts).toInstant()

    @Test
    fun the_pcs_list_reads_as_it_was_written() {
        val json = commands().getJSONObject("list")
        val list = CommandList.parse(json.toString())!!

        val listed = json.getJSONArray("commands")
        assertEquals(listed.length(), list.commands.size)
        for (i in 0 until listed.length()) {
            val c = listed.getJSONObject(i)
            val info = list.commands[i]
            // In the PC's order, with the PC's labels.
            assertEquals(c.getString("name"), info.name)
            assertEquals(c.getString("label"), info.label)
            assertEquals(c.getString("kind"), info.kind)
            assertEquals(c.getBoolean("confirm"), info.confirm)
            if (info.kind == CommandKinds.SYSTEM || info.kind == CommandKinds.CUSTOM) assertTrue(info.asks)
        }

        val wake = json.optJSONObject("wake")
        if (wake == null) {
            assertTrue(list.wakeMacs.isEmpty())
        } else {
            val macs = wake.getJSONArray("macs")
            assertEquals((0 until macs.length()).map { macs.getString(it).lowercase().replace('-', ':') }, list.wakeMacs)
        }

        val pending = json.optJSONObject("pending")
        if (pending == null) {
            assertNull(list.pending)
        } else {
            assertEquals(pending.getString("name"), list.pending!!.name)
            assertEquals(instant(pending.getString("at")).toEpochMilli(), list.pending!!.atMillis)
        }
    }

    @Test
    fun the_sealed_request_opens_with_the_agreed_key() {
        val o = commands()
        val envelope = ClipEnvelope.parse(o.getJSONObject("envelope").toString())!!
        assertTrue(ClipProtocol.isValidId(envelope.id))

        val opened = CommandRequest.open(clipKey(), envelope)!!
        val request = o.getJSONObject("request")
        assertEquals(1, request.getInt("schema"))
        assertEquals(request.getString("command"), opened.command)
        assertEquals(instant(request.getString("ts")), instant(opened.ts))
        assertEquals(CommandRequest.parse(request.toString()), opened)

        // Another key — a PC this phone agreed nothing with — opens nothing.
        assertNull(CommandRequest.open(ByteArray(ClipCrypto.KEY_BYTES), envelope))
    }

    @Test
    fun a_request_this_phone_seals_has_the_shape_the_pc_reads() {
        val key = clipKey()
        val request = CommandRequest.of(RemoteCommandNames.RESTART, nowMillis = 1_795_000_000_000L)
        assertEquals(setOf("schema", "command", "ts"), request.toJson().keys().asSequence().toSet())
        assertEquals(1, request.toJson().getInt("schema"))
        assertEquals(Instant.ofEpochMilli(1_795_000_000_000L), instant(request.ts))

        val id = ClipProtocol.newId()
        val envelope = request.seal(key, id)
        assertEquals(id, envelope.id)
        assertEquals(setOf("schema", "id", "nonce", "data"), envelope.toJson().keys().asSequence().toSet())
        val opened = CommandRequest.open(key, ClipEnvelope.parse(envelope.toJson().toString())!!)
        assertEquals(request, opened)

        // A fresh nonce every time: the same request never seals to the same data.
        assertFalse(envelope.data == request.seal(key, id).data)
    }

    @Test
    fun the_pcs_answers_read_as_outcomes() {
        val o = commands()
        val done = CommandOutcome.of(200, o.getJSONObject("resultDone").toString())
        assertEquals(CommandOutcome.Ok(CommandStates.DONE), (done as CommandOutcome.Ok).copy(at = null))

        val scheduled = CommandOutcome.of(200, o.getJSONObject("resultScheduled").toString()) as CommandOutcome.Ok
        assertEquals(CommandStates.SCHEDULED, scheduled.state)
        assertNotNull(scheduled.atMillis)

        val errorJson = o.getJSONObject("resultError")
        assertFalse(errorJson.getBoolean("ok"))
        val refused = CommandOutcome.of(409, errorJson.toString())
        assertEquals(CommandOutcome.Refused(errorJson.getString("error")), refused)
    }

    @Test
    fun http_answers_without_a_body_still_mean_something() {
        assertEquals(CommandOutcome.Unsupported, CommandOutcome.of(404, null))
        assertEquals(CommandOutcome.Unsupported, CommandOutcome.of(405, "<html></html>"))
        assertEquals(CommandOutcome.Refused(CommandErrors.NOT_ALLOWED), CommandOutcome.of(403, null))
        assertEquals(CommandOutcome.Refused(CommandErrors.BUSY), CommandOutcome.of(429, ""))
        assertEquals(CommandOutcome.Refused(CommandErrors.INVALID), CommandOutcome.of(400, null))
        assertEquals(CommandOutcome.Failed("HTTP 502"), CommandOutcome.of(502, null))
        assertTrue(CommandOutcome.of(200, "not json") is CommandOutcome.Failed)
        assertEquals(CommandOutcome.Refused(CommandErrors.FAILED), CommandOutcome.of(200, """{"schema":1,"ok":false}"""))
        assertEquals(
            CommandOutcome.Refused(CommandErrors.NO_USER_SESSION),
            CommandOutcome.of(409, """{"schema":1,"ok":false,"error":"no_user_session"}"""),
        )
    }

    @Test
    fun a_key_the_pc_does_not_hold_is_agreed_again() {
        assertTrue(CommandOutcome.of(409, """{"schema":1,"ok":false,"error":"clip_key_needed"}""").needsNewKey)
        assertTrue(CommandOutcome.of(400, """{"schema":1,"ok":false,"error":"invalid_command"}""").needsNewKey)
        assertTrue(CommandOutcome.of(400, null).needsNewKey)
        assertFalse(CommandOutcome.of(403, """{"schema":1,"ok":false,"error":"not_allowed"}""").needsNewKey)
        assertFalse(CommandOutcome.of(409, """{"schema":1,"ok":false,"error":"replayed"}""").needsNewKey)
        assertFalse(CommandOutcome.Unreachable.needsNewKey)
    }

    @Test
    fun a_list_is_read_defensively() {
        val json = """
            {"schema":1,"commands":[
              {"name":"lock","label":"잠그기","kind":"session","confirm":false},
              {"name":"restart","label":"다시 시작","kind":"system","confirm":false},
              {"name":"custom:backup","label":"백업","kind":"custom"},
              {"name":"","label":"이름 없음","kind":"session","confirm":false},
              {"name":"lock","label":"또 잠그기","kind":"session","confirm":false},
              {"name":"future_thing","label":null}
            ],"wake":{"macs":["AA-BB-CC-DD-EE-FF","aa:bb:cc:dd:ee:ff","not a mac","00:00:00:00:00:00"]}}
        """.trimIndent()
        val list = CommandList.parse(json)!!
        assertEquals(listOf("lock", "restart", "custom:backup", "future_thing"), list.commands.map { it.name })
        assertEquals("잠그기", list.commands[0].label)
        assertFalse(list.commands[0].asks)
        // A PC that says a restart needs no confirmation doesn't talk the phone out of it.
        assertTrue(list.commands[1].asks)
        assertTrue(list.commands[2].confirm)
        assertTrue(list.commands[2].asks)
        // No label: the name stands in; no kind: one is inferred.
        assertEquals("future_thing", list.commands[3].label)
        assertEquals(CommandKinds.SESSION, list.commands[3].kind)
        assertEquals(listOf("aa:bb:cc:dd:ee:ff"), list.wakeMacs)
        assertNull(list.pending)

        assertNull(CommandList.parse("""{"schema":2,"commands":[]}"""))
        assertNull(CommandList.parse("nope"))
        assertNull(CommandList.parse(null))
        assertTrue(CommandList.parse("""{"schema":1}""")!!.commands.isEmpty())
    }
}
