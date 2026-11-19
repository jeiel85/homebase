package io.github.jeiel85.homebase.push

/**
 * Commands this phone sent to its PCs, as notification center entries (DECISIONS M-69, docs/REMOTE_COMMANDS.md §4.7):
 * to which PC, which command and how it went — nothing else. They keep the center's rules (30 days, 500 entries), are
 * never news (this phone sent them) and have a filter chip of their own ("보낸 명령"). Pure, so unit tests pin it.
 */
object CommandHistory {
    const val SENT = "command-sent"
    const val SENT_LABEL = "보낸 명령"

    fun isCommand(category: String): Boolean = category == SENT

    /** [label] (the command as the PC names it, or "다시 시작 취소") sent to [pcName] ([pcId]); [outcome] is a short line. */
    fun sent(id: String, pcId: String, pcName: String, label: String, outcome: String, color: Int?, now: Long): HistoryEntry = HistoryEntry(
        id = id,
        pcId = pcId,
        pcName = pcName,
        category = SENT,
        source = "",
        title = label,
        body = outcome,
        count = 1,
        sentAt = now,
        receivedAt = now,
        shown = true,
        color = color,
    )
}
