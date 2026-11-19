package io.github.jeiel85.homebase.share

import android.content.Context
import io.github.jeiel85.homebase.clip.ClipShare
import io.github.jeiel85.homebase.data.PcEntry
import io.github.jeiel85.homebase.data.PcStore
import io.github.jeiel85.homebase.net.Endpoint
import io.github.jeiel85.homebase.net.HomebaseApi
import io.github.jeiel85.homebase.net.TransferAnswer
import io.github.jeiel85.homebase.net.TransferCrypto
import io.github.jeiel85.homebase.net.TransferErrors
import io.github.jeiel85.homebase.net.TransferMessages
import io.github.jeiel85.homebase.net.TransferProtocol
import io.github.jeiel85.homebase.net.TransferStatus
import io.github.jeiel85.homebase.net.UnauthorizedException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import org.json.JSONObject
import java.io.IOException
import java.io.InputStream
import java.security.MessageDigest
import kotlin.coroutines.coroutineContext

/** How an upload ended. */
sealed interface UploadOutcome {
    /** Every file arrived and matched. [saved] is the PC's `tray` (saved now) or `waiting` (nobody signed in). */
    data class Done(val saved: String) : UploadOutcome

    /** It can't succeed as it is; [reason] is a line for the user. */
    data class Failed(val reason: String) : UploadOutcome

    /** Worth trying again later (no path, the PC busy): the job is rescheduled and goes on from the PC's status. */
    data class Retry(val reason: String) : UploadOutcome
}

/**
 * One upload (DECISIONS M-64, docs/FILE_SHARE.md 2a): clipboard key → create (or the status of a transfer the PC
 * already has) → every file read once, in order, hashing as it goes and sending the chunks the PC doesn't have →
 * done with the hashes. A transfer the PC lost or found incomplete is gone through again from its status, a few
 * times at most; then the job tries later. Nothing is kept on the phone between runs: the PC's status is where
 * it goes on from.
 */
