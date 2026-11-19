package io.github.jeiel85.homebase.ui

import io.github.jeiel85.homebase.net.AdviceReply
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.ZoneId

/** Wording around the PC's AI advice (DECISIONS M-60). */
class AdviceTextTest {

    private fun reply(
        ok: Boolean = true,
        cached: Boolean = false,
        error: String? = null,
        message: String? = null,
    ) = AdviceReply(
        ok = ok,
        text = if (ok) "Fine." else null,
        summary = null,
        model = "llama3.2:1b",
        generatedAt = "2026-11-19T01:30:00+00:00",
        cached = cached,
        error = error,
        message = message,
    )

    @Test
    fun meta_names_the_model_and_the_local_time() {
        assertEquals("모델 llama3.2:1b · 10:30에 작성", adviceMeta(reply(), ZoneId.of("Asia/Seoul")))
    }

    @Test
    fun meta_says_when_the_pc_handed_out_the_same_advice_again() {
        assertTrue(adviceMeta(reply(cached = true), ZoneId.of("Asia/Seoul")).endsWith("1분 안에 다시 물어 방금 것을 보여 줌"))
    }

    @Test
    fun known_failures_say_what_to_do_on_the_pc() {
        for (code in listOf(AdviceReply.DISABLED, AdviceReply.UNREACHABLE, AdviceReply.MODEL_MISSING, AdviceReply.TIMEOUT)) {
            val text = adviceError(reply(ok = false, error = code, message = "english detail"))
            assertFalse(code, text.contains("english detail"))
        }
        assertTrue(adviceError(reply(ok = false, error = AdviceReply.MODEL_MISSING)).contains("모델 받기"))
        assertTrue(adviceError(reply(ok = false, error = AdviceReply.UNREACHABLE)).contains("Ollama를 시작"))
    }

    @Test
    fun an_unknown_failure_shows_the_pc_detail() {
        assertEquals(
            "조언을 만들지 못했습니다. (out of memory)",
            adviceError(reply(ok = false, error = "server_error", message = "out of memory")),
        )
        assertEquals("조언을 만들지 못했습니다. (failed)", adviceError(reply(ok = false, error = "failed")))
    }
}
