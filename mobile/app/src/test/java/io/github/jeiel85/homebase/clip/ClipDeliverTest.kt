package io.github.jeiel85.homebase.clip

import org.junit.Assert.assertEquals
import org.junit.Test

/** #117: received clipboard text is kept, and on disk, before the notification whose tap opens it goes up (M-70). */
class ClipDeliverTest {
    private fun run(text: String?, writes: Boolean): List<String> {
        val calls = mutableListOf<String>()
        ClipShare.deliver(
            text = text,
            write = { calls += "write:$it"; writes },
            offer = { calls += "offer" },
            keep = { calls += "keep:$it" },
            awaitKept = { calls += "await" },
            copied = { calls += "copied:$it" },
        )
        return calls
    }

    @Test
    fun copied_text_is_kept_and_written_before_its_notification() {
        assertEquals(listOf("write:안녕", "keep:안녕", "await", "copied:안녕"), run("안녕", writes = true))
    }

    @Test
    fun text_the_clipboard_refused_is_neither_kept_nor_announced() {
        assertEquals(listOf("write:안녕"), run("안녕", writes = false))
    }

    @Test
    fun long_text_is_only_offered() {
        assertEquals(listOf("offer"), run(null, writes = true))
    }
}
