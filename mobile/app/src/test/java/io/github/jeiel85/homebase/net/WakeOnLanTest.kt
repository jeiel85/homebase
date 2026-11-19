package io.github.jeiel85.homebase.net

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/** docs/REMOTE_COMMANDS.md §4.6: the magic packet a sleeping PC's adapter wakes for, and the addresses it goes to. */
class WakeOnLanTest {
    private val mac = byteArrayOf(0xAA.toByte(), 0xBB.toByte(), 0xCC.toByte(), 0x01, 0x23, 0x45)

    @Test
    fun mac_addresses_are_read_in_the_usual_spellings() {
        assertArrayEquals(mac, WakeOnLan.parseMac("aa:bb:cc:01:23:45"))
        assertArrayEquals(mac, WakeOnLan.parseMac("AA-BB-CC-01-23-45"))
        assertArrayEquals(mac, WakeOnLan.parseMac("aabbcc012345"))
        assertArrayEquals(mac, WakeOnLan.parseMac("  Aa:bB:cc:01:23:45 "))
        assertEquals("aa:bb:cc:01:23:45", WakeOnLan.normalize("AA-BB-CC-01-23-45"))
    }

    @Test
    fun what_is_not_a_mac_address_is_refused() {
        assertNull(WakeOnLan.parseMac(null))
        assertNull(WakeOnLan.parseMac(""))
        assertNull(WakeOnLan.parseMac("aa:bb:cc:01:23"))
        assertNull(WakeOnLan.parseMac("aa:bb:cc:01:23:45:67"))
        assertNull(WakeOnLan.parseMac("aa:bb-cc:01:23:45"))
        assertNull(WakeOnLan.parseMac("gg:bb:cc:01:23:45"))
        assertNull(WakeOnLan.parseMac("aabbcc01234"))
        // No adapter has these.
        assertNull(WakeOnLan.parseMac("00:00:00:00:00:00"))
        assertNull(WakeOnLan.parseMac("ff:ff:ff:ff:ff:ff"))
    }

    @Test
    fun the_magic_packet_is_six_ff_bytes_then_the_mac_sixteen_times() {
        val packet = WakeOnLan.magicPacket(mac)
        assertEquals(102, packet.size)
        assertEquals(WakeOnLan.PACKET_BYTES, packet.size)
        for (i in 0 until 6) assertEquals(0xFF.toByte(), packet[i])
        for (r in 0 until 16) assertArrayEquals(mac, packet.copyOfRange(6 + r * 6, 12 + r * 6))
    }

    @Test(expected = IllegalArgumentException::class)
    fun a_packet_needs_a_whole_mac() {
        WakeOnLan.magicPacket(byteArrayOf(1, 2, 3))
    }

    @Test
    fun the_directed_broadcast_sets_the_host_bits() {
        fun ip(a: Int, b: Int, c: Int, d: Int) = WakeOnLan.intOf(byteArrayOf(a.toByte(), b.toByte(), c.toByte(), d.toByte()))
        assertEquals(ip(192, 168, 0, 255), WakeOnLan.directedBroadcast(ip(192, 168, 0, 23), 24))
        assertEquals(ip(10, 255, 255, 255), WakeOnLan.directedBroadcast(ip(10, 1, 2, 3), 8))
        assertEquals(ip(172, 16, 7, 255), WakeOnLan.directedBroadcast(ip(172, 16, 4, 9), 22))
        assertArrayEquals(byteArrayOf(192.toByte(), 168.toByte(), 0, 255.toByte()), WakeOnLan.bytesOf(ip(192, 168, 0, 255)))
        // Point-to-point and host routes have no broadcast worth sending to.
        assertNull(WakeOnLan.directedBroadcast(ip(192, 168, 0, 23), 31))
        assertNull(WakeOnLan.directedBroadcast(ip(192, 168, 0, 23), 32))
        assertNull(WakeOnLan.directedBroadcast(ip(192, 168, 0, 23), 0))
    }
}
