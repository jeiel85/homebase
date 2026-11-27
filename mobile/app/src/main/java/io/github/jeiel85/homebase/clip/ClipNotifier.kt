package io.github.jeiel85.homebase.clip

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import io.github.jeiel85.homebase.MainActivity
import io.github.jeiel85.homebase.R
import io.github.jeiel85.homebase.net.ClipProtocol
import io.github.jeiel85.homebase.net.ShareRelay
import io.github.jeiel85.homebase.push.PushNotifier

/**
 * Notifications about text a PC sent (DECISIONS M-62), in their own quiet channel: no sound, no
 * vibration — the user just sent it themselves. A tap on "copied" opens the notification center on
 * "받은 클립보드" with that text open, to read, copy again or delete (M-70).
 */
object ClipNotifier {
    private const val CHANNEL_ID = "hb.clip"
    private const val TAG = "hb.clip"

    /** How long "copied" stays in the shade: it reports something done, not something to do. */
    private const val COPIED_TIMEOUT_MS = 2 * 60_000L

    /** [MainActivity] opens the notification center on "받은 클립보드", with [EXTRA_CLIP_ID]'s entry open. */
    const val ACTION_OPEN_RECEIVED = "io.github.jeiel85.homebase.action.OPEN_RECEIVED_CLIPS"
    const val EXTRA_CLIP_ID = "io.github.jeiel85.homebase.RECEIVED_CLIP_ID"

    /** Characters of the text shown in the notification (never for sensitive text). */
    private const val PREVIEW_CHARS = 120

