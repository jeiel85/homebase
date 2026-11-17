package io.github.jeiel85.homebase.mesh

import android.content.Context
import android.os.Build
import android.os.SystemClock
import android.provider.Settings
import android.util.Log
import io.github.jeiel85.homebase.data.PcEntry
import io.github.jeiel85.homebase.data.PcStore
import io.github.jeiel85.homebase.net.GroupInfo
import io.github.jeiel85.homebase.net.HomebaseApi
import io.github.jeiel85.homebase.net.PairingOffer
import io.github.jeiel85.homebase.push.PushNotifier
import io.github.jeiel85.homebase.push.PushRegistrar
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.sync.Mutex

/**
 * The phone's side of the device group (DECISIONS M-47): pairing with one PC means joining its group.
 * Every so often the phone reads each paired PC's group and
 *  - links two of its PCs that don't know each other (it fetches a one-time offer from one and hands it
 *    to the other — `POST /api/v1/group/offer` then `/group/join`), and
 *  - pairs itself with the group's PCs it is missing, through a PC that holds their token
 *    (`POST /api/v1/group/introduce`).
 * A PC the user deleted on this phone is not joined again automatically (pairing it by hand undoes that).
 * The same pass reconciles the PCs' aliases with the group's (DECISIONS M-51, [AliasSync]).
 */
object GroupSync {
    private const val TAG = "HomebaseGroup"
    private const val PREFS = "homebase.group"
    private const val KEY_DECLINED = "declined"

    /** At most this often from the list's poll and the background job; pull to refresh forces it. */
    const val INTERVAL_MS = 10 * 60_000L

    private val mutex = Mutex()
    private var lastRunElapsed = 0L

    /** What to do: PCs to link to each other, and PCs to pair this phone with (through whom). */
    data class Plan(
        /** (PC that joins, PC whose offer it gets) for paired PCs that don't know each other. */
        val links: List<Pair<String, String>>,
        /** (PC that introduces, PC to pair with). */
        val joins: List<Pair<String, String>>,
    )

    /**
     * Pure planning from what the paired PCs answered ([groups]: pcId → its group). Two paired PCs
     * that list each other in neither direction get linked; a group PC this phone lacks is joined
     * through a PC that holds its token, unless the user [declined] it.
     */
    fun plan(mine: List<String>, groups: Map<String, GroupInfo>, declined: Set<String>): Plan {
        val links = mutableListOf<Pair<String, String>>()
        for (i in mine.indices) {
            for (j in i + 1 until mine.size) {
                val a = groups[mine[i]] ?: continue
                val b = groups[mine[j]] ?: continue
                if (a.pcs.none { it.pcId == b.pcId } && b.pcs.none { it.pcId == a.pcId }) links += a.pcId to b.pcId
            }
        }
        val joins = mutableListOf<Pair<String, String>>()
        val planned = mutableSetOf<String>()
        for (pcId in mine) {
            val g = groups[pcId] ?: continue
            for (m in g.pcs) {
                if (!m.token || m.pcId in mine || m.pcId in declined || !planned.add(m.pcId)) continue
                joins += g.pcId to m.pcId
            }
        }
        return Plan(links, joins)
    }

    fun declined(context: Context): Set<String> =
        prefs(context).getStringSet(KEY_DECLINED, null)?.toSet() ?: emptySet()

    /** The user deleted [pcId] on this phone: don't bring it back on the group's behalf. */
    fun decline(context: Context, pcId: String) =
        prefs(context).edit().putStringSet(KEY_DECLINED, declined(context) + pcId).apply()

    /** Paired by hand again: it may follow the group once more. */
    fun undecline(context: Context, pcId: String) {
        val now = declined(context)
        if (pcId in now) prefs(context).edit().putStringSet(KEY_DECLINED, now - pcId).apply()
    }

