package io.github.jeiel85.homebase.push

import android.Manifest
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationChannelGroup
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.Typeface
import android.os.Build
import android.service.notification.StatusBarNotification
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import io.github.jeiel85.homebase.MainActivity
import io.github.jeiel85.homebase.R
import io.github.jeiel85.homebase.data.PcMark
import java.security.MessageDigest
import java.time.OffsetDateTime

/**
 * Shows decrypted pushes. Channels are split per PC (a channel group named after the PC) and per
 * source — each Windows app for forwarded toasts, each alert kind otherwise — so the user can mute
 * one app on one PC from Android's notification settings without losing the rest (DECISIONS M-19).
 *
 * How each one appears — popup, sound, vibration, bundling, or not at all — follows the user's
 * [AlertProfile] for it (DECISIONS M-37). Bundled notifications share a group with a summary, so a
 * burst from one app folds into one entry in the shade instead of a long list.
 *
 * Each one carries its sender's badge and colour, with the sender first in the header line, so
 * notifications from two PCs are told apart without reading them (see [NotificationLook], M-38).
 */
object PushNotifier {
    const val EXTRA_PC_ID = "io.github.jeiel85.homebase.PC_ID"

    /**
     * Action of a bundle summary's tap: opens the notification center rather than one PC. An action,
     * not an extra, so its PendingIntent can never be mistaken for a PC's (extras are not compared).
     */
    const val ACTION_OPEN_CENTER = "io.github.jeiel85.homebase.action.OPEN_NOTIFICATIONS"

    /** Newest notifications a bundle keeps in the shade; older ones stay in the notification center. */
    internal const val MAX_IN_BUNDLE = 8

    /** Lines a bundle's summary lists (lock screen, watches, Android versions that show the summary). */
    private const val SUMMARY_LINES = 5

    private const val SUMMARY_TAG = "hb.bundle"

    fun canNotify(context: Context): Boolean {
        if (Build.VERSION.SDK_INT >= 33 &&
            ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            return false
        }
        return NotificationManagerCompat.from(context).areNotificationsEnabled()
    }

