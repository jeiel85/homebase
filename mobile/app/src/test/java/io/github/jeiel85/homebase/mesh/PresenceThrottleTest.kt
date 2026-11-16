package io.github.jeiel85.homebase.mesh

import io.github.jeiel85.homebase.net.PresenceReport
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class PresenceThrottleTest {
    private val report = PresenceReport(appVersion = "0.31.0", battery = 80, charging = false, network = "wifi")

    @Test
    fun the_first_report_to_a_pc_goes_at_once() {
        assertTrue(MeshSync.presenceDue(null, null, report, 5_000))
    }

    @Test
    fun the_same_state_is_sent_again_only_after_a_minute() {
        // The list polls every 15 s.
        assertFalse(MeshSync.presenceDue(report, 0, report, 15_000))
        assertFalse(MeshSync.presenceDue(report, 0, report, MeshSync.RESEND_MS - 1))
        assertTrue(MeshSync.presenceDue(report, 0, report, MeshSync.RESEND_MS))
    }

    @Test
    fun a_changed_state_goes_at_once() {
        assertTrue(MeshSync.presenceDue(report, 0, report.copy(battery = 79), 15_000))
        assertTrue(MeshSync.presenceDue(report, 0, report.copy(charging = true), 15_000))
        assertTrue(MeshSync.presenceDue(report, 0, report.copy(network = "cellular"), 15_000))
    }

    @Test
    fun a_clock_that_went_back_does_not_hold_reports_back() {
        assertTrue(MeshSync.presenceDue(report, 100_000, report, 5_000))
    }
}