internal class Uploader(
    private val context: Context,
    private val api: HomebaseApi,
    private val pcId: String,
    private val transferId: String,
    private val files: List<OutgoingFile>,
    /** Phones (the PC's device ids) the PC passes the files on to instead of saving them (docs/SHARE_CONVENIENCE.md §4.8). */
    private val to: List<String> = emptyList(),
    /** [via]: the path the PC answered on last, for the notification (Cloudflare is slow, §4.7). */
    private val onProgress: (sent: Long, total: Long, via: Endpoint?) -> Unit,
) {
    private val total = files.sumOf { it.size }

    /** The path that answered last: tried first for the next chunk. */
    private var via: Endpoint? = null

    private class Restart : Exception()

    private class Changed(val name: String) : Exception()

    /** The PC can't open a chunk at its position: its key for this transfer is not this phone's any more. */
    private class BadChunk : Exception()

    suspend fun run(): UploadOutcome {
        val pc = withContext(Dispatchers.IO) { PcStore(context).get(pcId) }
            ?: return UploadOutcome.Failed("이 PC는 더 이상 목록에 없습니다")
        val name = pc.displayName
        return try {
            upload(pc, name)
        } catch (_: UnauthorizedException) {
            UploadOutcome.Failed("${name}에서 이 휴대폰의 연결이 해제되었습니다. 다시 페어링하세요.")
        } catch (e: UnreadableFileException) {
            UploadOutcome.Failed("'${e.name}'${FileShare.objectParticle(e.name)} 읽지 못했습니다. 다시 보내세요.")
        } catch (e: Changed) {
            UploadOutcome.Failed("보내는 동안 '${e.name}'이(가) 바뀌었습니다. 다시 보내세요.")
        } catch (_: BadChunk) {
            // The clipboard key was agreed again while this transfer was under way; a new send starts clean.
            UploadOutcome.Failed("${name}이(가) 받은 내용을 열지 못했습니다. 다시 보내세요.")
        } catch (_: IOException) {
            UploadOutcome.Retry("${name}에 닿지 않습니다")
        }
    }

    private suspend fun upload(pc: PcEntry, name: String): UploadOutcome {
        val tooOld = UploadOutcome.Failed("${name}의 Homebase를 업데이트하세요")
        var key = ClipShare.keyFor(context, api, pc, stale = null) ?: return tooOld
        var rekeyed = false
        repeat(MAX_PASSES) {
            // 1. Create, or the status of the transfer the PC already has under this id.
            var created = post(pc, TransferProtocol.PATH, json(meta(key)))
            if (!rekeyed && (created.error == TransferErrors.KEY_NEEDED || created.code == 400)) {
                // The PC holds no key for this phone, or another one (the metadata doesn't open): agree again, once.
                rekeyed = true
                key = ClipShare.keyFor(context, api, pc, stale = key) ?: return tooOld
                created = post(pc, TransferProtocol.PATH, json(meta(key)))
            }
            val status = when {
                created.ok -> TransferStatus.parse(created.body) ?: return UploadOutcome.Retry("${name}의 응답을 읽지 못했습니다")
                else -> return refused(created, name, tooOld)
            }

            // 2. The chunks the PC doesn't have, hashing every file as it is read.
            val hashes = try {
                sendFiles(pc, key, status)
            } catch (_: Restart) {
                return@repeat // the PC lost the transfer: create it again and go on from its status
            }

            // 3. Done: the PC checks every file against its hash.
            val done = post(pc, "${TransferProtocol.DONE_PATH}?id=$transferId", json(TransferMessages.sealHashes(key, transferId, hashes).toJson()))
            when {
                done.ok -> return UploadOutcome.Done(savedOf(done.body))
                done.error == TransferErrors.INCOMPLETE || done.error == TransferErrors.UNKNOWN -> return@repeat
                done.error == TransferErrors.HASH_MISMATCH -> return UploadOutcome.Failed("보내는 동안 파일이 바뀌었습니다. 다시 보내세요.")
                else -> return refused(done, name, tooOld)
            }
        }
        return UploadOutcome.Retry("${name}이(가) 전송을 마무리하지 못했습니다")
    }

    private fun meta(key: ByteArray): JSONObject =
        TransferMessages.sealMeta(key, transferId, files.map { f -> f.toTransferFile() }, to = to).toJson()

    /** Sends what is missing from [status] and returns each file's SHA-256 (lowercase hex). */
    private suspend fun sendFiles(pc: PcEntry, clipKey: ByteArray, status: TransferStatus): List<String> {
        val transferKey = TransferCrypto.transferKey(clipKey, transferId)
        val buffer = ByteArray(TransferProtocol.CHUNK_BYTES)
        var sent = files.withIndex().sumOf { (f, file) ->
            status.arrived(f).sumOf { n -> TransferProtocol.chunkLength(file.size, n).toLong() }
        }
        onProgress(sent, total, via)
        val hashes = mutableListOf<String>()
        for ((f, file) in files.withIndex()) {
            val arrived = status.arrived(f)
            val digest = MessageDigest.getInstance("SHA-256")
            open(file).use { input ->
                for (n in 0 until TransferProtocol.chunkCount(file.size)) {
                    coroutineContext.ensureActive()
                    val length = TransferProtocol.chunkLength(file.size, n)
                    if (!readFully(input, buffer, length, file.name)) throw Changed(file.name) // shorter than it was
                    digest.update(buffer, 0, length)
                    if (n in arrived) continue
                    sendChunk(pc, f, n, TransferCrypto.sealChunk(transferKey, f, n, buffer, 0, length))
                    sent += length
                    onProgress(sent, total, via)
                }
                if (readByte(input, file.name) >= 0) throw Changed(file.name) // longer than it was
            }
            hashes += hex(digest.digest())
        }
        return hashes
    }

    /** One chunk, again after 2 s while the PC is busy. Throws [Restart] when the PC no longer has the transfer. */
    private suspend fun sendChunk(pc: PcEntry, f: Int, n: Long, sealed: ByteArray) {
        val path = "${TransferProtocol.CHUNK_PATH}?id=$transferId&f=$f&n=$n"
        repeat(MAX_BUSY_TRIES) {
            val answer = post(pc, path, sealed.toRequestBody(OCTET))
            when {
                answer.ok -> return
                answer.code == 503 || answer.code in 500..599 -> delay(BUSY_WAIT_MS)
                answer.error == TransferErrors.UNKNOWN -> throw Restart()
                answer.error == TransferErrors.BAD_CHUNK -> throw BadChunk()
                else -> throw IOException("HTTP ${answer.code}: ${answer.message ?: answer.error}")
            }
        }
        throw IOException("PC가 계속 바쁩니다")
    }

    private suspend fun post(pc: PcEntry, path: String, body: okhttp3.RequestBody): TransferAnswer {
        val answer = api.transfer(pc, path, Request.Builder().post(body), prefer = via)
        via = answer.via
        return answer
    }

    /** What a refusal of create or done means for the user. */
    private fun refused(answer: TransferAnswer, name: String, tooOld: UploadOutcome.Failed): UploadOutcome = when {
        answer.code == 404 || answer.code == 405 -> tooOld
        answer.error == TransferErrors.TOO_LARGE -> UploadOutcome.Failed("${name}이(가) 받기에 너무 큽니다 (파일당 2GB, 한 번에 4GB)")
        answer.error == TransferErrors.NO_SPACE -> UploadOutcome.Failed("${name}에 남은 공간이 부족합니다")
        answer.error == TransferErrors.KEY_NEEDED -> UploadOutcome.Failed("${name}와(과) 암호 키를 맞추지 못했습니다. 다시 보내세요.")
        answer.code in 500..599 -> UploadOutcome.Retry(answer.message ?: "${name}이(가) 잠시 응답하지 못했습니다")
        else -> UploadOutcome.Failed("${name}이(가) 받지 않았습니다: ${answer.message ?: "HTTP ${answer.code}"}")
    }

    private fun open(file: OutgoingFile): InputStream = try {
        context.contentResolver.openInputStream(file.uri) ?: throw UnreadableFileException(file.name)
    } catch (e: UnreadableFileException) {
        throw e
    } catch (e: Exception) {
        // Gone, or the app that shared it took the permission back.
        throw UnreadableFileException(file.name, e)
    }

    companion object {
        private val OCTET = "application/octet-stream".toMediaType()
        private val JSON = "application/json; charset=utf-8".toMediaType()

        /** Create–send–done rounds in one run before the job tries again later. */
        private const val MAX_PASSES = 3

        /** A busy PC is asked again every [BUSY_WAIT_MS] this many times (one minute) before the run gives up. */
        private const val MAX_BUSY_TRIES = 30
        private const val BUSY_WAIT_MS = 2_000L

        private fun json(o: JSONObject) = o.toString().toRequestBody(JSON)

        /** Lowercase hex, as the PC compares hashes. */
        internal fun hex(bytes: ByteArray): String = bytes.joinToString("") { b -> (b.toInt() and 0xff).toString(16).padStart(2, '0') }

        /** `saved` of a `done` answer: `tray`, `waiting`, or `forwarded` (passed on to other phones, §4.8). */
        internal fun savedOf(body: String?): String = try {
            JSONObject(body ?: "").optString("saved").ifEmpty { "tray" }
        } catch (_: Exception) {
            "tray"
        }

        /** Reads exactly [length] bytes; false when the stream ends first. */
        internal fun readFully(input: InputStream, buffer: ByteArray, length: Int, name: String): Boolean {
            var off = 0
            while (off < length) {
                val r = try {
                    input.read(buffer, off, length - off)
                } catch (e: IOException) {
                    throw UnreadableFileException(name, e)
                }
                if (r < 0) return false
                off += r
            }
            return true
        }

        private fun readByte(input: InputStream, name: String): Int = try {
            input.read()
        } catch (e: IOException) {
            throw UnreadableFileException(name, e)
        }
    }
}
