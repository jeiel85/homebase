package io.github.jeiel85.homebase.net

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * Parses the same fixtures the .NET ProtocolGoldenTests serialize (docs/mobile-protocol), so the
 * PC and the app can't drift apart silently.
 */
class ProtocolTest {

    private fun fixture(name: String): String {
        // Gradle runs unit tests with the module directory (mobile/app) as the working directory.
        val file = File("../../docs/mobile-protocol/$name")
        return file.readText(Charsets.UTF_8)
    }

    @Test
    fun snapshot_fixture_parses_with_every_field() {
        val s = LiveSnapshot.parse(fixture("snapshot.v1.json"))

        assertEquals("HOME-PC", s.pcName)
        assertEquals(23.4, s.system.cpuPct, 0.001)
        assertEquals(34_359_738_368L, s.system.memTotalBytes)
        assertTrue(s.sessionAvailable)
        assertTrue(s.netPerProcess)
        assertEquals(2, s.apps.size)

        val terminal = s.apps[0]
        assertEquals("WindowsTerminal", terminal.process)
        assertTrue(terminal.foreground)
        assertEquals("✳ Homebase 모바일 앱", terminal.windows.single().title)
        assertEquals(2048L, terminal.usage.netRxBps)
        assertEquals("node", terminal.children.single().name)

        // Omitted (null) network rates stay null so the UI shows "—", not 0.
        assertNull(s.apps[1].usage.netRxBps)
        assertEquals("Homebase 모바일 앱", s.claudeSessions.single().title)
        assertEquals("""D:\Project\homebase""", s.claudeSessions.single().project)
    }

    @Test
    fun snapshot_fixture_carries_the_hardware_panel() {
        val h = LiveSnapshot.parse(fixture("snapshot.v1.json")).hardware!!

        assertEquals("AMD Ryzen 7 250 w/ Radeon 780M Graphics", h.cpuName)
        assertEquals(61.5, h.cpuTempC!!, 0.001)
        assertNull(h.boardTempC)

        val (dgpu, igpu) = h.gpus
        assertEquals("NVIDIA GeForce RTX 5060 Laptop GPU", dgpu.name)
        assertEquals("nvidia", dgpu.vendor)
        assertFalse(dgpu.integrated)
        assertEquals(37.0, dgpu.loadPct!!, 0.001)
        assertEquals(41.2, dgpu.powerW!!, 0.001)
        assertNull(dgpu.powerLimitW) // a laptop GPU has none: null, not 0
        assertNull(dgpu.fanPct)
        // A discrete card shows its own VRAM, an integrated one the system memory it uses.
        assertEquals(1_492_123_648L to 8_546_942_976L, dgpu.memory)
        assertTrue(igpu.integrated)
        assertEquals(355_807_232L to 8_217_880_576L, igpu.memory)
        assertEquals(3.2, igpu.powerPct!!, 0.001)

        assertEquals(listOf("C:", "D:"), h.disks.map { it.name })
        assertEquals(96.8, h.disks[1].usedPct, 0.1)
        val ping = h.ping!!
        assertEquals("1.1.1.1", ping.host)
        assertEquals(10.3, ping.avgMs!!, 0.001)
        assertEquals(0.0, ping.lossPct, 0.001)
        assertEquals(30, ping.samples)
    }

    @Test
    fun snapshot_from_an_older_pc_has_no_hardware() {
        val old = org.json.JSONObject(fixture("snapshot.v1.json")).apply { remove("hardware") }.toString()
        assertNull(LiveSnapshot.parse(old).hardware)
    }

    @Test
    fun hardware_values_the_pc_cannot_read_stay_null() {
        val h = Hardware.parse(
            org.json.JSONObject("""{"cpuName":null,"gpus":[{"name":"GPU","integrated":false},{"integrated":true}],"disks":[]}"""),
        )
        assertNull(h.cpuName)
        assertNull(h.ping)
        val g = h.gpus.single() // the entry without a name is dropped
        assertNull(g.loadPct)
        assertNull(g.tempC)
        assertNull(g.memory)
    }

