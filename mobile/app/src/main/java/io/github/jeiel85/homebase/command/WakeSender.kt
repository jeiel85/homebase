package io.github.jeiel85.homebase.command

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.util.Log
import android.widget.Toast
import io.github.jeiel85.homebase.net.WakeOnLan
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.IOException
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress

/**
 * Wakes a sleeping PC from this phone (docs/REMOTE_COMMANDS.md §4.6): the magic packet for each MAC address the PC
 * listed ([CommandPrefs.wakeMacs]), by UDP broadcast on the phone's Wi-Fi (or Ethernet) — to 255.255.255.255 and to
 * that network's own broadcast address, ports 9 and 7. Only works on the PC's LAN, with Wake-on-LAN on in the PC's
 * firmware and adapter; nothing says whether it woke (the PC screen reconnects by itself when it does).
 */
object WakeSender {
    private const val TAG = "HomebaseWake"

    enum class Result { SENT, NO_LAN, FAILED }

    private val LIMITED_BROADCAST: InetAddress = InetAddress.getByAddress(byteArrayOf(-1, -1, -1, -1))

    /** Sends [pcId]'s wake packets off the main thread and says how it went in a toast. */
    fun wake(context: Context, pcId: String) {
        val app = context.applicationContext
        RemoteCommands.scope.launch {
            val result = send(app, CommandPrefs(app).wakeMacs(pcId))
            withContext(Dispatchers.Main) {
                val line = when (result) {
                    Result.SENT -> CommandText.WAKE_SENT
                    Result.NO_LAN -> CommandText.WAKE_NO_LAN
                    Result.FAILED -> CommandText.WAKE_FAILED
                }
                Toast.makeText(app, line, Toast.LENGTH_LONG).show()
            }
        }
    }

    /** The packets for [macs] on every Wi-Fi or Ethernet network the phone is on. */
    suspend fun send(context: Context, macs: List<String>): Result = withContext(Dispatchers.IO) {
        val packets = macs.mapNotNull(WakeOnLan::parseMac).map(WakeOnLan::magicPacket)
        if (packets.isEmpty()) return@withContext Result.FAILED
        val lans = try {
            lans(context)
        } catch (e: SecurityException) {
            Log.w(TAG, "networks unreadable: ${e.javaClass.simpleName}")
            emptyList()
        }
        if (lans.isEmpty()) return@withContext Result.NO_LAN
        var sent = 0
        for ((network, broadcasts) in lans) {
            // Bound to that network, so a VPN (Tailscale) holding the default route doesn't swallow the broadcast.
            sent += sendAll(packets, listOf(LIMITED_BROADCAST) + broadcasts) { network.bindSocket(it) }
        }
        // Binding refused on every network: the default route, which on Wi-Fi without a VPN is that Wi-Fi.
        if (sent == 0) sent = sendAll(packets, listOf(LIMITED_BROADCAST) + lans.flatMap { it.second }) {}
        if (sent > 0) Result.SENT else Result.FAILED
    }

    /** Every packet to every target on both ports, from one socket; how many went out. */
    private fun sendAll(packets: List<ByteArray>, targets: List<InetAddress>, bind: (DatagramSocket) -> Unit): Int {
        var sent = 0
        try {
            DatagramSocket().use { socket ->
                socket.broadcast = true
                bind(socket)
                for (packet in packets) {
                    for (target in targets.distinct()) {
                        for (port in WakeOnLan.PORTS) {
                            try {
                                socket.send(DatagramPacket(packet, packet.size, target, port))
                                sent++
                            } catch (e: IOException) {
                                Log.i(TAG, "wake packet to ${target.hostAddress}:$port not sent: ${e.javaClass.simpleName}")
                            }
                        }
                    }
                }
            }
        } catch (e: IOException) {
            Log.i(TAG, "wake socket failed: ${e.javaClass.simpleName}")
        } catch (e: SecurityException) {
            Log.i(TAG, "wake socket refused: ${e.javaClass.simpleName}")
        }
        return sent
    }

    /** Wi-Fi and Ethernet networks (not VPN, not mobile data) with their IPv4 directed broadcast addresses. */
    private fun lans(context: Context): List<Pair<Network, List<InetAddress>>> {
        val cm = context.applicationContext.getSystemService(ConnectivityManager::class.java) ?: return emptyList()
        val found = mutableListOf<Pair<Network, List<InetAddress>>>()
        @Suppress("DEPRECATION") // allNetworks is the only synchronous snapshot of every network.
        for (network in cm.allNetworks) {
            val caps = cm.getNetworkCapabilities(network) ?: continue
            if (caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN)) continue
            if (!caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) && !caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET)) continue
            val links = cm.getLinkProperties(network)?.linkAddresses ?: continue
            val broadcasts = links.mapNotNull { la ->
                val a = la.address as? Inet4Address ?: return@mapNotNull null
                WakeOnLan.directedBroadcast(WakeOnLan.intOf(a.address), la.prefixLength)
                    ?.let { InetAddress.getByAddress(WakeOnLan.bytesOf(it)) }
            }
            found += network to broadcasts
        }
        return found
    }
}
