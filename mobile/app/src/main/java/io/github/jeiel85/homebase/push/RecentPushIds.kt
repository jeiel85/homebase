package io.github.jeiel85.homebase.push

import android.content.Context

/**
 * Ids of relayed notifications shown in the last half hour (DECISIONS M-28). When this phone and the
 * sending phone are both paired with two PCs, each PC pushes the same relayed notification; the PCs
 * give it the same id, and the second copy is dropped here. Kept in preferences because the receiver
 * process may be gone between the two pushes.
 */
class RecentPushIds(context: Context) {
    private val prefs = context.applicationContext.getSharedPreferences(NAME, Context.MODE_PRIVATE)

    /** True the first time [id] is seen within [WINDOW_MS]; records it. */
    @Synchronized
    fun firstSighting(id: String, now: Long = System.currentTimeMillis()): Boolean {
        val kept = prefs.getStringSet(KEY, emptySet()).orEmpty()
            .mapNotNull { entry -> entry.split('@').takeIf { it.size == 2 }?.let { it[0] to (it[1].toLongOrNull() ?: 0L) } }
            .filter { now - it.second <= WINDOW_MS }
        val result = Companion.firstSighting(kept, id, now)
        prefs.edit().putStringSet(KEY, result.second.map { "${it.first}@${it.second}" }.toSet()).apply()
        return result.first
    }

    companion object {
        private const val NAME = "homebase.push.recent"
        private const val KEY = "ids"
        const val WINDOW_MS = 30 * 60_000L
        const val MAX = 200

        /** Pure core: whether [id] is new among [kept] (id, seenAt), and the list to keep next. */
        fun firstSighting(kept: List<Pair<String, Long>>, id: String, now: Long): Pair<Boolean, List<Pair<String, Long>>> {
            val fresh = kept.filter { now - it.second <= WINDOW_MS }
            if (fresh.any { it.first == id }) return false to fresh
            return true to (fresh + (id to now)).takeLast(MAX)
        }
    }
}
