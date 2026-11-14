package io.github.jeiel85.homebase.push

/**
 * What the user can choose to receive (DECISIONS M-25). Each group covers one or more of the PC's
 * notification `category` values (docs/MOBILE_PROTOCOL.md). The app stores and sends the muted
 * **categories**, not group keys, so the PC needs no idea of how the app groups them; a category
 * a newer PC adds is received until the user turns its group off.
 */
data class NotificationGroup(
    val key: String,
    val label: String,
    val description: String,
    val categories: List<String>,
)

object NotificationGroups {
    const val TOAST = "windows-toast"

    /** Notifications another device sent through a PC (DECISIONS M-28). */
    const val DEVICE = "device-notification"

    /** The Tray's "send test" — never muted, so the path can always be checked. */
    const val TEST = "test"

    val ALL: List<NotificationGroup> = listOf(
        NotificationGroup("toast", "Windows 앱 알림", "메신저·메일 등 PC에 뜬 Windows 알림", listOf(TOAST)),
        NotificationGroup(
            "watch", "감시 경보", "프로세스·서비스·웹사이트(HTTP)·포트 감시",
            listOf("process_watch", "service_watch", "http_monitor", "port_monitor"),
        ),
        NotificationGroup("eventlog", "이벤트 로그", "Windows 이벤트 로그의 오류·경고", listOf("event_log")),
        NotificationGroup("advisor", "PC 상태 조언", "디스크·메모리 같은 상태 점검 결과", listOf("advisor")),
        NotificationGroup("boot", "부팅", "PC가 켜졌다는 알림", listOf("boot")),
        NotificationGroup("device", "다른 기기 알림", "연결한 다른 휴대폰·PC에 온 알림을 PC가 전달", listOf(DEVICE)),
    )

    /** Every category but app notifications (this PC's and other devices') — the "앱 알림만" preset. */
    val ALL_BUT_TOASTS: Set<String> = ALL.flatMap { it.categories }.filter { it != TOAST && it != DEVICE }.toSet()

    /** Whether a message of [category] should be dropped. [TEST] always passes. */
    fun isMuted(category: String, muted: Set<String>): Boolean = category != TEST && category in muted
}
