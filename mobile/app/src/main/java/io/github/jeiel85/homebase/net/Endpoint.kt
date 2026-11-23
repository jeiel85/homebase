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

        /** null for malformed entries, disallowed hosts, or transport/TLS mismatches. */
        fun parse(o: JSONObject): Endpoint? {
            val transport = o.optString("transport")
            val host = o.optString("host").trim()
            val port = o.optInt("port", -1)
            val tls = o.optBoolean("tls")
            val pin = o.optString("certSha256").lowercase().ifBlank { null }
            if (transport.isBlank() || !isSafeHost(host) || port !in 1..65535) return null
            val ipv4 = RouteSelector.ipv4(host)
            val ok = when (transport) {
                LAN -> tls && ipv4 != null && isPrivateIpv4(ipv4) && pin != null && pin.matches(PIN)
                TAILSCALE -> !tls && (if (ipv4 != null) RouteSelector.isTailnet(ipv4) else isMagicDns(host))
                CLOUDFLARE -> tls && ipv4 == null && isDnsName(host)
                LOOPBACK -> !tls && (host == "127.0.0.1" || host == "::1")
                else -> false
            }
            return if (ok) Endpoint(transport, host, port, tls, pin) else null
        }

        fun parseList(arr: JSONArray?): List<Endpoint> = arr.objects().mapNotNull(::parse)

        /** v1 QR / stored entries: Tailscale hosts over plain HTTP on one port. */
        fun fromLegacyHosts(hosts: List<String>, port: Int): List<Endpoint> =
            hosts.map { it.trim() }.filter(::isSafeHost).map { h ->
                Endpoint(if (h == "127.0.0.1") LOOPBACK else TAILSCALE, h, port, tls = false)
            }

        internal fun isSafeHost(host: String): Boolean =
            host.isNotEmpty() && host.length <= 253 && host.none { c ->
                c.isWhitespace() || c.code < 0x20 || c in "@/?#\\"
            }

        internal fun isPrivateIpv4(ip: Int): Boolean {
            val a = (ip ushr 24) and 0xFF
            val b = (ip ushr 16) and 0xFF
            return a == 10 || (a == 172 && b in 16..31) || (a == 192 && b == 168)
        }

        internal fun isDnsName(host: String): Boolean =
            host.contains('.') && !host.endsWith('.') && DNS_NAME.matches(host)

        internal fun isMagicDns(host: String): Boolean =
            isDnsName(host) && host.endsWith(".ts.net", ignoreCase = true)

        /** True when cleartext HTTP is allowed to [host] (Tailscale WireGuard hop or local emulator loopback). */
        internal fun allowsCleartextHost(host: String): Boolean {
            if (host.equals("localhost", ignoreCase = true) || host == "127.0.0.1" || host == "::1") return true
            val ip = RouteSelector.ipv4(host)
            return if (ip != null) RouteSelector.isTailnet(ip) else isMagicDns(host)
        }

        private val PIN = Regex("^[0-9a-f]{64}$")
        private val DNS_NAME = Regex("^[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)+$")
    }
}

/** `GET /api/v1/endpoints` → the PC's current paths. */
fun parseEndpointsResponse(json: String): List<Endpoint> {
    val o = JSONObject(json)
    val schema = o.optInt("schema")
    if (schema != SCHEMA) throw ProtocolException("Unsupported schema $schema")
    return Endpoint.parseList(o.optJSONArray("endpoints"))
}
