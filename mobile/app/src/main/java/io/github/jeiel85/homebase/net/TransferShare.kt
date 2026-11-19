package io.github.jeiel85.homebase.net

import org.json.JSONArray
import org.json.JSONObject
import java.nio.ByteBuffer
import java.time.Instant
import javax.crypto.Cipher
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/**
 * Files sent from this phone to a PC (DECISIONS M-64, docs/FILE_SHARE.md 2a), mirroring
 * `LocalOpsBot.Core.Mobile.TransferShare` (fixture `transfer.v1.json`). A transfer is 1–[MAX_FILES] files cut
 * into [CHUNK_BYTES] chunks:
 * 1. `POST /api/v1/transfer` — a [ClipEnvelope] (id = the transfer id this phone picked) sealing the metadata
 *    with the clipboard key agreed with that PC (M-62).
 * 2. `POST /api/v1/transfer/chunk?id=&f=&n=` — one chunk sealed with the transfer key at its position.
 * 3. `GET /api/v1/transfer?id=` — which chunks arrived, to go on after a break.
 * 4. `POST /api/v1/transfer/done?id=` — the SHA-256 of every file, sealed like the metadata.
 */
object TransferProtocol {
    const val PATH = "/api/v1/transfer"
    const val CHUNK_PATH = "/api/v1/transfer/chunk"
    const val DONE_PATH = "/api/v1/transfer/done"

    /** Plaintext chunk size; the last chunk of a file is shorter, an empty file has none. */
    const val CHUNK_BYTES = 4 * 1024 * 1024

    const val MAX_FILES = 20
    const val MAX_FILE_BYTES = 2L * 1024 * 1024 * 1024
    const val MAX_TRANSFER_BYTES = 4L * 1024 * 1024 * 1024
    const val MAX_NAME_CHARS = 1024
    const val MAX_MIME_CHARS = 128

    /** How long a PC keeps an unfinished transfer; this phone gives up after the same time. */
    const val KEEP_FOR_MS = 24 * 60 * 60_000L

    fun chunkCount(size: Long): Long = if (size <= 0) 0 else (size + CHUNK_BYTES - 1) / CHUNK_BYTES

    /** Plaintext length of chunk [n] of a file of [size] bytes (0 when out of range). */
    fun chunkLength(size: Long, n: Long): Int =
        if (n < 0 || n >= chunkCount(size)) 0 else minOf(CHUNK_BYTES.toLong(), size - n * CHUNK_BYTES).toInt()

    /**
     * Why [files] can't go in one transfer, in a line for the user, or null when they can. The PC checks the
     * same bounds (413 `too_large`); saying so before anything is sent saves a pointless upload.
     */
    fun problem(files: List<TransferFile>): String? = when {
        files.isEmpty() -> "보낼 파일이 없습니다"
        files.size > MAX_FILES -> "한 번에 ${MAX_FILES}개까지 보낼 수 있습니다 (${files.size}개 고름)"
        files.any { it.size > MAX_FILE_BYTES } -> "파일 하나는 2GB까지 보낼 수 있습니다"
        files.sumOf { it.size } > MAX_TRANSFER_BYTES -> "한 번에 4GB까지 보낼 수 있습니다"
        else -> null
    }
}

/** One file as this phone describes it to the PC (the PC never takes [name] as a path). */
data class TransferFile(val name: String, val size: Long, val mime: String? = null) {
    fun toJson(): JSONObject = JSONObject()
        .put("name", name.take(TransferProtocol.MAX_NAME_CHARS))
        .put("size", size)
        .apply { mime?.takeIf { it.isNotEmpty() && it.length <= TransferProtocol.MAX_MIME_CHARS }?.let { put("mime", it) } }
}

/**
 * The transfer key and chunk sealing. Transfer key = HKDF-SHA256 (IKM = the clipboard key agreed with the PC,
 * salt = 32 zero bytes, info = `"homebase transfer v1"` followed by the 16 bytes of the transfer id). Chunk =
 * AES-256-GCM with nonce = file index (4 bytes) ‖ chunk index (8 bytes), big-endian, no additional data; the
 * request body is ciphertext ‖ tag. A key per transfer makes positional nonces safe, and a chunk moved to another
 * position doesn't open.
 */
