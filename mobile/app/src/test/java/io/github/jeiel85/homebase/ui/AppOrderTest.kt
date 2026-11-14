package io.github.jeiel85.homebase.ui

import io.github.jeiel85.homebase.net.AppEntry
import io.github.jeiel85.homebase.net.Usage
import io.github.jeiel85.homebase.net.WindowTitle
import org.junit.Assert.assertEquals
import org.junit.Test

class AppOrderTest {

    private companion object {
        const val MB = 1024L * 1024L
    }

    private fun app(pid: Int, name: String, cpu: Double, mem: Long = 100 * MB, foreground: Boolean = false) = AppEntry(
        pid = pid,
        process = name,
        foreground = foreground,
        windows = listOf(WindowTitle(name, foreground)),
        usage = Usage(cpu, mem, 0, null, null),
        children = emptyList(),
    )

    private fun List<AppEntry>.pids() = map { it.pid }

    @Test
    fun cpu_foregroundFirstThenCpuThenMemory() {
        val result = AppOrder().arrange(
            listOf(app(1, "a", 2.0), app(2, "b", 30.0), app(3, "c", 5.0, foreground = true), app(4, "d", 2.0, mem = 900 * MB)),
            AppSort.Cpu,
            nowMillis = 1_000,
        )
        assertEquals(listOf(3, 2, 4, 1), result.pids())
    }

    @Test
    fun cpu_idleNoiseDoesNotReorder() {
        val order = AppOrder()
        // 0.1 % vs 0.4 % are both idle → memory places them once, and that order stands after.
        order.arrange(listOf(app(1, "a", 0.4, mem = 100 * MB), app(2, "b", 0.1, mem = 500 * MB)), AppSort.Cpu, 0)
        val later = order.arrange(listOf(app(1, "a", 0.1, mem = 900 * MB), app(2, "b", 0.4, mem = 500 * MB)), AppSort.Cpu, 60_000)
        assertEquals(listOf(2, 1), later.pids())
    }

    @Test
    fun cpu_keepsPlacesUntilReRankInterval() {
        val order = AppOrder(reRankMillis = 10_000)
        assertEquals(listOf(1, 2), order.arrange(listOf(app(1, "a", 50.0), app(2, "b", 10.0)), AppSort.Cpu, 0).pids())
        // b spikes; within the interval nothing moves.
        repeat(5) { i ->
            val r = order.arrange(listOf(app(1, "a", 1.0), app(2, "b", 90.0)), AppSort.Cpu, 1_000L * (i + 1))
            assertEquals(listOf(1, 2), r.pids())
        }
        // At the interval the smoothed values have crossed over and b moves up.
        assertEquals(listOf(2, 1), order.arrange(listOf(app(1, "a", 1.0), app(2, "b", 90.0)), AppSort.Cpu, 10_000).pids())
    }

    @Test
    fun cpu_rankUsesSmoothedValueNotOneSpike() {
        val order = AppOrder(reRankMillis = 0, alpha = 0.2)
        repeat(10) { order.arrange(listOf(app(1, "a", 20.0), app(2, "b", 0.0)), AppSort.Cpu, it.toLong()) }
        // One second of b at 40 % moves its average to 8 %, still under a's 20 %.
        val r = order.arrange(listOf(app(1, "a", 20.0), app(2, "b", 40.0)), AppSort.Cpu, 100)
        assertEquals(listOf(1, 2), r.pids())
    }

    @Test
    fun cpu_valueHoveringOnABandEdgeDoesNotFlip() {
        val order = AppOrder(reRankMillis = 0, alpha = 1.0)
        assertEquals(listOf(1, 2), order.arrange(listOf(app(1, "a", 6.0), app(2, "b", 4.0)), AppSort.Cpu, 0).pids())
        // a dips under 5 %, b rises over it — neither is 30 % past the edge, so nothing moves.
        repeat(5) {
            assertEquals(listOf(1, 2), order.arrange(listOf(app(1, "a", 4.6), app(2, "b", 5.4)), AppSort.Cpu, 1L + it).pids())
        }
        // Clearly past the edge, they swap.
        assertEquals(listOf(2, 1), order.arrange(listOf(app(1, "a", 2.0), app(2, "b", 9.0)), AppSort.Cpu, 10).pids())
    }

    @Test
    fun newAppGoesWhereItRanksAndClosedAppLeaves_withoutWaiting() {
        val order = AppOrder(reRankMillis = 10_000)
        order.arrange(listOf(app(1, "a", 50.0), app(2, "b", 10.0), app(3, "c", 1.0)), AppSort.Cpu, 0)
        val r = order.arrange(listOf(app(1, "a", 50.0), app(3, "c", 1.0), app(4, "d", 20.0)), AppSort.Cpu, 1_000)
        assertEquals(listOf(1, 4, 3), r.pids())
    }

    @Test
    fun memory_foregroundFirstThenMemory() {
        val r = AppOrder().arrange(
            listOf(app(1, "a", 90.0, mem = 10 * MB), app(2, "b", 0.0, mem = 300 * MB), app(3, "c", 0.0, mem = 20 * MB, foreground = true)),
            AppSort.Memory,
            0,
        )
        assertEquals(listOf(3, 2, 1), r.pids())
    }

    @Test
    fun memory_similarSizesKeepTheirPlaces() {
        val order = AppOrder(reRankMillis = 0)
        assertEquals(listOf(1, 2), order.arrange(listOf(app(1, "a", 0.0, mem = 105 * MB), app(2, "b", 0.0, mem = 100 * MB)), AppSort.Memory, 0).pids())
        assertEquals(listOf(1, 2), order.arrange(listOf(app(1, "a", 0.0, mem = 100 * MB), app(2, "b", 0.0, mem = 106 * MB)), AppSort.Memory, 1).pids())
        assertEquals(listOf(2, 1), order.arrange(listOf(app(1, "a", 0.0, mem = 100 * MB), app(2, "b", 0.0, mem = 400 * MB)), AppSort.Memory, 2).pids())
    }

    @Test
    fun name_isCaseInsensitiveAndIgnoresForegroundAndLoad() {
        val r = AppOrder().arrange(
            listOf(app(1, "chrome", 90.0), app(2, "Code", 1.0, foreground = true), app(3, "Arc", 0.0), app(4, "chrome", 0.0)),
            AppSort.Name,
            0,
        )
        assertEquals(listOf(3, 1, 4, 2), r.pids())
    }

    @Test
    fun changingSortReRanksImmediately() {
        val order = AppOrder(reRankMillis = 10_000)
        order.arrange(listOf(app(1, "z", 50.0), app(2, "a", 1.0)), AppSort.Cpu, 0)
        assertEquals(listOf(2, 1), order.arrange(listOf(app(1, "z", 50.0), app(2, "a", 1.0)), AppSort.Name, 500).pids())
    }

    @Test
    fun fromName_unknownFallsBackToCpu() {
        assertEquals(AppSort.Cpu, AppSort.fromName(null))
        assertEquals(AppSort.Cpu, AppSort.fromName("Nope"))
        assertEquals(AppSort.Name, AppSort.fromName("Name"))
    }
}
