package io.github.jeiel85.homebase.ui

import io.github.jeiel85.homebase.data.PcEntry
import io.github.jeiel85.homebase.net.AdviceReply
import io.github.jeiel85.homebase.push.NotificationGroups
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Advice kept in the notification center, and the alerts that offer "AI 조언" (DECISIONS M-61). */
class AdviceHistoryTest {

    private val pc = PcEntry(pcId = "pc1", name = "HOME-PC", endpoints = emptyList(), deviceId = "d1", token = "t")

    private fun reply(text: String?, ok: Boolean = true, generatedAt: String? = "2026-11-19T01:30:00+00:00") = AdviceReply(
        ok = ok,
        text = text,
        summary = "CPU load: 92%",
        model = "llama3.2:1b",
        generatedAt = generatedAt,
        cached = false,
        error = if (ok) null else "unreachable",
        message = null,
    )

    @Test
    fun advice_is_filed_quietly_under_pc_state_advice_with_its_first_line_as_title() {
        val e = adviceHistoryEntry(pc, reply("\n- CPU가 바쁩니다. chrome을 확인하세요.\n- 디스크를 정리하세요."), now = 1_000L)!!

        assertEquals("advice-1795051800000", e.id) // 2026-11-19T01:30:00Z
        assertEquals("pc1", e.pcId)
        assertEquals("HOME-PC", e.pcName)
        assertEquals("advisor", e.category)
        assertEquals("CPU가 바쁩니다. chrome을 확인하세요.", e.title)
        assertTrue(e.body!!.contains("디스크를 정리하세요"))
        assertEquals(1_795_051_800_000L, e.sentAt)
        assertEquals(1_000L, e.receivedAt)
        assertTrue(e.quiet)
        assertFalse(e.shown)
    }

    @Test
    fun the_same_advice_handed_out_again_has_the_same_id() {
        val first = adviceHistoryEntry(pc, reply("Fine."), now = 1L)!!
        val again = adviceHistoryEntry(pc, reply("Fine."), now = 50_000L)!!
        assertEquals(first.id, again.id)
    }

    @Test
    fun a_long_first_line_is_cut() {
        val e = adviceHistoryEntry(pc, reply("가".repeat(300)), now = 1L)!!
        assertEquals(120, e.title.length)
        assertTrue(e.title.endsWith("…"))
    }

    @Test
    fun nothing_is_kept_without_advice() {
        assertNull(adviceHistoryEntry(pc, reply(null, ok = false), now = 1L))
        assertNull(adviceHistoryEntry(pc, reply(null), now = 1L))
    }

    @Test
    fun an_unreadable_time_falls_back_to_now() {
        assertEquals(42L, adviceHistoryEntry(pc, reply("Fine.", generatedAt = "soon"), now = 42L)!!.sentAt)
    }

    @Test
    fun only_alerts_about_something_failing_offer_ai_advice() {
        val known = NotificationGroups.ALL.flatMap { it.categories }.toSet()
        assertTrue(known.containsAll(NotificationGroups.ADVICE_ON))
        for (c in listOf("advisor", NotificationGroups.TOAST, NotificationGroups.DEVICE, NotificationGroups.AGENT_APPROVAL, "boot", NotificationGroups.TEST)) {
            assertFalse(c, c in NotificationGroups.ADVICE_ON)
        }
        assertTrue("event_log" in NotificationGroups.ADVICE_ON)
        assertTrue("process_watch" in NotificationGroups.ADVICE_ON)
    }
}
