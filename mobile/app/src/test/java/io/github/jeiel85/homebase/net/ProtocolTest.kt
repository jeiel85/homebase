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
    fun pairing_offer_fixture_parses_and_hosts_keep_their_order() {
        val offer = PairingOffer.parse(fixture("pairing-offer.v1.json"))!!
        assertEquals(listOf("home-pc.tail1234.ts.net", "100.101.102.103"), offer.hosts)
        assertEquals(47820, offer.port)
        assertEquals("q8V3k1yZp0Wm2LrT5nXa9Q", offer.code)
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
