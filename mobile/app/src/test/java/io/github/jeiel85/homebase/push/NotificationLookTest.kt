package io.github.jeiel85.homebase.push

import io.github.jeiel85.homebase.data.PcEntry
import io.github.jeiel85.homebase.data.PcMark
import io.github.jeiel85.homebase.data.PcMarks
import org.junit.Assert.assertEquals
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
}
