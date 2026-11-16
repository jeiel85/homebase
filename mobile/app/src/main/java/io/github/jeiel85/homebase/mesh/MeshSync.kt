package io.github.jeiel85.homebase.mesh

import android.app.ActivityManager
import android.app.NotificationManager
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.net.wifi.WifiInfo
import android.os.BatteryManager
import android.os.Build
import android.os.Environment
import android.os.PowerManager
import android.os.StatFs
import android.os.SystemClock
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
import java.util.concurrent.ConcurrentHashMap

/**
 * Tells every paired PC this phone's state and keeps what they answer: the devices paired with
 * each PC (DECISIONS M-27). The PC list screen shows the union as "다른 기기".
 */
object MeshSync {
    private const val TAG = "HomebaseMesh"

    /** How long a PC's answer is trusted for "online": the PC's own window (DeviceStatusBoard.OnlineWindow). */
    const val FRESH_MS = 2 * 60_000L

    /** A PC's device list and when this phone received it (System.currentTimeMillis). */
    data class HubView(val response: DevicesResponse, val receivedAt: Long)

    private val _hubs = MutableStateFlow<Map<String, HubView>>(emptyMap())

    /** pcId → that PC's last device list. */
    val hubs: StateFlow<Map<String, HubView>> = _hubs

    /**
     * This phone's own state, as far as Android tells it without extra permissions (DECISIONS M-27, M-46).
     * Each extra reading is best effort: one that fails is left out, never the whole report.
     */
    fun report(context: Context): PresenceReport {
        val battery = context.registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
        val level = battery?.getIntExtra(BatteryManager.EXTRA_LEVEL, -1) ?: -1
        val scale = battery?.getIntExtra(BatteryManager.EXTRA_SCALE, -1) ?: -1
        val plugged = battery?.getIntExtra(BatteryManager.EXTRA_PLUGGED, 0) ?: 0
        val temp = battery?.getIntExtra(BatteryManager.EXTRA_TEMPERATURE, Int.MIN_VALUE) ?: Int.MIN_VALUE
        val caps = activeCaps(context)
        val mem = safe { memory(context) }
        val storage = safe { storage() }
        val power = context.getSystemService(PowerManager::class.java)
        return PresenceReport(
            appVersion = BuildConfig.VERSION_NAME,
            battery = if (level >= 0 && scale > 0) (level * 100 / scale).coerceIn(0, 100) else null,
            charging = if (battery != null) plugged != 0 else null,
            network = caps?.let { network(it) },
            memUsedBytes = mem?.first,
            memTotalBytes = mem?.second,
            uptimeSec = SystemClock.elapsedRealtime() / 1000,
            os = "Android ${Build.VERSION.RELEASE}",
            storageUsedBytes = storage?.first,
            storageTotalBytes = storage?.second,
            // Tenths of a degree; some phones report 0 when they have no sensor.
            batteryTempC = if (temp != Int.MIN_VALUE && temp != 0) temp / 10.0 else null,
            batteryHealth = battery?.getIntExtra(BatteryManager.EXTRA_HEALTH, 0)?.let { health(it) },
            plug = plug(plugged),
            chargeFullSec = if (plugged != 0) safe { chargeFullSec(context) } else null,
            powerSave = power?.let { safe { it.isPowerSaveMode } },
            thermal = power?.let { safe { thermal(it) } },
            signal = caps?.let { safe { signal(it) } },
            linkMbps = caps?.let { safe { linkMbps(it) } },
            forwarding = MeshPrefs(context).forwardEnabled() && NotificationForwarder.hasAccess(context),
            dnd = safe { dnd(context) },
        )
    }

    private inline fun <T> safe(block: () -> T?): T? = try {
        block()
    } catch (e: Exception) {
        null
    }

    private fun activeCaps(context: Context): NetworkCapabilities? {
        val cm = context.getSystemService(ConnectivityManager::class.java) ?: return null
        return cm.getNetworkCapabilities(cm.activeNetwork)
    }

