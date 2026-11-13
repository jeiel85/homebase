package io.github.jeiel85.homebase.push

import android.content.Context
import android.util.Log
import io.github.jeiel85.homebase.data.PcEntry
import io.github.jeiel85.homebase.data.PcStore
import io.github.jeiel85.homebase.net.HomebaseApi
import io.github.jeiel85.homebase.net.UnauthorizedException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.launch
import java.util.concurrent.ConcurrentHashMap

/**
 * Keeps every paired PC told about this phone's push endpoint (`POST /api/v1/push`).
 * Called when the endpoint changes, after pairing, and whenever a PC answers (list probe or live
 * connect), so a PC that was offline or restarted with an empty state catches up on the next
 * contact. Within one process each PC is posted once per endpoint; the PC ignores unchanged repeats.
 */
object PushRegistrar {
    private const val TAG = "HomebasePush"

    /** Background work that must outlive a screen (a registration started from a broadcast or a list probe). */
    val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    private val synced = ConcurrentHashMap<String, String>()

    fun ensureAsync(context: Context, api: HomebaseApi, pc: PcEntry) {
        val app = context.applicationContext
        scope.launch { ensure(app, api, pc) }
    }

    suspend fun ensure(context: Context, api: HomebaseApi, pc: PcEntry) {
        val state = PushState(context)
        val subscription = state.subscription() ?: return
        if (synced[pc.pcId] == subscription.endpoint) return
        try {
            api.registerPush(pc, subscription)
            synced[pc.pcId] = subscription.endpoint
            state.markRegistered(pc.pcId, true)
        } catch (e: UnauthorizedException) {
            state.markRegistered(pc.pcId, false)
        } catch (e: Exception) {
            Log.i(TAG, "push registration with ${pc.name} deferred: ${e.message}")
        }
    }

    suspend fun ensureAll(context: Context, api: HomebaseApi, store: PcStore) = coroutineScope {
        store.all().map { async { ensure(context, api, it) } }.awaitAll()
    }

    /** The endpoint changed or push was turned off: every PC needs telling again. */
    fun forgetSynced() = synced.clear()

    /** Push turned off: remove the subscription from every reachable PC (best effort). */
    suspend fun unregisterAll(context: Context, api: HomebaseApi, store: PcStore) = coroutineScope {
        forgetSynced()
        val state = PushState(context)
        store.all().map { pc ->
            async {
                runCatching { api.unregisterPush(pc) }
                state.markRegistered(pc.pcId, false)
            }
        }.awaitAll()
    }
}
