package io.github.jeiel85.homebase.push

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** DECISIONS M-69: commands sent, in the notification center — which PC, which command, how it went; nothing else. */
class CommandHistoryTest {
    @Test
    fun an_entry_says_which_pc_which_command_and_how_it_went() {
        val e = CommandHistory.sent("c1", "pc1", "집 PC", "다시 시작", "60초 뒤 다시 시작 예약됨", color = 3, now = 1_000L)
        assertEquals(CommandHistory.SENT, e.category)
        assertEquals("다시 시작", e.title)
        assertEquals("60초 뒤 다시 시작 예약됨", e.body)
        assertEquals("", e.source)
        assertEquals(1_000L, e.sentAt)
        assertEquals(1_000L, e.receivedAt)
        assertEquals(3, e.color)
        assertTrue(e.uris.isEmpty())
        assertTrue(CommandHistory.isCommand(e.category))
    }

    @Test
    fun sent_commands_have_their_own_chip_and_are_no_news() {
        assertEquals("보낸 명령", PushHistoryRules.categoryLabel(CommandHistory.SENT))
        val sent = CommandHistory.sent("c", "pc1", "PC", "잠그기", "완료", null, now = 300)
        val received = FileHistory.received("r", "pc1", "PC", null, listOf(FileHistory.Item("a.txt", 1, "text/plain")), emptyList(), null, now = 200)
        assertFalse(PushHistoryRules.isUnread(sent, lastSeenAt = 100))
        assertEquals(1, PushHistoryRules.unreadCount(listOf(sent, received), lastSeenAt = 100))
        assertFalse(FileHistory.isFile(sent.category))
    }

    @Test
    fun entries_survive_the_stored_form() {
        val sent = CommandHistory.sent("c", "pc1", "PC", "절전", "PC에 닿지 않음", 1, now = 5_000)
        val back = PushHistoryRules.fromJson(PushHistoryRules.toJson(listOf(sent)))
        assertEquals(listOf(sent), back)
    }
}
