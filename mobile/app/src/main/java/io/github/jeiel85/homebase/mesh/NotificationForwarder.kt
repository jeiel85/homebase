package io.github.jeiel85.homebase.mesh

import android.app.Notification
import android.content.ComponentName
import android.content.Context
import android.provider.Settings
import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification
import android.util.Log
import io.github.jeiel85.homebase.data.PcStore
import io.github.jeiel85.homebase.net.DeviceNotification
import io.github.jeiel85.homebase.net.HomebaseApi
import io.github.jeiel85.homebase.net.NetworkProbe
import io.github.jeiel85.homebase.push.PushRegistrar
import kotlinx.coroutines.launch
import java.time.Instant
import java.util.UUID

/**
 * Sends this phone's notifications to every paired PC (DECISIONS M-28), which shows them and passes
 * them on to its other phones. Needs Android's "notification access" for this app *and* the switch
 * in Settings → 다른 기기 (off by default); with either off, nothing leaves the phone.
 *
 * Skipped: this app's own notifications (they came from a PC), ongoing ones (music, navigation,
 * downloads), group summaries, empty ones, and apps the user left out ([MeshPrefs.excluded]).
 * A notification an app re-posts unchanged (Android updates them often) is sent once.
 * A PC that can't be reached misses it — there is no queue, notifications go stale fast.
 */
class NotificationForwarder : NotificationListenerService() {

    override fun onNotificationPosted(sbn: StatusBarNotification) {
        val prefs = MeshPrefs(this)
        if (!prefs.forwardEnabled()) return
        val n = extract(this, sbn) ?: return
        prefs.rememberApp(sbn.packageName, n.app)
        if (sbn.packageName in prefs.excluded()) return
        if (!Recent.firstTime(sbn.key, n.title, n.body)) return

        val app = applicationContext
        PushRegistrar.scope.launch {
            val api = HomebaseApi(networks = NetworkProbe(app)::current)
            val store = PcStore(app)
            for (pc in store.all()) {
                try {
                    if (!api.sendNotification(pc, n)) Log.i(TAG, "${pc.name} can't relay notifications (old version or forwarding off)")
                } catch (e: Exception) {
                    Log.i(TAG, "notification to ${pc.name} not delivered: ${e.message}")
                }
            }
        }
    }

    companion object {
        private const val TAG = "HomebaseForward"

        /** Whether Android lets this app read notifications (Settings → notification access). */
        fun hasAccess(context: Context): Boolean {
            val flat = Settings.Secure.getString(context.contentResolver, "enabled_notification_listeners") ?: return false
            val me = ComponentName(context, NotificationForwarder::class.java)
            return flat.split(':').any { ComponentName.unflattenFromString(it) == me }
        }

        /** The notification as sent to the PC, or null when it is not worth sending. */
        internal fun extract(context: Context, sbn: StatusBarNotification): DeviceNotification? {
            if (sbn.packageName == context.packageName) return null
            val notification = sbn.notification ?: return null
            if (sbn.isOngoing || notification.flags and Notification.FLAG_GROUP_SUMMARY != 0) return null
            val extras = notification.extras ?: return null
            val title = extras.getCharSequence(Notification.EXTRA_TITLE)?.toString()?.trim().orEmpty()
            val body = (extras.getCharSequence(Notification.EXTRA_BIG_TEXT) ?: extras.getCharSequence(Notification.EXTRA_TEXT))
                ?.toString()?.trim()?.ifBlank { null }
            if (title.isEmpty() && body == null) return null
            val label = try {
                val pm = context.packageManager
                pm.getApplicationLabel(pm.getApplicationInfo(sbn.packageName, 0)).toString()
            } catch (_: Exception) {
                sbn.packageName
            }
            return DeviceNotification(
                id = UUID.randomUUID().toString().replace("-", ""),
                app = label,
                title = title.ifEmpty { label },
                body = body,
                ts = Instant.ofEpochMilli(sbn.postTime).toString(),
            )
        }
    }

    /** Remembers what was sent in the last minute, per notification key and text. */
    internal object Recent {
        private const val WINDOW_MS = 60_000L
        private val sent = LinkedHashMap<String, Long>()

        @Synchronized
        fun firstTime(key: String, title: String, body: String?, now: Long = System.currentTimeMillis()): Boolean {
            sent.entries.removeAll { now - it.value > WINDOW_MS }
            val k = "$key|$title|${body.orEmpty()}"
            if (sent.containsKey(k)) return false
            sent[k] = now
            return true
        }
    }
}
