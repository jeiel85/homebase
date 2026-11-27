package io.github.jeiel85.homebase.share

import io.github.jeiel85.homebase.clip.ClipShare
import io.github.jeiel85.homebase.net.ClipContent
import io.github.jeiel85.homebase.net.ClipSendResult
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** docs/QUICK_SEND.md §4–5: what a PC shortcut sends at once, how often, and how the PC's answer is read. */
class QuickSendTest {
    private val threads = "https://www.threads.com/@zuck/post/DeZtp-TkQHh"

    @Test
    fun `off sends nothing at once`() {
        assertFalse(QuickSend.allows(QuickSendMode.OFF, threads))
        assertFalse(QuickSend.allows(QuickSendMode.OFF, "메모"))
    }

    @Test
    fun `links only needs an http or https address`() {
        assertTrue(QuickSend.allows(QuickSendMode.LINKS, threads))
        assertTrue(QuickSend.allows(QuickSendMode.LINKS, "이거 봐 HTTP://example.com/a 웃김"))
        assertFalse(QuickSend.allows(QuickSendMode.LINKS, "그냥 메모"))
        assertFalse(QuickSend.allows(QuickSendMode.LINKS, "threads.com/@zuck/post/x"))
        assertFalse(QuickSend.allows(QuickSendMode.LINKS, "https://"))
        assertFalse(QuickSend.allows(QuickSendMode.LINKS, "ftp://example.com"))
    }

    @Test
    fun `links and text takes any non-blank text`() {
        assertTrue(QuickSend.allows(QuickSendMode.TEXT, "그냥 메모"))
        assertTrue(QuickSend.allows(QuickSendMode.TEXT, threads))
        assertFalse(QuickSend.allows(QuickSendMode.TEXT, "  "))
        assertFalse(QuickSend.allows(QuickSendMode.TEXT, null))
    }

    @Test
    fun `mode keys round-trip and unknown is off`() {
        QuickSendMode.entries.forEach { assertEquals(it, QuickSendMode.of(it.key)) }
        assertEquals(QuickSendMode.OFF, QuickSendMode.of(null))
        assertEquals(QuickSendMode.OFF, QuickSendMode.of("browser"))
    }

    @Test
    fun `ten a minute then the share center`() {
        val now = 1_000_000L
        var times = emptyList<Long>()
        repeat(QuickSend.LIMIT) { i -> times = QuickSend.admit(times, now + i)!! }
        assertNull(QuickSend.admit(times, now + 20))
        // A minute after the first, one slot is free again.
        val later = now + QuickSend.WINDOW_MS
        val next = QuickSend.admit(times, later)!!
        assertEquals(QuickSend.LIMIT, next.size)
        assertEquals(later, next.last())
    }

    @Test
    fun `old and future times are dropped`() {
        val now = 10_000_000L
        val kept = QuickSend.admit(listOf(now - QuickSend.WINDOW_MS - 1, now - 5, now + 60_000), now)!!
        assertEquals(listOf(now - 5, now), kept)
    }

    @Test
    fun `quick is sealed only when true and read back`() {
        val quick = ClipContent.of(threads, sensitive = false, nowMillis = 0, quick = true)
        assertTrue(quick.toJson().getBoolean("quick"))
        assertTrue(ClipContent.parse(quick.toJson().toString())!!.quick)
        val plain = ClipContent.of(threads, sensitive = false, nowMillis = 0)
        assertFalse(plain.toJson().has("quick"))
        assertFalse(ClipContent.parse(plain.toJson().toString())!!.quick)
    }

    @Test
    fun `a pc that knows quick says whether it held it and whether Archiver runs`() {
        assertEquals(
            ClipSendResult.QuickTaken(held = false, archiverStopped = false),
            ClipSendResult.of(200, """{"schema":1,"accepted":true,"quick":true,"held":false,"archiver":"running"}"""),
        )
        assertEquals(
            ClipSendResult.QuickTaken(held = true, archiverStopped = false),
            ClipSendResult.of(200, """{"schema":1,"accepted":true,"quick":true,"held":true}"""),
        )
        assertEquals(
            ClipSendResult.QuickTaken(held = false, archiverStopped = true),
            ClipSendResult.of(200, """{"schema":1,"accepted":true,"quick":true,"held":false,"archiver":"stopped"}"""),
        )
    }

    @Test
    fun `an older pc answers without quick`() {
        assertEquals(ClipSendResult.Sent, ClipSendResult.of(200, """{"schema":1,"accepted":true}"""))
        assertEquals(ClipSendResult.Sent, ClipSendResult.of(200, null))
    }

    @Test
    fun `quick lines for the toast and the notification center`() {
        val (placed, placedOutcome) = ClipShare.quickLines(ClipSendResult.QuickTaken(false, false), "집 PC")!!
        assertEquals("집 PC에 바로 보냈습니다", placed)
        assertEquals("바로 보냄", placedOutcome)
        val (held, heldOutcome) = ClipShare.quickLines(ClipSendResult.QuickTaken(true, false), "집 PC")!!
        assertTrue(held.contains("알림으로"))
        assertEquals("바로 보냄 · PC에서 알림으로 받음", heldOutcome)
        assertTrue(ClipShare.quickLines(ClipSendResult.QuickTaken(false, true), "집 PC")!!.first.contains("Threads Archiver가 꺼져"))
        assertTrue(ClipShare.quickLines(ClipSendResult.Sent, "집 PC")!!.first.contains("업데이트"))
        assertNull(ClipShare.quickLines(ClipSendResult.NoUserSession, "집 PC"))
        assertNull(ClipShare.quickLines(ClipSendResult.Failed("x"), "집 PC"))
    }

    @Test
    fun `quick answer needs quick true`() {
        assertNull(ClipSendResult.quickTaken("""{"schema":1,"accepted":true,"quick":false,"held":true}"""))
        assertNull(ClipSendResult.quickTaken("not json"))
    }
}
