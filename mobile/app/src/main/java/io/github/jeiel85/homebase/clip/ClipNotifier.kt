package io.github.jeiel85.homebase.clip

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import io.github.jeiel85.homebase.R
import io.github.jeiel85.homebase.net.ClipProtocol
import io.github.jeiel85.homebase.push.PushNotifier

/**
 * Notifications about text a PC sent (DECISIONS M-62), in their own quiet channel: no sound, no
 * vibration — the user just sent it themselves. Not recorded in the notification center, which keeps
 * alerts, not clipboard contents.
 */
object ClipNotifier {
    private const val CHANNEL_ID = "hb.clip"
    private const val TAG = "hb.clip"

    /** How long "copied" stays in the shade: it reports something done, not something to do. */
    private const val COPIED_TIMEOUT_MS = 2 * 60_000L

    /** Characters of the text shown in the notification (never for sensitive text). */
    private const val PREVIEW_CHARS = 120

    /** [pcName]'s text is on the clipboard now. */
    fun copied(context: Context, pcName: String, text: String, sensitive: Boolean, id: String) {
        if (!ensureChannel(context)) return
        val preview = if (sensitive) "민감한 내용이라 미리 보기를 숨겼습니다" else preview(text)
        val title = "${pcName}에서 받은 텍스트를 복사했습니다"
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
            .setAutoCancel(true)
            .setTimeoutAfter(COPIED_TIMEOUT_MS)
            .build()
        post(context, id, notification)
    }

    /** [pcName]'s text was too long for a push: tap to fetch and copy it, within the PC's 10 minutes. */
    fun offer(context: Context, pcId: String, pcName: String, id: String, chars: Int) {
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
        val title = "${pcName}에서 긴 텍스트(${String.format(java.util.Locale.ROOT, "%,d", chars)}자)"
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
