package io.github.jeiel85.homebase.share

import android.content.Context

/**
 * "PC 바로가기로 공유하면 바로 보내기" (docs/QUICK_SEND.md §4): what a share sheet PC shortcut may send without the
 * share center. Off by default — any app can open the share sheet activity with a shortcut id — and then the PC still
 * decides what goes on its clipboard (its own range, §6.1).
 */
enum class QuickSendMode(val key: String, val label: String) {
    OFF("off", "끔"),
    LINKS("links", "링크만"),
    TEXT("text", "링크와 텍스트"),
    ;

    companion object {
        fun of(key: String?): QuickSendMode = entries.firstOrNull { it.key == key } ?: OFF
    }
}

/** The pure rules of quick sending — tests pin them. */
object QuickSend {
    /** At most this many quick sends ... */
    const val LIMIT = 10

    /** ... in this rolling window; past it the share center opens instead. */
    const val WINDOW_MS = 60_000L

    /** An `http://` or `https://` address with a host. */
    private val LINK = Regex("""(?i)\bhttps?://[^\s/?#]+""")

    fun hasLink(text: String): Boolean = LINK.containsMatchIn(text)

    /** Whether [mode] lets [text] go without the share center. The text itself is sent unchanged. */
    fun allows(mode: QuickSendMode, text: String?): Boolean = when {
        text.isNullOrBlank() -> false
        mode == QuickSendMode.TEXT -> true
        mode == QuickSendMode.LINKS -> hasLink(text)
        else -> false
    }

    /**
     * The send times to keep after one more at [now], or null when [times] (earlier quick sends) already hold [LIMIT]
     * within [WINDOW_MS] — then nothing is counted. Times outside the window (or in the future: the clock moved back)
     * are dropped.
     */
    fun admit(times: List<Long>, now: Long): List<Long>? {
        val recent = times.filter { it in (now - WINDOW_MS + 1)..now }
        return if (recent.size >= LIMIT) null else recent + now
    }
}

/** [QuickSend.admit] against the times kept in [SharePrefs]: true when this send may go (and is now counted). */
internal object QuickSendGate {
    @Synchronized
    fun admit(context: Context, now: Long = System.currentTimeMillis()): Boolean {
        val prefs = SharePrefs(context)
        val next = QuickSend.admit(prefs.quickSendTimes(), now) ?: return false
        prefs.setQuickSendTimes(next)
        return true
    }
}
