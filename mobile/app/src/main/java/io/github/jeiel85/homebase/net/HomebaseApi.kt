package io.github.jeiel85.homebase.net

import io.github.jeiel85.homebase.data.PcEntry
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.channels.awaitClose
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.callbackFlow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.flow.flowOn
import kotlinx.coroutines.withContext
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import org.json.JSONObject
import java.io.IOException
import java.util.concurrent.TimeUnit

/** Pairing failed in a way the user should read (wrong/expired code, lockout). */
class PairException(message: String) : Exception(message)

/** The PC no longer knows this phone (revoked in the Tray) — re-pair. */
class UnauthorizedException : Exception("이 PC에서 휴대폰 연결이 해제되었습니다. 다시 페어링하세요.")

/** What the detail screen shows about the live connection. */
sealed interface LiveState {
    data object Connecting : LiveState
    data class Live(val snapshot: LiveSnapshot, val host: String) : LiveState
    data class Retrying(val reason: String, val inSeconds: Int) : LiveState
    data object Unauthorized : LiveState
    data class Incompatible(val reason: String) : LiveState
}

/**
 * Direct phone → PC calls over the tailnet. No relay server exists (DECISIONS M-1): each PC is
 * reached at the MagicDNS name or fixed Tailscale IP from its QR code, tried in order.
 */
class HomebaseApi(
    private val client: OkHttpClient = defaultClient,
) {

    suspend fun info(pc: PcEntry): Pair<PcInfo, String> = withContext(Dispatchers.IO) {
        var last: Exception = IOException("no hosts")
        for (host in pc.orderedHosts) {
            try {
                val req = Request.Builder().url(url(host, pc.port, "/api/v1/info")).build()
                quick.newCall(req).execute().use { res ->
                    if (!res.isSuccessful) throw IOException("HTTP ${res.code}")
                    return@withContext PcInfo.parse(res.body!!.string()) to host
                }
            } catch (e: Exception) {
                last = e
            }
        }
        throw last
    }

    /** Tries each QR host until one answers; returns the pairing result and the host that worked. */
    suspend fun pair(offer: PairingOffer, deviceName: String, deviceModel: String): Pair<PairResponse, String> =
        withContext(Dispatchers.IO) {
            val body = JSONObject()
                .put("code", offer.code)
                .put("deviceName", deviceName)
                .put("deviceModel", deviceModel)
                .toString()
                .toRequestBody(JSON)
            var unreachable: Exception? = null
            for (host in offer.hosts) {
                val res: Response = try {
                    quick.newCall(Request.Builder().url(url(host, offer.port, "/api/v1/pair")).post(body).build()).execute()
                } catch (e: IOException) {
                    unreachable = e
                    continue
                }
                res.use {
                    val text = it.body?.string()
                    if (it.isSuccessful) return@withContext PairResponse.parse(text!!) to host
                    throw PairException(errorMessage(text) ?: "페어링 실패 (HTTP ${it.code})")
                }
            }
            throw IOException(
                "PC에 연결할 수 없습니다. 휴대폰과 PC가 모두 Tailscale에 연결되어 있는지 확인하세요." +
                    (unreachable?.message?.let { " ($it)" } ?: ""),
            )
        }

    /**
     * Live snapshots while collected. Reconnects with backoff on network loss; stops on 401 or a
     * schema mismatch. Cancelling the collector closes the socket, which makes the PC stop sampling.
     */
    fun live(pc: PcEntry, onGoodHost: (String) -> Unit): Flow<LiveState> = flow {
        var attempt = 0
        while (true) {
            emit(LiveState.Connecting)
            var failure: Throwable? = null
            for (host in pc.orderedHosts) {
                try {
                    socketFlow(host, pc).collect { snapshot ->
                        if (attempt != 0) attempt = 0
                        onGoodHost(host)
                        emit(LiveState.Live(snapshot, host))
                    }
                    failure = IOException("연결이 닫혔습니다")
                } catch (e: UnauthorizedException) {
                    emit(LiveState.Unauthorized)
                    return@flow
                } catch (e: ProtocolException) {
                    emit(LiveState.Incompatible(e.message ?: "호환되지 않는 버전"))
                    return@flow
                } catch (e: IOException) {
                    failure = e
                    continue
                }
                break // got snapshots from this host, then lost it → back off and retry all hosts
            }
            val wait = BACKOFF_SECONDS[minOf(attempt++, BACKOFF_SECONDS.lastIndex)]
            emit(LiveState.Retrying(failure?.message ?: "연결 실패", wait))
            delay(wait * 1000L)
        }
    }.flowOn(Dispatchers.IO)

    private fun socketFlow(host: String, pc: PcEntry): Flow<LiveSnapshot> = callbackFlow {
        val request = Request.Builder()
            .url(url(host, pc.port, "/api/v1/live"))
            .header("Authorization", "Bearer ${pc.token}")
            .build()
        val ws = client.newWebSocket(request, object : WebSocketListener() {
            override fun onMessage(webSocket: WebSocket, text: String) {
                try {
                    trySend(LiveSnapshot.parse(text))
                } catch (e: ProtocolException) {
                    close(e)
                } catch (e: Exception) {
                    close(IOException("잘못된 응답: ${e.message}"))
                }
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                close(if (response?.code == 401) UnauthorizedException() else (t as? IOException ?: IOException(t.message)))
            }

            // The PC closes with 1008 when this phone was revoked in the Tray mid-session.
            override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                if (code == CLOSE_REVOKED) close(UnauthorizedException()) else close()
                webSocket.close(1000, null)
            }

            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                if (code == CLOSE_REVOKED) close(UnauthorizedException()) else close()
            }
        })
        awaitClose { ws.close(1000, "bye") }
    }

    private fun url(host: String, port: Int, path: String): String {
        val h = if (host.contains(':') && !host.startsWith("[")) "[$host]" else host
        return "http://$h:$port$path"
    }

    companion object {
        private val JSON = "application/json; charset=utf-8".toMediaType()
        private val BACKOFF_SECONDS = intArrayOf(1, 2, 5, 10, 30)

        /** WebSocket policy-violation close the Agent sends to a revoked device. */
        private const val CLOSE_REVOKED = 1008

        val defaultClient: OkHttpClient = OkHttpClient.Builder()
            .connectTimeout(5, TimeUnit.SECONDS)
            .readTimeout(30, TimeUnit.SECONDS) // snapshots arrive every second; 30 s silence = dead
            .pingInterval(15, TimeUnit.SECONDS)
            .build()

        private val quick: OkHttpClient = defaultClient.newBuilder()
            .connectTimeout(3, TimeUnit.SECONDS)
            .readTimeout(5, TimeUnit.SECONDS)
            .build()
    }
}
