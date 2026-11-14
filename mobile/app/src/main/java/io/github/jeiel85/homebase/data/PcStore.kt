package io.github.jeiel85.homebase.data

import android.content.Context
import io.github.jeiel85.homebase.net.Endpoint
import io.github.jeiel85.homebase.net.LocalNetworks
import io.github.jeiel85.homebase.net.RouteSelector
import org.json.JSONArray
import org.json.JSONObject

/** A paired PC and every path to it (from the QR, refreshed from the PC after each connect). */
data class PcEntry(
    val pcId: String,
    val name: String,
    val endpoints: List<Endpoint>,
    val deviceId: String,
    val token: String,
    /** [Endpoint.key] of the path that worked last; preferred among equally good paths. */
    val lastGood: String? = null,
    /** PC time (epoch ms) of the last path list received by push; older or replayed pushes are ignored. */
    val pushedEndpointsAt: Long? = null,
) {
    /** Paths to try now, best first, for the phone's current network (DECISIONS M-18). */
    fun orderedEndpoints(net: LocalNetworks?): List<Endpoint> = RouteSelector.order(endpoints, net, lastGood)
}

/**
 * Paired PCs in SharedPreferences. The bearer token is encrypted with [KeystoreCipher], so a backup
 * or file copy doesn't carry usable credentials.
 */
class PcStore(context: Context) {

    private val prefs = context.applicationContext.getSharedPreferences(NAME, Context.MODE_PRIVATE)
    private val cipher = KeystoreCipher(KEY_ALIAS)

    @Synchronized
    fun all(): List<PcEntry> {
        val raw = prefs.getString(KEY_PCS, null) ?: return emptyList()
        return try {
            val arr = JSONArray(raw)
            (0 until arr.length()).mapNotNull { i -> arr.optJSONObject(i)?.let(::fromJson) }
        } catch (_: Exception) {
            emptyList()
        }
    }

    fun get(pcId: String): PcEntry? = all().firstOrNull { it.pcId == pcId }

    /** Adds or replaces (re-pairing the same PC keeps one entry). */
    @Synchronized
    fun upsert(pc: PcEntry) = save(all().filterNot { it.pcId == pc.pcId } + pc)

    @Synchronized
    fun remove(pcId: String) = save(all().filterNot { it.pcId == pcId })

    @Synchronized
    fun rememberGood(pcId: String, endpoint: Endpoint) {
        val list = all()
        if (list.none { it.pcId == pcId && it.lastGood != endpoint.key }) return
        save(list.map { if (it.pcId == pcId) it.copy(lastGood = endpoint.key) else it })
    }

    /** The PC's current paths (DHCP change, new Quick Tunnel address, Tailscale turned on). */
    @Synchronized
    fun updateEndpoints(pcId: String, endpoints: List<Endpoint>) {
        if (endpoints.isEmpty()) return
        val list = all()
        if (list.none { it.pcId == pcId && it.endpoints != endpoints }) return
        save(list.map { if (it.pcId == pcId) it.copy(endpoints = endpoints) else it })
    }

    /**
     * Paths the PC pushed (DECISIONS M-23). Returns false when the PC isn't paired or the push is not
     * newer than the last one applied (a relayed copy arriving late must not bring back old paths).
     */
    @Synchronized
    fun applyPushedEndpoints(pcId: String, endpoints: List<Endpoint>, pcTimeMillis: Long): Boolean {
        val list = all()
        val pc = list.firstOrNull { it.pcId == pcId } ?: return false
        if (!acceptsPush(pc, endpoints, pcTimeMillis)) return false
        save(list.map { if (it.pcId == pcId) it.copy(endpoints = endpoints, pushedEndpointsAt = pcTimeMillis) else it })
        return true
    }

    private fun save(list: List<PcEntry>) {
        val arr = JSONArray()
        list.forEach { arr.put(toJson(it)) }
        prefs.edit().putString(KEY_PCS, arr.toString()).apply()
    }

    private fun toJson(pc: PcEntry) = JSONObject()
        .put("pcId", pc.pcId)
        .put("name", pc.name)
        .put("endpoints", JSONArray().apply { pc.endpoints.forEach { put(it.toJson()) } })
        .put("deviceId", pc.deviceId)
        .put("token", encrypt(pc.token))
        .put("lastGood", pc.lastGood)
        .apply { pc.pushedEndpointsAt?.let { put("pushedEndpointsAt", it) } }

    private fun fromJson(o: JSONObject): PcEntry? {
        val token = decrypt(o.optString("token")) ?: return null
        val (endpoints, lastGood) = storedEndpoints(o)
        return PcEntry(
            pcId = o.optString("pcId"),
            name = o.optString("name"),
            endpoints = endpoints,
            deviceId = o.optString("deviceId"),
            token = token,
            lastGood = lastGood,
            pushedEndpointsAt = if (o.has("pushedEndpointsAt")) o.optLong("pushedEndpointsAt") else null,
        )
    }

    private fun encrypt(plain: String): String = cipher.encrypt(plain.toByteArray(Charsets.UTF_8))

    /** null when the Keystore key is gone (e.g. app data restored elsewhere) — the PC must be re-paired. */
    private fun decrypt(encoded: String): String? = cipher.decrypt(encoded)?.toString(Charsets.UTF_8)

    companion object {
        /** Whether a pushed path list should replace [pc]'s: non-empty and newer than the last push applied. */
        fun acceptsPush(pc: PcEntry, endpoints: List<Endpoint>, pcTimeMillis: Long): Boolean =
            endpoints.isNotEmpty() && (pc.pushedEndpointsAt == null || pcTimeMillis > pc.pushedEndpointsAt)

        /**
         * Endpoints and last-good key from a stored entry. Entries saved before multi-path support
         * have `hosts` + `port` + `lastGoodHost` (Tailscale only); they are read as Tailscale endpoints.
         */
        fun storedEndpoints(o: JSONObject): Pair<List<Endpoint>, String?> {
            if (o.has("endpoints")) {
                return Endpoint.parseList(o.optJSONArray("endpoints")) to o.optString("lastGood").ifBlank { null }
            }
            val hosts = o.optJSONArray("hosts")?.let { a -> (0 until a.length()).map { a.optString(it) } } ?: emptyList()
            val endpoints = Endpoint.fromLegacyHosts(hosts, o.optInt("port"))
            val lastHost = o.optString("lastGoodHost").ifBlank { null }
            return endpoints to endpoints.firstOrNull { it.host == lastHost }?.key
        }

        private const val NAME = "homebase_pcs"
        private const val KEY_PCS = "pcs"
        private const val KEY_ALIAS = "homebase_pc_tokens"
    }
}
