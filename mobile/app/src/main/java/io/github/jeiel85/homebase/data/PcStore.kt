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
    /** The user's own name for this PC, shared with the device group (DECISIONS M-36, M-51); null = the PC's name. */
    val alias: String? = null,
    /** This phone's time (epoch ms) when that path list was applied; lets a PC whose clock went back be heard. */
    val pushedEndpointsReceivedAt: Long? = null,
    /** Index into [PcMarks.PALETTE] of this PC's badge colour, fixed once given (DECISIONS M-38). */
    val color: Int? = null,
    /**
     * When [alias] was set or cleared (epoch ms, by the clock of the device where it was done), for the
     * group's latest-wins rule (DECISIONS M-51); null for an alias set before aliases were shared.
     */
    val aliasAt: Long? = null,
) {
    /** What the app shows for this PC: the alias when set, else the name the PC reported. */
    val displayName: String get() = alias ?: name

    /** The badge on this PC's notifications and card: its colour and the first letter of [displayName]. */
    val mark: PcMark get() = PcMark(PcMarks.letter(displayName), color ?: PcMarks.slot(pcId))

    /**
     * The name for something the PC sent now under [reported] (a push, a history entry, the hub's
     * own name): the alias when set, else what the PC reports — [name] is refreshed only when the app
     * reaches the PC ([PcStore.updateName]), so it can still be an older name.
     */
    fun shownName(reported: String): String = alias ?: reported.ifBlank { name }

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

    /**
     * Every PC has a badge colour here: entries stored before colours existed get theirs in list
     * order, and keep it from the next save on (every change saves a list read through this).
     */
    @Synchronized
    fun all(): List<PcEntry> {
        val raw = prefs.getString(KEY_PCS, null) ?: return emptyList()
        return try {
            val arr = JSONArray(raw)
            PcMarks.withColors((0 until arr.length()).mapNotNull { i -> arr.optJSONObject(i)?.let(::fromJson) })
        } catch (_: Exception) {
            emptyList()
        }
    }

    fun get(pcId: String): PcEntry? = all().firstOrNull { it.pcId == pcId }

    /** Adds or replaces (re-pairing the same PC keeps one entry, and the alias the user gave it). */
    @Synchronized
    fun upsert(pc: PcEntry) = save(upserted(all(), pc))

    /**
     * Sets or clears (blank, or the PC's own name) the alias the user typed, stamped now for the group
     * (DECISIONS M-51). True when it changed — then it is to be shared ([io.github.jeiel85.homebase.mesh.GroupSync.shareAlias]).
     */
    @Synchronized
    fun setAlias(pcId: String, input: String?, nowMillis: Long = System.currentTimeMillis()): Boolean {
        val list = all()
        val pc = list.firstOrNull { it.pcId == pcId } ?: return false
        val alias = normalizeAlias(input, pc.name)
        if (alias == pc.alias) return false
        // Strictly after the entry it replaces, even if this clock is behind the device that set that one.
        val at = maxOf(nowMillis, (pc.aliasAt ?: -1L) + 1)
        save(list.map { if (it.pcId == pcId) it.copy(alias = alias, aliasAt = at) else it })
        return true
    }

    /**
     * Takes the group's newer aliases (pcId → alias or null, and when it was decided). Returns the PCs
     * whose shown name changed, for their notification groups.
     */
    @Synchronized
    fun applyGroupAliases(updates: Map<String, Pair<String?, Long>>): List<String> {
        if (updates.isEmpty()) return emptyList()
        val list = all()
        val renamed = mutableListOf<String>()
        val next = list.map { pc ->
            val (input, at) = updates[pc.pcId] ?: return@map pc
            val alias = normalizeAlias(input, pc.name)
            if (alias != pc.alias) renamed += pc.pcId
            pc.copy(alias = alias, aliasAt = at)
        }
        if (next != list) save(next)
        return renamed
    }

    /**
     * The name the PC reports now (`/info`, a live snapshot) in place of the stored one, which is from
     * pairing — a PC renamed in Windows would otherwise keep its old name wherever no alias covers it.
     * The alias is left as it is. True when the stored name changed.
     */
    @Synchronized
    fun updateName(pcId: String, reported: String): Boolean {
        val next = renamed(all(), pcId, reported) ?: return false
        save(next)
        return true
    }

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
     * newer than the last one applied (a relayed copy arriving late must not bring back old paths) —
     * see [acceptsPush] for a PC whose clock went backwards.
     */
    @Synchronized
    fun applyPushedEndpoints(
        pcId: String,
        endpoints: List<Endpoint>,
        pcTimeMillis: Long,
        receivedAtMillis: Long = System.currentTimeMillis(),
    ): Boolean {
        val list = all()
        val pc = list.firstOrNull { it.pcId == pcId } ?: return false
        if (!acceptsPush(pc, endpoints, pcTimeMillis, receivedAtMillis)) return false
        save(
            list.map {
                if (it.pcId == pcId) {
                    it.copy(endpoints = endpoints, pushedEndpointsAt = pcTimeMillis, pushedEndpointsReceivedAt = receivedAtMillis)
                } else {
                    it
                }
            },
        )
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
        .apply { pc.pushedEndpointsReceivedAt?.let { put("pushedEndpointsReceivedAt", it) } }
        .apply { pc.alias?.let { put("alias", it) } }
        .apply { pc.color?.let { put("color", it) } }
        .apply { pc.aliasAt?.let { put("aliasAt", it) } }

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
            alias = normalizeAlias(o.optString("alias"), o.optString("name")),
            pushedEndpointsReceivedAt = if (o.has("pushedEndpointsReceivedAt")) o.optLong("pushedEndpointsReceivedAt") else null,
            color = PcMarks.validColor(if (o.has("color")) o.optInt("color", -1) else null),
            aliasAt = if (o.has("aliasAt")) o.optLong("aliasAt") else null,
        )
    }

    private fun encrypt(plain: String): String = cipher.encrypt(plain.toByteArray(Charsets.UTF_8))

    /** null when the Keystore key is gone (e.g. app data restored elsewhere) — the PC must be re-paired. */
    private fun decrypt(encoded: String): String? = cipher.decrypt(encoded)?.toString(Charsets.UTF_8)

    companion object {
        /** Longest alias kept; the list card shows one line, so a longer one would only be cut off. */
        const val ALIAS_MAX = 40
        private val WHITESPACE = Regex("""\s+""")

        /**
         * The alias to store for [input]: trimmed, inner runs of whitespace (a pasted newline) made one
         * space, cut to [ALIAS_MAX] characters. Blank, or the same as the PC's own [name], means none.
         */
        fun normalizeAlias(input: String?, name: String): String? {
            val cleaned = input?.trim()?.replace(WHITESPACE, " ")?.let(::cutTo) ?: return null
            return cleaned.takeUnless { it.isEmpty() || it == name }
        }

        /** First [ALIAS_MAX] code points, so an emoji at the edge is not split in half. */
        private fun cutTo(s: String): String {
            if (s.codePointCount(0, s.length) <= ALIAS_MAX) return s
            return s.substring(0, s.offsetByCodePoints(0, ALIAS_MAX)).trimEnd()
        }

        /**
         * [list] with [pc] added or replacing the same PC. A re-paired entry keeps the old alias and
         * badge colour; a new PC gets a colour no other PC has (while there are free ones).
         */
        fun upserted(list: List<PcEntry>, pc: PcEntry): List<PcEntry> {
            val old = list.firstOrNull { it.pcId == pc.pcId }
            val kept = if (pc.alias == null && pc.aliasAt == null) {
                pc.copy(alias = old?.alias, aliasAt = old?.aliasAt, color = pc.color ?: old?.color)
            } else {
                pc.copy(color = pc.color ?: old?.color)
            }
            return PcMarks.withColors(list.filterNot { it.pcId == pc.pcId } + kept)
        }

        /** [list] with [pcId] renamed to [reported] (trimmed); null when it is blank, unchanged, or the PC isn't listed. */
        fun renamed(list: List<PcEntry>, pcId: String, reported: String): List<PcEntry>? {
            val name = reported.trim()
            if (name.isEmpty() || list.none { it.pcId == pcId && it.name != name }) return null
            return list.map { if (it.pcId == pcId) it.copy(name = name) else it }
        }

        /**
         * A PC whose push is not newer by its own clock is still heard when this phone receives it at
         * least this long after the last one applied. Queued copies a distributor delivers on reconnect
         * arrive within seconds, so a late duplicate inside the margin still can't bring back old paths.
         */
        const val PUSH_RECEIVE_MARGIN_MS = 10L * 60 * 1000

        /**
         * Whether a pushed path list should replace [pc]'s: non-empty, and newer than the last push
         * applied by the PC's clock — or, when that clock went backwards (a bad RTC after a reboot, then
         * NTP), received [PUSH_RECEIVE_MARGIN_MS] or more after the last one by this phone's clock.
         * Without the second rule every later push of such a PC looks old and is ignored. The MAC is
         * checked before this ([io.github.jeiel85.homebase.push.PushEndpointsUpdate.verify]).
         */
        fun acceptsPush(pc: PcEntry, endpoints: List<Endpoint>, pcTimeMillis: Long, receivedAtMillis: Long): Boolean {
            if (endpoints.isEmpty()) return false
            val lastPcTime = pc.pushedEndpointsAt ?: return true
            if (pcTimeMillis > lastPcTime) return true
            // Applied before the receive time was kept: nothing to compare with, so the newer arrival wins once.
            val lastReceived = pc.pushedEndpointsReceivedAt ?: return true
            return receivedAtMillis - lastReceived >= PUSH_RECEIVE_MARGIN_MS
        }

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
