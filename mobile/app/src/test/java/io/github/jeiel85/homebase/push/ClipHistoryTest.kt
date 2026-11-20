package io.github.jeiel85.homebase.push

import io.github.jeiel85.homebase.clip.ClipShare
import io.github.jeiel85.homebase.net.Forwarded
import io.github.jeiel85.homebase.net.RelayPhone
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** DECISIONS M-70: clipboard text received and sent in the notification center — the text too, unless sensitive. */
class ClipHistoryTest {
    @Test
    fun a_received_entry_keeps_who_sent_it_and_the_text() {
        val e = ClipHistory.received("c1", "pc1", "집 PC", from = null, text = "회의실 B, 3시", sensitive = false, sentAt = 7L, color = 2, now = 9L)
        assertEquals(ClipHistory.RECEIVED, e.category)
        assertEquals("텍스트 9자", e.title)
        assertEquals("회의실 B, 3시", e.body)
        assertEquals("집 PC", ClipHistory.header(e, "집 PC"))
        assertEquals(7L to 9L, e.sentAt to e.receivedAt)
        assertEquals("받은 클립보드", PushHistoryRules.categoryLabel(e.category))

        val relayed = ClipHistory.received("c2", "pc1", "집 PC", from = "Galaxy S24", text = "x", sensitive = false, sentAt = 1L, color = null, now = 1L)
        assertEquals("Galaxy S24 · 집 PC 경유", ClipHistory.header(relayed, "집 PC"))
    }

    @Test
    fun sensitive_text_is_never_kept() {
        val e = ClipHistory.received("c1", "pc1", "집 PC", null, "hunter2", sensitive = true, sentAt = 1L, color = null, now = 1L)
        assertNull(e.body)
        assertEquals("텍스트 7자 · 민감한 내용이라 보관 안 함", e.title)

        val sent = ClipHistory.sent("c2", "pc1", "집 PC", emptyList(), "pw", sensitive = true, outcome = "보냄", color = null, now = 1L)
        assertNull(sent.body)
        assertEquals("텍스트 2자 · 민감한 내용이라 보관 안 함 · 보냄", sent.title)
    }

    @Test
    fun a_sent_entry_says_to_whom_and_how_it_went_and_is_no_news() {
        val e = ClipHistory.sent("c3", "pc1", "집 PC", listOf("Galaxy Tab S9"), "주소: 서울시 중구", false, "보냄", color = 1, now = 5L)
        assertEquals(ClipHistory.SENT, e.category)
        assertEquals("텍스트 10자 · 보냄", e.title)
        assertEquals("주소: 서울시 중구", e.body)
        assertEquals("Galaxy Tab S9 · 집 PC 경유", ClipHistory.header(e, "집 PC"))
        assertFalse(PushHistoryRules.isUnread(e, lastSeenAt = 0L))

        val received = ClipHistory.received("c4", "pc1", "집 PC", null, "x", false, 5L, null, 5L)
        assertTrue(PushHistoryRules.isUnread(received, lastSeenAt = 0L))
    }

    @Test
    fun long_text_keeps_its_first_two_thousand_characters_never_half_a_pair() {
        val text = "가".repeat(ClipHistory.KEPT_CHARS + 10)
        assertEquals(ClipHistory.KEPT_CHARS, ClipHistory.body(text, false)!!.length)
        assertEquals("텍스트 2,010자 · 앞 2,000자만 보관", ClipHistory.title(text.length, false))

        val emoji = "a".repeat(ClipHistory.KEPT_CHARS - 1) + "😀tail"
        val kept = ClipHistory.body(emoji, false)!!
        assertEquals(ClipHistory.KEPT_CHARS - 1, kept.length)
        assertFalse(Character.isHighSurrogate(kept.last()))
    }

    @Test
    fun a_tapped_received_text_notification_opens_its_entry() {
        val e = ClipHistory.received("c5", "pc1", "집 PC", null, "x", false, 1L, null, 1L)
        val view = PushHistoryRules.focus(listOf(e), ClipHistory.RECEIVED, "c5")
        assertEquals("받은 클립보드", view.group)
        assertEquals(e.key, view.expanded)
    }

    @Test
    fun entries_survive_the_stored_form() {
        val e = ClipHistory.received("c6", "pc1", "집 PC", "Galaxy S24", "줄 하나\n줄 둘", false, 3L, 4, 5L)
        assertEquals(listOf(e), PushHistoryRules.fromJson(PushHistoryRules.toJson(listOf(e))))
    }

    @Test
    fun a_relayed_send_says_which_phones_it_missed() {
        val tab = RelayPhone("a1", "Galaxy Tab S9", clip = true, files = true)
        val pad = RelayPhone("a2", "iPad", clip = true, files = true)
        assertEquals("보냄", ClipShare.relayedOutcome(listOf(Forwarded("a1", true), Forwarded("a2", true)), listOf(tab, pad)))
        assertEquals("일부만 보냄 — iPad 실패", ClipShare.relayedOutcome(listOf(Forwarded("a1", true), Forwarded("a2", false)), listOf(tab, pad)))
        assertEquals("실패: 다른 휴대폰에 넘기지 못함", ClipShare.relayedOutcome(emptyList(), listOf(tab, pad)))
    }
}