    @Test(expected = ProtocolException::class)
    fun unknown_schema_is_rejected_not_half_parsed() {
        LiveSnapshot.parse(fixture("snapshot.v1.json").replaceFirst("\"schema\": 1", "\"schema\": 2"))
    }

    @Test
    fun pairing_offer_fixture_parses_every_path_in_order() {
        val offer = PairingOffer.parse(fixture("pairing-offer.v1.json"))!!
        assertEquals(listOf("lan", "tailscale", "tailscale", "cloudflare"), offer.endpoints.map { it.transport })
        val lan = offer.endpoints[0]
        assertEquals("192.168.45.101", lan.host)
        assertTrue(lan.tls)
        assertEquals("3f6c1b1e8f0d4a7b9c2e5d8a1b4c7e0f3a6d9c2b5e8f1a4d7c0b3e6f9a2d5c8b", lan.certSha256)
        assertEquals("https://192.168.45.101:47820/api/v1/live", lan.url("/api/v1/live"))
        assertEquals("http://home-pc.tail1234.ts.net:47820/api/v1/info", offer.endpoints[1].url("/api/v1/info"))
        val cf = offer.endpoints[3]
        assertEquals("https://home-pc.example.com/api/v1/info", cf.url("/api/v1/info"))
        assertNull(cf.certSha256)
        assertEquals("q8V3k1yZp0Wm2LrT5nXa9Q", offer.code)
    }

    @Test
    fun pairing_link_fixture_parses_to_the_same_offer() {
        val link = fixture("pairing-link.v1.txt").trim()
        val fromLink = PairingOffer.parse(link)!!
        val fromJson = PairingOffer.parse(fixture("pairing-offer.v1.json"))!!
        assertEquals(fromJson, fromLink)

        // The install page hands the same payload to the app as homebase://pair/<payload>.
        val payload = PairingLink.payload(link)!!
        assertEquals(fromJson, PairingOffer.parse("homebase://pair/$payload"))
        assertTrue(PairingLink.isLink("homebase://pair/$payload"))
    }

    @Test
    fun other_urls_and_broken_links_are_not_offers() {
        assertFalse(PairingLink.isLink("https://jeiel85.github.io/homebase/"))
        assertFalse(PairingLink.isLink("https://jeiel85.github.io/homebase/pair/"))
        assertFalse(PairingLink.isLink("https://evil.example/homebase/pair/#abc"))
        assertNull(PairingOffer.parse("https://jeiel85.github.io/homebase/pair/#!!!not-base64"))
        assertNull(PairingOffer.parse("homebase://pair/"))
    }

    @Test
    fun v1_qr_without_endpoints_becomes_tailscale_paths() {
        val legacy = """{"hb":1,"pcId":"p","pcName":"PC","hosts":["pc.ts.net","100.1.2.3"],"port":47820,"code":"c"}"""
        val offer = PairingOffer.parse(legacy)!!
        assertEquals(listOf("tailscale", "tailscale"), offer.endpoints.map { it.transport })
        assertEquals(listOf("pc.ts.net", "100.1.2.3"), offer.endpoints.map { it.host })
        assertFalse(offer.endpoints.any { it.tls })
    }

    @Test
    fun lan_endpoint_without_a_valid_pin_is_dropped() {
        val pin = "a".repeat(64)
        val text = """{"hb":1,"pcId":"p","pcName":"PC","hosts":[],"port":47820,"code":"c","endpoints":[""" +
            """{"transport":"lan","host":"192.168.0.2","port":47820,"tls":true},""" +
            """{"transport":"lan","host":"192.168.0.3","port":47820,"tls":false,"certSha256":"$pin"},""" +
            """{"transport":"cloudflare","host":"x.trycloudflare.com","port":443,"tls":true}]}"""
        val offer = PairingOffer.parse(text)!!
        assertEquals(listOf("cloudflare"), offer.endpoints.map { it.transport })
        assertTrue(offer.endpoints.single().isQuickTunnel)
    }

