package io.github.jeiel85.homebase.ui

import io.github.jeiel85.homebase.net.AgentInfo
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/** Wording of the AI session card (DECISIONS M-44). */
class AgentLookTest {

    @Test
    fun status_line_leads_with_what_the_session_does() {
        assertEquals("입력 대기 · 5분째", AgentLook.statusLine(AgentInfo(status = AgentInfo.WAITING), 300, 10))
        assertEquals("작업 중 · 방금부터", AgentLook.statusLine(AgentInfo(status = AgentInfo.BUSY), 20, 5))
        assertEquals("작업 중 · 1시간 5분째", AgentLook.statusLine(AgentInfo(status = AgentInfo.BUSY), 3900, 5))
        assertEquals("쉬는 중 · 3분 전 활동", AgentLook.statusLine(AgentInfo(status = AgentInfo.IDLE), 999, 180))
        assertEquals("입력 대기", AgentLook.statusLine(AgentInfo(status = AgentInfo.WAITING), null, null))
        // A PC older than v0.32.0 sends no detail: the line is the last activity, as before.
        assertEquals("방금 활동", AgentLook.statusLine(null, null, 30))
        assertEquals("2분 전 활동", AgentLook.statusLine(AgentInfo(), null, 150))
        assertEquals("", AgentLook.statusLine(null, null, null))
    }

    @Test
    fun claude_model_ids_read_as_names_and_others_stay() {
        assertEquals("Opus 5.5", AgentLook.model("claude-opus-5-5", null))
        assertEquals("Sonnet 4.5 · high", AgentLook.model("claude-sonnet-4-5", "high"))
        assertEquals("Haiku 4.5 20251001", AgentLook.model("claude-haiku-4-5-20251001", null))
        assertEquals("gpt-5.6-luna · low", AgentLook.model("gpt-5.6-luna", "low"))
        assertNull(AgentLook.model(null, null))
    }

    @Test
    fun context_is_a_share_when_the_window_is_known() {
        assertEquals("컨텍스트 16%", AgentLook.context(AgentInfo(contextTokens = 42000, contextWindow = 258400)))
        assertEquals("컨텍스트 185k", AgentLook.context(AgentInfo(contextTokens = 184503)))
        assertNull(AgentLook.context(AgentInfo()))
        assertEquals(1f, AgentInfo(contextTokens = 300, contextWindow = 200).contextShare)
    }

    @Test
    fun tokens_shorten() {
        assertEquals("950", AgentLook.tokens(950))
        assertEquals("185k", AgentLook.tokens(184503))
        assertEquals("1.2M", AgentLook.tokens(1_200_000))
    }

    @Test
    fun facts_name_mode_tool_pr_and_total() {
        assertEquals(
            listOf("계획 모드", "마지막 도구 github › create_pr", "PR #56", "누적 51k 토큰"),
            AgentLook.facts(AgentInfo(mode = "plan", lastTool = "mcp__github__create_pr", prNumber = 56, totalTokens = 51000)),
        )
        assertEquals(emptyList<String>(), AgentLook.facts(AgentInfo(mode = "default")))
        assertEquals("custom", AgentLook.mode("custom"))
    }
}
