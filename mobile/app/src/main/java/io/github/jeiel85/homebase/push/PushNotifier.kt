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
import android.os.Bundle
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
 * [AlertProfile] for it (DECISIONS M-37). Bundled notifications all share one Android group with a
 * summary — one "Homebase" stack in the shade whatever PC or app sent them (M-52) — and each source
 * keeps at most [MAX_IN_BUNDLE] of them there.
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

    /** Newest notifications one source keeps in the shade; older ones stay in the notification center. */
    internal const val MAX_IN_BUNDLE = 8

    /**
     * Newest notifications the Homebase stack keeps in the shade, all sources together: well under
     * Android's limit of about 50 active notifications per app, past which new ones are dropped.
     */
    internal const val MAX_IN_STACK = 24

    /** The one Android group every bundled Homebase notification joins (M-52). */
    private const val STACK_GROUP = "hb.stack"

    /** Extras a stacked notification carries for the summary and the per-source limit. */
    private const val EXTRA_BUNDLE_KEY = "io.github.jeiel85.homebase.BUNDLE_KEY"
    private const val EXTRA_SENDER = "io.github.jeiel85.homebase.SENDER"

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
        // A permission prompt stands on its own so its Allow / Deny buttons are always in reach (M-48).
        val stacked = profile.bundle && message.approval == null
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
        if (stacked) {
            builder.setGroup(STACK_GROUP)
                .addExtras(
                    Bundle().apply {
                        putString(EXTRA_BUNDLE_KEY, bundleKey(message))
                        putString(EXTRA_SENDER, NotificationLook.sender(message))
                    },
                )
                // Only the notifications themselves ring; the summary updates silently.
                .setGroupAlertBehavior(NotificationCompat.GROUP_ALERT_CHILDREN)
                // Swiping the last one away must take the summary with it.
                .setDeleteIntent(dismissed(context))
        }
        val id = message.id.hashCode()
        message.approval?.let { addApprovalActions(context, builder, message, it, id) }
        if (!post(context, null, id, builder.build())) return false
        if (stacked) updateStack(context, channelId, message, id, mark, badge)
        return true
    }

    /**
     * Allow / Deny on a Claude Code permission prompt (DECISIONS M-48). Both need the phone unlocked
     * (Android 12+), so nobody can answer from the lock screen. The notification stays until it is
     * answered and goes away by itself when the PC stops waiting.
     */
    private fun addApprovalActions(context: Context, builder: NotificationCompat.Builder, message: PushMessage, approval: PushApproval, id: Int) {
        fun action(decision: String, label: String, request: Int): NotificationCompat.Action {
            val intent = Intent(context, ApprovalActionReceiver::class.java)
                .setAction(ApprovalActionReceiver.ACTION)
                .putExtra(ApprovalActionReceiver.EXTRA_PC_ID, message.pcId)
                .putExtra(ApprovalActionReceiver.EXTRA_APPROVAL_ID, approval.id)
                .putExtra(ApprovalActionReceiver.EXTRA_DECISION, decision)
                .putExtra(ApprovalActionReceiver.EXTRA_NOTIFICATION_ID, id)
            val pending = PendingIntent.getBroadcast(
                context, request, intent, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
            )
            return NotificationCompat.Action.Builder(0, label, pending).setAuthenticationRequired(true).build()
        }
        builder.addAction(action(ApprovalActionReceiver.ALLOW, "허용", id * 2))
            .addAction(action(ApprovalActionReceiver.DENY, "거부", id * 2 + 1))
            .setAutoCancel(false)
        approval.expiresAtMillis?.let { builder.setTimeoutAfter((it - System.currentTimeMillis()).coerceAtLeast(1_000)) }
    }

    /** Replaces an answered prompt's notification with what became of it; it then times out on its own. */
    fun showApprovalResult(context: Context, id: Int, text: String) {
        if (!canNotify(context)) return
        val nm = context.getSystemService(NotificationManager::class.java)
        val current = try {
            nm.activeNotifications.firstOrNull { it.id == id && it.tag == null }?.notification
        } catch (_: Exception) {
            null
        } ?: return
        val updated = NotificationCompat.Builder(context, current)
            .clearActions()
            .setContentText(text)
            .setStyle(NotificationCompat.BigTextStyle().bigText(text))
            .setOnlyAlertOnce(true)
            .setAutoCancel(true)
            .setTimeoutAfter(15_000)
            .build()
        post(context, null, id, updated)
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

    /** What [MAX_IN_BUNDLE] counts by (see [NotificationPriority.bundleKey]). */
    private fun bundleKey(message: PushMessage): String =
        NotificationPriority.bundleKey(message.category, message.pcId, message.source)

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
     * Keeps the Homebase stack in order after a notification joined it: drops the oldest past
     * [MAX_IN_BUNDLE] of its source and past [MAX_IN_STACK] in all, then adds or updates the summary
     * once the stack holds two or more. The notification just posted may not be listed as active yet —
     * `notify` is queued — so it is counted by hand.
     */
    private fun updateStack(
        context: Context,
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
            .filter { it.notification.group == STACK_GROUP && !it.isSummary() && !(it.tag == null && it.id == postedId) }
            .sortedByDescending { it.postTime }
        val dropped = NotificationStack.overflow(
            others.map { it.notification.extras.getString(EXTRA_BUNDLE_KEY).orEmpty() },
            bundleKey(message),
            MAX_IN_BUNDLE,
            MAX_IN_STACK,
        )
        others.filterIndexed { i, _ -> i in dropped }.forEach { nm.cancel(it.tag, it.id) }
        val kept = others.filterIndexed { i, _ -> i !in dropped }
        if (kept.isEmpty()) return // one notification needs no summary
        val count = kept.size + 1
        val senders = listOf(NotificationLook.sender(message)) +
            kept.map { it.notification.extras.getString(EXTRA_SENDER).orEmpty() }
        val lines = listOf(NotificationStack.line(NotificationLook.header(message), NotificationLook.title(message), message.body)) +
            kept.map {
                val extras = it.notification.extras
                NotificationStack.line(
                    extras.getCharSequence(Notification.EXTRA_SUB_TEXT)?.toString(),
                    extras.getCharSequence(Notification.EXTRA_TITLE)?.toString().orEmpty(),
                    extras.getCharSequence(Notification.EXTRA_TEXT)?.toString(),
                )
            }
        val from = NotificationStack.senders(senders)
        val title = "새 알림 ${count}건"
        val style = NotificationCompat.InboxStyle().setBigContentTitle(title).setSummaryText(from)
        lines.take(SUMMARY_LINES).forEach { style.addLine(it) }
        val center = PendingIntent.getActivity(
            context,
            REQUEST_CENTER,
            Intent(context, MainActivity::class.java)
                .setAction(ACTION_OPEN_CENTER)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        // Android files a stack under its summary — alerting or silent — so the summary goes in the
        // channel of the stack's most important notification, not of the newest.
        val summaryChannel = (listOf(channelId) + kept.map { it.notification.channelId }).distinct()
            .maxByOrNull { nm.getNotificationChannel(it)?.importance ?: NotificationManager.IMPORTANCE_NONE } ?: channelId
        // One sender's stack wears its badge; a mixed one keeps the app's own look.
        val oneSender = senders.distinct().size == 1
        val summary = NotificationCompat.Builder(context, summaryChannel)
            .setSmallIcon(R.drawable.ic_notification)
            .apply { if (oneSender) setColor(mark.argb).setLargeIcon(badge) }
            .setContentTitle(title)
            .setContentText(from)
            .setStyle(style)
            .setSubText(from)
            .setNumber(count)
            .setCategory(category(message))
            .setGroup(STACK_GROUP)
            .setGroupSummary(true)
            .setGroupAlertBehavior(NotificationCompat.GROUP_ALERT_CHILDREN)
            .setOnlyAlertOnce(true)
            .setContentIntent(center)
            .setAutoCancel(true)
            .build()
        post(context, SUMMARY_TAG, STACK_GROUP.hashCode(), summary)
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