object TransferCrypto {
    private val INFO = "homebase transfer v1".toByteArray(Charsets.UTF_8)
    private val SALT = ByteArray(32)

    fun transferKey(clipKey: ByteArray, transferId: String): ByteArray {
        require(ClipProtocol.isValidId(transferId)) { "A transfer id is 32 lowercase hex characters." }
        val prk = hmac(SALT, clipKey)
        // L = 32 is one block: T(1) = HMAC(PRK, info || 0x01).
        return hmac(prk, INFO + hex(transferId) + byteArrayOf(1)).copyOf(ClipCrypto.KEY_BYTES)
    }

    fun nonce(file: Int, chunk: Long): ByteArray =
        ByteBuffer.allocate(ClipCrypto.NONCE_BYTES).putInt(file).putLong(chunk).array()

    /** Seals [length] bytes of [plain] from [offset]; the result is ciphertext ‖ tag. */
    fun sealChunk(transferKey: ByteArray, file: Int, chunk: Long, plain: ByteArray, offset: Int = 0, length: Int = plain.size): ByteArray {
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, SecretKeySpec(transferKey, "AES"), GCMParameterSpec(ClipCrypto.TAG_BITS, nonce(file, chunk)))
        return cipher.doFinal(plain, offset, length)
    }

    /** The plaintext, or null when it doesn't open at that position with that key. */
    fun openChunk(transferKey: ByteArray, file: Int, chunk: Long, sealed: ByteArray): ByteArray? = try {
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, SecretKeySpec(transferKey, "AES"), GCMParameterSpec(ClipCrypto.TAG_BITS, nonce(file, chunk)))
        cipher.doFinal(sealed)
    } catch (_: Exception) {
        null
    }

    private fun hex(id: String): ByteArray = ByteArray(id.length / 2) { i -> id.substring(i * 2, i * 2 + 2).toInt(16).toByte() }

    private fun hmac(key: ByteArray, data: ByteArray): ByteArray =
        Mac.getInstance("HmacSHA256").apply { init(SecretKeySpec(key, "HmacSHA256")) }.doFinal(data)
}

/** Metadata and hashes travel as a [ClipEnvelope] (id = transfer id) sealed with the agreed clipboard key. */
object TransferMessages {
    fun sealMeta(clipKey: ByteArray, transferId: String, files: List<TransferFile>, ts: String = Instant.now().toString(), nonce: ByteArray? = null): ClipEnvelope {
        val meta = JSONObject()
            .put("schema", SCHEMA)
            .put("files", JSONArray().apply { files.forEach { put(it.toJson()) } })
            .put("ts", ts)
        return seal(clipKey, transferId, meta, nonce)
    }

    /** [sha256] is the lowercase hex SHA-256 of each file, in order. */
    fun sealHashes(clipKey: ByteArray, transferId: String, sha256: List<String>, nonce: ByteArray? = null): ClipEnvelope =
        seal(clipKey, transferId, JSONObject().put("schema", SCHEMA).put("sha256", JSONArray(sha256)), nonce)

    /** The files of a sealed metadata envelope, or null when it doesn't open or isn't metadata of this schema. */
    fun openMeta(clipKey: ByteArray, envelope: ClipEnvelope): List<TransferFile>? = try {
        val o = open(clipKey, envelope)
        val files = o?.optJSONArray("files")
        if (o == null || files == null) {
            null
        } else {
            files.objects().map { f ->
                TransferFile(f.getString("name"), f.getLong("size"), if (f.isNull("mime")) null else f.optString("mime").ifEmpty { null })
            }
        }
    } catch (_: Exception) {
        null
    }

    /** The hashes of a sealed hashes envelope, or null. */
    fun openHashes(clipKey: ByteArray, envelope: ClipEnvelope): List<String>? = try {
        open(clipKey, envelope)?.optJSONArray("sha256")?.strings()
    } catch (_: Exception) {
        null
    }

