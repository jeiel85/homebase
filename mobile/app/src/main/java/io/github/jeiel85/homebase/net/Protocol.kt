package io.github.jeiel85.homebase.net

import org.json.JSONArray
import org.json.JSONObject

/**
 * Wire models mirroring `LocalOpsBot.Core.Mobile.MobileContracts` (docs/MOBILE_PROTOCOL.md).
 * Parsing uses the platform's org.json — no serialization library in the APK. Every parser
 * returns `null` (or throws [ProtocolException]) instead of half-filled objects.
 */
const val SCHEMA = 1

class ProtocolException(message: String) : Exception(message)

/**
 * Pairing QR. [endpoints] lists every path (LAN with its certificate pin, Tailscale, Cloudflare);
 * a QR from an older PC has only `hosts` + `port`, which become Tailscale endpoints.
 */
data class PairingOffer(
    val pcId: String,
    val pcName: String,
    val endpoints: List<Endpoint>,
    val code: String,
) {
    companion object {
        /** QR text (pairing link, or the raw JSON of earlier PCs) → offer, or null when it isn't a Homebase code. */
        fun parse(text: String): PairingOffer? = try {
            val o = JSONObject(PairingLink.decode(text) ?: text.trim())
            if (o.optInt("hb") != 1) {
                null
            } else {
                val endpoints = if (o.has("endpoints")) {
                    Endpoint.parseList(o.optJSONArray("endpoints"))
                } else {
                    Endpoint.fromLegacyHosts(o.optJSONArray("hosts")?.strings() ?: emptyList(), o.optInt("port"))
                        .filter { it.port in 1..65535 }
                }
                val offer = PairingOffer(
                    pcId = o.getString("pcId"),
                    pcName = o.getString("pcName"),
                    endpoints = endpoints,
                    code = o.getString("code"),
                )
                offer.takeIf { it.endpoints.isNotEmpty() && it.code.isNotBlank() && it.pcId.isNotBlank() }
            }
        } catch (_: Exception) {
            null
        }
    }
}

data class PcInfo(val pcId: String, val pcName: String, val agentVersion: String, val protocol: Int) {
    companion object {
        fun parse(json: String): PcInfo {
            val o = JSONObject(json)
            return PcInfo(o.getString("pcId"), o.getString("pcName"), o.optString("agentVersion"), o.optInt("protocol"))
        }
    }
}

data class PairResponse(val deviceId: String, val token: String, val pcId: String, val pcName: String) {
    companion object {
        fun parse(json: String): PairResponse {
            val o = JSONObject(json)
            return PairResponse(o.getString("deviceId"), o.getString("token"), o.getString("pcId"), o.getString("pcName"))
        }
    }
}

data class Usage(val cpuPct: Double, val memBytes: Long, val ioBps: Long, val netRxBps: Long?, val netTxBps: Long?)

data class ProcessEntry(val pid: Int, val name: String, val usage: Usage)

data class WindowTitle(val title: String, val foreground: Boolean)

data class AppEntry(
    val pid: Int,
    val process: String,
    val foreground: Boolean,
    val windows: List<WindowTitle>,
    val usage: Usage,
    val children: List<ProcessEntry>,
)

data class ClaudeSession(val title: String, val project: String?, val lastActivity: String)

data class SystemTotals(
    val cpuPct: Double,
    val memUsedBytes: Long,
    val memTotalBytes: Long,
    val ioBps: Long,
    val netRxBps: Long,
    val netTxBps: Long,
    val uptimeSec: Long,
)

data class LiveSnapshot(
    val ts: String,
    val pcName: String,
    val system: SystemTotals,
    val sessionAvailable: Boolean,
    val netPerProcess: Boolean,
    val apps: List<AppEntry>,
    val processes: List<ProcessEntry>,
    val claudeSessions: List<ClaudeSession>,
    /** The work tab (DECISIONS M-26); null from a PC older than v0.23.0, which only sends [claudeSessions]. */
    val work: List<WorkItem>? = null,
) {
    /** What the work tab lists: [work] when the PC sends it, else the Claude sessions as work items. */
    val workItems: List<WorkItem>
        get() = work ?: claudeSessions.map { WorkItem(WorkItem.AGENT, "Claude Code", it.title, it.project, it.lastActivity, false) }

    companion object {
        fun parse(json: String): LiveSnapshot {
            val o = JSONObject(json)
            val schema = o.optInt("schema")
            if (schema != SCHEMA) throw ProtocolException("Unsupported snapshot schema $schema (app expects $SCHEMA). Update the app or the PC.")
            val s = o.getJSONObject("system")
            return LiveSnapshot(
                ts = o.optString("ts"),
                pcName = o.optString("pcName"),
                system = SystemTotals(
                    s.optDouble("cpuPct", 0.0), s.optLong("memUsedBytes"), s.optLong("memTotalBytes"),
                    s.optLong("ioBps"), s.optLong("netRxBps"), s.optLong("netTxBps"), s.optLong("uptimeSec"),
                ),
                sessionAvailable = o.optBoolean("sessionAvailable"),
                netPerProcess = o.optBoolean("netPerProcess"),
                apps = o.optJSONArray("apps").objects().map { a ->
                    AppEntry(
                        pid = a.optInt("pid"),
                        process = a.optString("process"),
                        foreground = a.optBoolean("foreground"),
                        windows = a.optJSONArray("windows").objects().map { WindowTitle(it.optString("title"), it.optBoolean("foreground")) },
                        usage = a.getJSONObject("usage").toUsage(),
                        children = a.optJSONArray("children").objects().map { it.toProcess() },
                    )
                },
                processes = o.optJSONArray("processes").objects().map { it.toProcess() },
                claudeSessions = o.optJSONArray("claudeSessions").objects().map {
                    ClaudeSession(it.optString("title"), it.optString("project").ifBlank { null }, it.optString("lastActivity"))
                },
                work = WorkItem.parseList(o.optJSONArray("work")),
            )
        }

        private fun JSONObject.toUsage() = Usage(
            cpuPct = optDouble("cpuPct", 0.0),
            memBytes = optLong("memBytes"),
            ioBps = optLong("ioBps"),
            netRxBps = if (has("netRxBps") && !isNull("netRxBps")) optLong("netRxBps") else null,
            netTxBps = if (has("netTxBps") && !isNull("netTxBps")) optLong("netTxBps") else null,
        )

        private fun JSONObject.toProcess() = ProcessEntry(optInt("pid"), optString("name"), getJSONObject("usage").toUsage())
    }
}

/** Server error body `{ schema, error, message }`. */
fun errorMessage(body: String?): String? = try {
    body?.let { JSONObject(it).optString("message").ifBlank { null } }
} catch (_: Exception) {
    null
}

internal fun JSONArray?.objects(): List<JSONObject> =
    if (this == null) emptyList() else (0 until length()).mapNotNull { optJSONObject(it) }

internal fun JSONArray.strings(): List<String> = (0 until length()).map { optString(it) }
