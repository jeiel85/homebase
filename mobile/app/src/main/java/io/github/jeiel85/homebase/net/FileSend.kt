package io.github.jeiel85.homebase.net

import org.json.JSONObject
import java.time.OffsetDateTime

/**
 * Files a PC sends to this phone (DECISIONS M-65, docs/FILE_SHARE.md 2b), mirroring `LocalOpsBot.Core.Mobile.FileSend`
 * (fixtures `push-share.v1.json`, `transfer-offer.v1.json`, and the chunks of `transfer.v1.json`):
 * 1. a push of kind [PUSH_KIND] (a [ClipPush] whose id is the transfer id) sealing a [ShareNotice];
 * 2. `GET /api/v1/transfer/offer?id=` — a [ClipEnvelope] sealing the [TransferOffer] (names, sizes, hashes);
 * 3. `GET /api/v1/transfer/fetch?id=&f=&n=` — one chunk, sealed with [TransferCrypto] exactly like an upload's (2a);
 * 4. `POST /api/v1/transfer/received?id=` — every file is saved.
 * All of it under the clipboard key agreed with that PC (M-62).
 */
object FileSendProtocol {
    const val OFFER_PATH = "/api/v1/transfer/offer"
    const val FETCH_PATH = "/api/v1/transfer/fetch"
    const val RECEIVED_PATH = "/api/v1/transfer/received"

    const val PUSH_KIND = "share"

    /** Listed in `features` of `POST /api/v1/push`: the PC only offers files to phones that fetch them. */
    const val FEATURE = "share"

    /** Received without asking (DECISIONS M-65, decision 2): this much or less, on a network that isn't metered. */
    const val AUTO_RECEIVE_BYTES = 25_000_000L

    /** Whether a transfer of [totalBytes] is received at once rather than on the user's tap. */
    fun autoReceive(totalBytes: Long, metered: Boolean): Boolean = !metered && totalBytes in 0..AUTO_RECEIVE_BYTES

    /** Whether a notice stamped [ts] by the PC is past the day the PC keeps the files (unreadable stamps are not). */
    fun isExpired(ts: String, nowMillis: Long): Boolean {
        val at = runCatching { OffsetDateTime.parse(ts).toInstant().toEpochMilli() }.getOrNull() ?: return false
        return nowMillis - at > TransferProtocol.KEEP_FOR_MS
    }
}

/** What the `share` push says: enough for a notification. [name] is the first file's (cut short by the PC). */
data class ShareNotice(val count: Int, val totalBytes: Long, val name: String?, val ts: String) {
    companion object {
        /** The notice in [push], sealed with [clipKey]; null when it doesn't open or isn't a notice of this schema. */
        fun open(clipKey: ByteArray, push: ClipPush): ShareNotice? {
            if (!ClipProtocol.isValidId(push.id)) return null
            val plain = ClipCrypto.open(clipKey, push.nonce, push.data) ?: return null
            return try {
                val o = JSONObject(plain.toString(Charsets.UTF_8))
                if (o.optInt("schema") != SCHEMA) {
                    null
                } else {
                    ShareNotice(
                        count = o.optInt("count"),
                        totalBytes = o.optLong("totalBytes"),
                        name = if (o.isNull("name")) null else o.optString("name").ifEmpty { null },
                        ts = o.getString("ts"),
                    ).takeIf { it.count in 1..TransferProtocol.MAX_FILES && it.totalBytes >= 0 }
                }
            } catch (_: Exception) {
                null
            }
        }
    }
}

/** One file the PC offers, with its SHA-256 (lowercase hex) to check what arrives. */
data class OfferFile(val name: String, val size: Long, val mime: String?, val sha256: String)

/** The files the PC offers, in order. */
data class TransferOffer(val files: List<OfferFile>, val ts: String) {
    val totalBytes: Long get() = files.sumOf { it.size }

    companion object {
        private val HASH = Regex("^[0-9a-f]{64}$")

        /**
         * The offer in [envelope], sealed with [clipKey]; null when it doesn't open, isn't an offer of this schema, or is
         * out of bounds (no files, more than the PC may send, a negative size, a hash that isn't 64 lowercase hex).
         */
        fun open(clipKey: ByteArray, envelope: ClipEnvelope): TransferOffer? {
            if (!ClipProtocol.isValidId(envelope.id)) return null
            val plain = ClipCrypto.open(clipKey, envelope.nonce, envelope.data) ?: return null
            return try {
                val o = JSONObject(plain.toString(Charsets.UTF_8))
                if (o.optInt("schema") != SCHEMA) return null
                val files = o.getJSONArray("files").objects().map { f ->
                    OfferFile(
                        name = f.getString("name"),
                        size = f.getLong("size"),
                        mime = if (f.isNull("mime")) null else f.optString("mime").ifEmpty { null },
                        sha256 = f.getString("sha256"),
                    )
                }
                val ok = files.size in 1..TransferProtocol.MAX_FILES &&
                    files.all { it.size in 0..TransferProtocol.MAX_FILE_BYTES && HASH.matches(it.sha256) }
                if (ok) TransferOffer(files, o.optString("ts")) else null
            } catch (_: Exception) {
                null
            }
        }
    }
}

/**
 * A file name from a PC made safe to save under (DECISIONS M-65): no path, no control or bidirectional-override
 * characters (which can fake an extension), no leading or trailing dots and spaces, at most [MAX_CHARS] characters
 * keeping the extension. Never empty.
 */
object ReceivedNames {
    const val MAX_CHARS = 200

    fun clean(name: String?, fallback: String = "file"): String {
        val base = (name ?: "").substringAfterLast('/').substringAfterLast('\\')
        val kept = buildString {
            for (c in base) {
                if (Character.isISOControl(c) || isBidiControl(c) || c in "<>:\"|?*") continue
                append(c)
            }
        }.trim().trim('.', ' ')
        val s = kept.ifEmpty { fallback }
        val dot = s.lastIndexOf('.')
        val ext = if (dot > 0 && s.length - dot <= 20) s.substring(dot) else ""
        val stem = if (ext.isEmpty()) s else s.substring(0, dot)
        if (stem.length + ext.length <= MAX_CHARS) return s
        var cut = MAX_CHARS - ext.length
        if (cut > 0 && Character.isHighSurrogate(stem[cut - 1])) cut-- // don't split an emoji
        return stem.substring(0, cut.coerceAtLeast(1)) + ext
    }

    // LRM/RLM/ALM, the embedding and override controls, and the isolates: they reorder what is shown.
    private fun isBidiControl(c: Char): Boolean =
        c == '‎' || c == '‏' || c == '؜' || c in '‪'..'‮' || c in '⁦'..'⁩'
}