    /**
     * [pcName]'s text is on the clipboard now. [from]: another phone sent it through that PC (docs/SHARE_CONVENIENCE.md
     * §4.8) — "Galaxy S24에서(집 PC 경유) 텍스트 — 복사됨".
     */
    fun copied(context: Context, pcName: String, text: String, sensitive: Boolean, id: String, from: String? = null) {
        if (!ensureChannel(context)) return
        val preview = if (sensitive) "민감한 내용이라 미리 보기를 숨겼습니다" else preview(text)
        val title = if (from.isNullOrBlank()) "${pcName}에서 받은 텍스트를 복사했습니다" else "${ShareRelay.fromLine(pcName, from)} 텍스트 — 복사됨"
        val notification = NotificationCompat.Builder(context, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(title)
            .setContentText(preview)
            .setStyle(NotificationCompat.BigTextStyle().bigText(preview))
            // The lock screen says only that something was copied, not what.
            .setVisibility(NotificationCompat.VISIBILITY_PRIVATE)
            .setPublicVersion(publicVersion(context, title))
            .setCategory(NotificationCompat.CATEGORY_STATUS)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .setOnlyAlertOnce(true)
            .setContentIntent(openReceived(context, id))
            .setAutoCancel(true)
            .setTimeoutAfter(COPIED_TIMEOUT_MS)
            .build()
        post(context, id, notification)
    }

    /**
     * [pcName]'s text was too long for a push: tap to fetch and copy it, within the PC's 10 minutes. [from]: another
     * phone sent it through that PC.
     */
    fun offer(context: Context, pcId: String, pcName: String, id: String, chars: Int, from: String? = null) {
        if (!ensureChannel(context)) return
        val tap = PendingIntent.getActivity(
            context,
            id.hashCode(),
            Intent(context, ClipActionActivity::class.java)
                .setAction(ClipActionActivity.ACTION_FETCH)
                .putExtra(ClipActionActivity.EXTRA_PC_ID, pcId)
                .putExtra(ClipActionActivity.EXTRA_CLIP_ID, id)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        val title = "${ShareRelay.fromLine(pcName, from)} 긴 텍스트(${String.format(java.util.Locale.ROOT, "%,d", chars)}자)"
        val notification = NotificationCompat.Builder(context, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(title)
            .setContentText("눌러서 복사 — 10분 안에 받으세요")
            .setVisibility(NotificationCompat.VISIBILITY_PRIVATE)
            .setPublicVersion(publicVersion(context, "PC에서 긴 텍스트를 보냈습니다"))
            .setCategory(NotificationCompat.CATEGORY_STATUS)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .setContentIntent(tap)
            .setAutoCancel(true)
            .setTimeoutAfter(ClipProtocol.OFFER_LIFETIME_MS)
            .build()
        post(context, id, notification)
    }

    /**
     * A quick send to [pcName] did not reach it (docs/QUICK_SEND.md §4.2): [line] says why. [다시 보내기] sends the same
     * text to the same PC again without asking; [다른 기기로] opens the share center with it. One notification per PC and
     * text, so two failed links each keep their buttons; a later success of the same text takes it away.
     */
    fun quickFailed(context: Context, pcId: String, pcName: String, text: String, line: String) {
        if (!ensureChannel(context)) return
        val key = quickKey(pcId, text)
        val retry = PendingIntent.getActivity(
            context,
            "quick-retry:$key".hashCode(),
            Intent(context, ClipActionActivity::class.java)
                .setAction(ClipActionActivity.ACTION_QUICK_RESEND)
                .putExtra(ClipActionActivity.EXTRA_PC_ID, pcId)
                .putExtra(Intent.EXTRA_TEXT, text)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        val elsewhere = PendingIntent.getActivity(
            context,
            "quick-other:$key".hashCode(),
            Intent(context, ClipShareActivity::class.java)
                .setAction(Intent.ACTION_SEND)
                .setType("text/plain")
                .putExtra(Intent.EXTRA_TEXT, text)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        val title = "${pcName}에 보내지 못함"
        val body = "$line\n${preview(text)}"
        val notification = NotificationCompat.Builder(context, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(title)
            .setContentText(line)
            .setStyle(NotificationCompat.BigTextStyle().bigText(body))
            .setVisibility(NotificationCompat.VISIBILITY_PRIVATE)
            .setPublicVersion(publicVersion(context, title))
            .setCategory(NotificationCompat.CATEGORY_ERROR)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .setContentIntent(elsewhere)
            .addAction(0, "다시 보내기", retry)
            .addAction(0, "다른 기기로", elsewhere)
            .setAutoCancel(true)
            .build()
        post(context, key, notification)
    }

    /** Takes away the failure notification of [text] to [pcId] (it was sent again, or went another way). */
    fun cancelQuickFailed(context: Context, pcId: String, text: String) {
        NotificationManagerCompat.from(context).cancel(TAG, quickKey(pcId, text).hashCode())
    }

    private fun quickKey(pcId: String, text: String) = "quick:$pcId:${text.hashCode()}"

    /** The "copied" notification's tap: the received texts in the notification center, [id]'s open. */
    private fun openReceived(context: Context, id: String): PendingIntent = PendingIntent.getActivity(
        context,
        "clip-open:$id".hashCode(),
        Intent(context, MainActivity::class.java)
            .setAction(ACTION_OPEN_RECEIVED)
            .putExtra(EXTRA_CLIP_ID, id)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP),
        PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
    )

    /** Takes [id]'s notification away (its text was fetched). */
    fun cancel(context: Context, id: String) {
        NotificationManagerCompat.from(context).cancel(TAG, id.hashCode())
    }

    /** One line of the text: newlines and runs of spaces folded, cut at [PREVIEW_CHARS]. */
    internal fun preview(text: String): String {
        val folded = text.trim().replace(Regex("\\s+"), " ")
        return if (folded.length <= PREVIEW_CHARS) folded else folded.take(PREVIEW_CHARS) + "…"
    }

    private fun publicVersion(context: Context, title: String) = NotificationCompat.Builder(context, CHANNEL_ID)
        .setSmallIcon(R.drawable.ic_notification)
        .setContentTitle("Homebase")
        .setContentText(title)
        .build()

    /** False when notifications are off for the app or this channel; the clipboard is set either way. */
    /**
     * Whether a quick send's failure can be reported (docs/QUICK_SEND.md §4.2): the app may notify and this channel isn't
     * turned off. Otherwise a failed send after the share screen closed would only flash a toast, with no [다시 보내기].
     */
    fun canReportQuick(context: Context): Boolean = ensureChannel(context)

    private fun ensureChannel(context: Context): Boolean {
        if (!PushNotifier.canNotify(context)) return false
        val nm = context.getSystemService(NotificationManager::class.java) ?: return false
        val existing = nm.getNotificationChannel(CHANNEL_ID)
        if (existing != null) return existing.importance != NotificationManager.IMPORTANCE_NONE
        nm.createNotificationChannel(
            NotificationChannel(CHANNEL_ID, "받은 클립보드", NotificationManager.IMPORTANCE_LOW).apply {
                description = "PC에서 보낸 텍스트를 휴대폰 클립보드에 넣었을 때"
                setShowBadge(false)
                enableVibration(false)
                setSound(null, null)
            },
        )
        return true
    }

    private fun post(context: Context, id: String, notification: android.app.Notification) {
        try {
            NotificationManagerCompat.from(context).notify(TAG, id.hashCode(), notification)
        } catch (_: SecurityException) {
            // Notification permission withdrawn meanwhile: the clipboard is set anyway.
        }
    }
}
