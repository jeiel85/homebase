package io.github.jeiel85.homebase.command

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.util.Log
import android.widget.Toast
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import io.github.jeiel85.homebase.MainActivity
import io.github.jeiel85.homebase.R
import io.github.jeiel85.homebase.clip.ClipShare
import io.github.jeiel85.homebase.data.PcEntry
import io.github.jeiel85.homebase.data.PcStore
import io.github.jeiel85.homebase.net.ClipCrypto
import io.github.jeiel85.homebase.net.ClipProtocol
import io.github.jeiel85.homebase.net.CommandErrors
import io.github.jeiel85.homebase.net.CommandList
import io.github.jeiel85.homebase.net.CommandOutcome
import io.github.jeiel85.homebase.net.CommandStates
import io.github.jeiel85.homebase.net.HomebaseApi
import io.github.jeiel85.homebase.net.NetworkProbe
import io.github.jeiel85.homebase.net.RemoteCommandNames
import io.github.jeiel85.homebase.net.RemoteCommandProtocol
import io.github.jeiel85.homebase.net.UnauthorizedException
import io.github.jeiel85.homebase.net.UnconfirmedException
import io.github.jeiel85.homebase.net.WakeOnLan
import io.github.jeiel85.homebase.push.CommandHistory
import io.github.jeiel85.homebase.push.PushHistory
import io.github.jeiel85.homebase.push.PushNotifier
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlin.coroutines.cancellation.CancellationException

/**
 * Asking a PC to carry out a command (DECISIONS M-69, docs/REMOTE_COMMANDS.md): every way in — the PC screen's card,
 * a PC card's "잠그기", the restart notification's [취소] — ends in [send], so they all seal it the same way, say the
 * same thing about the outcome and leave the same "보낸 명령" line in the notification center.
 */
object RemoteCommands {
    private const val TAG = "HomebaseCommand"

    /** Requests outlive the screen that started them: one already on its way still gets its answer recorded. */
    internal val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    /** What came of one request, and the line that says so. */
    data class Sent(val outcome: CommandOutcome, val message: String)

    /** [pc]'s commands, or null when it is older than remote commands; remembers its MAC addresses for [WakeSender]. */
    suspend fun list(context: Context, api: HomebaseApi, pc: PcEntry): CommandList? {
        // The entry the caller holds may predate paths the live screen stored meanwhile.
        val current = withContext(Dispatchers.IO) { PcStore(context).get(pc.pcId) } ?: pc
        val list = api.commands(current) ?: return null
        withContext(Dispatchers.IO) { CommandPrefs(context).setWakeMacs(pc.pcId, list.wakeMacs) }
        return list
    }

    /**
     * Sends [name] ([label] as the PC calls it) to [pcId] off the main thread and hands what came of it to [onDone] on
     * the main thread. With [cancel] it calls off [name] (a restart counting down, due at [restartAt]) instead.
     */
    fun launch(
        context: Context,
        api: HomebaseApi,
        pcId: String,
        name: String,
        label: String,
        cancel: Boolean = false,
        restartAt: Long? = null,
        onDone: (Sent) -> Unit,
    ) {
        val app = context.applicationContext
        scope.launch {
            val sent = send(app, api, pcId, name, label, cancel, restartAt)
            withContext(Dispatchers.Main) { onDone(sent) }
        }
    }

    /**
     * Sends [name] to [pcId] and says how it went. Uses the clipboard key agreed with that PC (M-62), agreeing one first
     * when there is none; when the PC answers that it has no key for this phone or can't open the request (its key
     * differs), a new key is agreed and the request sent once more — with a new id, as every request has. A restart the
     * PC [CommandStates.SCHEDULED] gets an ongoing notification with [취소] ([CommandNotifier]).
     */
    suspend fun send(
        context: Context,
        api: HomebaseApi,
        pcId: String,
        name: String,
        label: String,
        cancel: Boolean = false,
        restartAt: Long? = null,
        budgetMillis: Long? = null,
    ): Sent {
        val pc = withContext(Dispatchers.IO) { PcStore(context).get(pcId) }
            ?: return Sent(CommandOutcome.Failed("목록에 없는 PC"), "이 PC는 더 이상 연결되어 있지 않습니다")
        val outcome = try {
            deliver(context, api, pc, name, cancel, budgetMillis)
        } catch (e: CancellationException) {
            throw e
        } catch (_: UnauthorizedException) {
            CommandOutcome.Unpaired
        } catch (_: UnconfirmedException) {
            CommandOutcome.Unconfirmed
        } catch (e: Exception) {
            Log.i(TAG, "command to ${pc.name} failed: ${e.javaClass.simpleName}")
            CommandOutcome.Unreachable
        }
        val line = CommandText.resultLine(pc.displayName, name, label, outcome, cancel)
        withContext(Dispatchers.IO) {
            record(context, pc, if (cancel) CommandText.CANCEL_LABEL else label, outcome)
            updateNotification(context, pc, cancel, outcome, line, restartAt)
        }
        return Sent(outcome, line)
    }

