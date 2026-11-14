package io.github.jeiel85.homebase.push

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class NotificationGroupsTest {
    /** The `category` values in docs/MOBILE_PROTOCOL.md, minus the Tray test. */
    private val protocolCategories = setOf(
        "windows-toast", "process_watch", "service_watch", "http_monitor", "port_monitor", "event_log", "advisor", "boot",
    )

    @Test
    fun every_protocol_category_is_in_exactly_one_group() {
        val all = NotificationGroups.ALL.flatMap { it.categories }
        assertEquals(all.size, all.toSet().size)
        assertEquals(protocolCategories, all.toSet())
    }

    @Test
    fun windows_only_preset_mutes_everything_but_toasts() {
        assertEquals(protocolCategories - "windows-toast", NotificationGroups.ALL_BUT_TOASTS)
    }

    @Test
    fun muted_categories_drop_but_the_test_always_passes() {
        val muted = setOf("event_log", "test")
        assertTrue(NotificationGroups.isMuted("event_log", muted))
        assertFalse(NotificationGroups.isMuted("boot", muted))
        assertFalse(NotificationGroups.isMuted("test", muted))
    }
}