    private fun prefs(context: Context) = context.applicationContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)

    /** Runs [sync] when [INTERVAL_MS] has passed (or [force]); true when this phone joined a new PC or a PC was renamed. */
    suspend fun syncIfDue(context: Context, api: HomebaseApi, store: PcStore, force: Boolean = false): Boolean {
        val now = SystemClock.elapsedRealtime()
        if (!force && lastRunElapsed != 0L && now - lastRunElapsed in 0 until INTERVAL_MS) return false
        if (!mutex.tryLock()) return false
        return try {
            lastRunElapsed = now
            sync(context, api, store)
        } finally {
            mutex.unlock()
        }
    }

    private suspend fun sync(context: Context, api: HomebaseApi, store: PcStore): Boolean {
        val pcs = store.all()
        if (pcs.isEmpty()) return false
        val groups = pcs.mapNotNull { pc ->
            try {
                api.group(pc)?.let { pc.pcId to it }
            } catch (e: Exception) {
                null // unreachable now, or a PC older than v0.35.0
            }
        }.toMap()
        if (groups.isEmpty()) return false
        val renamed = syncAliases(context, api, store, groups)
        val plan = plan(pcs.map { it.pcId }, groups, declined(context))

        for ((joiner, target) in plan.links) {
            val a = store.get(joiner) ?: continue
            val b = store.get(target) ?: continue
            try {
                val offer = api.groupOffer(b) ?: continue
                api.groupJoin(a, offer)
                Log.i(TAG, "linked ${a.name} with ${b.name}")
            } catch (e: Exception) {
                Log.i(TAG, "linking ${a.name} with ${b.name} failed: ${e.message}")
            }
        }

        var joined = false
        for ((via, target) in plan.joins) {
            val introducer = store.get(via) ?: continue
            try {
                val offerJson = api.groupIntroduce(introducer, target) ?: continue
                val offer = PairingOffer.parse(offerJson) ?: continue
                val (res, endpoint) = api.pair(offer, deviceName(context), "${Build.MANUFACTURER} ${Build.MODEL}")
                val pc = PcEntry(res.pcId, res.pcName, offer.endpoints, res.deviceId, res.token, endpoint.key)
                store.upsert(pc)
                PushRegistrar.ensureAsync(context, api, pc)
                joined = true
                Log.i(TAG, "joined ${res.pcName} through ${introducer.name}")
            } catch (e: Exception) {
                Log.i(TAG, "joining $target through ${introducer.name} failed: ${e.message}")
            }
        }
        if (joined) PresenceScheduler.sync(context)
        return joined || renamed
    }

    /**
     * Takes the group's newer aliases for this phone's PCs and hands the PCs what this phone decided
     * later than they know (DECISIONS M-51). True when a PC's shown name changed.
     */
    private suspend fun syncAliases(context: Context, api: HomebaseApi, store: PcStore, groups: Map<String, GroupInfo>): Boolean {
        val plan = AliasSync.plan(store.all(), groups)
        val renamed = store.applyGroupAliases(plan.apply.mapValues { (_, e) -> e.alias to e.atMillis })
        for (id in renamed) store.get(id)?.let { PushNotifier.renamePc(context, it.pcId, it.displayName) }
        for ((pcId, entries) in plan.upload) {
            val pc = store.get(pcId) ?: continue
            try {
                api.groupAlias(pc, entries)
            } catch (e: Exception) {
                Log.i(TAG, "sharing aliases with ${pc.name} failed: ${e.message}")
            }
        }
        if (renamed.isNotEmpty()) Log.i(TAG, "took ${renamed.size} alias(es) from the group")
        return renamed.isNotEmpty()
    }

    /**
     * The user named [pcId] on this phone: hand it to every paired PC now (each PC passes it on to the
     * PCs it syncs with). One that can't be reached gets it from the next [sync].
     */
    suspend fun shareAlias(api: HomebaseApi, store: PcStore, pcId: String) {
        val entry = store.get(pcId)?.let(AliasSync::local) ?: return
        coroutineScope {
            store.all().map { pc ->
                async {
                    try {
                        api.groupAlias(pc, listOf(entry))
                    } catch (e: Exception) {
                        Log.i(TAG, "sharing the alias with ${pc.name} failed: ${e.message}")
                    }
                }
            }.awaitAll()
        }
    }

    /** The same name the pairing screen sends. */
    fun deviceName(context: Context): String =
        Settings.Global.getString(context.contentResolver, "device_name")?.takeIf { it.isNotBlank() }
            ?: "${Build.MANUFACTURER} ${Build.MODEL}"
}
