package io.github.jeiel85.homebase.net

import io.github.jeiel85.homebase.push.WebPush
import org.json.JSONArray
import org.json.JSONObject
import java.security.KeyPair
import java.security.PrivateKey
import java.security.SecureRandom
import java.security.interfaces.ECPublicKey
import java.time.Instant
import java.util.Base64
import java.util.UUID
import javax.crypto.Cipher
import javax.crypto.KeyAgreement
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/**
 * Clipboard text sent by hand between this phone and its PCs (DECISIONS M-62, docs/CLIPBOARD_SHARE.md
 * phase 1), mirroring `LocalOpsBot.Core.Mobile.ClipShare` (fixtures `clip.v1.json`, `push-clip.v1.json`):
 * - phone → PC: `POST /api/v1/clip` with a [ClipEnvelope];
 * - PC → phone: a push of kind [ClipProtocol.PUSH_KIND] ([ClipPush]); text too long for one push is
 *   fetched with `GET /api/v1/clip?id=`.
 *
 * The text is always sealed with [ClipCrypto] under a key only this phone and that PC hold, agreed by ECDH
 * over `POST /api/v1/clip/key` ([ClipKeyAgreement]). Not derived from the bearer token: the token travels
 * in every request, and the Cloudflare path ends TLS at Cloudflare's edge. Every paired PC shares this
 * phone's push keys, so the per-PC key is also what stops one PC from reading or forging another's text.
 */
object ClipProtocol {
    const val PATH = "/api/v1/clip"

    /** Where this phone and a PC agree their clipboard key. */
    const val KEY_PATH = "/api/v1/clip/key"

    const val PUSH_KIND = "clip"

    /** Listed in `features` of `POST /api/v1/push`: the PC only offers phones that read clip pushes. */
    const val FEATURE = "clip"

    /** Longest text sent at once, in UTF-16 units (String.length here, `string.Length` on the PC). */
    const val MAX_CHARS = 100_000

    /** How long a PC keeps text too long for a push; the phone fetches it within this. */
    const val OFFER_LIFETIME_MS = 10 * 60_000L

    private val ID = Regex("^[0-9a-f]{32}$")

    fun isValidId(id: String?): Boolean = id != null && ID.matches(id)

    fun newId(): String = UUID.randomUUID().toString().replace("-", "")
}

/**
 * AES-256-GCM with the key this phone and a PC agreed ([ClipKeyAgreement]): HKDF-SHA256 over the raw P-256
 * ECDH secret (the 32-byte x coordinate), salt 32 zero bytes, info `"homebase clip v1"` followed by the
 * device's and then the PC's 65-byte uncompressed public key — that order on both sides. A 12-byte random
 * nonce per message; the data is ciphertext followed by the 16-byte tag (what `AES/GCM/NoPadding` writes).
 * No additional data. Platform crypto only (M-3).
 */
object ClipCrypto {
    const val KEY_BYTES = 32
    const val NONCE_BYTES = 12
    const val TAG_BITS = 128
    const val PUBLIC_KEY_BYTES = 65

    private val INFO = "homebase clip v1".toByteArray(Charsets.UTF_8)

    /** RFC 5869 says a missing salt is HashLen zero bytes; Java's HMAC rejects an empty key, so spell it out. */
    private val SALT = ByteArray(32)

    private val random = SecureRandom()

    /**
     * The key from [own] (this side's private key) and [peerPublic] (the other side's, checked to be a P-256
     * point first). [devicePublic] and [pcPublic] go into the info in that order whichever side this is.
     * Throws [java.security.GeneralSecurityException] when [peerPublic] is not a P-256 point.
     */
    fun agreedKey(own: PrivateKey, peerPublic: ByteArray, devicePublic: ByteArray, pcPublic: ByteArray): ByteArray {
        val agreement = KeyAgreement.getInstance("ECDH")
        agreement.init(own)
        agreement.doPhase(WebPush.publicKey(peerPublic), true)
        val prk = hmac(SALT, agreement.generateSecret())
        // L = 32 is one block: T(1) = HMAC(PRK, info || 0x01).
        return hmac(prk, INFO + devicePublic + pcPublic + byteArrayOf(1)).copyOf(KEY_BYTES)
    }