    private suspend fun deliver(context: Context, api: HomebaseApi, pc: PcEntry, name: String, cancel: Boolean, budgetMillis: Long?): CommandOutcome {
        val stored = ClipCrypto.decodeKey(pc.clipKey)
        val key = stored ?: ClipShare.keyFor(context, api, pc, stale = null) ?: return CommandOutcome.Unsupported
        val first = api.sendCommand(pc, key, name, cancel, budgetMillis)
        if (stored == null || !first.needsNewKey) return first
        Log.i(TAG, "clipboard key with ${pc.name} no longer matches; agreeing a new one")
        val fresh = ClipShare.keyFor(context, api, pc, stale = key) ?: return CommandOutcome.Unsupported
        return api.sendCommand(pc, fresh, name, cancel, budgetMillis)
    }

    /** "보낸 명령" in the notification center: which PC, which command, how it went. */
    private fun record(context: Context, pc: PcEntry, label: String, outcome: CommandOutcome) {
        try {
            val entry = CommandHistory.sent(
                ClipProtocol.newId(), pc.pcId, pc.name, label, CommandText.historyLine(outcome), pc.color, System.currentTimeMillis(),
            )
            PushHistory.record(context, entry)
        } catch (e: Exception) {
            Log.w(TAG, "command not recorded: ${e.javaClass.simpleName}")
        }
    }

    /** Keeps the restart notification true to what the PC said. */
    private fun updateNotification(context: Context, pc: PcEntry, cancel: Boolean, outcome: CommandOutcome, line: String, restartAt: Long?) {
        if (!cancel) {
            if (outcome is CommandOutcome.Ok && outcome.state == CommandStates.SCHEDULED) {
                val at = outcome.atMillis ?: (System.currentTimeMillis() + RemoteCommandProtocol.RESTART_DELAY_SECONDS * 1000L)
                CommandNotifier.scheduled(context, pc.pcId, pc.displayName, at)
            }
            return
        }
        val settled = outcome is CommandOutcome.Ok || (outcome is CommandOutcome.Refused && outcome.error == CommandErrors.NOTHING_PENDING)
        val timeLeft = restartAt != null && restartAt > System.currentTimeMillis()
        if (!settled && timeLeft && restartAt != null && CommandNotifier.isShowing(context, pc.pcId)) {
            // Not called off: the countdown and [취소] again, with what went wrong.
            CommandNotifier.scheduled(context, pc.pcId, pc.displayName, restartAt, note = line)
        } else {
            // Called off, nothing to call off, or no time left to try again: say so in place of the countdown.
            CommandNotifier.finished(context, pc.pcId, line)
        }
    }

    /** A result line in a toast; long ones stay a little longer. */
    fun toast(context: Context, text: String) {
        val app = context.applicationContext
        if (text.length > 30) {
            Toast.makeText(app, text, Toast.LENGTH_LONG).show()
        } else {
            Toast.makeText(app, text, Toast.LENGTH_SHORT).show()
        }
    }
}

/** Per PC, the MAC addresses it listed for Wake-on-LAN — remembered, since a sleeping PC can't be asked. */
class CommandPrefs(context: Context) {
    private val prefs = context.applicationContext.getSharedPreferences(NAME, Context.MODE_PRIVATE)

