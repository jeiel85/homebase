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
}
