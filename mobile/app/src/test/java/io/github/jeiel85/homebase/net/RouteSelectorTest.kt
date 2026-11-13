package io.github.jeiel85.homebase.net

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** One QR, the right path automatically: same Wi-Fi → LAN, Tailscale VPN up → Tailscale, else Cloudflare. */
class RouteSelectorTest {

    private val lan = Endpoint(Endpoint.LAN, "192.168.45.101", 47820, true, "a".repeat(64))
    private val tsName = Endpoint(Endpoint.TAILSCALE, "home-pc.tail1234.ts.net", 47820, false)
    private val tsIp = Endpoint(Endpoint.TAILSCALE, "100.101.102.103", 47820, false)
    private val cf = Endpoint(Endpoint.CLOUDFLARE, "home-pc.example.com", 443, true)
    private val all = listOf(cf, tsName, tsIp, lan) // deliberately not in preference order

    private fun subnet(ip: String, prefix: Int) = Subnet(RouteSelector.ipv4(ip)!!, prefix)

    @Test
    fun same_wifi_puts_lan_first() {
        val net = LocalNetworks(listOf(subnet("192.168.45.20", 24)), tailnetUp = true)
        assertEquals(listOf(lan, tsName, tsIp, cf), RouteSelector.order(all, net, null))
    }

    @Test
    fun tailscale_vpn_up_away_from_home_prefers_tailscale_then_cloudflare() {
        val net = LocalNetworks(listOf(subnet("10.20.0.8", 16)), tailnetUp = true)
        assertEquals(listOf(tsName, tsIp, cf, lan), RouteSelector.order(all, net, null))
    }

    @Test
    fun no_tailscale_on_another_network_goes_straight_to_cloudflare() {
        val net = LocalNetworks(listOf(subnet("10.20.0.8", 16)), tailnetUp = false)
        assertEquals(listOf(cf, tsName, tsIp, lan), RouteSelector.order(all, net, null))
    }

    @Test
    fun unknown_network_falls_back_to_lan_tailscale_cloudflare() {
        assertEquals(listOf(lan, tsName, tsIp, cf), RouteSelector.order(all, null, null))
    }

    @Test
    fun last_good_path_wins_only_within_its_rank() {
        val net = LocalNetworks(emptyList(), tailnetUp = true)
        assertEquals(listOf(tsIp, tsName, cf, lan), RouteSelector.order(all, net, tsIp.key))
        // A last-good LAN path from another network does not jump ahead of reachable ones.
        assertEquals(listOf(tsName, tsIp, cf, lan), RouteSelector.order(all, net, lan.key))
    }

    @Test
    fun subnet_and_tailnet_math() {
        assertTrue(subnet("192.168.45.0", 24).contains(RouteSelector.ipv4("192.168.45.255")!!))
        assertFalse(subnet("192.168.45.0", 24).contains(RouteSelector.ipv4("192.168.46.1")!!))
        assertTrue(subnet("10.0.0.0", 8).contains(RouteSelector.ipv4("10.255.1.1")!!))
        assertTrue(RouteSelector.isTailnet(RouteSelector.ipv4("100.101.102.103")!!))
        assertFalse(RouteSelector.isTailnet(RouteSelector.ipv4("100.128.0.1")!!))
        assertNull(RouteSelector.ipv4("home-pc.example.com"))
        assertNull(RouteSelector.ipv4("192.168.045.1"))
    }
}