    fun wakeMacs(pcId: String): List<String> =
        prefs.getString(KEY_MACS + pcId, null)?.split(',')?.mapNotNull(WakeOnLan::normalize)?.distinct() ?: emptyList()

    /** What the PC listed last; nothing listed forgets what it listed before. */
    fun setWakeMacs(pcId: String, macs: List<String>) {
        val valid = macs.mapNotNull(WakeOnLan::normalize).distinct()
        if (valid == wakeMacs(pcId)) return
        val editor = prefs.edit()
        if (valid.isEmpty()) editor.remove(KEY_MACS + pcId) else editor.putString(KEY_MACS + pcId, valid.joinToString(","))
        editor.apply()
    }

    /** Which of [pcIds] can be woken from this phone. */
    fun wakeable(pcIds: Collection<String>): Set<String> = pcIds.filter { wakeMacs(it).isNotEmpty() }.toSet()

    /** A PC deleted from the app. */
    fun forget(pcId: String) = prefs.edit().remove(KEY_MACS + pcId).apply()

    private companion object {
        const val NAME = "homebase.commands"
        const val KEY_MACS = "macs."
    }
}

/**
 * The notification of a restart counting down on a PC (DECISIONS M-69): "집 PC: 60초 뒤 다시 시작합니다" with the time
 * left and [취소], in a channel of its own. It goes by itself when the time is up; a cancel replaces it with what came
 * of it. The PC tells the phone when it is back (a normal push).
 */
internal object CommandNotifier {
    private const val CHANNEL_ID = "hb.command"
    private const val TAG = "hb.command"

    /** How long a "cancelled" note stays: it reports something done. */
    private const val DONE_TIMEOUT_MS = 15_000L

    private fun id(pcId: String): Int = "restart:$pcId".hashCode()

    /** [pcName] restarts at [atMillis]; [note] replaces the default line (a cancel that didn't go through). */
    fun scheduled(context: Context, pcId: String, pcName: String, atMillis: Long, note: String? = null) {
        val left = atMillis - System.currentTimeMillis()
        if (left <= 0) {
            cancel(context, pcId)
            return
        }
        if (!ensureChannel(context)) return
        val seconds = (left + 999) / 1000
        val text = note ?: "저장하지 않은 작업은 사라질 수 있습니다. PC 앞에서도 취소할 수 있습니다."
        val notification = builder(context, pcId)
            .setContentTitle("${pcName}: ${seconds}초 뒤 다시 시작합니다")
            .setContentText(text)
            .setStyle(NotificationCompat.BigTextStyle().bigText(text))
            .setWhen(atMillis)
            .setShowWhen(true)
            .setUsesChronometer(true)
            .setChronometerCountDown(true)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setTimeoutAfter(left)
            .addAction(0, "취소", cancelIntent(context, pcId, pcName, atMillis))
            .build()
        post(context, pcId, notification)
    }

    /** [취소] was tapped: say so until the PC answers. */
    fun cancelling(context: Context, pcId: String, pcName: String, atMillis: Long) {
        if (!ensureChannel(context)) return
        val left = atMillis - System.currentTimeMillis()
        val notification = builder(context, pcId)
            .setContentTitle("${pcName}: 다시 시작을 취소하는 중…")
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .apply { if (left > 0) setTimeoutAfter(left) }
            .build()
        post(context, pcId, notification)
    }

    /** What came of a cancel, in place of the countdown when it is still showing; gone after a moment. */
    fun finished(context: Context, pcId: String, text: String) {
        if (!isShowing(context, pcId) || !ensureChannel(context)) return
        val notification = builder(context, pcId)
            .setContentTitle(text)
            .setStyle(NotificationCompat.BigTextStyle().bigText(text))
            .setOnlyAlertOnce(true)
            .setAutoCancel(true)
            .setTimeoutAfter(DONE_TIMEOUT_MS)
            .build()
        post(context, pcId, notification)
    }

    fun isShowing(context: Context, pcId: String): Boolean = try {
        context.getSystemService(NotificationManager::class.java)?.activeNotifications
            ?.any { it.tag == TAG && it.id == id(pcId) } == true
    } catch (_: Exception) {
        false
    }

    fun cancel(context: Context, pcId: String) {
        NotificationManagerCompat.from(context).cancel(TAG, id(pcId))
    }