    private fun seal(clipKey: ByteArray, transferId: String, o: JSONObject, nonce: ByteArray?): ClipEnvelope {
        val (n, data) = ClipCrypto.seal(clipKey, o.toString().toByteArray(Charsets.UTF_8), nonce)
        return ClipEnvelope(transferId, n, data)
    }

    private fun open(clipKey: ByteArray, envelope: ClipEnvelope): JSONObject? {
        if (!ClipProtocol.isValidId(envelope.id)) return null
        val plain = ClipCrypto.open(clipKey, envelope.nonce, envelope.data) ?: return null
        val o = JSONObject(plain.toString(Charsets.UTF_8))
        return if (o.optInt("schema") == SCHEMA) o else null
    }
}

/**
 * Answer to `POST` and `GET /api/v1/transfer`. [have] holds, per file, the chunks that arrived as
 * [ChunkRanges] text (`"0-127,130"`, empty when none).
 */
data class TransferStatus(val id: String, val state: String, val chunkBytes: Int, val have: List<String>) {
    /** The chunks of file [file] that arrived; empty when the PC listed none (or fewer files). */
    fun arrived(file: Int): Set<Long> = ChunkRanges.parse(have.getOrNull(file))?.toSet() ?: emptySet()

    companion object {
        fun parse(json: String?): TransferStatus? = try {
            val o = JSONObject(json ?: "")
            if (o.optInt("schema") != SCHEMA) {
                null
            } else {
                TransferStatus(
                    o.getString("id"),
                    o.optString("state"),
                    o.optInt("chunkBytes", TransferProtocol.CHUNK_BYTES),
                    o.optJSONArray("have")?.strings() ?: emptyList(),
                )
            }
        } catch (_: Exception) {
            null
        }
    }
}

/** What a PC answered to one transfer call, and the path it answered on (tried first next time). */
data class TransferAnswer(val code: Int, val body: String?, val via: Endpoint, val bytes: ByteArray? = null) {
    val ok: Boolean get() = code in 200..299

    /** The `error` code of a `{schema, error, message}` body. */
    val error: String? get() = errorCode(body)

    val message: String? get() = errorMessage(body)
}

/** Error codes of the transfer routes (with `clip_key_needed` from M-62). */
object TransferErrors {
    const val INVALID = "invalid_transfer"
    const val TOO_LARGE = "too_large"
    const val NO_SPACE = "insufficient_storage"
    const val UNKNOWN = "unknown_transfer"
    const val BUSY = "busy"
    const val INCOMPLETE = "incomplete"
    const val HASH_MISMATCH = "hash_mismatch"
    const val BAD_CHUNK = "bad_chunk"
    const val KEY_NEEDED = "clip_key_needed"
}

/** Chunk numbers as compact ranges: `"0-127,130,132-140"` (sorted, empty for none). */
object ChunkRanges {
    fun format(chunks: Iterable<Long>): String {
        val runs = mutableListOf<LongArray>() // [start, end] of each run
        for (n in chunks.toSortedSet()) {
            val last = runs.lastOrNull()
            if (last != null && n == last[1] + 1) last[1] = n else runs += longArrayOf(n, n)
        }
        return runs.joinToString(",") { (s, e) -> if (e > s) "$s-$e" else "$s" }
    }

    /** The chunk numbers in [text]; null when it isn't in the format. */
    fun parse(text: String?): List<Long>? {
        val result = mutableListOf<Long>()
        if (text.isNullOrEmpty()) return result
        for (part in text.split(',')) {
            val dash = part.indexOf('-')
            if (dash < 0) {
                result += number(part) ?: return null
                continue
            }
            val from = number(part.substring(0, dash)) ?: return null
            val to = number(part.substring(dash + 1)) ?: return null
            if (to < from) return null
            for (n in from..to) result += n
        }
        return result
    }

    /** Digits only, like the PC's `NumberStyles.None`: no sign, no spaces. */
    private fun number(s: String): Long? = if (s.isEmpty() || !s.all { it in '0'..'9' }) null else s.toLongOrNull()
}
