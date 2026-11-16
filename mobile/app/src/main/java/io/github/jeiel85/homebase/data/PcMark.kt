package io.github.jeiel85.homebase.data

import java.text.BreakIterator
import java.util.Locale

/**
 * What tells one PC (or relayed device) apart at a glance: a colour and the first letter of its
 * name, drawn as a round badge on its Android notifications and next to its name in the app, so the
 * badge on a notification matches the one on the PC's card (DECISIONS M-38).
 */
data class PcMark(val letter: String, val colorIndex: Int) {
    /** Badge colour as ARGB; white text on it reads at the badge sizes used. */
    val argb: Int get() = PcMarks.PALETTE[colorIndex]
}

object PcMarks {
    /**
     * Badge colours, distinct from each other and from the app's alarm red, each dark enough for
     * white text. Order matters: a PC's stored index points into this list.
     */
    val PALETTE = intArrayOf(
        0xFF1E63C6.toInt(), // blue
        0xFF00796B.toInt(), // teal
        0xFF7B4FC4.toInt(), // violet
        0xFFC2410C.toInt(), // orange
        0xFFB0306A.toInt(), // magenta
        0xFF2E7D32.toInt(), // green
        0xFF3F4CB0.toInt(), // indigo
        0xFF7A5A3A.toInt(), // brown
    )

    /** First character of [name] as the user would see it (an emoji or a Hangul syllable whole), upper-case. */
    fun letter(name: String): String {
        val s = name.trim()
        if (s.isEmpty()) return "?"
        val it = BreakIterator.getCharacterInstance(Locale.ROOT).apply { setText(s) }
        var end = it.next()
        // Older break rules (the JVM the unit tests run on) split a joined emoji such as 👨‍💻 at the
        // zero-width joiner; Android's ICU does not. Keep the pieces together on both.
        while (end < s.length && (s[end - 1] == ZWJ || s[end] == ZWJ)) end = it.next()
        return s.substring(0, end).uppercase(Locale.ROOT)
    }

    private const val ZWJ = '‍'

    /** Where [key]'s colour search starts; the same key always lands on the same slot. */
    fun slot(key: String): Int = Math.floorMod(key.hashCode(), PALETTE.size)

    /**
     * [list] with a colour for every PC that has none yet: the first one not taken by another PC,
     * from [slot] of its id. PCs that have one keep it, so a PC paired later, re-pairing or removing
     * another never recolours a PC the user already knows. Only once every colour is in use do two
     * PCs share one.
     */
    fun withColors(list: List<PcEntry>): List<PcEntry> {
        if (list.all { it.color != null }) return list
        val taken = list.mapNotNull { it.color }.toMutableList()
        return list.map { pc ->
            if (pc.color != null) return@map pc
            val start = slot(pc.pcId)
            val free = PALETTE.indices.map { (start + it) % PALETTE.size }.firstOrNull { it !in taken } ?: start
            taken += free
            pc.copy(color = free)
        }
    }

    /** A stored colour index, or null when it is missing or outside [PALETTE] (a newer build's). */
    fun validColor(index: Int?): Int? = index?.takeIf { it in PALETTE.indices }

    /** The mark of a PC this phone is not paired with (any more), or of a relayed device: its name decides it. */
    fun unpaired(name: String): PcMark = PcMark(letter(name), slot(name.trim().lowercase(Locale.ROOT)))
}
