package io.github.jeiel85.homebase.push

import android.app.Service
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.os.Binder
import android.os.IBinder
import android.util.Log
import io.github.jeiel85.homebase.clip.ClipShare
import io.github.jeiel85.homebase.data.PcStore
import io.github.jeiel85.homebase.net.ClipProtocol
import io.github.jeiel85.homebase.net.FileSendProtocol
import io.github.jeiel85.homebase.share.FileReceive
import io.github.jeiel85.homebase.net.HomebaseApi
import io.github.jeiel85.homebase.net.NetworkProbe
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors
import java.util.concurrent.RejectedExecutionException

/**
 * Broadcasts from the distributor (UnifiedPush AND_3). Anything whose token isn't our current one is
 * not handled — a NEW_ENDPOINT or MESSAGE for such a token is answered with UNREGISTER so the distributor
 * drops that leftover registration (M-76); a MESSAGE must also decrypt with this phone's key and auth secret (AES-GCM tag), so a
 * forged broadcast from another app shows nothing. Messages for a PC that isn't paired are dropped.
 */
class PushReceiver : BroadcastReceiver() {

    override fun onReceive(context: Context, intent: Intent) {
        val state = PushState(context)
        val token = intent.getStringExtra(UnifiedPush.EXTRA_TOKEN)
        if (UnifiedPush.isStale(intent.action, token, state.token)) {
            // A registration this install no longer has — one left by the app before it was reinstalled, or
            // replaced without being let go. Only this app can end it, and it learns the token only now (M-76).
            UnifiedPush.unregisterStale(context, token!!)
            Log.i(TAG, "let go of a registration this install doesn't hold (${intent.action?.substringAfterLast('.')})")
            return
        }
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
                if (bytes != null) inBackground(context) { app -> handleMessage(app, state, bytes) }
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
        when (PushEndpointsUpdate.kindOf(plain)) {
            PushEndpointsUpdate.KIND -> {
                handleEndpoints(context, plain)
                return
            }
            // Text a PC sent by hand (DECISIONS M-62): onto the clipboard, kept as "받은 클립보드" (M-70).
            ClipProtocol.PUSH_KIND -> {
                ClipShare.receive(context, plain)
                return
            }
            // Files a PC sends (DECISIONS M-65): a notification to fetch them, never the notification center.
            FileSendProtocol.PUSH_KIND -> {
                FileReceive.onPush(context, plain)
                return
            }
        }
        val message = try {
            PushMessage.parse(plain)
        } catch (e: Exception) {
            Log.w(TAG, "dropped an unreadable push: ${e.message}")
            return
        }
        val pcs = PcStore(context).all()
        val pc = pcs.firstOrNull { it.pcId == message.pcId } ?: run {
            Log.i(TAG, "dropped a push from an unpaired PC")
            return
        }
        // Every paired PC shares this phone's WebPush keys: verify per-PC MAC when present, and require it
        // for Claude Code approval prompts so another PC cannot spoof an Allow/Deny prompt for this PC.
        if ((message.mac != null || message.approval != null) && !message.verifyMac(pc.token)) {
            Log.w(TAG, "dropped a push with missing or invalid per-PC MAC")
            return
        }
        // The PC skips muted categories itself (M-25); this catches PCs from before that and the
        // moments between changing the choice and the PC hearing about it.
        if (NotificationGroups.isMuted(message.category, state.mutedCategories)) {
            Log.i(TAG, "dropped a push in muted category ${message.category}")
            return
        }
        // The same relayed notification can come from two PCs (both paired with both phones): show it once.
        if (message.category == NotificationGroups.DEVICE && !RecentPushIds(context).firstSighting(message.id)) {
            Log.i(TAG, "dropped a relayed notification already shown")
            return
        }
        // How the user wants this kind / this app to appear (DECISIONS M-37).
        val profile = NotificationRuleStore(context).resolve(message.category, message.source, message.priority)
        // Shown under the user's alias for the PC, else the name the PC sent; the history keeps what the
        // PC sent and maps it when drawn. The badge is the PC's (or the relayed device's), as on its card (M-38).
        val shownMessage = message.copy(pcName = pc.shownName(message.pcName))
        val mark = NotificationLook.mark(shownMessage, pc, pcs.mapNotNull { it.color })
        val shown = PushNotifier.show(context, shownMessage, profile, mark)
        // The notification center keeps it even when Android blocked the notification (DECISIONS M-31)
        // or the user keeps this kind out of the shade.
        try {
            PushHistory.record(context, HistoryEntry.of(message, System.currentTimeMillis(), shown, quiet = !profile.shade, color = mark.colorIndex))
            // The file is written on the history's own thread; keep the broadcast open until it is on disk.
            PushHistory.awaitWrites(HISTORY_WRITE_WAIT_MS)
        } catch (e: Exception) {
            Log.w(TAG, "could not add a push to the history: ${e.javaClass.simpleName}")
        }
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

    /**
     * Runs [work] off the main thread with the broadcast kept open ([goAsync]) until it is done:
     * decrypting a push, reading the PC tokens and encrypting the history are Android Keystore calls,
     * which take tens of ms on some phones. One worker thread keeps pushes in the order they arrived.
     */
    private fun inBackground(context: Context, work: (Context) -> Unit) {
        val pending = goAsync()
        val app = context.applicationContext
        try {
            worker.execute {
                try {
                    work(app)
                } catch (e: Exception) {
                    // Nothing may escape a pool thread (it would crash the app); the push is lost, as before.
                    Log.w(TAG, "could not handle a push: ${e.javaClass.simpleName}")
                } finally {
                    pending.finish()
                }
            }
        } catch (e: RejectedExecutionException) {
            pending.finish()
        }
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

        /** Longest a push waits for its history write; well inside the broadcast's ~10 s. */
        const val HISTORY_WRITE_WAIT_MS = 4_000L

        /** Handles MESSAGE broadcasts one at a time, in arrival order (see [inBackground]). */
        val worker: ExecutorService = Executors.newSingleThreadExecutor { r -> Thread(r, "hb-push-receiver") }
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
