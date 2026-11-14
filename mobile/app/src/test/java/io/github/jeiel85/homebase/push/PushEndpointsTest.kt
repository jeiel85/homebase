package io.github.jeiel85.homebase.push

import io.github.jeiel85.homebase.data.PcEntry
import io.github.jeiel85.homebase.data.PcStore
import io.github.jeiel85.homebase.net.Endpoint
import io.github.jeiel85.homebase.net.ProtocolException
import io.github.jeiel85.homebase.net.parseEndpointsResponse
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import java.time.OffsetDateTime

/** DECISIONS M-23: a PC pushes its new paths; only the PC holding this phone's token for it can sign them. */
class PushEndpointsTest {

    private fun fixture(name: String) = File("../../docs/mobile-protocol/$name").readText(Charsets.UTF_8)

    /** The token `ProtocolGoldenTests.EndpointsFixtureToken` signed the fixture with. */
    private val token = "fixture-token"

    @Test
    fun fixture_from_the_pc_verifies_and_carries_the_endpoints_fixture_paths() {
        val update = PushEndpointsUpdate.verify(fixture("push-endpoints.v1.json"), token)

        assertEquals("6f1c0d2e9b8a4c7d", update.pcId)
        assertEquals(OffsetDateTime.parse("2026-11-14T08:30:00+00:00").toInstant().toEpochMilli(), update.tsEpochMillis)
        assertEquals(parseEndpointsResponse(fixture("endpoints.v1.json")), update.endpoints)
    }

    @Test
    fun kind_tells_path_updates_from_notifications() {
        assertEquals(PushEndpointsUpdate.KIND, PushEndpointsUpdate.kindOf(fixture("push-endpoints.v1.json")))
        assertNull(PushEndpointsUpdate.kindOf(fixture("push-message.v1.json")))
        assertNull(PushEndpointsUpdate.kindOf("not json"))
    }

    @Test
    fun another_pcs_token_does_not_verify() {
        // Every PC can encrypt to this phone; only the MAC ties the paths to the right PC.
        assertThrows(ProtocolException::class.java) {
            PushEndpointsUpdate.verify(fixture("push-endpoints.v1.json"), "token-of-another-pc")
        }
    }

    @Test
    fun tampered_data_does_not_verify() {
        val o = JSONObject(fixture("push-endpoints.v1.json"))
        val inner = JSONObject(WebPush.unb64url(o.getString("data")).toString(Charsets.UTF_8))
        inner.getJSONArray("endpoints").getJSONObject(3).put("host", "attacker.trycloudflare.com")
        o.put("data", WebPush.b64url(inner.toString().toByteArray(Charsets.UTF_8)))
        assertThrows(ProtocolException::class.java) { PushEndpointsUpdate.verify(o.toString(), token) }
    }

    @Test
    fun envelope_pc_must_match_the_signed_pc() {
        val o = JSONObject(fixture("push-endpoints.v1.json")).put("pcId", "someotherpc")
        assertThrows(ProtocolException::class.java) { PushEndpointsUpdate.verify(o.toString(), token) }
    }

    @Test
    fun only_a_newer_non_empty_list_replaces_the_stored_one() {
        val paths = listOf(Endpoint(Endpoint.CLOUDFLARE, "new.trycloudflare.com", 443, true))
        val fresh = PcEntry("pc", "HOME-PC", emptyList(), "dev", "t")
        assertTrue(PcStore.acceptsPush(fresh, paths, 1_000))
        assertFalse(PcStore.acceptsPush(fresh, emptyList(), 1_000))

        val pushed = fresh.copy(pushedEndpointsAt = 1_000)
        assertFalse(PcStore.acceptsPush(pushed, paths, 1_000)) // the same push relayed twice
        assertFalse(PcStore.acceptsPush(pushed, paths, 999))
        assertTrue(PcStore.acceptsPush(pushed, paths, 1_001))
    }
}
