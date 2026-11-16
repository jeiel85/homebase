package io.github.jeiel85.homebase.push

import io.github.jeiel85.homebase.data.PcEntry
import io.github.jeiel85.homebase.data.PcMark
import io.github.jeiel85.homebase.data.PcMarks

/**
 * How a push reads in Android's notification shade (DECISIONS M-38). Who sent it — the PC, or the
 * device a relayed notification came from — leads the header line and the badge; the app or alert
 * kind follows it there, so the bold title is left for what the notification says.
 *
 * ```
 * Homebase · 작업실 · 카카오톡 · 지금        (작)
 * 홍길동
 * 오늘 회의 몇 시예요?
 * ```
 */
object NotificationLook {
    /** Between the device and the app in a relayed notification's source ("Galaxy S24 · 카카오톡"). */
    private const val DEVICE_SEPARATOR = " · "

    /** Who sent [message]: the PC (its pcName is already the alias, if any), or the relayed device. */
    fun sender(message: PushMessage): String = sender(message.category, message.source, message.pcName)

    /** Who sent a notification of [category] from [source] through the PC called [pcName]. */
    fun sender(category: String, source: String, pcName: String): String =
        if (category == NotificationGroups.DEVICE) deviceOf(source) else pcName

    /**
     * The header after "Homebase": sender, then the app (Windows or relayed notifications) or the
     * alert kind. Android shortens it from the end when it does not fit, so the sender always shows.
     */
    fun header(message: PushMessage): String {
        val what = when {
            message.category == NotificationGroups.DEVICE -> appOf(message.source)
            NotificationPriority.hasApps(message.category) -> message.source
            else -> PushNotifier.CATEGORY_LABELS[message.source] ?: message.source
        }
        val who = sender(message)
        return if (what.isBlank() || what == who) who else "$who · $what"
    }

    /**
     * The bold line: the PC sends "카카오톡: 홍길동" (or "카카오톡: 알림 3건"); the header already names
     * the app, so it is dropped here. A notification with no title of its own keeps the app's name.
     */
    fun title(message: PushMessage): String =
        message.title.removePrefix("${message.source}: ").trim().ifEmpty { message.title }

    /** The badge: the paired PC's own mark, or for a relayed notification one made from the device's name. */
    fun mark(message: PushMessage, pc: PcEntry?): PcMark =
        mark(message.category, message.source, message.pcId, message.pcName, pc)

    /**
     * [mark] for a notification center entry; [pc] is null for a PC no longer paired, which keeps the
     * colour its id starts from (the one it most likely had) and the first letter of its name.
     */
    fun mark(category: String, source: String, pcId: String, pcName: String, pc: PcEntry?): PcMark = when {
        category == NotificationGroups.DEVICE -> PcMarks.unpaired(deviceOf(source))
        pc != null -> pc.mark
        else -> PcMark(PcMarks.letter(pcName), PcMarks.slot(pcId))
    }

    /** The device part of a relayed notification's source; the whole source when it has no device part. */
    fun deviceOf(source: String): String = source.substringBefore(DEVICE_SEPARATOR).trim().ifEmpty { source }

    /** The app part of a relayed notification's source; empty when it names only a device. */
    fun appOf(source: String): String =
        if (DEVICE_SEPARATOR in source) source.substringAfter(DEVICE_SEPARATOR).trim() else ""
}
