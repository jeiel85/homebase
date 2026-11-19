package io.github.jeiel85.homebase.share

import android.content.Context
import android.net.Uri
import io.github.jeiel85.homebase.data.PcEntry
import io.github.jeiel85.homebase.data.PcStore
import io.github.jeiel85.homebase.net.ClipCrypto
import io.github.jeiel85.homebase.net.ClipEnvelope
import io.github.jeiel85.homebase.net.Endpoint
import io.github.jeiel85.homebase.net.FileSendProtocol
import io.github.jeiel85.homebase.net.HomebaseApi
import io.github.jeiel85.homebase.net.ReceivedNames
import io.github.jeiel85.homebase.net.TransferAnswer
import io.github.jeiel85.homebase.net.TransferCrypto
import io.github.jeiel85.homebase.net.TransferErrors
import io.github.jeiel85.homebase.net.TransferOffer
import io.github.jeiel85.homebase.net.TransferProtocol
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
import java.security.MessageDigest
import kotlin.coroutines.coroutineContext

/** A file received and saved: where it is now, for [열기] and [공유]. */
data class SavedFile(val uri: Uri, val name: String, val mime: String?)

/** How a download ended. */
sealed interface DownloadOutcome {
    data class Done(val files: List<SavedFile>) : DownloadOutcome
    data class Failed(val reason: String) : DownloadOutcome
    /** Worth trying again later (no path to the PC): the job is rescheduled and goes on. */
    data class Retry(val reason: String) : DownloadOutcome
}

/**
 * Files finished so far per transfer (DECISIONS M-65), so a job Android stopped goes on with the next file: the file
 * that was being written is thrown away and fetched again from its first chunk.
 */
internal object ReceiveProgress {
    private const val PREFS = "homebase.receive"

    /** File index → the saved file's URI. */
    fun finished(context: Context, transferId: String): Map<Int, String> {
        val raw = prefs(context).getString(transferId, null) ?: return emptyMap()
        return try {
            val o = JSONObject(raw)
            o.keys().asSequence().mapNotNull { k -> k.toIntOrNull()?.let { it to o.getString(k) } }.toMap()
        } catch (_: Exception) {
            emptyMap()
        }
    }

    fun markFinished(context: Context, transferId: String, file: Int, uri: String) {
        val o = JSONObject(finished(context, transferId).mapKeys { it.key.toString() })
        o.put(file.toString(), uri)
        prefs(context).edit().putString(transferId, o.toString()).apply()
    }

    fun clear(context: Context, transferId: String) = prefs(context).edit().remove(transferId).apply()

    private fun prefs(context: Context) = context.applicationContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
}

/**
 * One download (DECISIONS M-65, docs/FILE_SHARE.md 2b): the offer → each file in order, its chunks fetched in order,
 * each opened at its position with the transfer key, written while the file is hidden and hashed as it goes → the
 * file shown only when its SHA-256 is the offer's → `received`. The PC's chunks are sealed exactly like this phone's
 * uploads (2a), under the clipboard key agreed with that PC.
 */
