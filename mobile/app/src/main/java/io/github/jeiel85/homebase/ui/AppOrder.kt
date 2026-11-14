package io.github.jeiel85.homebase.ui

import android.content.Context
import io.github.jeiel85.homebase.net.AppEntry
import kotlin.math.floor
import kotlin.math.ln

/**
 * How the 앱 tab orders its cards. [description] is shown above the list so the order is never a
 * guess — the PC sends the list in an order that changes every second, and that order is not the
 * one on screen.
 */
enum class AppSort(val label: String, val description: String) {
    Cpu("CPU", "사용 중인 앱 먼저, 그다음 최근 CPU 평균이 높은 순 · 비슷하면 자리 유지 · 10초마다 다시 정렬"),
    Memory("메모리", "사용 중인 앱 먼저, 그다음 메모리를 많이 쓰는 순 · 비슷하면 자리 유지 · 10초마다 다시 정렬"),
    Name("이름", "프로세스 이름순(ABC·가나다) · 순서가 바뀌지 않음"),
    ;

    companion object {
        fun fromName(name: String?): AppSort = entries.firstOrNull { it.name == name } ?: Cpu
    }
}

/** The chosen [AppSort], kept across launches next to the theme choice. */
object AppSortSetting {
    private const val PREFS = "homebase_app"
    private const val KEY = "app_sort"

    fun load(context: Context): AppSort = AppSort.fromName(prefs(context).getString(KEY, null))

    fun save(context: Context, sort: AppSort) {
        prefs(context).edit().putString(KEY, sort.name).apply()
    }

    private fun prefs(context: Context) = context.applicationContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
}

/**
 * Turns the PC's per-second app list into an order a person can read.
 *
 * The PC ranks by the CPU of the last second, and idle apps trade places on noise (0.1 % vs 0.2 %),
 * so every snapshot reshuffled the cards under the reader's thumb. Four things hold it still:
 *
 * - **Re-rank at most every [reRankMillis].** Between re-ranks the apps on screen keep their
 *   places; the numbers on the cards stay live.
 * - **Rank CPU by a smoothed value.** An exponential average ([alpha] per sample, ≈ the last 5 s
 *   at one snapshot a second), so one spike does not move a card.
 * - **Rank by band, not by exact value, and bands are sticky.** CPU falls in idle / 1 / 5 / 15 /
 *   40 % bands and memory in ×1.5 steps; an app changes band only once it is [MARGIN] past the
 *   edge, so a value hovering on an edge does not flip. Within a band the previous order stands.
 * - **New apps go where they rank at once, closed apps leave at once.** Waiting for the next
 *   re-rank would show a list that is wrong about what is open.
 *
 * Changing the sort re-ranks immediately. One instance per PC screen; not thread-safe.
 */
internal class AppOrder(
    private val reRankMillis: Long = 10_000,
    private val alpha: Double = 0.2,
) {
    private val smoothedCpu = HashMap<Int, Double>()
    private val band = HashMap<Int, Int>()
    private var order: List<Int> = emptyList()
    private var rankedSort: AppSort? = null
    private var rankedAt = 0L

    /**
     * Input : one snapshot's apps, the chosen sort, a monotonic clock in millis
     * Output: the same apps in display order
     */
    fun arrange(apps: List<AppEntry>, sort: AppSort, nowMillis: Long): List<AppEntry> {
        val byPid = apps.associateBy { it.pid }
        smoothedCpu.keys.retainAll(byPid.keys)
        band.keys.retainAll(byPid.keys)
        for (a in apps) {
            val prev = smoothedCpu[a.pid]
            smoothedCpu[a.pid] = if (prev == null) a.usage.cpuPct else prev + alpha * (a.usage.cpuPct - prev)
        }

        if (rankedSort != sort) band.clear()
        val due = rankedSort != sort || nowMillis - rankedAt >= reRankMillis || nowMillis < rankedAt
        val previous = order.withIndex().associate { (i, pid) -> pid to i }
        order = if (due) {
            rankedSort = sort
            rankedAt = nowMillis
            for (a in apps) band[a.pid] = nextBand(a, sort)
            apps.sortedWith(comparator(sort, previous)).map { it.pid }
        } else {
            for (a in apps) if (a.pid !in band) band[a.pid] = nextBand(a, sort)
            insertNew(order.filter { it in byPid }, apps, byPid, comparator(sort, previous))
        }
        return order.map(byPid::getValue)
    }

    /** Keeps [kept] as it is and places each app not in it before the first kept app it outranks. */
    private fun insertNew(
        kept: List<Int>,
        apps: List<AppEntry>,
        byPid: Map<Int, AppEntry>,
        comparator: Comparator<AppEntry>,
    ): List<Int> {
        val keptSet = kept.toHashSet()
        val result = kept.toMutableList()
        apps.filter { it.pid !in keptSet }.sortedWith(comparator).forEach { app ->
            val at = result.indexOfFirst { comparator.compare(app, byPid.getValue(it)) < 0 }
            if (at < 0) result.add(app.pid) else result.add(at, app.pid)
        }
        return result
    }

    private fun comparator(sort: AppSort, previous: Map<Int, Int>): Comparator<AppEntry> {
        val byName = compareBy<AppEntry, String>(String.CASE_INSENSITIVE_ORDER) { it.process }.thenBy { it.pid }
        if (sort == AppSort.Name) return byName
        // Same band: the previous order stands; apps without one (new) fall to memory, then name.
        return compareByDescending<AppEntry> { it.foreground }
            .thenByDescending { band[it.pid] ?: 0 }
            .thenBy { previous[it.pid] ?: Int.MAX_VALUE }
            .thenByDescending { it.usage.memBytes }
            .then(byName)
    }

    /** The app's band after this sample: it moves only when the value is [MARGIN] past an edge. */
    private fun nextBand(app: AppEntry, sort: AppSort): Int {
        val v = when (sort) {
            AppSort.Cpu -> smoothedCpu[app.pid] ?: app.usage.cpuPct
            else -> app.usage.memBytes.toDouble()
        }
        val raw: (Double) -> Int = if (sort == AppSort.Cpu) ::cpuBand else ::memBand
        val current = band[app.pid] ?: return raw(v)
        val up = raw(v / MARGIN)
        val down = raw(v * MARGIN)
        return when {
            up > current -> up
            down < current -> down
            else -> current
        }
    }

    internal companion object {
        /** How far past a band edge a value must go before the app changes band (30 %). */
        const val MARGIN = 1.3

        private val CPU_EDGES = doubleArrayOf(1.0, 5.0, 15.0, 40.0)

        fun cpuBand(pct: Double): Int = CPU_EDGES.count { pct >= it }

        /** ×1.5 steps from 1 MB: 100 MB and 150 MB are one step apart. */
        fun memBand(bytes: Double): Int {
            val mb = bytes / (1024.0 * 1024.0)
            return if (mb < 1.0) 0 else 1 + floor(ln(mb) / ln(1.5)).toInt()
        }
    }
}
