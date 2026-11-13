package io.github.jeiel85.homebase.push

import android.Manifest
import android.app.NotificationChannel
import android.app.NotificationChannelGroup
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import io.github.jeiel85.homebase.MainActivity
import io.github.jeiel85.homebase.R
import java.security.MessageDigest
import java.time.OffsetDateTime

/**
 * Shows decrypted pushes. Channels are split per PC (a channel group named after the PC) and per
 * source — each Windows app for forwarded toasts, each alert kind otherwise — so the user can mute
 * one app on one PC from Android's notification settings without losing the rest (DECISIONS M-19).
 */
object PushNotifier {
    const val EXTRA_PC_ID = "io.github.jeiel85.homebase.PC_ID"

    fun canNotify(context: Context): Boolean {
        if (Build.VERSION.SDK_INT >= 33 &&
            ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            return false
        }
        return NotificationManagerCompat.from(context).areNotificationsEnabled()
    }

    fun show(context: Context, message: PushMessage) {
        if (!canNotify(context)) return
        val channelId = ensureChannel(context, message)
        val open = PendingIntent.getActivity(
            context,
            message.pcId.hashCode(),
            Intent(context, MainActivity::class.java)
                .putExtra(EXTRA_PC_ID, message.pcId)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        val notification = NotificationCompat.Builder(context, channelId)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(message.title)
            .setContentText(message.body)
            .setStyle(message.body?.let { NotificationCompat.BigTextStyle().bigText(it) })
            .setSubText(message.pcName)
            .setWhen(epochMillis(message.ts))
            .setShowWhen(true)
            .setNumber(if (message.count > 1) message.count else 0)
            .setCategory(if (message.isToast) NotificationCompat.CATEGORY_MESSAGE else NotificationCompat.CATEGORY_STATUS)
            .setPriority(if (message.isToast) NotificationCompat.PRIORITY_DEFAULT else NotificationCompat.PRIORITY_HIGH)
            .setGroup("hb.pc.${message.pcId}")
            .setContentIntent(open)
            .setAutoCancel(true)
            .build()
        try {
            NotificationManagerCompat.from(context).notify(message.id.hashCode(), notification)
        } catch (_: SecurityException) {
            // Permission revoked between the check and the post.
        }
    }

    /** Removes a PC's channels when it is deleted from the app. */
    fun forgetPc(context: Context, pcId: String) {
        if (Build.VERSION.SDK_INT < 26) return
        context.getSystemService(NotificationManager::class.java).deleteNotificationChannelGroup(groupId(pcId))
    }

    fun channelId(pcId: String, source: String): String = "hb.$pcId.${hash(source.lowercase())}"

    /** Human name for a channel: the Windows app for toasts, a Korean label for alert kinds. */
    fun channelName(message: PushMessage): String =
        if (message.isToast) "${message.source} (Windows 알림)" else CATEGORY_LABELS[message.source] ?: message.source

    private fun groupId(pcId: String) = "hb.pc.$pcId"

    private fun ensureChannel(context: Context, message: PushMessage): String {
        val id = channelId(message.pcId, message.source)
        val nm = context.getSystemService(NotificationManager::class.java)
        nm.createNotificationChannelGroup(NotificationChannelGroup(groupId(message.pcId), message.pcName))
        if (nm.getNotificationChannel(id) == null) {
            val importance = if (message.isToast) NotificationManager.IMPORTANCE_DEFAULT else NotificationManager.IMPORTANCE_HIGH
            nm.createNotificationChannel(
                NotificationChannel(id, channelName(message), importance).apply {
                    group = groupId(message.pcId)
                    description = if (message.isToast) {
                        "${message.pcName}에서 전달한 ${message.source} 알림"
                    } else {
                        "${message.pcName}의 ${channelName(message)} 알림"
                    }
                },
            )
        }
        return id
    }

    private fun epochMillis(ts: String): Long =
        runCatching { OffsetDateTime.parse(ts).toInstant().toEpochMilli() }.getOrDefault(System.currentTimeMillis())

    private fun hash(s: String): String =
        MessageDigest.getInstance("SHA-256").digest(s.toByteArray(Charsets.UTF_8)).take(6).joinToString("") { "%02x".format(it) }

    private val CATEGORY_LABELS = mapOf(
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
