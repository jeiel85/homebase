package io.github.jeiel85.homebase.push

import java.util.Locale

/**
 * Clipboard text this phone received from and sent to its PCs, as notification center entries (DECISIONS M-70): who,
 * how long, how it went, and the text itself — its first [KEPT_CHARS] characters — so it can be read and copied again.
 * Text marked sensitive (a password manager's) is never kept. They keep the center's rules (30 days, 500 entries,
 * encrypted on this phone), are never bundled, and have filter chips of their own ("받은 클립보드", "보낸 클립보드").
 * Pure, so unit tests pin it.
 */
object ClipHistory {
    const val RECEIVED = "clip-received"
    const val SENT = "clip-sent"

    const val RECEIVED_LABEL = "받은 클립보드"
    const val SENT_LABEL = "보낸 클립보드"

    /** Characters of the text kept in an entry — the PC keeps as many. */
    const val KEPT_CHARS = 2000

    fun isClip(category: String): Boolean = category == RECEIVED || category == SENT

    /**
     * Text [pcName] ([pcId]) sent — or, with [from], another phone sent through it — that is on the clipboard now.
     * [id] is the clip's id, which the notification's tap looks for.
     */
    fun received(
        id: String,
        pcId: String,
        pcName: String,
        from: String?,
        text: String,
        sensitive: Boolean,
        sentAt: Long,
        color: Int?,
        now: Long,
    ): HistoryEntry = HistoryEntry(
        id = id,
        pcId = pcId,
        pcName = pcName,
        category = RECEIVED,
        source = from?.trim().orEmpty(),
        title = title(text.length, sensitive),
        body = body(text, sensitive),
        count = 1,
        sentAt = sentAt,
        receivedAt = now,
        shown = true,
        color = color,
    )

    /** Text sent to [pcName] ([pcId]) — or through it to the phones [toNames] — and how it went ("보냄", "실패: …"). */
    fun sent(
        id: String,
        pcId: String,
        pcName: String,
        toNames: List<String>,
        text: String,
        sensitive: Boolean,
        outcome: String,
        color: Int?,
        now: Long,
    ): HistoryEntry = HistoryEntry(
        id = id,
        pcId = pcId,
        pcName = pcName,
        category = SENT,
        source = toNames.joinToString(", "),
        title = title(text.length, sensitive, outcome),
        body = body(text, sensitive),
        count = 1,
        sentAt = now,
        receivedAt = now,
        shown = true,
        color = color,
    )

    /**
     * "텍스트 1,234자", then what was not kept (" · 민감한 내용이라 보관 안 함", " · 앞 2,000자만 보관"), then [outcome]
     * when there is one — the PC's notification center words it the same way.
     */
    fun title(chars: Int, sensitive: Boolean, outcome: String? = null): String {
        var title = "텍스트 ${count(chars)}자"
        if (sensitive) {
            title += " · 민감한 내용이라 보관 안 함"
        } else if (chars > KEPT_CHARS) {
            title += " · 앞 ${count(KEPT_CHARS)}자만 보관"
        }
        return if (outcome.isNullOrEmpty()) title else "$title · $outcome"
    }

    /** The text kept: none when sensitive, else at most [KEPT_CHARS], never half a surrogate pair. */
    fun body(text: String, sensitive: Boolean): String? {
        if (sensitive || text.isEmpty()) return null
        if (text.length <= KEPT_CHARS) return text
        val cut = if (Character.isHighSurrogate(text[KEPT_CHARS - 1])) KEPT_CHARS - 1 else KEPT_CHARS
        return text.substring(0, cut)
    }

    /** "Galaxy Tab S9 · 집 PC 경유" for text that went through the PC to another phone (or came from one), else the PC. */
    fun header(entry: HistoryEntry, pcName: String): String =
        if (entry.source.isBlank()) pcName else "${entry.source} · $pcName 경유"

    private fun count(n: Int) = String.format(Locale.ROOT, "%,d", n)
}