    /** How [PcEntry.clipKey] holds a key. */
    fun encodeKey(key: ByteArray): String = b64url(key)

    /** A stored key, or null when there is none or it is not [KEY_BYTES] long. */
    fun decodeKey(text: String?): ByteArray? {
        if (text.isNullOrEmpty()) return null
        return try {
            unb64url(text).takeIf { it.size == KEY_BYTES }
        } catch (_: Exception) {
            null
        }
    }

    /** Returns (nonce, data), both base64url without padding. [nonce] is for tests only. */
    fun seal(key: ByteArray, plaintext: ByteArray, nonce: ByteArray? = null): Pair<String, String> {
        val n = nonce ?: ByteArray(NONCE_BYTES).also(random::nextBytes)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(TAG_BITS, n))
        return b64url(n) to b64url(cipher.doFinal(plaintext))
    }

    /** The plaintext, or null when the values are malformed or the tag doesn't match (wrong key, altered). */
    fun open(key: ByteArray, nonce: String?, data: String?): ByteArray? {
        if (nonce == null || data == null) return null
        return try {
            val n = unb64url(nonce)
            val d = unb64url(data)
            if (n.size != NONCE_BYTES || d.size < TAG_BITS / 8) {
                null
            } else {
                val cipher = Cipher.getInstance("AES/GCM/NoPadding")
                cipher.init(Cipher.DECRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(TAG_BITS, n))
                cipher.doFinal(d)
            }
        } catch (_: Exception) {
            null
        }
    }

    private fun hmac(key: ByteArray, data: ByteArray): ByteArray =
        Mac.getInstance("HmacSHA256").apply { init(SecretKeySpec(key, "HmacSHA256")) }.doFinal(data)

    private fun b64url(data: ByteArray): String = Base64.getUrlEncoder().withoutPadding().encodeToString(data)

    private fun unb64url(text: String): ByteArray = Base64.getUrlDecoder().decode(text)
}

/**
 * The sealed part. [text] is null in a push whose text didn't fit (fetch it); [chars] is its length
 * either way. [sensitive]: a password manager put it on the clipboard, so keep it out of previews.
 * [to]: phone → PC only — the device ids (the PC's) of other phones the PC passes it on to instead of taking
 * it (docs/SHARE_CONVENIENCE.md §4.8, [ShareRelay]). [from]: PC → phone only — the name of the phone that
 * sent it through the PC. Both are left out when empty; an older PC sends neither. [quick]: phone → PC only — sent
 * from a share sheet PC shortcut without the share center (docs/QUICK_SEND.md §5), so the PC puts it on its clipboard
 * only when it falls in that PC's range; left out when false, ignored by an older PC.
 */
data class ClipContent(
    val text: String?,
    val chars: Int,
    val ts: String,
    val sensitive: Boolean = false,
    val to: List<String> = emptyList(),
    val from: String? = null,
    val quick: Boolean = false,
) {
    fun toJson(): JSONObject = JSONObject()
        .put("schema", SCHEMA)
        .apply { text?.let { put("text", it) } }
        .put("chars", chars)
        .put("ts", ts)
        .apply { if (sensitive) put("sensitive", true) }
        .apply { if (to.isNotEmpty()) put("to", JSONArray(to)) }
        .apply { from?.let { put("from", it) } }
        .apply { if (quick) put("quick", true) }

    companion object {
        fun of(
            text: String,
            sensitive: Boolean,
            nowMillis: Long = System.currentTimeMillis(),
            to: List<String> = emptyList(),
            quick: Boolean = false,
        ) = ClipContent(text, text.length, Instant.ofEpochMilli(nowMillis).toString(), sensitive, to, quick = quick)

        /** Null for another schema or a malformed payload — never a half-filled one. */
        fun parse(plain: ByteArray): ClipContent? = parse(plain.toString(Charsets.UTF_8))

        /** [parse] of the JSON text itself; fields this version doesn't know are ignored. */
        fun parse(json: String): ClipContent? = try {
            val o = JSONObject(json)
            if (o.optInt("schema") != SCHEMA) {
                null
            } else {
                val text = if (o.isNull("text")) null else o.optString("text")
                ClipContent(
                    text = text,
                    chars = o.optInt("chars", text?.length ?: 0),
                    ts = o.getString("ts"),
                    sensitive = o.optBoolean("sensitive", false),
                    to = o.optJSONArray("to")?.strings()?.map { it.trim() }?.filter { it.isNotEmpty() } ?: emptyList(),
                    from = if (o.isNull("from")) null else o.optString("from").trim().ifEmpty { null },
                    quick = o.optBoolean("quick", false),
                )
            }
        } catch (_: Exception) {
            null
        }
    }
}