    /**
     * Posts [message] as [profile] says. False when nothing reached the shade: Android notifications
     * blocked, this app/PC turned off in Android's settings, or [AlertProfile.shade] off (the
     * notification center still has it).
     */
    fun show(context: Context, message: PushMessage, profile: AlertProfile, mark: PcMark): Boolean {
        if (!profile.shade || !canNotify(context)) return false
        val channelId = ensureChannel(context, message, profile) ?: return false
        val open = PendingIntent.getActivity(
            context,
            message.pcId.hashCode(),
            Intent(context, MainActivity::class.java)
                .putExtra(EXTRA_PC_ID, message.pcId)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        val group = if (profile.bundle) groupKey(message) else null
        // Who sent it leads the header and the badge; the title keeps only what the notification says (M-38).
        val badge = badge(context, mark)
        val builder = NotificationCompat.Builder(context, channelId)
            .setSmallIcon(R.drawable.ic_notification)
            .setColor(mark.argb)
            .setLargeIcon(badge)
            .setContentTitle(NotificationLook.title(message))
            .setContentText(message.body)
            .setStyle(message.body?.let { NotificationCompat.BigTextStyle().bigText(it) })
            .setSubText(NotificationLook.header(message))
            .setWhen(epochMillis(message.ts))
            .setShowWhen(true)
            .setNumber(if (message.count > 1) message.count else 0)
            .setCategory(category(message))
            .setPriority(priority(profile))
            .setContentIntent(open)
            .setAutoCancel(true)
        if (group != null) {
            builder.setGroup(group)
                // Only the notifications themselves ring; the summary updates silently.
                .setGroupAlertBehavior(NotificationCompat.GROUP_ALERT_CHILDREN)
                // Swiping the last one away must take the summary with it.
                .setDeleteIntent(dismissed(context))
        }
        val id = message.id.hashCode()
        if (!post(context, null, id, builder.build())) return false
        if (group != null) updateBundle(context, group, channelId, message, id, mark, badge)
        return true
    }

    /**
     * Removes bundle summaries left without notifications — the user swiped them away one by one or
     * opened them. Android would otherwise keep showing the summary on its own.
     */
    fun tidy(context: Context) {
        val nm = context.getSystemService(NotificationManager::class.java)
        val active = try {
            nm.activeNotifications
        } catch (_: Exception) {
            return
        }
        val withChildren = active.filterNot { it.isSummary() }.mapNotNull { it.notification.group }.toSet()
        active.filter { it.isSummary() && it.tag == SUMMARY_TAG && it.notification.group !in withChildren }
            .forEach { nm.cancel(it.tag, it.id) }
    }

    /**
     * Renames a PC's channel group in Android's notification settings after the user changed its alias.
     * Nothing to do before the PC's first notification: the group is created, with the alias, then.
     */
    fun renamePc(context: Context, pcId: String, displayName: String) {
        val nm = context.getSystemService(NotificationManager::class.java)
        if (nm.notificationChannelGroups.any { it.id == groupId(pcId) }) {
            nm.createNotificationChannelGroup(NotificationChannelGroup(groupId(pcId), displayName))
        }
    }

    /** Removes a PC's channels when it is deleted from the app. */
    fun forgetPc(context: Context, pcId: String) {
        if (Build.VERSION.SDK_INT < 26) return
        context.getSystemService(NotificationManager::class.java).deleteNotificationChannelGroup(groupId(pcId))
    }

    /** One PC's channels for one source share this prefix; the [AlertProfile.channelSuffix] follows it. */
    fun channelId(pcId: String, source: String): String = "hb.$pcId.${hash(source.lowercase())}"

    /** The channel for [source] on [pcId] shown as [profile]. */
    fun channelId(pcId: String, source: String, profile: AlertProfile): String = "${channelId(pcId, source)}.${profile.channelSuffix}"

    /** Human name for a channel: the Windows app for toasts, a Korean label for alert kinds. */
    fun channelName(message: PushMessage): String =
        if (message.isToast) "${message.source} (Windows 알림)" else CATEGORY_LABELS[message.source] ?: message.source

    private fun groupId(pcId: String) = "hb.pc.$pcId"

    /** The notification group of a bundle (see [NotificationPriority.bundleKey]). */
    private fun groupKey(message: PushMessage): String =
        "hb.b.${hash(NotificationPriority.bundleKey(message.category, message.pcId, message.source))}"

    /**
     * The channel to post [message] in, created when missing. Null when the user turned this source off
     * in Android's settings: that stays off whatever the in-app priority says (M-19).
     *
     * The previous channel of the same PC and source (another priority, or the single channel from
     * before M-37) is deleted, so Android's settings list one channel per app and PC.
     */
    private fun ensureChannel(context: Context, message: PushMessage, profile: AlertProfile): String? {
        val nm = context.getSystemService(NotificationManager::class.java)
        nm.createNotificationChannelGroup(NotificationChannelGroup(groupId(message.pcId), message.pcName))
        val base = channelId(message.pcId, message.source)
        val id = channelId(message.pcId, message.source, profile)
        val siblings = nm.notificationChannels.filter { it.id == base || it.id.startsWith("$base.") }
        if (siblings.any { it.importance == NotificationManager.IMPORTANCE_NONE }) return null
        if (siblings.none { it.id == id }) {
            nm.createNotificationChannel(
                NotificationChannel(id, channelName(message), importance(profile)).apply {
                    group = groupId(message.pcId)
                    description = when {
                        message.isToast -> "${message.pcName}에서 전달한 ${message.source} 알림"
                        message.category == NotificationGroups.DEVICE -> "${message.pcName}이(가) 전달한 ${message.source} 알림"
                        else -> "${message.pcName}의 ${channelName(message)} 알림"
                    } + " · 앱의 알림 우선순위: ${NotificationPriority.describe(profile)}"
                    if (!profile.sound) setSound(null, null)
                    enableVibration(profile.vibrate)
                },
            )
        }
        siblings.filter { it.id != id }.forEach { nm.deleteNotificationChannel(it.id) }
        return id
    }

    /**
     * Adds a summary over the bundle once it holds two or more, and keeps it at [MAX_IN_BUNDLE]
     * (dropping the oldest from the shade). The notification just posted may not be listed as
     * active yet — `notify` is queued — so it is counted by hand.
     */
    private fun updateBundle(
        context: Context,
        group: String,
        channelId: String,
        message: PushMessage,
        postedId: Int,
        mark: PcMark,
        badge: Bitmap,
    ) {
        val nm = context.getSystemService(NotificationManager::class.java)
        val active = try {
            nm.activeNotifications
        } catch (_: Exception) {
            return
        }
        val others = active
            .filter { it.notification.group == group && !it.isSummary() && !(it.tag == null && it.id == postedId) }
            .sortedByDescending { it.postTime }
        others.drop(MAX_IN_BUNDLE - 1).forEach { nm.cancel(it.tag, it.id) }
        val kept = others.take(MAX_IN_BUNDLE - 1)
        if (kept.isEmpty()) return // one notification needs no summary
        val count = kept.size + 1
        val title = bundleTitle(message)
        val lines = listOf(NotificationPriority.shortLine(message.title, message.body, message.source)) +
            kept.map {
                val extras = it.notification.extras
                NotificationPriority.shortLine(
                    extras.getCharSequence(Notification.EXTRA_TITLE)?.toString().orEmpty(),
                    extras.getCharSequence(Notification.EXTRA_TEXT)?.toString(),
                    message.source,
                )
            }
        val style = NotificationCompat.InboxStyle().setBigContentTitle(title).setSummaryText("${count}건")
        lines.take(SUMMARY_LINES).forEach { style.addLine(it) }
        val center = PendingIntent.getActivity(
            context,
            REQUEST_CENTER,
            Intent(context, MainActivity::class.java)
                .setAction(ACTION_OPEN_CENTER)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        val summary = NotificationCompat.Builder(context, channelId)
            .setSmallIcon(R.drawable.ic_notification)
            .setColor(mark.argb)
            .setLargeIcon(badge)
            .setContentTitle(title)
            .setContentText("새 알림 ${count}건")
            .setStyle(style)
            .setSubText(NotificationLook.sender(message))
            .setNumber(count)
            .setCategory(category(message))
            .setGroup(group)
            .setGroupSummary(true)
            .setGroupAlertBehavior(NotificationCompat.GROUP_ALERT_CHILDREN)
            .setOnlyAlertOnce(true)
            .setContentIntent(center)
            .setAutoCancel(true)
            .build()
        post(context, SUMMARY_TAG, group.hashCode(), summary)
    }

    /**
     * What a bundle is called: the app for app notifications, the alert kind otherwise. Who sent them
     * is the summary's header, as on the notifications themselves.
     */
    private fun bundleTitle(message: PushMessage): String = when {
        message.category == NotificationGroups.DEVICE -> NotificationLook.appOf(message.source).ifEmpty { message.source }
        NotificationPriority.hasApps(message.category) -> message.source
        else -> channelName(message)
    }

    /**
     * The round badge shown as the notification's large icon: the mark's colour with its letter in
     * white, at the size Android shows large icons.
     */
    private fun badge(context: Context, mark: PcMark): Bitmap {
        val size = context.resources.getDimensionPixelSize(android.R.dimen.notification_large_icon_width).coerceAtLeast(48)
        val bitmap = Bitmap.createBitmap(size, size, Bitmap.Config.ARGB_8888)
        val canvas = Canvas(bitmap)
        val half = size / 2f
        canvas.drawCircle(half, half, half, Paint(Paint.ANTI_ALIAS_FLAG).apply { color = mark.argb })
        val text = Paint(Paint.ANTI_ALIAS_FLAG).apply {
            color = Color.WHITE
            textAlign = Paint.Align.CENTER
            typeface = Typeface.DEFAULT_BOLD
            textSize = size * 0.46f
        }
        canvas.drawText(mark.letter, half, half - (text.descent() + text.ascent()) / 2, text)
        return bitmap
    }

    private fun post(context: Context, tag: String?, id: Int, notification: Notification): Boolean = try {
        NotificationManagerCompat.from(context).notify(tag, id, notification)
        true
    } catch (_: SecurityException) {
        // Permission revoked between the check and the post.
        false
    }

    private fun dismissed(context: Context): PendingIntent = PendingIntent.getBroadcast(
        context,
        REQUEST_DISMISSED,
        Intent(context, NotificationDismissReceiver::class.java),
        PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
    )

    private fun StatusBarNotification.isSummary(): Boolean = notification.flags and Notification.FLAG_GROUP_SUMMARY != 0

    private fun category(message: PushMessage): String =
        if (NotificationPriority.hasApps(message.category)) NotificationCompat.CATEGORY_MESSAGE else NotificationCompat.CATEGORY_STATUS

    private fun importance(profile: AlertProfile): Int = when (profile.importance) {
        AlertProfile.Importance.HIGH -> NotificationManager.IMPORTANCE_HIGH
        AlertProfile.Importance.DEFAULT -> NotificationManager.IMPORTANCE_DEFAULT
        AlertProfile.Importance.LOW -> NotificationManager.IMPORTANCE_LOW
    }

    /** Only read before Android 8, which has no channels; kept consistent with the channel. */
    private fun priority(profile: AlertProfile): Int = when (profile.importance) {
        AlertProfile.Importance.HIGH -> NotificationCompat.PRIORITY_HIGH
        AlertProfile.Importance.DEFAULT -> NotificationCompat.PRIORITY_DEFAULT
        AlertProfile.Importance.LOW -> NotificationCompat.PRIORITY_LOW
    }

    private fun epochMillis(ts: String): Long =
        runCatching { OffsetDateTime.parse(ts).toInstant().toEpochMilli() }.getOrDefault(System.currentTimeMillis())

    private fun hash(s: String): String =
        MessageDigest.getInstance("SHA-256").digest(s.toByteArray(Charsets.UTF_8)).take(6).joinToString("") { "%02x".format(it) }

    private const val REQUEST_CENTER = 0x4842_0001
    private const val REQUEST_DISMISSED = 0x4842_0002

    internal val CATEGORY_LABELS = mapOf(
        "process_watch" to "프로세스 감시",
        "service_watch" to "서비스 감시",
        "http_monitor" to "HTTP 감시",
        "port_monitor" to "포트 감시",
        "event_log" to "이벤트 로그",
        "advisor" to "PC 상태 조언",
        "boot" to "부팅",
        "test" to "테스트",
    )
}

/** A bundled notification was swiped away: drop its summary if it was the last one (see [PushNotifier.tidy]). */
class NotificationDismissReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        PushNotifier.tidy(context)
    }
}