    private fun network(caps: NetworkCapabilities): String? = when {
        caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) -> "wifi"
        caps.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) -> "cellular"
        caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET) -> "ethernet"
        else -> null
    }

    /** Used and total RAM. */
    private fun memory(context: Context): Pair<Long, Long>? {
        val am = context.getSystemService(ActivityManager::class.java) ?: return null
        val info = ActivityManager.MemoryInfo().also { am.getMemoryInfo(it) }
        return if (info.totalMem > 0) (info.totalMem - info.availMem).coerceAtLeast(0) to info.totalMem else null
    }

    /** Used and total space of the data partition (where apps and photos live). */
    private fun storage(): Pair<Long, Long>? {
        val fs = StatFs(Environment.getDataDirectory().path)
        val total = fs.totalBytes
        return if (total > 0) (total - fs.availableBytes).coerceAtLeast(0) to total else null
    }

    internal fun health(code: Int): String? = when (code) {
        BatteryManager.BATTERY_HEALTH_GOOD -> "good"
        BatteryManager.BATTERY_HEALTH_OVERHEAT -> "overheat"
        BatteryManager.BATTERY_HEALTH_DEAD -> "dead"
        BatteryManager.BATTERY_HEALTH_OVER_VOLTAGE -> "over_voltage"
        BatteryManager.BATTERY_HEALTH_COLD -> "cold"
        BatteryManager.BATTERY_HEALTH_UNSPECIFIED_FAILURE -> "failure"
        else -> null
    }

    internal fun plug(plugged: Int): String? = when (plugged) {
        BatteryManager.BATTERY_PLUGGED_AC -> "ac"
        BatteryManager.BATTERY_PLUGGED_USB -> "usb"
        BatteryManager.BATTERY_PLUGGED_WIRELESS -> "wireless"
        8 -> "dock" // BATTERY_PLUGGED_DOCK, API 33
        else -> null
    }

    private fun chargeFullSec(context: Context): Long? {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.P) return null
        val bm = context.getSystemService(BatteryManager::class.java) ?: return null
        val ms = bm.computeChargeTimeRemaining()
        return if (ms > 0) ms / 1000 else null
    }

    private fun thermal(power: PowerManager): String? {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) return null
        return when (power.currentThermalStatus) {
            PowerManager.THERMAL_STATUS_NONE -> "none"
            PowerManager.THERMAL_STATUS_LIGHT -> "light"
            PowerManager.THERMAL_STATUS_MODERATE -> "moderate"
            PowerManager.THERMAL_STATUS_SEVERE -> "severe"
            PowerManager.THERMAL_STATUS_CRITICAL -> "critical"
            PowerManager.THERMAL_STATUS_EMERGENCY -> "emergency"
            PowerManager.THERMAL_STATUS_SHUTDOWN -> "shutdown"
            else -> null
        }
    }

    /** 0–4 bars from the signal strength Android attaches to the active network (no location permission). */
    private fun signal(caps: NetworkCapabilities): Int? {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) return null
        return signalBars(caps.signalStrength, caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI))
    }

    /** Bars for an RSSI-like dBm value; Wi-Fi and mobile networks use different scales. */
    internal fun signalBars(dbm: Int, wifi: Boolean): Int? {
        if (dbm == Int.MIN_VALUE || dbm >= 0) return null // NetworkCapabilities.SIGNAL_STRENGTH_UNSPECIFIED
        val steps = if (wifi) intArrayOf(-88, -77, -66, -55) else intArrayOf(-115, -105, -95, -85)
        return steps.count { dbm >= it }
    }

    private fun linkMbps(caps: NetworkCapabilities): Int? {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) return null
        val wifi = caps.transportInfo as? WifiInfo ?: return null
        return wifi.linkSpeed.takeIf { it > 0 }
    }

    private fun dnd(context: Context): Boolean? {
        val nm = context.getSystemService(NotificationManager::class.java) ?: return null
        return nm.currentInterruptionFilter.let { it != NotificationManager.INTERRUPTION_FILTER_ALL && it != NotificationManager.INTERRUPTION_FILTER_UNKNOWN }
    }

    /**
     * How often the PC list's poll (every 15 s) re-sends an unchanged state to a PC. Inside the PC's
     * online window (2 min) and this phone's [FRESH_MS], so neither side sees the other go stale.
     */
    const val RESEND_MS = 60_000L

    /** What was last reported to a PC, and when (elapsedRealtime). */
    private class Sent(val report: PresenceReport, val atElapsed: Long)

    private val lastSent = ConcurrentHashMap<String, Sent>()

    /** Reports to one PC; failures are quiet (the PC list already shows reachability). */
    suspend fun reportTo(context: Context, api: HomebaseApi, pc: PcEntry) = send(api, pc, report(context))

    /**
     * For the PC list's poll: reports to [pc] only when this phone's state changed since the last report
     * to it, [RESEND_MS] has passed, or [force] (pull to refresh) — the same battery and network every
     * 15 s tells the PC nothing new.
     */
    suspend fun reportIfDue(context: Context, api: HomebaseApi, pc: PcEntry, force: Boolean = false) {
        val report = report(context)
        val last = lastSent[pc.pcId]
        if (force || presenceDue(last?.report, last?.atElapsed, report, SystemClock.elapsedRealtime())) send(api, pc, report)
    }

    /**
     * Whether [report] should go to a PC that last got [last] at [lastAt]: new, noticeably changed
     * ([PresenceReport.noticeable]), or [RESEND_MS] old. Memory, uptime and the like change on every
     * poll and would otherwise send a report every 15 s.
     */
    fun presenceDue(last: PresenceReport?, lastAt: Long?, report: PresenceReport, now: Long): Boolean =
        last == null || lastAt == null || report.noticeable() != last.noticeable() || now - lastAt >= RESEND_MS || now < lastAt

    private suspend fun send(api: HomebaseApi, pc: PcEntry, report: PresenceReport) {
        try {
            val devices = api.presence(pc, report)
            // Also a PC too old for presence (null): asking it again every 15 s would not change that.
            lastSent[pc.pcId] = Sent(report, SystemClock.elapsedRealtime())
            if (devices != null) _hubs.update { it + (pc.pcId to HubView(devices, System.currentTimeMillis())) }
        } catch (e: Exception) {
            Log.i(TAG, "presence to ${pc.name} skipped: ${e.message}")
        }
    }

    suspend fun reportAll(context: Context, api: HomebaseApi, store: PcStore) = coroutineScope {
        store.all().map { async { reportTo(context, api, it) } }.awaitAll()
    }

    fun forget(pcId: String) {
        _hubs.update { it - pcId }
        lastSent.remove(pcId)
    }

    /** A device seen through one or more PCs. [via] names the PC(s) that listed it. */
    data class OtherDevice(val entry: DeviceEntry, val via: List<String>)

    /**
     * Every device the PCs know, without this phone itself and without the PCs already in the list
     * (they have their own cards). A device listed by two PCs (a phone paired with both) shows once,
     * with its freshest state. "Online" from a PC answer older than [FRESH_MS] (that PC is out of
     * reach now) is not trusted: nobody can vouch for the device any more.
     */
    fun otherDevices(hubs: Map<String, HubView>, myPcs: List<PcEntry>, now: Long = System.currentTimeMillis()): List<OtherDevice> {
        val myDeviceIds = myPcs.map { it.deviceId }.toSet()
        val myPcNames = myPcs.map { it.name.lowercase() }.toSet() + hubs.values.map { it.response.hub.pcName.lowercase() }
        val merged = LinkedHashMap<String, OtherDevice>()
        for ((pcId, view) in hubs) {
            val response = view.response
            val stale = now - view.receivedAt > FRESH_MS
            val mine = myPcs.firstOrNull { it.pcId == pcId }
            // The user's alias first; else the PC's current name (it may have been renamed since pairing).
            val hubName = mine?.shownName(response.hub.pcName) ?: response.hub.pcName.ifBlank { "PC" }
            for (listed in response.devices) {
                val d = if (stale && listed.online) listed.copy(online = false) else listed
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
