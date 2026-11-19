package io.github.jeiel85.homebase.ui

import android.content.Context
import androidx.compose.runtime.mutableStateMapOf
import io.github.jeiel85.homebase.data.PcEntry
import io.github.jeiel85.homebase.data.PcStore
import io.github.jeiel85.homebase.net.AdviceReply
import io.github.jeiel85.homebase.net.HomebaseApi
import io.github.jeiel85.homebase.net.UnauthorizedException
import io.github.jeiel85.homebase.push.HistoryEntry
import io.github.jeiel85.homebase.push.PushHistory
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.time.OffsetDateTime

/** "AI 조언" under a PC's panel (DECISIONS M-60): nothing asked yet, asking, the PC's answer, or why there is none. */
internal sealed interface AdviceUi {
    data object Idle : AdviceUi
    data object Asking : AdviceUi
    data class Done(val reply: AdviceReply) : AdviceUi
    data class Failed(val message: String) : AdviceUi
}

/**
 * The PC's AI advice, one card per PC, kept for the app's process rather than one screen (DECISIONS
 * M-61): turning the phone keeps a shown answer and a request under way, a push's "AI 조언" button can
 * start one before the PC's screen is up, and closing the card cancels a request still under way.
 * Newly written advice is also kept in the notification center.
 */
object AdviceRequests {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private val states = mutableStateMapOf<String, AdviceUi>()
    private val jobs = HashMap<String, Job>()

    /** What [pcId]'s card shows; read in a composable, it recomposes on change. */
    internal fun state(pcId: String): AdviceUi = states[pcId] ?: AdviceUi.Idle

    /** Asks [pcId]'s AI, unless a request for it is already under way. Main thread. */
    fun ask(context: Context, api: HomebaseApi, store: PcStore, pcId: String) {
        if (states[pcId] is AdviceUi.Asking) return
        val pc = store.get(pcId) ?: return
        val app = context.applicationContext
        states[pcId] = AdviceUi.Asking
        jobs[pcId] = scope.launch {
            val result = try {
                api.advise(pc)?.let { AdviceUi.Done(it) }
                    ?: AdviceUi.Failed("이 PC의 Homebase가 이 기능보다 오래된 버전입니다. PC를 v0.46.0 이상으로 업데이트하면 여기서 조언을 받을 수 있습니다.")
            } catch (e: kotlin.coroutines.cancellation.CancellationException) {
                throw e
            } catch (e: UnauthorizedException) {
                AdviceUi.Failed(e.message ?: "이 PC에서 휴대폰 연결이 해제되었습니다.")
            } catch (e: Exception) {
                AdviceUi.Failed("PC에 연결하지 못했습니다. (${HomebaseApi.describe(e)})")
            }
            states[pcId] = result
            jobs.remove(pcId)
            val entry = (result as? AdviceUi.Done)?.let { adviceHistoryEntry(pc, it.reply, System.currentTimeMillis()) }
            // Deleted from the list meanwhile (close() normally cancels first): keep nothing for it.
            if (entry != null && store.get(pcId) != null) {
                // Loading the history reads the disk (and the Keystore) the first time.
                withContext(Dispatchers.IO) {
                    PushHistory.ensureLoaded(app)
                    // The PC hands the same advice out again for a minute: keep it once.
                    if (PushHistory.state.value.entries.none { it.pcId == entry.pcId && it.id == entry.id }) PushHistory.record(app, entry)
                }
            }
        }
    }

    /** Closes [pcId]'s card; a request still under way is cancelled. Main thread. Also when the PC is deleted. */
    fun close(pcId: String) {
        jobs.remove(pcId)?.cancel()
        states.remove(pcId)
    }
}

private val LIST_MARKER = Regex("""^(?:[-*•]|\d+[.)])\s*""")

/** Category the notification center files advice under — the "PC 상태 조언" kind (DECISIONS M-25). */
internal const val ADVICE_CATEGORY = "advisor"

/**
 * The notification-center entry for [reply] (DECISIONS M-61), or null when there is no advice to keep.
 * Its id comes from when the PC wrote it, so the same advice handed out again is recognised. It is
 * filed quietly (center only): it was read on the PC's screen, not posted to the shade.
 */
internal fun adviceHistoryEntry(pc: PcEntry, reply: AdviceReply, now: Long): HistoryEntry? {
    val text = reply.text
    if (!reply.ok || text == null) return null
    val at = reply.generatedAt?.let { runCatching { OffsetDateTime.parse(it).toInstant().toEpochMilli() }.getOrNull() } ?: now
    // A list marker ("-", "*", "•", "1.", "2)") and markdown emphasis don't belong in a title.
    val firstLine = text.lineSequence()
        .map { it.trim().replace(LIST_MARKER, "").replace("**", "").replace("__", "").trim() }
        .firstOrNull { it.isNotEmpty() }.orEmpty()
    return HistoryEntry(
        id = "advice-$at",
        pcId = pc.pcId,
        pcName = pc.name,
        category = ADVICE_CATEGORY,
        source = "AI 조언",
        title = if (firstLine.length <= 120) firstLine else firstLine.take(119) + "…",
        body = text,
        count = 1,
        sentAt = at,
        receivedAt = now,
        shown = false,
        quiet = true,
    )
}