/** `POST /api/v1/clip` body and `GET /api/v1/clip?id=` answer. */
data class ClipEnvelope(val id: String, val nonce: String, val data: String) {
    fun toJson(): JSONObject = JSONObject()
        .put("schema", SCHEMA)
        .put("id", id)
        .put("nonce", nonce)
        .put("data", data)

    /**
     * The content, sealed with [key] (the one agreed with that PC), or null when it doesn't open or carries
     * no usable text (empty, or longer than [ClipProtocol.MAX_CHARS]).
     */
    fun open(key: ByteArray): ClipContent? {
        if (!ClipProtocol.isValidId(id)) return null
        val plain = ClipCrypto.open(key, nonce, data) ?: return null
        val content = ClipContent.parse(plain) ?: return null
        val text = content.text
        return if (text.isNullOrEmpty() || text.length > ClipProtocol.MAX_CHARS) null else content
    }

    companion object {
        fun seal(key: ByteArray, id: String, content: ClipContent, nonce: ByteArray? = null): ClipEnvelope {
            val (n, data) = ClipCrypto.seal(key, content.toJson().toString().toByteArray(Charsets.UTF_8), nonce)
            return ClipEnvelope(id, n, data)
        }

        fun parse(json: String): ClipEnvelope? = try {
            val o = JSONObject(json)
            if (o.optInt("schema") != SCHEMA) null else ClipEnvelope(o.getString("id"), o.getString("nonce"), o.getString("data"))
        } catch (_: Exception) {
            null
        }
    }
}

/** What a PC said to `POST /api/v1/clip`. */
sealed interface ClipSendResult {
    /** It is on that PC's clipboard. */
    data object Sent : ClipSendResult

    /**
     * Sent with `quick` to a PC that knows it (docs/QUICK_SEND.md §5): [held] — outside that PC's range, so it is not on
     * the clipboard but in a notification there; [archiverStopped] — the text has a Threads link and Threads Archiver
     * isn't running on that PC. An older PC answers [Sent] instead (it put the text on its clipboard).
     */
    data class QuickTaken(val held: Boolean, val archiverStopped: Boolean) : ClipSendResult

    /** Sent with `to` (docs/SHARE_CONVENIENCE.md §4.8): the PC pushed it on to each phone in [forwarded], or not. */
    data class Relayed(val forwarded: List<Forwarded>) : ClipSendResult

    /** Nobody is signed in to that PC, so there is no clipboard to put it on (409 `no_user_session`). */
    data object NoUserSession : ClipSendResult

    /** The PC holds no clipboard key for this phone: agree one and send again (409 `clip_key_needed`). */
    data object KeyNeeded : ClipSendResult

    /** The PC's clipboard was busy and nothing was pasted (503 `clipboard_busy`). */
    data object ClipboardBusy : ClipSendResult

    /** That PC's Homebase is older than clipboard sharing (404). */
    data object Unsupported : ClipSendResult

    /** The PC could not open or accept it (400 `invalid_clip`) — also when its key differs from this phone's. */
    data object Rejected : ClipSendResult

    data class Failed(val reason: String) : ClipSendResult

