package io.github.jeiel85.homebase.push

/**
 * Pure rules of the one "Homebase" stack in Android's notification shade (DECISIONS M-52): which
 * notifications it drops when full and what its summary says. No Android types, so unit tests pin them.
 */
object NotificationStack {
    /**
     * Which of the stack's other notifications to drop after one of source [newKey] joined it:
     * [othersKeys] are their sources, newest first. The newest are kept, at most [perSource] of one
     * source and [total] in all, the new one included.
     */
    fun overflow(othersKeys: List<String>, newKey: String, perSource: Int, total: Int): Set<Int> {
        val counts = mutableMapOf(newKey to 1)
        var kept = 1
        val dropped = mutableSetOf<Int>()
        othersKeys.forEachIndexed { i, key ->
            val n = counts[key] ?: 0
            if (n >= perSource || kept >= total) {
                dropped += i
            } else {
                counts[key] = n + 1
                kept++
            }
        }
        return dropped
    }

    /**
     * Who the stack holds notifications from, for the summary's header: [names] newest first, each
     * once; past two, the rest as a count ("작업실, 거실 외 2").
     */
    fun senders(names: List<String>): String {
        val distinct = names.map { it.trim() }.filter { it.isNotEmpty() }.distinct()
        return if (distinct.size <= 2) distinct.joinToString(", ") else "${distinct[0]}, ${distinct[1]} 외 ${distinct.size - 2}"
    }

    /**
     * One line of the summary: the notification's header (sender and app, see [NotificationLook.header]),
     * then its title and the first line of its body — "작업실 · 카카오톡 — 홍길동: 오늘 회의 몇 시예요?".
     */
    fun line(header: String?, title: String, body: String?): String {
        val what = NotificationPriority.shortLine(title, body, "")
        val who = header?.trim().orEmpty()
        return if (who.isEmpty()) what else "$who — $what"
    }
}
