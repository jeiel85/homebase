package io.github.jeiel85.homebase.ui

import io.github.jeiel85.homebase.push.HistoryEntry
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/** #109: a tapped notification's entry is scrolled to where the notification center lays it out. */
class CenterLayoutTest {
    private fun e(id: String) = HistoryEntry(
        id = id, pcId = "pc1", pcName = "HOME-PC", category = "windows-toast", source = "KakaoTalk",
        title = id, body = null, count = 1, sentAt = 0, receivedAt = id.hashCode().toLong(), shown = true,
    )

    // Today: a single entry, then a bundle of three. Yesterday: one single entry.
    private val a = e("a")
    private val b1 = e("b1")
    private val b2 = e("b2")
    private val b3 = e("b3")
    private val c = e("c")
    private val days = listOf(
        listOf(listOf(a) to "stack:today:a", listOf(b1, b2, b3) to "stack:today:b"),
        listOf(listOf(c) to "stack:yesterday:c"),
    )

    @Test
    fun entries_are_counted_after_what_leads_and_each_day_heading() {
        // [chips] [today] [a] [b card] ...
        assertEquals(2, CenterLayout.indexOf(a.key, leading = 1, days = days, openStack = null))
        // Closed bundle: its card, then yesterday's heading, then c.
        assertEquals(5, CenterLayout.indexOf(c.key, leading = 1, days = days, openStack = null))
        assertEquals(1, CenterLayout.indexOf(a.key, leading = 0, days = days, openStack = null))
    }

    @Test
    fun an_entry_in_a_closed_bundle_is_its_card_and_in_an_open_one_itself() {
        assertEquals(3, CenterLayout.indexOf(b2.key, leading = 1, days = days, openStack = null))
        // Open: [b card] [b1] [b2] [b3], then yesterday.
        assertEquals(5, CenterLayout.indexOf(b2.key, leading = 1, days = days, openStack = "stack:today:b"))
        assertEquals(8, CenterLayout.indexOf(c.key, leading = 1, days = days, openStack = "stack:today:b"))
    }

    @Test
    fun an_entry_not_shown_has_no_place() {
        assertNull(CenterLayout.indexOf("missing", leading = 1, days = days, openStack = null))
    }
}
