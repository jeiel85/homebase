package io.github.jeiel85.homebase.command

import io.github.jeiel85.homebase.net.CommandErrors
import io.github.jeiel85.homebase.net.CommandInfo
import io.github.jeiel85.homebase.net.CommandKinds
import io.github.jeiel85.homebase.net.CommandOutcome
import io.github.jeiel85.homebase.net.CommandStates
import io.github.jeiel85.homebase.net.RemoteCommandNames
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/** DECISIONS M-69: how the PC screen lays the commands out, and what the app says about them. */
class CommandTextTest {
    private fun info(name: String, kind: String = RemoteCommandNames.kindOf(name)) =
        CommandInfo(name, RemoteCommandNames.label(name), kind, confirm = kind != CommandKinds.SESSION)

    @Test
    fun the_card_groups_what_the_pc_listed_and_nothing_else() {
        // The PC's order (as `RemoteCommandNames.Session` lists them), with two custom commands and one this version doesn't know.
        val listed = listOf(
            "lock", "screen_off", "mute", "volume_up", "volume_down", "media_play_pause", "media_next", "media_previous",
            "restart", "sleep", "custom:backup", "custom:lights", "custom:fan", "hibernate",
        ).map { info(it) }
        val rows = CommandLayout.rows(listed).map { row -> row.map { it.name } }
        assertEquals(
            listOf(
                listOf("lock", "screen_off"),
                listOf("media_previous", "media_play_pause", "media_next"),
                listOf("mute", "volume_down", "volume_up"),
                listOf("restart", "sleep"),
                listOf("custom:backup", "custom:lights"),
                listOf("custom:fan", "hibernate"),
            ),
            rows,
        )

        // Only lock and the volume keys turned on: two rows, nothing invented.
        val few = CommandLayout.rows(listOf(info("volume_up"), info("lock"), info("mute"))).map { row -> row.map { it.name } }
        assertEquals(listOf(listOf("lock"), listOf("mute", "volume_up")), few)
        assertTrue(CommandLayout.rows(emptyList()).isEmpty())
    }

    @Test
    fun results_say_what_happened_in_the_apps_words() {
        val pc = "집 PC"
        assertEquals("집 PC을(를) 잠갔습니다", CommandText.resultLine(pc, "lock", "잠그기", CommandOutcome.Ok(CommandStates.DONE)))
        val scheduled = CommandText.resultLine(pc, "restart", "다시 시작", CommandOutcome.Ok(CommandStates.SCHEDULED, "2026-11-20T01:01:00+00:00"))
        assertTrue(scheduled.startsWith("집 PC이(가) 60초 뒤 다시 시작합니다"))
        // Sleep: the PC can't be reached any more, and [깨우기] is the way back.
        val sleep = CommandText.resultLine(pc, "sleep", "절전", CommandOutcome.Ok(CommandStates.DONE))
        assertTrue(sleep.contains("닿지 않습니다") && sleep.contains("[깨우기]") && sleep.contains("Wake-on-LAN"))
        assertEquals(
            "집 PC의 다시 시작을 취소했습니다",
            CommandText.resultLine(pc, "restart", "다시 시작", CommandOutcome.Ok(CommandStates.CANCELLED), cancel = true),
        )
        // Turned off on the PC: say so, and where to turn it on.
        val off = CommandText.resultLine(pc, "lock", "잠그기", CommandOutcome.Refused(CommandErrors.NOT_ALLOWED))
        assertTrue(off.startsWith("집 PC에서 '잠그기' 명령이 꺼져 있습니다"))
        assertTrue(off.contains("원격 명령"))
        assertEquals("집 PC에 닿지 않습니다", CommandText.resultLine(pc, "lock", "잠그기", CommandOutcome.Unreachable))
        assertTrue(CommandText.resultLine(pc, "mute", "음소거", CommandOutcome.Refused(CommandErrors.BUSY)).contains("너무 자주"))
        assertTrue(CommandText.resultLine(pc, "restart", "다시 시작", CommandOutcome.Refused(CommandErrors.NOTHING_PENDING), cancel = true).contains("취소할 다시 시작이 없습니다"))
        assertTrue(CommandText.resultLine(pc, "lock", "잠그기", CommandOutcome.Refused(CommandErrors.NO_USER_SESSION)).contains("로그인한 사용자가 없어"))
        assertTrue(CommandText.resultLine(pc, "lock", "잠그기", CommandOutcome.Unsupported).contains("업데이트"))
    }

    @Test
    fun the_notification_center_gets_a_few_words() {
        assertEquals("완료", CommandText.historyLine(CommandOutcome.Ok(CommandStates.DONE)))
        assertEquals("60초 뒤 다시 시작 예약됨", CommandText.historyLine(CommandOutcome.Ok(CommandStates.SCHEDULED)))
        assertEquals("취소됨", CommandText.historyLine(CommandOutcome.Ok(CommandStates.CANCELLED)))
        assertEquals("거절 — PC에서 꺼져 있음", CommandText.historyLine(CommandOutcome.Refused(CommandErrors.NOT_ALLOWED)))
        assertEquals("PC에 닿지 않음", CommandText.historyLine(CommandOutcome.Unreachable))
    }

    @Test
    fun the_question_before_a_restart_warns_about_unsaved_work() {
        assertEquals(
            "집 PC을(를) 다시 시작할까요? 저장하지 않은 작업은 사라질 수 있습니다. PC에는 60초 카운트다운과 취소가 뜹니다.",
            CommandText.confirmText("집 PC", "restart", "다시 시작"),
        )
        assertTrue(CommandText.confirmText("집 PC", "custom:backup", "백업").contains("'백업'"))
    }
}
