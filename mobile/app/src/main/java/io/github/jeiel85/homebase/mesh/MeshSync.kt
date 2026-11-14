package io.github.jeiel85.homebase.mesh

import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.os.BatteryManager
import android.util.Log
import io.github.jeiel85.homebase.BuildConfig
import io.github.jeiel85.homebase.data.PcEntry
import io.github.jeiel85.homebase.data.PcStore
import io.github.jeiel85.homebase.net.DeviceEntry
import io.github.jeiel85.homebase.net.DeviceKind
import io.github.jeiel85.homebase.net.DevicesResponse
import io.github.jeiel85.homebase.net.HomebaseApi
import io.github.jeiel85.homebase.net.PresenceReport
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.update

/**
 * Tells every paired PC this phone's state and keeps what they answer: the devices paired with
 * each PC (DECISIONS M-27). The PC list screen shows the union as "다른 기기".
 */
object MeshSync {
    private const val TAG = "HomebaseMesh"

    private val _hubs = MutableStateFlow<Map<String, DevicesResponse>>(emptyMap())

    /** pcId → that PC's last device list. */
    val hubs: StateFlow<Map<String, DevicesResponse>> = _hubs

    /** This phone's own state, as far as Android tells it without extra permissions. */
    fun report(context: Context): PresenceReport {
        val battery = context.registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
        val level = battery?.getIntExtra(BatteryManager.EXTRA_LEVEL, -1) ?: -1
        val scale = battery?.getIntExtra(BatteryManager.EXTRA_SCALE, -1) ?: -1
        val plugged = battery?.getIntExtra(BatteryManager.EXTRA_PLUGGED, 0) ?: 0
        return PresenceReport(
            appVersion = BuildConfig.VERSION_NAME,
            battery = if (level >= 0 && scale > 0) (level * 100 / scale).coerceIn(0, 100) else null,
            charging = if (battery != null) plugged != 0 else null,
            network = network(context),
        )
    }

    private fun network(context: Context): String? {
        val cm = context.getSystemService(ConnectivityManager::class.java) ?: return null
        val caps = cm.getNetworkCapabilities(cm.activeNetwork) ?: return null
        return when {
            caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) -> "wifi"
            caps.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) -> "cellular"
            caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET) -> "ethernet"
            else -> null
        }
    }

    /** Reports to one PC; failures are quiet (the PC list already shows reachability). */
    suspend fun reportTo(context: Context, api: HomebaseApi, pc: PcEntry) {
        try {
            val devices = api.presence(pc, report(context)) ?: return
            _hubs.update { it + (pc.pcId to devices) }
        } catch (e: Exception) {
            Log.i(TAG, "presence to ${pc.name} skipped: ${e.message}")
        }
    }

    suspend fun reportAll(context: Context, api: HomebaseApi, store: PcStore) = coroutineScope {
        store.all().map { async { reportTo(context, api, it) } }.awaitAll()
    }

    fun forget(pcId: String) = _hubs.update { it - pcId }

    /** A device seen through one or more PCs. [via] names the PC(s) that listed it. */
    data class OtherDevice(val entry: DeviceEntry, val via: List<String>)

    /**
     * Every device the PCs know, without this phone itself and without the PCs already in the list
     * (they have their own cards). A device listed by two PCs (a phone paired with both) shows once,
     * with its freshest state.
     */
    fun otherDevices(hubs: Map<String, DevicesResponse>, myPcs: List<PcEntry>): List<OtherDevice> {
        val myDeviceIds = myPcs.map { it.deviceId }.toSet()
        val myPcNames = myPcs.map { it.name.lowercase() }.toSet() + hubs.values.map { it.hub.pcName.lowercase() }
        val merged = LinkedHashMap<String, OtherDevice>()
        for ((pcId, response) in hubs) {
            val hubName = response.hub.pcName.ifBlank { myPcs.firstOrNull { it.pcId == pcId }?.name ?: "PC" }
            for (d in response.devices) {
                if (d.deviceId in myDeviceIds) continue
                if (d.kind == DeviceKind.PC && d.name.lowercase() in myPcNames) continue
                val key = "${d.kind}|${d.name}|${d.model ?: ""}"
                val prev = merged[key]
                merged[key] = when {
                    prev == null -> OtherDevice(d, listOf(hubName))
                    // Prefer the online / most recently seen copy (ISO-8601 instants compare as strings
                    // only within one offset; the PC always writes +00:00).
                    d.online && !prev.entry.online || (d.online == prev.entry.online && (d.lastSeenAt ?: "") > (prev.entry.lastSeenAt ?: "")) ->
                        OtherDevice(d, prev.via + hubName)
                    else -> prev.copy(via = prev.via + hubName)
                }
            }
        }
        return merged.values.sortedWith(compareBy({ it.entry.kind != DeviceKind.PC }, { !it.entry.online }, { it.entry.name }))
    }
}
