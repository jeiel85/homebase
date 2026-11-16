package io.github.jeiel85.homebase.ui

import io.github.jeiel85.homebase.net.AgentInfo
import java.util.Locale

/**
 * How an AI session's detail reads on its work-tab card (DECISIONS M-44). Pure, so unit tests pin
 * the wording; the card only lays these out.
 */
object AgentLook {
    /** "3분", "1시간 5분", "방금" for under a minute. */
    fun span(secs: Long): String = when {
        secs < 60 -> "방금"
        secs < 3600 -> "${secs / 60}분"
        else -> "${secs / 3600}시간 ${secs % 3600 / 60}분"
    }

    /**
     * The card's first line. [sinceSecs] is how long the session has been in its status,
     * [activitySecs] how long ago its transcript last changed; either may be unknown.
     */
    fun statusLine(agent: AgentInfo?, sinceSecs: Long?, activitySecs: Long?): String {
        val since = sinceSecs?.let { if (it < 60) "방금부터" else "${span(it)}째" }
        return when (agent?.status) {
            AgentInfo.WAITING -> listOfNotNull("입력 대기", since).joinToString(" · ")
            AgentInfo.BUSY -> listOfNotNull("작업 중", since).joinToString(" · ")
            AgentInfo.IDLE -> listOfNotNull("쉬는 중", activitySecs?.let { activity(it) }).joinToString(" · ")
            else -> activitySecs?.let { activity(it) } ?: ""
        }
    }

    private fun activity(secs: Long): String = if (secs < 60) "방금 활동" else "${span(secs)} 전 활동"

    /**
     * "Opus 5.5" for Claude's ids (claude-opus-5-5), other ids as they are (gpt-5.5); the reasoning
     * effort follows when the tool records it.
     */
    fun model(model: String?, effort: String?): String? {
        val name = model?.let { m ->
            if (m.startsWith("claude-")) {
                m.removePrefix("claude-")
                    .replace(Regex("""(\d+)-(\d+)(?=$|-)"""), "$1.$2")
                    .replace('-', ' ')
                    .replaceFirstChar { it.titlecase(Locale.ROOT) }
            } else {
                m
            }
        }
        return listOfNotNull(name, effort?.takeIf { it.isNotBlank() }).joinToString(" · ").ifEmpty { null }
    }

    /** Claude Code's permission modes in the user's words; an unknown one as Claude names it. */
    fun mode(mode: String?): String? = when (mode) {
        null, "", "default", "normal" -> null
        "plan" -> "계획 모드"
        "acceptEdits" -> "편집 자동 수락"
        "bypassPermissions" -> "권한 확인 생략"
        else -> mode
    }

    /** 184503 → "185k", 1_200_000 → "1.2M". */
    fun tokens(n: Long): String = when {
        n < 1000 -> "$n"
        n < 1_000_000 -> "${(n + 500) / 1000}k"
        else -> String.format(Locale.ROOT, "%.1fM", n / 1_000_000.0)
    }

    /** "컨텍스트 16%" when the window is known, else the tokens in use. */
    fun context(agent: AgentInfo): String? {
        val share = agent.contextShare
        return when {
            share != null -> "컨텍스트 ${(share * 100).toInt()}%"
            agent.contextTokens != null -> "컨텍스트 ${tokens(agent.contextTokens)}"
            else -> null
        }
    }

    /** Small facts after the model: mode, last tool, pull request, session total. */
    fun facts(agent: AgentInfo): List<String> = listOfNotNull(
        mode(agent.mode),
        agent.lastTool?.let { "마지막 도구 ${it.removePrefix("mcp__").replace("__", " › ")}" },
        agent.prNumber?.let { "PR #$it" },
        agent.totalTokens?.let { "누적 ${tokens(it)} 토큰" },
    )
}
