package io.github.jeiel85.homebase.ui

import io.github.jeiel85.homebase.net.Hardware
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class HardwarePanelTest {
    private fun ping(lastMs: Double?, lossPct: Double, samples: Int) = Hardware.Ping("1.1.1.1", lastMs, null, lossPct, samples)

    @Test
    fun a_full_minute_without_one_ping_back_reads_as_icmp_blocked() {
        assertTrue(pingLooksBlocked(ping(null, 100.0, PING_FULL_WINDOW)))
    }

    @Test
    fun a_short_or_partial_loss_is_still_shown_as_loss() {
        // The window starts when the screen opens: under a minute of samples is not enough to tell.
        assertFalse(pingLooksBlocked(ping(null, 100.0, PING_FULL_WINDOW - 1)))
        // Some pings came back: ICMP gets through, the loss is real.
        assertFalse(pingLooksBlocked(ping(null, 96.7, PING_FULL_WINDOW)))
        assertFalse(pingLooksBlocked(ping(12.0, 0.0, PING_FULL_WINDOW)))
    }

    @Test
    fun gpu_names_drop_the_vendor_and_trademark_marks() {
        assertEquals("RTX 5060 Laptop", shortGpuName("NVIDIA GeForce RTX 5060 Laptop GPU"))
        assertEquals("Radeon 780M Graphics", shortGpuName("AMD Radeon 780M Graphics"))
        assertEquals("Radeon Graphics", shortGpuName("AMD Radeon(TM) Graphics"))
        assertEquals("UHD Graphics 770", shortGpuName("Intel(R) UHD Graphics 770"))
        assertEquals("NVIDIA", shortGpuName("NVIDIA")) // nothing left: keep the name as it is
    }
}