    @Test
    fun disallowed_hosts_and_cleartext_non_tailscale_endpoints_are_dropped() {
        val pin = "a".repeat(64)
        val text = """{"hb":1,"pcId":"p","pcName":"PC","hosts":[],"port":47820,"code":"c","endpoints":[""" +
            """{"transport":"cloudflare","host":"evil.example.com","port":80,"tls":false},""" +
            """{"transport":"cloudflare","host":"1.2.3.4","port":443,"tls":true},""" +
            """{"transport":"tailscale","host":"192.168.1.5","port":47820,"tls":false},""" +
            """{"transport":"tailscale","host":"8.8.8.8","port":47820,"tls":false},""" +
            """{"transport":"tailscale","host":"evil.com/path","port":47820,"tls":false},""" +
            """{"transport":"tailscale","host":"user@evil.ts.net","port":47820,"tls":false},""" +
            """{"transport":"lan","host":"8.8.8.8","port":47820,"tls":true,"certSha256":"$pin"},""" +
            """{"transport":"tailscale","host":"100.100.1.2","port":47820,"tls":false}]}"""
        val offer = PairingOffer.parse(text)!!
        assertEquals(listOf("100.100.1.2"), offer.endpoints.map { it.host })
        assertTrue(Endpoint.allowsCleartextHost("100.100.1.2"))
        assertTrue(Endpoint.allowsCleartextHost("pc.tail123.ts.net"))
        assertTrue(Endpoint.allowsCleartextHost("127.0.0.1"))
        assertFalse(Endpoint.allowsCleartextHost("192.168.1.1"))
        assertFalse(Endpoint.allowsCleartextHost("example.com"))
    }

    @Test
    fun endpoints_fixture_matches_the_offer_paths() {
        val list = parseEndpointsResponse(fixture("endpoints.v1.json"))
        assertEquals(4, list.size)
        assertEquals(PairingOffer.parse(fixture("pairing-offer.v1.json"))!!.endpoints, list)
    }

    @Test
    fun non_homebase_qr_text_is_not_an_offer() {
        assertNull(PairingOffer.parse("https://example.com"))
        assertNull(PairingOffer.parse("{\"hb\":2,\"pcId\":\"x\"}"))
        assertNull(PairingOffer.parse("{\"hb\":1,\"pcId\":\"x\",\"pcName\":\"y\",\"hosts\":[],\"port\":47820,\"code\":\"c\"}"))
        assertFalse(PairingOffer.parse(fixture("pairing-offer.v1.json")) == null)
    }

    @Test
    fun error_body_message_is_extracted() {
        assertEquals("코드 만료", errorMessage("{\"schema\":1,\"error\":\"code_expired\",\"message\":\"코드 만료\"}"))
        assertNull(errorMessage("not json"))
    }

    @Test
    fun advice_fixture_parses_as_plain_text_with_its_basis() {
        val a = AdviceReply.parse(fixture("advice.v1.json"))

        assertTrue(a.ok)
        assertTrue(a.text!!.startsWith("- CPU가 92%로"))
        assertTrue(a.text!!.contains("\n- C: 드라이브"))
        assertTrue(a.summary!!.contains("CPU load: 92%"))
        assertEquals("llama3.2:1b", a.model)
        assertEquals("2026-11-19T01:30:00+00:00", a.generatedAt)
        assertFalse(a.cached)
        assertNull(a.error)
    }

    @Test
    fun advice_failure_carries_its_code_and_no_text() {
        val a = AdviceReply.parse("{\"schema\":1,\"ok\":false,\"model\":\"llama3.2:1b\",\"generatedAt\":\"2026-11-19T01:30:00+00:00\",\"cached\":false,\"error\":\"model_missing\",\"message\":\"not pulled\"}")

        assertFalse(a.ok)
        assertNull(a.text)
        assertEquals(AdviceReply.MODEL_MISSING, a.error)
        assertEquals("not pulled", a.message)
    }
}
