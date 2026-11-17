package io.github.jeiel85.homebase.push

import io.github.jeiel85.homebase.data.PcEntry
import io.github.jeiel85.homebase.data.PcMark
import io.github.jeiel85.homebase.data.PcMarks
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class NotificationLookTest {
    private fun msg(category: String, source: String, title: String, pcName: String = "작업실") =
        PushMessage("id", "pc-1", pcName, category, "info", title, "본문", source, 1, "2026-11-16T10:00:00+09:00")

    private val pc = PcEntry("pc-1", "DESKTOP-V0JCEPJ", emptyList(), "dev", "t", alias = "작업실", color = 3)

    @Test
    fun a_windows_notification_names_the_pc_then_the_app_and_keeps_the_sender_as_title() {
        val m = msg("windows-toast", "카카오톡", "카카오톡: 홍길동")

        assertEquals("작업실 · 카카오톡", NotificationLook.header(m))
        assertEquals("홍길동", NotificationLook.title(m))
    }

    @Test
    fun a_bundled_windows_notification_keeps_its_count_as_title() {
        val m = msg("windows-toast", "카카오톡", "카카오톡: 알림 3건")

        assertEquals("알림 3건", NotificationLook.title(m))
    }

    @Test
    fun a_notification_without_its_own_title_keeps_the_app_name() {
        assertEquals("카카오톡", NotificationLook.title(msg("windows-toast", "카카오톡", "카카오톡")))
    }

    @Test
    fun an_alert_names_the_pc_then_the_kind() {
        val m = msg("process_watch", "process_watch", "프로세스가 멈췄습니다: backup.exe")

        assertEquals("작업실 · 프로세스 감시", NotificationLook.header(m))
        assertEquals("프로세스가 멈췄습니다: backup.exe", NotificationLook.title(m))
    }

    @Test
    fun an_unknown_alert_kind_shows_as_sent() {
        assertEquals("작업실 · new_kind", NotificationLook.header(msg("new_kind", "new_kind", "제목")))
    }

    @Test
    fun a_relayed_notification_leads_with_the_device_not_the_pc_it_came_through() {
        val m = msg(NotificationGroups.DEVICE, "Galaxy S24 · 카카오톡", "Galaxy S24 · 카카오톡: 홍길동")

        assertEquals("Galaxy S24", NotificationLook.sender(m))
        assertEquals("Galaxy S24 · 카카오톡", NotificationLook.header(m))
        assertEquals("홍길동", NotificationLook.title(m))
    }

    @Test
    fun a_relayed_source_without_a_device_part_is_all_device() {
        val m = msg(NotificationGroups.DEVICE, "Galaxy S24", "Galaxy S24: 홍길동")

        assertEquals("Galaxy S24", NotificationLook.header(m))
        assertEquals("", NotificationLook.appOf("Galaxy S24"))
    }

    @Test
    fun the_badge_is_the_paired_pc_own_or_the_relayed_device() {
        assertEquals(pc.mark, NotificationLook.mark(msg("windows-toast", "카카오톡", "t"), pc))
        assertEquals("작", NotificationLook.mark(msg("windows-toast", "카카오톡", "t"), pc).letter)
        assertEquals(
            PcMarks.unpaired("Galaxy S24"),
            NotificationLook.mark(msg(NotificationGroups.DEVICE, "Galaxy S24 · 카카오톡", "t"), pc),
        )
        // A PC no longer paired: its id still picks the colour, its name the letter.
        assertEquals(PcMark("작", PcMarks.slot("pc-1")), NotificationLook.mark(msg("boot", "boot", "t"), null))
    }

    @Test
    fun a_relayed_title_loses_the_app_prefix_also_alone_or_under_another_device_name() {
        // #69: the expanded stack showed "카카오톡: 김철수" under a header that already says 카카오톡.
        assertEquals("김철수", NotificationLook.title(msg(NotificationGroups.DEVICE, "Galaxy S24 · 카카오톡", "카카오톡: 김철수")))
        assertEquals("김철수", NotificationLook.title(msg(NotificationGroups.DEVICE, "내 폰 · 카카오톡", "Galaxy S24 · 카카오톡: 김철수")))
        // Only the app's own prefix goes; another "x: y" title stays whole.
        assertEquals("회의: 3시", NotificationLook.title(msg(NotificationGroups.DEVICE, "Galaxy S24 · 카카오톡", "회의: 3시")))
        assertEquals("카카오톡: 김철수", NotificationLook.title(msg("windows-toast", "Slack", "카카오톡: 김철수")))
        assertEquals("카카오톡:", NotificationLook.title(msg(NotificationGroups.DEVICE, "Galaxy S24 · 카카오톡", "카카오톡:")))
    }

    @Test
    fun a_relayed_device_badge_avoids_the_paired_pcs_colours_and_history_keeps_the_one_it_wore() {
        val free = PcMarks.unpaired("Galaxy S24")
        val relayed = msg(NotificationGroups.DEVICE, "Galaxy S24 · 카카오톡", "t")
        val shifted = NotificationLook.mark(relayed, pc, listOf(free.colorIndex))
        assertEquals("G", shifted.letter)
        assertTrue(shifted.colorIndex != free.colorIndex)

        // The notification center draws what the shade drew, whatever is paired now (#57).
        assertEquals(PcMark("G", 5), NotificationLook.mark(NotificationGroups.DEVICE, "Galaxy S24 · 카카오톡", "pc-1", "작업실", pc, stored = 5))
        assertEquals(PcMark("작", 6), NotificationLook.mark("boot", "boot", "pc-gone", "작업실", null, stored = 6))
        // A paired PC is drawn with its own mark.
        assertEquals(pc.mark, NotificationLook.mark("boot", "boot", "pc-1", "작업실", pc, stored = 6))
    }
}
