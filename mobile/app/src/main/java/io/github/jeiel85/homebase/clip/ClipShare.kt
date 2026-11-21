package io.github.jeiel85.homebase.clip

import android.content.Context
import android.util.Log
import io.github.jeiel85.homebase.data.PcEntry
import io.github.jeiel85.homebase.data.PcStore
import io.github.jeiel85.homebase.net.ClipContent
import io.github.jeiel85.homebase.net.ClipCrypto
import io.github.jeiel85.homebase.net.ClipProtocol
import io.github.jeiel85.homebase.net.ClipPush
import io.github.jeiel85.homebase.net.ClipSendResult
import io.github.jeiel85.homebase.net.Forwarded
import io.github.jeiel85.homebase.net.HomebaseApi
import io.github.jeiel85.homebase.net.RelayPhone
import io.github.jeiel85.homebase.net.ShareRelay
import io.github.jeiel85.homebase.net.UnauthorizedException
import io.github.jeiel85.homebase.push.ClipHistory
import io.github.jeiel85.homebase.push.PushHistory
import io.github.jeiel85.homebase.push.RecentPushIds
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.time.OffsetDateTime
import java.util.concurrent.ConcurrentHashMap

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

    internal fun remember(context: Context, pcId: String) = prefs(context).edit().putString(KEY_LAST_PC, pcId).apply()

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

    /**
     * Sends [text] to [pc] and says how it went, in a line for a toast. Uses the clipboard key agreed with
     * that PC, agreeing one first when there is none. When the PC answers that it has no key for this phone
     * or can't open the text (its key differs — it was re-installed, or this phone's key was lost), a new
     * key is agreed and the text sent once more.
     */
    suspend fun send(context: Context, api: HomebaseApi, pc: PcEntry, text: String, sensitive: Boolean): String =
        send(context, api, pc, text, sensitive, phones = emptyList())

    /**
     * [send] through [pc] to other [phones] it listed (docs/SHARE_CONVENIENCE.md §4.8): the PC passes the text on
     * instead of putting it on its own clipboard, and says which phones it reached. Only for a PC that answered
     * `GET /api/v1/share/phones` — an older PC would ignore `to` and paste it itself.
     */
    suspend fun sendRelayed(context: Context, api: HomebaseApi, pc: PcEntry, text: String, phones: List<RelayPhone>): String =
        send(context, api, pc, text, sensitive = false, phones = phones)

    private suspend fun send(context: Context, api: HomebaseApi, pc: PcEntry, text: String, sensitive: Boolean, phones: List<RelayPhone>): String {
        problem(text)?.let { return it }
        val (line, outcome) = attempt(context, api, pc, text, sensitive, phones)
        // "보낸 클립보드" in the notification center (M-70): to whom, how it went, and the text unless it is sensitive.
        withContext(Dispatchers.IO) {
            try {
                val current = PcStore(context).get(pc.pcId) ?: pc
                PushHistory.record(
                    context,
                    ClipHistory.sent(
                        ClipProtocol.newId(), current.pcId, current.name, phones.map { it.name }, text, sensitive, outcome,
                        current.color, System.currentTimeMillis(),
                    ),
                )
                // The tile and the share sheet finish right after; keep the process until the entry is on disk.
                PushHistory.awaitWrites(HISTORY_WRITE_WAIT_MS)
            } catch (e: Exception) {
                Log.w(TAG, "sent text not recorded: ${e.javaClass.simpleName}")
            }
        }
        return line
    }

    /** One send and what came of it: a line for a toast, and a short one for the notification center. */
    private suspend fun attempt(
        context: Context,
        api: HomebaseApi,
        pc: PcEntry,
        text: String,
        sensitive: Boolean,
        phones: List<RelayPhone>,
    ): Pair<String, String> {
        // The entry the caller holds may predate a key agreed meanwhile (the registrar's, another send).
        val current = withContext(Dispatchers.IO) { PcStore(context).get(pc.pcId) } ?: pc
        val name = current.displayName
        val tooOld = "${name}의 Homebase를 업데이트하세요" to "실패: PC의 Homebase가 오래됨"
        val to = phones.map { it.deviceId }
        return try {
            val stored = ClipCrypto.decodeKey(current.clipKey)
            val key = stored ?: keyFor(context, api, current, stale = null) ?: return tooOld
            var result = api.sendClip(current, key, text, sensitive, to)
            if (stored != null && (result == ClipSendResult.KeyNeeded || result == ClipSendResult.Rejected)) {
                Log.i(TAG, "clipboard key with ${current.name} no longer matches; agreeing a new one")
                result = api.sendClip(current, keyFor(context, api, current, stale = key) ?: return tooOld, text, sensitive, to)
            }
            when (val r = result) {
                ClipSendResult.Sent -> {
                    if (phones.isEmpty()) {
                        remember(context, current.pcId)
                        "${name}에 보냈습니다" to SENT
                    } else {
                        // A PC that took `to` always answers with `forwarded`; without it, it pasted the text itself.
                        "${name}이(가) 다른 휴대폰에 넘기지 못해 PC 클립보드에 넣었습니다" to "PC 클립보드에 들어감(넘기지 못함)"
                    }
                }
                is ClipSendResult.Relayed -> if (phones.isEmpty()) {
                    remember(context, current.pcId)
                    "${name}에 보냈습니다" to SENT
                } else {
                    relayedLine(r.forwarded, phones, name) to relayedOutcome(r.forwarded, phones)
                }
                ClipSendResult.NoUserSession -> "${name}에 로그인한 사용자가 없습니다" to "실패: PC에 로그인한 사용자가 없음"
                ClipSendResult.ClipboardBusy -> "${name}의 클립보드가 사용 중이었습니다. 다시 보내세요" to "실패: PC의 클립보드가 사용 중"
                ClipSendResult.KeyNeeded -> "${name}와(과) 암호 키를 맞추지 못했습니다. 다시 보내세요" to "실패: 암호 키를 맞추지 못함"
                ClipSendResult.Unsupported -> tooOld
                ClipSendResult.Rejected -> "${name}이(가) 텍스트를 받지 않았습니다" to "실패: PC가 받지 않음"
                is ClipSendResult.Failed -> "${name}에 보내지 못했습니다: ${r.reason}" to "실패: ${r.reason}"
            }
        } catch (_: UnauthorizedException) {
            "${name}에서 이 휴대폰의 연결이 해제되었습니다. 다시 페어링하세요." to "실패: 연결이 해제됨"
        } catch (e: Exception) {
            Log.i(TAG, "clipboard to ${current.name} failed: ${e.message}")
            "${name}에 닿지 않습니다" to "실패: PC에 닿지 않음"
        }
    }

    private const val SENT = "보냄"

    /** A relayed send in the notification center's words: "보냄", "실패: …", or "일부만 보냄 — Galaxy Tab S9 실패". */
    internal fun relayedOutcome(forwarded: List<Forwarded>, phones: List<RelayPhone>): String {
        val failed = phones.filterNot { p -> forwarded.any { it.deviceId == p.deviceId && it.ok } }
        return when {
            failed.isEmpty() -> SENT
            failed.size == phones.size -> "실패: 다른 휴대폰에 넘기지 못함"
            else -> "일부만 보냄 — ${ShareRelay.names(failed.map { it.name })} 실패"
        }
    }

    /**
     * What a relayed send came to, in a line: "Galaxy Tab S9에 보냈습니다(집 PC 경유)", and the phones the PC could not
     * push it to. A phone the PC didn't mention counts as not reached.
     */
    internal fun relayedLine(forwarded: List<Forwarded>, phones: List<RelayPhone>, pcName: String): String {
        val ok = phones.filter { p -> forwarded.any { it.deviceId == p.deviceId && it.ok } }
        val failed = phones - ok.toSet()
        return listOfNotNull(
            ok.takeIf { it.isNotEmpty() }?.let { "${ShareRelay.names(it.map { p -> p.name })}에 보냈습니다(${pcName} 경유)" },
            failed.takeIf { it.isNotEmpty() }?.let { "${ShareRelay.names(it.map { p -> p.name })}에 보내지 못했습니다(${pcName} 경유)" },
        ).joinToString("\n")
    }

    /**
     * One key exchange at a time per PC. The PC keeps the key of the exchange it answered last, so two at once
     * (the registrar's and a send's) could leave this phone holding the other one.
     */
    private val agreeing = ConcurrentHashMap<String, Mutex>()

    /**
     * The key to use with [pc]: under that PC's lock, the stored one when there is one other than [stale] (another
     * flow agreed it meanwhile), else a newly agreed and stored one. Null when the PC is too old for clipboard sharing.
     */
    internal suspend fun keyFor(context: Context, api: HomebaseApi, pc: PcEntry, stale: ByteArray?): ByteArray? =
        agreeing.getOrPut(pc.pcId) { Mutex() }.withLock {
            val stored = withContext(Dispatchers.IO) { PcStore(context).get(pc.pcId) }?.let { ClipCrypto.decodeKey(it.clipKey) }
            if (stored != null && (stale == null || !stored.contentEquals(stale))) return@withLock stored
            val key = api.agreeClipKey(pc) ?: return@withLock null
            withContext(Dispatchers.IO) { PcStore(context).setClipKey(pc.pcId, ClipCrypto.encodeKey(key)) }
            key
        }

    /** PCs that answered 404 to the key exchange in this process (older than clipboard sharing): not asked again. */
    private val withoutClipboard: MutableSet<String> = ConcurrentHashMap.newKeySet()

    /**
     * After push registration with [pc] succeeded (the registrar): agrees a clipboard key when there is
     * none yet. A PC only offers to send text to phones it holds a key for, so this is what makes PC → phone
     * work before this phone ever sent anything. Best effort: failures wait for the next registration pass,
     * a PC too old for it is skipped quietly.
     */
    suspend fun ensureKey(context: Context, api: HomebaseApi, pc: PcEntry) {
        if (pc.clipKey != null || pc.pcId in withoutClipboard) return
        val current = withContext(Dispatchers.IO) { PcStore(context).get(pc.pcId) } ?: return
        if (current.clipKey != null) return
        try {
            if (keyFor(context, api, current, stale = null) == null) {
                withoutClipboard.add(pc.pcId)
            } else {
                Log.i(TAG, "clipboard key ready with ${current.name}")
            }
        } catch (e: Exception) {
            Log.i(TAG, "clipboard key with ${current.name} deferred: ${e.message}")
        }
    }

    /** Fetches the long text [pcId] offered (notification tap) and copies it; a line for a toast. */
    suspend fun fetch(context: Context, api: HomebaseApi, pcId: String, id: String): String {
        if (!ClipProtocol.isValidId(id)) return "받을 텍스트를 찾지 못했습니다"
        val pc = withContext(Dispatchers.IO) { PcStore(context).get(pcId) } ?: return "이 PC는 더 이상 연결되어 있지 않습니다"
        val name = pc.displayName
        val key = ClipCrypto.decodeKey(pc.clipKey) ?: return "받을 텍스트를 열 키가 없습니다"
        return try {
            val content = api.fetchClip(pc, key, id) ?: return "시간이 지나 받을 수 없습니다"
            val text = content.text ?: return "시간이 지나 받을 수 없습니다"
            ClipNotifier.cancel(context, id)
            if (PhoneClipboard.write(context, text, content.sensitive)) {
                withContext(Dispatchers.IO) { record(context, pc, id, text, content) }
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
        val key = ClipCrypto.decodeKey(pc.clipKey) ?: run {
            Log.i(TAG, "dropped clipboard text from ${pc.name}: no clipboard key agreed with it")
            return
        }
        val content = push.open(key) ?: run {
            Log.w(TAG, "dropped clipboard text that doesn't open with ${pc.name}'s key")
            return
        }
        if (!RecentPushIds(context).firstSighting("clip:${push.id}")) return
        if (isStale(content.ts, System.currentTimeMillis())) {
            Log.i(TAG, "dropped stale clipboard text from ${pc.name}")
            return
        }
        val name = pc.displayName
        // Another phone's text the PC passed on (docs/SHARE_CONVENIENCE.md §4.8) names that phone in `from`.
        deliver(
            text = content.text,
            write = { PhoneClipboard.write(context, it, content.sensitive) },
            offer = { ClipNotifier.offer(context, pc.pcId, name, push.id, content.chars, content.from) },
            keep = { record(context, pc, push.id, it, content) },
            awaitKept = { PushHistory.awaitWrites(HISTORY_WRITE_WAIT_MS) },
            copied = { ClipNotifier.copied(context, name, it, content.sensitive, push.id, content.from) },
        )
    }

    /**
     * What [receive] does with text that opened, in its order — pure of Android, so tests pin it (#117). Without
     * [text] (too long for a push) only the "tap to copy" offer. Otherwise onto the clipboard, and only when that
     * worked: the notification center entry, waiting until it is on disk, then the "copied" notification — so a tap on
     * that notification finds the entry (M-70).
     */
    internal inline fun deliver(
        text: String?,
        write: (String) -> Boolean,
        offer: () -> Unit,
        keep: (String) -> Unit,
        awaitKept: () -> Unit,
        copied: (String) -> Unit,
    ) {
        if (text == null) {
            offer()
        } else if (write(text)) {
            keep(text)
            awaitKept()
            copied(text)
        }
    }

    /** "받은 클립보드" in the notification center (M-70): from whom, and the text unless it is sensitive. */
    private fun record(context: Context, pc: PcEntry, id: String, text: String, content: ClipContent) {
        try {
            val now = System.currentTimeMillis()
            val sentAt = runCatching { OffsetDateTime.parse(content.ts).toInstant().toEpochMilli() }.getOrDefault(now)
            PushHistory.record(
                context,
                ClipHistory.received(id, pc.pcId, pc.name, content.from, text, content.sensitive, sentAt, pc.color, now),
            )
        } catch (e: Exception) {
            Log.w(TAG, "received text not recorded: ${e.javaClass.simpleName}")
        }
    }

    private const val HISTORY_WRITE_WAIT_MS = 2_000L

    /** Whether text stamped [ts] by the PC is too old to put on the clipboard now; unreadable stamps are not. */
    internal fun isStale(ts: String, nowMillis: Long): Boolean {
        val at = runCatching { OffsetDateTime.parse(ts).toInstant().toEpochMilli() }.getOrNull() ?: return false
        return nowMillis - at > STALE_MS
    }

    private fun prefs(context: Context) = context.applicationContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
}
