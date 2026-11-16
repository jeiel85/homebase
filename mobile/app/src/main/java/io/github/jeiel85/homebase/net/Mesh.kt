package io.github.jeiel85.homebase.net

import org.json.JSONArray
import org.json.JSONObject

/*
 * Devices sharing state and notifications through a hub PC (DECISIONS M-27, M-28).
 * Wire shapes: docs/mobile-protocol/presence.v1.json, devices.v1.json, device-notification.v1.json.
 */

object DeviceKind {
    const val PHONE = "phone"
    const val PC = "pc"
}

/** One device's last report; every field may be missing. */
data class DeviceStatus(
    val appVersion: String? = null,
    val battery: Int? = null,
    val charging: Boolean? = null,
    val network: String? = null,
    val cpuPct: Double? = null,
    val memUsedBytes: Long? = null,
    val memTotalBytes: Long? = null,
    val uptimeSec: Long? = null,
) {
    companion object {
        fun parse(o: JSONObject?): DeviceStatus? = o?.let {
            DeviceStatus(
                appVersion = it.optStringOrNull("appVersion"),
                battery = it.optIntOrNull("battery"),
                charging = if (it.has("charging") && !it.isNull("charging")) it.optBoolean("charging") else null,
                network = it.optStringOrNull("network"),
                cpuPct = if (it.has("cpuPct") && !it.isNull("cpuPct")) it.optDouble("cpuPct") else null,
                memUsedBytes = it.optLongOrNull("memUsedBytes"),
                memTotalBytes = it.optLongOrNull("memTotalBytes"),
                uptimeSec = it.optLongOrNull("uptimeSec"),
            )
        }
    }
}

data class DeviceEntry(
    val deviceId: String,
    val name: String,
    val kind: String,
    val model: String?,
    val lastSeenAt: String?,
    val online: Boolean,
    val status: DeviceStatus?,
)

data class HubEntry(val pcId: String, val pcName: String, val agentVersion: String, val status: DeviceStatus?)

/** `POST /api/v1/presence` and `GET /api/v1/devices`. */
data class DevicesResponse(val hub: HubEntry, val devices: List<DeviceEntry>) {
    companion object {
        fun parse(json: String): DevicesResponse {
            val o = JSONObject(json)
            val schema = o.optInt("schema")
            if (schema != SCHEMA) throw ProtocolException("Unsupported devices schema $schema")
            val h = o.getJSONObject("hub")
            return DevicesResponse(
                HubEntry(h.optString("pcId"), h.optString("pcName"), h.optString("agentVersion"), DeviceStatus.parse(h.optJSONObject("status"))),
                o.optJSONArray("devices").objects().map {
                    DeviceEntry(
                        deviceId = it.optString("deviceId"),
                        name = it.optString("name"),
                        kind = if (it.optString("kind") == DeviceKind.PC) DeviceKind.PC else DeviceKind.PHONE,
                        model = it.optStringOrNull("model"),
                        lastSeenAt = it.optStringOrNull("lastSeenAt"),
                        online = it.optBoolean("online"),
                        status = DeviceStatus.parse(it.optJSONObject("status")),
                    )
                },
            )
        }
    }
}

/**
 * What this phone reports about itself (`POST /api/v1/presence`). The fields after [network] came with
 * v0.34.0 (DECISIONS M-46): the PC's device page shows them; the PC drops a value out of range.
 */
data class PresenceReport(
    val appVersion: String,
    val battery: Int?,
    val charging: Boolean?,
    val network: String?,
    val memUsedBytes: Long? = null,
    val memTotalBytes: Long? = null,
    val uptimeSec: Long? = null,
    val os: String? = null,
    val storageUsedBytes: Long? = null,
    val storageTotalBytes: Long? = null,
    val batteryTempC: Double? = null,
    val batteryHealth: String? = null,
    /** `ac`, `usb`, `wireless` or `dock` while plugged in. */
    val plug: String? = null,
    /** Seconds until full while charging, when Android estimates it. */
    val chargeFullSec: Long? = null,
    val powerSave: Boolean? = null,
    /** `none` … `shutdown` (PowerManager thermal status, Android 10+). */
    val thermal: String? = null,
    /** Wi-Fi or mobile signal, 0–4 bars. */
    val signal: Int? = null,
    /** Wi-Fi link speed. */
    val linkMbps: Int? = null,
    /** This phone forwards its notifications to the PCs (on and allowed). */
    val forwarding: Boolean? = null,
    /** Do Not Disturb is on. */
    val dnd: Boolean? = null,
) {
    fun toJson(): JSONObject = JSONObject()
        .put("schema", SCHEMA)
        .put("kind", DeviceKind.PHONE)
        .put("appVersion", appVersion)
        .apply {
            battery?.let { put("battery", it) }
            charging?.let { put("charging", it) }
            network?.let { put("network", it) }
            memUsedBytes?.let { put("memUsedBytes", it) }
            memTotalBytes?.let { put("memTotalBytes", it) }
            uptimeSec?.let { put("uptimeSec", it) }
            os?.let { put("os", it) }
            storageUsedBytes?.let { put("storageUsedBytes", it) }
            storageTotalBytes?.let { put("storageTotalBytes", it) }
            batteryTempC?.let { put("batteryTempC", it) }
            batteryHealth?.let { put("batteryHealth", it) }
            plug?.let { put("plug", it) }
            chargeFullSec?.let { put("chargeFullSec", it) }
            powerSave?.let { put("powerSave", it) }
            thermal?.let { put("thermal", it) }
            signal?.let { put("signal", it) }
            linkMbps?.let { put("linkMbps", it) }
            forwarding?.let { put("forwarding", it) }
            dnd?.let { put("dnd", it) }
        }

    /**
     * What makes a report worth sending before [MeshSync.RESEND_MS] has passed: the state a person
     * notices. Memory, storage, uptime and temperature move all the time and ride along once a minute.
     */
    fun noticeable(): List<Any?> = listOf(battery, charging, network, plug, powerSave, thermal, forwarding, dnd, signal)
}