internal class Downloader(
    private val context: Context,
    private val api: HomebaseApi,
    private val pcId: String,
    private val transferId: String,
    private val onProgress: (got: Long, total: Long) -> Unit,
) {
    /** The path that answered last: asked first for the next chunk. */
    private var via: Endpoint? = null

    private class Gone : Exception()
    private class Corrupt : Exception()
    private class SaveFailed(cause: Throwable) : Exception(cause)

    suspend fun run(): DownloadOutcome {
        val pc = withContext(Dispatchers.IO) { PcStore(context).get(pcId) }
            ?: return DownloadOutcome.Failed("이 PC는 더 이상 목록에 없습니다")
        val name = pc.displayName
        val key = ClipCrypto.decodeKey(pc.clipKey)
            ?: return DownloadOutcome.Failed("${name}와(과) 맞춘 암호 키가 없습니다. PC에서 다시 보내 달라고 하세요.")
        return try {
            download(pc, name, key)
        } catch (_: UnauthorizedException) {
            DownloadOutcome.Failed("${name}에서 이 휴대폰의 연결이 해제되었습니다. 다시 페어링하세요.")
        } catch (_: Gone) {
            DownloadOutcome.Failed("PC에서 더 이상 받을 수 없습니다(24시간 지남)")
        } catch (_: Corrupt) {
            DownloadOutcome.Failed("받은 파일이 손상되었습니다. 다시 보내 달라고 하세요.")
        } catch (_: SaveFailed) {
            DownloadOutcome.Failed("휴대폰에 저장하지 못했습니다. 저장 공간을 확인하세요.")
        } catch (_: IOException) {
            DownloadOutcome.Retry("${name}에 닿지 않습니다")
        }
    }

    private suspend fun download(pc: PcEntry, name: String, clipKey: ByteArray): DownloadOutcome {
        val offered = get(pc, "${FileSendProtocol.OFFER_PATH}?id=$transferId")
        val offer = when {
            offered.ok -> ClipEnvelope.parse(offered.body ?: "")?.let { TransferOffer.open(clipKey, it) }
                ?: return DownloadOutcome.Failed("${name}이(가) 보낸 목록을 열지 못했습니다. PC에서 다시 보내 달라고 하세요.")
            offered.error == TransferErrors.UNKNOWN -> throw Gone()
            offered.code == 404 || offered.code == 405 -> return DownloadOutcome.Failed("${name}의 Homebase를 업데이트하세요")
            offered.code in 500..599 -> return DownloadOutcome.Retry(offered.message ?: "${name}이(가) 잠시 응답하지 못했습니다")
            else -> return DownloadOutcome.Failed("${name}이(가) 보내 주지 않았습니다: ${offered.message ?: "HTTP ${offered.code}"}")
        }

        val transferKey = TransferCrypto.transferKey(clipKey, transferId)
        val finished = ReceiveProgress.finished(context, transferId)
        val total = offer.totalBytes
        var got = offer.files.withIndex().filter { it.index in finished }.sumOf { it.value.size }
        onProgress(got, total)
        val saved = mutableListOf<SavedFile>()
        for ((f, file) in offer.files.withIndex()) {
            val shown = ReceivedNames.clean(file.name, "file-${f + 1}")
            val already = finished[f]
            if (already != null) {
                saved += SavedFile(Uri.parse(already), shown, file.mime)
                continue
            }
            val dest = try {
                ReceivedStore.create(context, shown, file.mime)
            } catch (e: Exception) {
                throw SaveFailed(e)
            }
            var whole = false
            try {
                val digest = MessageDigest.getInstance("SHA-256")
                val out = try {
                    dest.open()
                } catch (e: Exception) {
                    throw SaveFailed(e)
                }
                out.use {
                    for (n in 0 until TransferProtocol.chunkCount(file.size)) {
                        coroutineContext.ensureActive()
                        val plain = TransferCrypto.openChunk(transferKey, f, n, fetch(pc, f, n)) ?: throw Corrupt()
                        if (plain.size != TransferProtocol.chunkLength(file.size, n)) throw Corrupt()
                        digest.update(plain)
                        try {
                            it.write(plain)
                        } catch (e: IOException) {
                            throw SaveFailed(e)
                        }
                        got += plain.size
                        onProgress(got, total)
                    }
                }
                if (Uploader.hex(digest.digest()) != file.sha256) throw Corrupt()
                try {
                    dest.publish()
                } catch (e: Exception) {
                    throw SaveFailed(e)
                }
                whole = true
                ReceiveProgress.markFinished(context, transferId, f, dest.uri.toString())
                saved += SavedFile(dest.uri, shown, file.mime)
            } finally {
                // Stopped, cut off or not matching: nothing half-written stays.
                if (!whole) dest.discard()
            }
        }

        // The PC's tray says the phone has them; best effort, the files are saved either way.
        try {
            api.transfer(pc, "${FileSendProtocol.RECEIVED_PATH}?id=$transferId",
                Request.Builder().post("{\"schema\":1}".toRequestBody(JSON)), prefer = via)
        } catch (_: IOException) {
        } catch (_: UnauthorizedException) {
        }
        ReceiveProgress.clear(context, transferId)
        return DownloadOutcome.Done(saved)
    }

    /** One chunk, again after 2 s while the PC is busy. */
    private suspend fun fetch(pc: PcEntry, f: Int, n: Long): ByteArray {
        val path = "${FileSendProtocol.FETCH_PATH}?id=$transferId&f=$f&n=$n"
        repeat(MAX_BUSY_TRIES) {
            val answer = api.transfer(pc, path, Request.Builder().get(), prefer = via, binary = true)
            via = answer.via
            when {
                answer.ok -> return answer.bytes ?: throw IOException("empty chunk")
                answer.code in 500..599 -> delay(BUSY_WAIT_MS)
                answer.error == TransferErrors.UNKNOWN -> throw Gone()
                answer.error == TransferErrors.BAD_CHUNK -> throw Corrupt()
                else -> throw IOException("HTTP ${answer.code}: ${answer.message ?: answer.error}")
            }
        }
        throw IOException("PC가 계속 바쁩니다")
    }

    private suspend fun get(pc: PcEntry, path: String): TransferAnswer {
        val answer = api.transfer(pc, path, Request.Builder().get(), prefer = via)
        via = answer.via
        return answer
    }

    private companion object {
        val JSON = "application/json; charset=utf-8".toMediaType()
        const val MAX_BUSY_TRIES = 30
        const val BUSY_WAIT_MS = 2_000L
    }
}