    private fun builder(context: Context, pcId: String): NotificationCompat.Builder =
        NotificationCompat.Builder(context, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_notification)
            .setCategory(NotificationCompat.CATEGORY_STATUS)
            .setPriority(NotificationCompat.PRIORITY_DEFAULT)
            .setContentIntent(openPc(context, pcId))

    /** A tap opens the PC's screen, like a push from it. */
    private fun openPc(context: Context, pcId: String): PendingIntent = PendingIntent.getActivity(
        context,
        id(pcId),
        Intent(context, MainActivity::class.java)
            .putExtra(PushNotifier.EXTRA_PC_ID, pcId)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP),
        PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
    )

    private fun cancelIntent(context: Context, pcId: String, pcName: String, atMillis: Long): PendingIntent = PendingIntent.getBroadcast(
        context,
        id(pcId),
        Intent(context, RestartCancelReceiver::class.java)
            .setAction(RestartCancelReceiver.ACTION)
            .putExtra(RestartCancelReceiver.EXTRA_PC_ID, pcId)
            .putExtra(RestartCancelReceiver.EXTRA_PC_NAME, pcName)
            .putExtra(RestartCancelReceiver.EXTRA_AT, atMillis),
        PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
    )

    /** False when notifications are off for the app or this channel; the restart goes on either way. */
    private fun ensureChannel(context: Context): Boolean {
        if (!PushNotifier.canNotify(context)) return false
        val nm = context.getSystemService(NotificationManager::class.java) ?: return false
        val existing = nm.getNotificationChannel(CHANNEL_ID)
        if (existing != null) return existing.importance != NotificationManager.IMPORTANCE_NONE
        nm.createNotificationChannel(
            NotificationChannel(CHANNEL_ID, "원격 명령", NotificationManager.IMPORTANCE_DEFAULT).apply {
                description = "휴대폰에서 PC를 다시 시작하게 했을 때, 남은 시간과 취소"
                setShowBadge(false)
                enableVibration(false)
                setSound(null, null)
            },
        )
        return true
    }

    private fun post(context: Context, pcId: String, notification: android.app.Notification) {
        try {
            NotificationManagerCompat.from(context).notify(TAG, id(pcId), notification)
        } catch (_: SecurityException) {
            // Notification permission withdrawn meanwhile: the restart goes on regardless.
        }
    }
}

/**
 * [취소] on a restart's notification: asks the PC to call it off (`POST /api/v1/commands/cancel`, sealed like the
 * request) off the main thread and puts what came of it in the notification. Only this app's own notification action
 * reaches it (not exported).
 */
class RestartCancelReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != ACTION) return
        val pcId = intent.getStringExtra(EXTRA_PC_ID) ?: return
        val pcName = intent.getStringExtra(EXTRA_PC_NAME).orEmpty()
        val at = intent.getLongExtra(EXTRA_AT, 0L)
        val app = context.applicationContext
        CommandNotifier.cancelling(app, pcId, pcName, at)
        val pending = goAsync()
        RemoteCommands.scope.launch {
            try {
                // A broadcast may run ~10 s: the budget bounds each blocking call itself.
                RemoteCommands.send(
                    app,
                    HomebaseApi(networks = NetworkProbe(app)::current),
                    pcId,
                    RemoteCommandNames.RESTART,
                    RemoteCommandNames.label(RemoteCommandNames.RESTART),
                    cancel = true,
                    restartAt = at.takeIf { it > 0 },
                    budgetMillis = 7_000,
                )
                PushHistory.awaitWrites(1_000)
            } catch (e: Exception) {
                Log.w("HomebaseCommand", "restart cancel failed: ${e.javaClass.simpleName}")
            } finally {
                pending.finish()
            }
        }
    }

    companion object {
        const val ACTION = "io.github.jeiel85.homebase.action.CANCEL_RESTART"
        const val EXTRA_PC_ID = "io.github.jeiel85.homebase.COMMAND_PC_ID"
        const val EXTRA_PC_NAME = "io.github.jeiel85.homebase.COMMAND_PC_NAME"
        const val EXTRA_AT = "io.github.jeiel85.homebase.COMMAND_AT"
    }
}
