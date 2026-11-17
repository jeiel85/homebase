package io.github.jeiel85.homebase.push

import org.junit.Assert.assertEquals
import org.junit.Test

class NotificationStackTest {
    @Test
    fun a_stack_under_both_limits_drops_nothing() {
        assertEquals(emptySet<Int>(), NotificationStack.overflow(listOf("a", "b", "a"), "a", perSource = 8, total = 24))
    }

    @Test
    fun one_source_keeps_its_newest_up_to_the_per_source_limit_the_new_one_included() {
        // The new "a" plus the two newest other "a"s make 3; the oldest "a" goes, "b" stays.
        assertEquals(setOf(3), NotificationStack.overflow(listOf("a", "b", "a", "a"), "a", perSource = 3, total = 24))
    }

    @Test
    fun a_burst_from_one_source_does_not_push_out_another_source() {
        val others = List(10) { "kakao" } + "pc-alert"

        val dropped = NotificationStack.overflow(others, "kakao", perSource = 8, total = 24)

        assertEquals((7 until 10).toSet(), dropped)
    }

    @Test
    fun the_whole_stack_keeps_its_newest_up_to_the_total_limit() {
        val others = listOf("a", "b", "c", "d", "e")

        assertEquals(setOf(2, 3, 4), NotificationStack.overflow(others, "z", perSource = 8, total = 3))
    }

    @Test
    fun senders_are_listed_newest_first_once_each_and_counted_past_two() {
        assertEquals("작업실", NotificationStack.senders(listOf("작업실", "작업실")))
        assertEquals("작업실, 거실", NotificationStack.senders(listOf("작업실", "거실", "작업실")))
        assertEquals("작업실, Galaxy S24 외 2", NotificationStack.senders(listOf("작업실", "Galaxy S24", "거실", " ", "노트북")))
    }

    @Test
    fun the_summary_says_where_dropped_notifications_went_only_when_some_were() {
        assertEquals(null, NotificationStack.droppedLine(0))
        assertEquals("이전 알림 3건은 알림 센터에 있습니다", NotificationStack.droppedLine(3))
    }

    @Test
    fun a_summary_line_names_who_sent_it_then_what_it_says() {
        assertEquals("작업실 · 카카오톡 — 홍길동: 오늘 회의 몇 시예요?", NotificationStack.line("작업실 · 카카오톡", "홍길동", "오늘 회의 몇 시예요?\n둘째 줄"))
        assertEquals("거실 · 프로세스 감시 — backup.exe 멈춤", NotificationStack.line("거실 · 프로세스 감시", "backup.exe 멈춤", null))
        assertEquals("홍길동", NotificationStack.line(null, "홍길동", ""))
    }
}