/** A notification this phone forwards to a PC (`POST /api/v1/notifications`). */
data class DeviceNotification(
    val id: String,
    val app: String,
    val title: String,
    val body: String?,
    /** ISO-8601 instant. */
    val ts: String,
) {
    fun toJson(): JSONObject = JSONObject()
        .put("schema", SCHEMA)
        .put("id", id)
        .put("app", app.take(MAX_APP))
        .put("title", title.take(MAX_TITLE))
        .apply { body?.let { put("body", it.take(MAX_BODY)) } }
        .put("ts", ts)

    companion object {
        // Same limits as the PC (DeviceNotification.Validate).
        const val MAX_APP = 64
        const val MAX_TITLE = 200
        const val MAX_BODY = 1000
    }
}

/**
 * What the PC knows about an AI coding session beyond its title (DECISIONS M-46). Every field is
 * optional: each comes from a file Claude Code or Codex writes for itself, and a PC older than
 * v0.34.0 sends none of it.
 */
data class AgentInfo(
    /** [BUSY], [WAITING] or [IDLE]; null when the PC cannot tell. */
    val status: String? = null,
    val waitingFor: String? = null,
    val statusSince: String? = null,
    val model: String? = null,
    val effort: String? = null,
    val contextTokens: Long? = null,
    val contextWindow: Long? = null,
    val totalTokens: Long? = null,
    val lastTool: String? = null,
    val mode: String? = null,
    val prNumber: Int? = null,
    /** The session on claude.ai (Remote Control): open it to type to the session. */
    val remoteUrl: String? = null,
    val sessionId: String? = null,
) {
    /** Context in use as a share of the window, when both are known. */
    val contextShare: Float?
        get() = if (contextTokens != null && contextWindow != null && contextWindow > 0) {
            (contextTokens.toFloat() / contextWindow).coerceIn(0f, 1f)
        } else {
            null
        }

    companion object {
        const val BUSY = "busy"
        const val WAITING = "waiting"
        const val IDLE = "idle"

        fun parse(o: JSONObject?): AgentInfo? = o?.let {
            AgentInfo(
                status = it.optStringOrNull("status"),
                waitingFor = it.optStringOrNull("waitingFor"),
                statusSince = it.optStringOrNull("statusSince"),
                model = it.optStringOrNull("model"),
                effort = it.optStringOrNull("effort"),
                contextTokens = it.optLongOrNull("contextTokens"),
                contextWindow = it.optLongOrNull("contextWindow"),
                totalTokens = it.optLongOrNull("totalTokens"),
                lastTool = it.optStringOrNull("lastTool"),
                mode = it.optStringOrNull("mode"),
                prNumber = it.optIntOrNull("prNumber"),
                remoteUrl = it.optStringOrNull("remoteUrl")?.takeIf { url -> url.startsWith("https://claude.ai/") },
                sessionId = it.optStringOrNull("sessionId"),
            )
        }
    }
}

/** One entry of the work tab (DECISIONS M-26). [agent] is set for AI sessions by PCs from v0.34.0 (M-46). */
data class WorkItem(
    val kind: String,
    val app: String,
    val title: String,
    val detail: String?,
    val lastActivity: String?,
    val foreground: Boolean,
    val agent: AgentInfo? = null,
) {
    companion object {
        const val AGENT = "agent"
        const val BROWSER = "browser"
        const val DOCUMENT = "document"
        const val EDITOR = "editor"

        fun parseList(arr: JSONArray?): List<WorkItem>? = arr?.objects()?.map {
            WorkItem(
                kind = it.optString("kind"),
                app = it.optString("app"),
                title = it.optString("title"),
                detail = it.optStringOrNull("detail"),
                lastActivity = it.optStringOrNull("lastActivity"),
                foreground = it.optBoolean("foreground"),
                agent = AgentInfo.parse(it.optJSONObject("agent")),
            )
        }
    }
}

internal fun JSONObject.optStringOrNull(key: String): String? =
    if (has(key) && !isNull(key)) optString(key).ifBlank { null } else null

internal fun JSONObject.optIntOrNull(key: String): Int? = if (has(key) && !isNull(key)) optInt(key) else null

internal fun JSONObject.optLongOrNull(key: String): Long? = if (has(key) && !isNull(key)) optLong(key) else null
