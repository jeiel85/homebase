package io.github.jeiel85.homebase.push

import android.app.Service
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.os.Binder
import android.os.IBinder
import android.util.Log
import io.github.jeiel85.homebase.data.PcStore
import io.github.jeiel85.homebase.net.HomebaseApi
import io.github.jeiel85.homebase.net.NetworkProbe
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull

/**
 * Broadcasts from the distributor (UnifiedPush AND_3). Anything whose token isn't our current one is
 * ignored; a MESSAGE must also decrypt with this phone's key and auth secret (AES-GCM tag), so a
 * forged broadcast from another app shows nothing. Messages for a PC that isn't paired are dropped.
 */
class PushReceiver : BroadcastReceiver() {

    override fun onReceive(context: Context, intent: Intent) {
        val state = PushState(context)
        val token = intent.getStringExtra(UnifiedPush.EXTRA_TOKEN)
        if (token == null || token != state.token) return
        val distributor = state.distributor ?: return
        val id = intent.getStringExtra(UnifiedPush.EXTRA_ID)

        when (intent.action) {
            UnifiedPush.ACTION_NEW_ENDPOINT -> {
                val endpoint = intent.getStringExtra(UnifiedPush.EXTRA_ENDPOINT)
                if (id != null) UnifiedPush.ack(context, distributor, token, id)
                if (endpoint.isNullOrBlank()) return
                state.setEndpoint(endpoint)
                // Also on an unchanged endpoint (the distributor's periodic ping): a PC that dropped
                // the subscription after a 404/410 — e.g. the phone was off for days — gets it back.
                // The PC doesn't rewrite its state for an unchanged subscription.
                PushRegistrar.forgetSynced()
                registerWithAllPcs(context)
            }

            UnifiedPush.ACTION_MESSAGE -> {
                val bytes = intent.getByteArrayExtra(UnifiedPush.EXTRA_BYTES_MESSAGE)
                if (id != null) UnifiedPush.ack(context, distributor, token, id)
                if (bytes != null) handleMessage(context, state, bytes)
            }

            UnifiedPush.ACTION_REGISTRATION_FAILED -> {
                if (state.endpoint != null) return // spec: ignore once NEW_ENDPOINT arrived for this token
                val reason = intent.getStringExtra(UnifiedPush.EXTRA_REASON) ?: "INTERNAL_ERROR"
                state.lastError = "배포자가 등록을 거절했습니다 ($reason)"
                state.clear()
            }

            UnifiedPush.ACTION_UNREGISTERED -> {
                state.lastError = "배포자가 등록을 해제했습니다. 다시 켜 주세요."
                state.clear()
                PushRegistrar.forgetSynced()
            }

            UnifiedPush.ACTION_TEMP_UNAVAILABLE -> {
                // The push server is down; the distributor sends NEW_ENDPOINT when it is back.
                state.lastError = "푸시 서버가 일시적으로 응답하지 않습니다"
            }
        }
    }

    private fun handleMessage(context: Context, state: PushState, bytes: ByteArray) {
        val plain = try {
            val keys = state.keys()
            WebPush.decrypt(bytes, keys.privateKey, keys.publicKey, keys.authSecret).toString(Charsets.UTF_8)
        } catch (e: Exception) {
            Log.w(TAG, "dropped an undecryptable push: ${e.javaClass.simpleName}")
            return
        }
        if (PushEndpointsUpdate.kindOf(plain) == PushEndpointsUpdate.KIND) {
            handleEndpoints(context, plain)
            return
        }
        val message = try {
            PushMessage.parse(plain)
        } catch (e: Exception) {
            Log.w(TAG, "dropped an unreadable push: ${e.message}")
            return
        }
        if (PcStore(context).get(message.pcId) == null) {
            Log.i(TAG, "dropped a push from an unpaired PC")
            return
        }
        // The PC skips muted categories itself (M-25); this catches PCs from before that and the
        // moments between changing the choice and the PC hearing about it.
        if (NotificationGroups.isMuted(message.category, state.mutedCategories)) {
            Log.i(TAG, "dropped a push in muted category ${message.category}")
            return
        }
        PushNotifier.show(context, message)
    }

    /** A PC's new paths (M-23): stored silently; a retrying detail screen picks them up on its next try. */
    private fun handleEndpoints(context: Context, plain: String) {
        val store = PcStore(context)
        val pc = store.get(PushEndpointsUpdate.pcIdOf(plain)) ?: run {
            Log.i(TAG, "dropped a path update from an unpaired PC")
            return
        }
        val update = try {
            PushEndpointsUpdate.verify(plain, pc.token)
        } catch (e: Exception) {
            Log.w(TAG, "dropped a path update: ${e.message}")
            return
        }
        val applied = store.applyPushedEndpoints(update.pcId, update.endpoints, update.tsEpochMillis)
        Log.i(TAG, "path update from ${pc.name}: ${if (applied) "applied" else "older than the last one, ignored"}")
    }

    /** A broadcast may only run ~10 s; PCs not reached by then catch up on their next contact. */
    private fun registerWithAllPcs(context: Context) {
        val pending = goAsync()
        val app = context.applicationContext
        PushRegistrar.scope.launch {
            try {
                withTimeoutOrNull(8_000) {
                    PushRegistrar.ensureAll(app, HomebaseApi(networks = NetworkProbe(app)::current), PcStore(app))
                }
            } finally {
                pending.finish()
            }
        }
    }

    private companion object {
        const val TAG = "HomebasePush"
    }
}

/**
 * Required by AND_3: the distributor may bind this for a few seconds so the app is raised to the
 * foreground importance while it handles a message. Nothing to do here besides existing.
 */
class RaiseToForegroundService : Service() {
    private val binder = Binder()
    override fun onBind(intent: Intent?): IBinder = binder
}
