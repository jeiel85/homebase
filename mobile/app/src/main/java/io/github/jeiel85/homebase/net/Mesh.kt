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

/** What this phone reports about itself (`POST /api/v1/presence`). */
data class PresenceReport(
    val appVersion: String,
    val battery: Int?,
    val charging: Boolean?,
    val network: String?,
) {
    fun toJson(): JSONObject = JSONObject()
        .put("schema", SCHEMA)
        .put("kind", DeviceKind.PHONE)
        .put("appVersion", appVersion)
        .apply {
            battery?.let { put("battery", it) }
            charging?.let { put("charging", it) }
            network?.let { put("network", it) }
        }
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

/** One entry of the work tab (DECISIONS M-26). */
data class WorkItem(
    val kind: String,
    val app: String,
    val title: String,
    val detail: String?,
    val lastActivity: String?,
    val foreground: Boolean,
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
            )
        }
    }
}

internal fun JSONObject.optStringOrNull(key: String): String? =
    if (has(key) && !isNull(key)) optString(key).ifBlank { null } else null

internal fun JSONObject.optIntOrNull(key: String): Int? = if (has(key) && !isNull(key)) optInt(key) else null

internal fun JSONObject.optLongOrNull(key: String): Long? = if (has(key) && !isNull(key)) optLong(key) else null