    companion object {
        /** What the PC's answer [code] / [body] (`{schema, error, message}` when not 2xx) means. */
        fun of(code: Int, body: String?): ClipSendResult {
            val error = errorCode(body)
            return when {
                code in 200..299 -> quickTaken(body) ?: Forwarded.parseList(body)?.let { Relayed(it) } ?: Sent
                code == 409 && error == "clip_key_needed" -> KeyNeeded
                code == 409 -> NoUserSession
                code == 503 && error == "clipboard_busy" -> ClipboardBusy
                code == 404 || code == 405 -> Unsupported
                code == 400 -> Rejected
                else -> Failed(errorMessage(body) ?: "HTTP $code")
            }
        }

        /** [QuickTaken] from a 2xx answer `{schema, accepted, quick: true, held, archiver?}`; null without `quick`. */
        internal fun quickTaken(body: String?): QuickTaken? = try {
            val o = JSONObject(body ?: "")
            if (!o.optBoolean("quick", false)) null else QuickTaken(o.optBoolean("held", false), o.optString("archiver") == "stopped")
        } catch (_: Exception) {
            null
        }
    }
}

/** The `error` code of a `{schema, error, message}` body, or null. */
internal fun errorCode(body: String?): String? = try {
    body?.let { JSONObject(it).optString("error").ifBlank { null } }
} catch (_: Exception) {
    null
}

/**
 * This phone's side of `POST /api/v1/clip/key` (DECISIONS M-62): a fresh P-256 pair for this one exchange.
 * Send [requestJson]; [finish] the PC's answer into the key. The private key is dropped with this object —
 * only the agreed 32 bytes are kept (in [io.github.jeiel85.homebase.data.PcEntry.clipKey]).
 */
class ClipKeyAgreement(private val pair: KeyPair = WebPush.generateKeyPair()) {
    /** This phone's public key: 0x04 || X || Y, each coordinate 32 bytes. */
    val devicePublic: ByteArray = WebPush.uncompressed(pair.public as ECPublicKey)

    fun requestJson(): JSONObject = JSONObject().put("schema", SCHEMA).put("pub", WebPush.b64url(devicePublic))

    /** The agreed key from the PC's answer `{schema, pub}`, or null when it is not a P-256 point of this schema. */
    fun finish(answer: String?): ByteArray? = try {
        val o = JSONObject(answer ?: "")
        if (o.optInt("schema") != SCHEMA) {
            null
        } else {
            val pcPublic = WebPush.unb64url(o.getString("pub"))
            if (pcPublic.size != ClipCrypto.PUBLIC_KEY_BYTES) null else ClipCrypto.agreedKey(pair.private, pcPublic, devicePublic, pcPublic)
        }
    } catch (_: Exception) {
        null
    }
}

/**
 * A decrypted push carrying clipboard text from [pcId]. It has no `title`, so app versions before
 * M-62 drop it like any push they can't read.
 */
data class ClipPush(val pcId: String, val id: String, val nonce: String, val data: String) {
    /**
     * The content, sealed with [key] (the one agreed with [pcId]), or null when it doesn't open — another
     * PC can't make this phone take text in a paired PC's name. [ClipContent.text] may be null (too long
     * for a push: fetch it).
     */
    fun open(key: ByteArray): ClipContent? {
        if (!ClipProtocol.isValidId(id)) return null
        val plain = ClipCrypto.open(key, nonce, data) ?: return null
        val content = ClipContent.parse(plain) ?: return null
        val text = content.text
        if (text != null && (text.isEmpty() || text.length > ClipProtocol.MAX_CHARS)) return null
        return content
    }

    companion object {
        /**
         * Null when [json] is not a push of [kind] and this schema. The same envelope also carries the `share` push of
         * files a PC sends (DECISIONS M-65, [FileSendProtocol.PUSH_KIND]).
         */
        fun parse(json: String, kind: String = ClipProtocol.PUSH_KIND): ClipPush? = try {
            val o = JSONObject(json)
            if (o.optInt("schema") != SCHEMA || o.optString("kind") != kind) {
                null
            } else {
                ClipPush(o.getString("pcId"), o.getString("id"), o.getString("nonce"), o.getString("data"))
                    .takeIf { it.pcId.isNotEmpty() }
            }
        } catch (_: Exception) {
            null
        }
    }
}
