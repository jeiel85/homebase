package io.github.jeiel85.homebase.clip

import android.content.Context
import android.util.Log
import io.github.jeiel85.homebase.data.PcEntry
import io.github.jeiel85.homebase.data.PcStore
import io.github.jeiel85.homebase.net.ClipProtocol
import io.github.jeiel85.homebase.net.ClipPush
import io.github.jeiel85.homebase.net.ClipSendResult
import io.github.jeiel85.homebase.net.HomebaseApi
import io.github.jeiel85.homebase.net.UnauthorizedException
import io.github.jeiel85.homebase.push.RecentPushIds
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.time.OffsetDateTime

/**
 * Sending this phone's clipboard text to a PC and taking text a PC sent (DECISIONS M-62,
 * docs/CLIPBOARD_SHARE.md phase 1). Every way in — the share sheet, the quick settings tile, a PC
 * card's menu — ends in [send], so they all say the same thing about the outcome.
 */
object ClipShare {
    private const val TAG = "HomebaseClip"
    private const val PREFS = "homebase.clip"
    private const val KEY_LAST_PC = "last_pc"

    /**
     * A clip push older than this (phone clock) is not put on the clipboard: the PC gives it a 10-minute
     * TTL, but a push server that keeps messages longer must not change the clipboard hours later.
     */
    internal const val STALE_MS = 30 * 60_000L

    /** Why [text] can't be sent, or null when it can. */
    fun problem(text: String?): String? = when {
        text.isNullOrEmpty() -> "클립보드에 보낼 텍스트가 없습니다"
        text.length > ClipProtocol.MAX_CHARS -> "텍스트가 너무 깁니다 (최대 ${String.format(java.util.Locale.ROOT, "%,d", ClipProtocol.MAX_CHARS)}자)"
        else -> null
    }

    /** The PC this phone last sent text to, offered first next time. */
    fun lastPcId(context: Context): String? = prefs(context).getString(KEY_LAST_PC, null)

    private fun remember(context: Context, pcId: String) = prefs(context).edit().putString(KEY_LAST_PC, pcId).apply()

    /** [pcs] with the last used one first, the rest in list order. */
    fun ordered(pcs: List<PcEntry>, lastPcId: String?): List<PcEntry> {
        val last = pcs.firstOrNull { it.pcId == lastPcId } ?: return pcs
        return listOf(last) + pcs.filter { it.pcId != last.pcId }
    }

    /**
     * Where the tile sends without asking: the only PC, or the one used last. Null means ask. The share
     * sheet always asks — another app can open it with any text, and only a tap should put that on a PC.
     */
    fun autoTarget(pcs: List<PcEntry>, lastPcId: String?): PcEntry? =
        pcs.singleOrNull() ?: pcs.firstOrNull { it.pcId == lastPcId }

    /** Sends [text] to [pc] and says how it went, in a line for a toast. */
    suspend fun send(context: Context, api: HomebaseApi, pc: PcEntry, text: String, sensitive: Boolean): String {
        problem(text)?.let { return it }
        val name = pc.displayName
        return try {
            when (val result = api.sendClip(pc, text, sensitive)) {
                ClipSendResult.Sent -> {
                    remember(context, pc.pcId)
                    "${name}에 보냈습니다"
                }
                ClipSendResult.NoUserSession -> "${name}에 로그인한 사용자가 없습니다"
                ClipSendResult.Unsupported -> "${name}의 Homebase를 업데이트하세요"
                ClipSendResult.Rejected -> "${name}이(가) 텍스트를 받지 않았습니다"
                is ClipSendResult.Failed -> "${name}에 보내지 못했습니다: ${result.reason}"
            }
        } catch (_: UnauthorizedException) {
            "${name}에서 이 휴대폰의 연결이 해제되었습니다. 다시 페어링하세요."
        } catch (e: Exception) {
            Log.i(TAG, "clipboard to ${pc.name} failed: ${e.message}")
            "${name}에 닿지 않습니다"
        }
    }

    /** Fetches the long text [pcId] offered (notification tap) and copies it; a line for a toast. */
    suspend fun fetch(context: Context, api: HomebaseApi, pcId: String, id: String): String {
        if (!ClipProtocol.isValidId(id)) return "받을 텍스트를 찾지 못했습니다"
        val pc = withContext(Dispatchers.IO) { PcStore(context).get(pcId) } ?: return "이 PC는 더 이상 연결되어 있지 않습니다"
        val name = pc.displayName
        return try {
            val content = api.fetchClip(pc, id) ?: return "시간이 지나 받을 수 없습니다"
            val text = content.text ?: return "시간이 지나 받을 수 없습니다"
            ClipNotifier.cancel(context, id)
            if (PhoneClipboard.write(context, text, content.sensitive)) {
                "${name}에서 받은 텍스트를 복사했습니다 (${String.format(java.util.Locale.ROOT, "%,d", text.length)}자)"
            } else {
                "클립보드에 넣지 못했습니다"
            }
        } catch (_: UnauthorizedException) {
            "${name}에서 이 휴대폰의 연결이 해제되었습니다. 다시 페어링하세요."
        } catch (e: Exception) {
            Log.i(TAG, "clipboard fetch from ${pc.name} failed: ${e.message}")
            "${name}에 닿지 않습니다"
        }
    }

    /**
     * A decrypted push of kind `clip` (called off the main thread by the push receiver). Dropped when it is
     * not from a paired PC, doesn't open with this phone's key for that PC, was seen already, or is stale.
     */
    fun receive(context: Context, plain: String) {
        val push = ClipPush.parse(plain) ?: run {
            Log.w(TAG, "dropped an unreadable clipboard push")
            return
        }
        val pc = PcStore(context).get(push.pcId) ?: run {
            Log.i(TAG, "dropped clipboard text from an unpaired PC")
            return
        }
        val content = push.open(pc.token) ?: run {
            Log.w(TAG, "dropped clipboard text that doesn't open with ${pc.name}'s key")
            return
        }
        if (!RecentPushIds(context).firstSighting("clip:${push.id}")) return
        if (isStale(content.ts, System.currentTimeMillis())) {
            Log.i(TAG, "dropped stale clipboard text from ${pc.name}")
            return
        }
        val name = pc.displayName
        val text = content.text
        if (text == null) {
            ClipNotifier.offer(context, pc.pcId, name, push.id, content.chars)
        } else if (PhoneClipboard.write(context, text, content.sensitive)) {
            ClipNotifier.copied(context, name, text, content.sensitive, push.id)
        }
    }

    /** Whether text stamped [ts] by the PC is too old to put on the clipboard now; unreadable stamps are not. */
    internal fun isStale(ts: String, nowMillis: Long): Boolean {
        val at = runCatching { OffsetDateTime.parse(ts).toInstant().toEpochMilli() }.getOrNull() ?: return false
        return nowMillis - at > STALE_MS
    }

    private fun prefs(context: Context) = context.applicationContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
}
