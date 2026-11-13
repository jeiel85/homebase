package io.github.jeiel85.homebase.net

import org.json.JSONArray
import org.json.JSONObject

/**
 * One way to reach a PC (mirrors `MobileEndpoint`, docs/MOBILE_PROTOCOL.md). LAN endpoints carry
 * the SHA-256 of the PC's self-signed certificate; the app accepts exactly that certificate.
 */
data class Endpoint(
    val transport: String,
    val host: String,
    val port: Int,
    val tls: Boolean,
    val certSha256: String? = null,
) {
    /** Stable identity for "last path that worked". */
    val key: String get() = "$transport|$host|$port"

    /** Short name shown as "connected via …". */
    val label: String
        get() = when (transport) {
            LAN -> "LAN"
            TAILSCALE -> "Tailscale"
            CLOUDFLARE -> if (isQuickTunnel) "Cloudflare (임시 주소)" else "Cloudflare"
            LOOPBACK -> "개발(loopback)"
            else -> transport
        }

    val isQuickTunnel: Boolean get() = transport == CLOUDFLARE && host.endsWith(".trycloudflare.com", ignoreCase = true)

    fun url(path: String): String {
        val scheme = if (tls) "https" else "http"
        val h = if (host.contains(':') && !host.startsWith("[")) "[$host]" else host
        val defaultPort = (tls && port == 443) || (!tls && port == 80)
        return if (defaultPort) "$scheme://$h$path" else "$scheme://$h:$port$path"
    }

    fun toJson(): JSONObject = JSONObject()
        .put("transport", transport)
        .put("host", host)
        .put("port", port)
        .put("tls", tls)
        .apply { certSha256?.let { put("certSha256", it) } }

    companion object {
        const val TAILSCALE = "tailscale"
        const val LAN = "lan"
        const val CLOUDFLARE = "cloudflare"
        const val LOOPBACK = "loopback"

        /** null for malformed entries, and for a LAN endpoint without a pin (never trust it blindly). */
        fun parse(o: JSONObject): Endpoint? {
            val transport = o.optString("transport")
            val host = o.optString("host")
            val port = o.optInt("port", -1)
            val tls = o.optBoolean("tls")
            val pin = o.optString("certSha256").lowercase().ifBlank { null }
            if (transport.isBlank() || host.isBlank() || port !in 1..65535) return null
            if (transport == LAN && (!tls || pin == null || !pin.matches(PIN))) return null
            return Endpoint(transport, host, port, tls, pin)
        }

        fun parseList(arr: JSONArray?): List<Endpoint> = arr.objects().mapNotNull(::parse)

        /** v1 QR / stored entries: Tailscale hosts over plain HTTP on one port. */
        fun fromLegacyHosts(hosts: List<String>, port: Int): List<Endpoint> =
            hosts.filter { it.isNotBlank() }.map { h ->
                Endpoint(if (h == "127.0.0.1") LOOPBACK else TAILSCALE, h, port, tls = false)
            }

        private val PIN = Regex("^[0-9a-f]{64}$")
    }
}

/** `GET /api/v1/endpoints` → the PC's current paths. */
fun parseEndpointsResponse(json: String): List<Endpoint> {
    val o = JSONObject(json)
    val schema = o.optInt("schema")
    if (schema != SCHEMA) throw ProtocolException("Unsupported schema $schema")
    return Endpoint.parseList(o.optJSONArray("endpoints"))
}
