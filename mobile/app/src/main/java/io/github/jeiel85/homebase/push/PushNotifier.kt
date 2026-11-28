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
import io.github.jeiel85.homebase.data.PcMarks
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

    /**
     * Action of a notification's own tap (DECISIONS M-81): opens the notification center on its entry, unfolded, rather
     * than its PC's screen (the entry's "PC 열기" leads there). [EXTRA_PC_ID], [EXTRA_PUSH_ID] and [EXTRA_CATEGORY] name it.
     */
    const val ACTION_OPEN_ENTRY = "io.github.jeiel85.homebase.action.OPEN_NOTIFICATION_ENTRY"
    const val EXTRA_PUSH_ID = "io.github.jeiel85.homebase.PUSH_ID"
    const val EXTRA_CATEGORY = "io.github.jeiel85.homebase.CATEGORY"

    /**
     * Action of an alert's "AI 조언" button (DECISIONS M-61): opens the PC and asks its AI at once.
     * [EXTRA_NOTIFICATION_ID] is the notification to take away.
     */
    const val ACTION_ASK_ADVICE = "io.github.jeiel85.homebase.action.ASK_ADVICE"
    const val EXTRA_NOTIFICATION_ID = "io.github.jeiel85.homebase.NOTIFICATION_ID"

    /** Newest notifications one source keeps in the shade; older ones stay in the notification center. */
    internal const val MAX_IN_BUNDLE = 8

    /**
     * Newest notifications the Homebase stack keeps in the shade, all sources together: well under
     * Android's limit of about 50 active notifications per app, past which new ones are dropped.
     */
    internal const val MAX_IN_STACK = 24

    /** The one Android group every bundled Homebase notification joins (M-52). */
    private const val STACK_GROUP = "hb.stack"

    /** The per-source groups of v0.38 and earlier (M-37), moved into [STACK_GROUP] by [tidy]. */
    private const val OLD_GROUP_PREFIX = "hb.b."

    /** Extras a stacked notification carries for the summary and the per-source limit. */
    private const val EXTRA_BUNDLE_KEY = "io.github.jeiel85.homebase.BUNDLE_KEY"
    private const val EXTRA_SENDER = "io.github.jeiel85.homebase.SENDER"
    private const val EXTRA_MARK_LETTER = "io.github.jeiel85.homebase.MARK_LETTER"
    private const val EXTRA_MARK_COLOR = "io.github.jeiel85.homebase.MARK_COLOR"

    /** Lines a bundle's summary lists (lock screen, watches, Android versions that show the summary). */
    private const val SUMMARY_LINES = 5

    private const val SUMMARY_TAG = "hb.bundle"

    /** Badges drawn so far, by mark and size: a burst of one PC's notifications draws its badge once (#57). */
    private val badges = object : android.util.LruCache<String, Bitmap>(16) {}

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
        val id = message.id.hashCode()
        // One PendingIntent per notification (its id; the action tells it apart from "AI 조언"'s), so each tap opens its own entry.
        val open = PendingIntent.getActivity(
            context,
            id,
            Intent(context, MainActivity::class.java)
                .setAction(ACTION_OPEN_ENTRY)
                .putExtra(EXTRA_PC_ID, message.pcId)
                .putExtra(EXTRA_PUSH_ID, message.id)
                .putExtra(EXTRA_CATEGORY, message.category)
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
                        // So a summary rebuilt later (a notification dismissed) can still wear its badge.
                        putString(EXTRA_MARK_LETTER, mark.letter)
                        putInt(EXTRA_MARK_COLOR, mark.colorIndex)
                    },
                )
                // Only the notifications themselves ring; the summary updates silently.
                .setGroupAlertBehavior(NotificationCompat.GROUP_ALERT_CHILDREN)
                // Swiping the last one away must take the summary with it.
                .setDeleteIntent(dismissed(context))
        }
        message.approval?.let { addApprovalActions(context, builder, message, it, id) }
        if (message.category in NotificationGroups.ADVICE_ON) builder.addAction(0, "AI 조언", askAdvice(context, message.pcId, id))
        if (!post(context, null, id, builder.build())) return false
        if (stacked) updateStack(context, channelId, message, id, mark)
        return true
    }

    /** "AI 조언" on an alert: one PendingIntent per notification (its id), told apart from the tap by its action. */
    private fun askAdvice(context: Context, pcId: String, id: Int): PendingIntent = PendingIntent.getActivity(
        context,
        id,
        Intent(context, MainActivity::class.java)
            .setAction(ACTION_ASK_ADVICE)
            .putExtra(EXTRA_PC_ID, pcId)
            .putExtra(EXTRA_NOTIFICATION_ID, id)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP),
        PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
    )

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
     * Keeps the shade's bundles true to what is left in them, after notifications were swiped away or
     * opened (no push follows those):
     * - a summary left without notifications is removed — Android would keep showing it on its own;
     * - the Homebase stack's summary is rewritten from the notifications still in it, so its count and
     *   lines match (#69);
     * - notifications still in the per-source bundles of an earlier version are moved into the stack
     *   (#69). Their old summaries empty out and go on the next pass (the app runs two, see MainActivity).
     */
    fun tidy(context: Context) {
        val nm = context.getSystemService(NotificationManager::class.java)
        val active = try {
            nm.activeNotifications
        } catch (_: Exception) {
            return
        }
        if (moveOldBundles(context, active)) return // the moves are queued; the next pass sees them
        val withChildren = active.filterNot { it.isSummary() }.mapNotNull { it.notification.group }.toSet()
        active.filter { it.isSummary() && it.tag == SUMMARY_TAG && it.notification.group !in withChildren }
            .forEach { nm.cancel(it.tag, it.id) }
        val children = active.filter { it.notification.group == STACK_GROUP && !it.isSummary() }.sortedByDescending { it.postTime }
        if (children.isEmpty()) {
            StackDrops.reset(context) // the stack is gone: the next one starts its own count
            return
        }
        // No summary yet: one notification needs none; two or more (moved in from old bundles) get one.
        if (active.none { it.isStackSummary() } && children.size < 2) return
        writeSummary(context, nm, children.map(::childOf), StackDrops.get(context), children.first().notification.category)
    }

    /**
     * Notifications of the per-source bundles from before M-52 (`hb.b.…`) re-posted into the one stack,
     * silently and in place (same id). Their summaries are not cancelled here: cancelling a summary
     * takes its group's notifications with it, and the re-posts may not have landed yet. True when any moved.
     */
    private fun moveOldBundles(context: Context, active: Array<StatusBarNotification>): Boolean {
        val old = active.filter { !it.isSummary() && it.notification.group?.startsWith(OLD_GROUP_PREFIX) == true }
        old.forEach { sbn ->
            val n = sbn.notification
            val moved = Notification.Builder.recoverBuilder(context, n)
                .setGroup(STACK_GROUP)
                .setGroupAlertBehavior(Notification.GROUP_ALERT_CHILDREN)
                .setOnlyAlertOnce(true)
                .setDeleteIntent(dismissed(context))
                .build()
            moved.extras.putString(EXTRA_BUNDLE_KEY, n.extras.getString(EXTRA_BUNDLE_KEY) ?: n.group)
            moved.extras.putString(
                EXTRA_SENDER,
                n.extras.getString(EXTRA_SENDER)
                    ?: n.extras.getCharSequence(Notification.EXTRA_SUB_TEXT)?.toString()?.substringBefore(" · ").orEmpty(),
            )
            post(context, sbn.tag, sbn.id, moved)
        }
        return old.isNotEmpty()
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
     * once the stack holds two or more (or keeps one up to date that is already there). The
     * notification just posted may not be listed as active yet — `notify` is queued — so it is counted by hand.
     */
    private fun updateStack(
        context: Context,
        channelId: String,
        message: PushMessage,
        postedId: Int,
        mark: PcMark,
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
        val previous = active.firstOrNull { it.isStackSummary() }
        // Nothing else in the shade: a new stack. The count of an earlier one can outlive it (the phone
        // restarted, which clears notifications without telling the app), so it starts over here too.
        if (others.isEmpty() && previous == null) StackDrops.reset(context)
        if (kept.isEmpty() && previous == null) return // one notification needs no summary
        val posted = StackChild(
            NotificationLook.sender(message),
            NotificationStack.line(NotificationLook.header(message), NotificationLook.title(message), message.body),
            channelId,
            mark,
        )
        writeSummary(context, nm, listOf(posted) + kept.map(::childOf), StackDrops.add(context, dropped.size), category(message))
    }

    /** One notification of the stack as its summary lists it. */
    private class StackChild(val sender: String, val line: String, val channelId: String, val mark: PcMark?)

    private fun childOf(sbn: StatusBarNotification): StackChild {
        val extras = sbn.notification.extras
        val mark = extras.getString(EXTRA_MARK_LETTER)?.let { letter ->
            PcMarks.validColor(extras.getInt(EXTRA_MARK_COLOR, -1))?.let { PcMark(letter, it) }
        }
        return StackChild(
            extras.getString(EXTRA_SENDER).orEmpty(),
            NotificationStack.line(
                extras.getCharSequence(Notification.EXTRA_SUB_TEXT)?.toString(),
                extras.getCharSequence(Notification.EXTRA_TITLE)?.toString().orEmpty(),
                extras.getCharSequence(Notification.EXTRA_TEXT)?.toString(),
            ),
            sbn.notification.channelId,
            mark,
        )
    }

    /**
     * Posts (or rewrites) the stack's summary for [children], newest first: "새 알림 N건", who sent them,
     * a line each, and — when the stack's limits took [dropped] out of the shade — where those went.
     */
    private fun writeSummary(
        context: Context,
        nm: NotificationManager,
        children: List<StackChild>,
        dropped: Int,
        category: String?,
    ) {
        val count = children.size
        val senders = children.map { it.sender }
        val from = NotificationStack.senders(senders)
        val title = "새 알림 ${count}건"
        val style = NotificationCompat.InboxStyle().setBigContentTitle(title).setSummaryText(from)
        children.take(SUMMARY_LINES).forEach { style.addLine(it.line) }
        NotificationStack.droppedLine(dropped)?.let { style.addLine(it) }
        val center = PendingIntent.getActivity(
            context,
            REQUEST_CENTER,
            Intent(context, MainActivity::class.java)
                .setAction(ACTION_OPEN_CENTER)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        // Android files a stack in the alerting section when its summary or any of its notifications
        // is alerting. The summary takes the stack's least important channel, so the notifications
        // alone decide — also after the alerting ones are dismissed, which no push follows.
        val summaryChannel = children.map { it.channelId }.distinct()
            .mapNotNull { id -> nm.getNotificationChannel(id)?.importance?.takeIf { it > NotificationManager.IMPORTANCE_NONE }?.let { id to it } }
            .minByOrNull { it.second }?.first ?: children.first().channelId
        // One sender's stack wears its badge; a mixed one keeps the app's own look.
        val mark = children.first().mark.takeIf { senders.distinct().size == 1 }
        val summary = NotificationCompat.Builder(context, summaryChannel)
            .setSmallIcon(R.drawable.ic_notification)
            .apply { if (mark != null) setColor(mark.argb).setLargeIcon(badge(context, mark)) }
            .setContentTitle(title)
            .setContentText(from)
            .setStyle(style)
            .setSubText(from)
            .setNumber(count)
            .setCategory(category)
            // Clearing the whole stack lands in tidy, which then starts the dropped count over.
            .setDeleteIntent(dismissed(context))
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
     * white, at the size Android shows large icons. Drawn once per mark and kept ([badges]); the
     * bitmap is only read after, by the notifications that carry it.
     */
    private fun badge(context: Context, mark: PcMark): Bitmap {
        val size = context.resources.getDimensionPixelSize(android.R.dimen.notification_large_icon_width).coerceAtLeast(48)
        val key = "${mark.letter}|${mark.colorIndex}|$size"
        badges.get(key)?.let { return it }
        return drawBadge(mark, size).also { badges.put(key, it) }
    }

    private fun drawBadge(mark: PcMark, size: Int): Bitmap {
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

    private fun StatusBarNotification.isStackSummary(): Boolean = isSummary() && tag == SUMMARY_TAG && notification.group == STACK_GROUP

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

/**
 * How many notifications the Homebase stack's limits took out of the shade since the stack began
 * (#69), for its summary. Kept on its own rather than in the summary's extras: notifications are
 * posted asynchronously, so a burst reads a summary that is not updated yet and the count was lost.
 * Back to 0 once the stack is empty (see [PushNotifier.tidy]).
 */
internal object StackDrops {
    private const val PREFS = "push_stack"
    private const val KEY = "dropped"

    private fun prefs(context: Context) = context.applicationContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)

    @Synchronized
    fun add(context: Context, n: Int): Int {
        val total = get(context) + n
        if (n > 0) prefs(context).edit().putInt(KEY, total).apply()
        return total
    }

    @Synchronized
    fun get(context: Context): Int = prefs(context).getInt(KEY, 0)

    @Synchronized
    fun reset(context: Context) {
        if (get(context) != 0) prefs(context).edit().putInt(KEY, 0).apply()
    }
}
