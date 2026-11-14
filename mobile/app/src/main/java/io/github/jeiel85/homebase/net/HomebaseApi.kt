package io.github.jeiel85.homebase.net

import io.github.jeiel85.homebase.data.PcEntry
import io.github.jeiel85.homebase.push.PushSubscription
import io.github.jeiel85.homebase.push.PushSubscriptionResponse
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
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.TimeUnit
import javax.net.ssl.SSLException

/** Pairing failed in a way the user should read (wrong/expired code, lockout). */
class PairException(message: String) : Exception(message)

/** The PC no longer knows this phone (revoked in the Tray) — re-pair. */
class UnauthorizedException : Exception("이 PC에서 휴대폰 연결이 해제되었습니다. 다시 페어링하세요.")

/** What the detail screen shows about the live connection. */
sealed interface LiveState {
    data object Connecting : LiveState
    data class Live(val snapshot: LiveSnapshot, val endpoint: Endpoint) : LiveState
    data class Retrying(val reason: String, val inSeconds: Int) : LiveState
    data object Unauthorized : LiveState
    data class Incompatible(val reason: String) : LiveState
}

/**
 * Direct phone → PC calls. No relay server of our own exists (DECISIONS M-1, M-17): each PC is
 * reached over one of the paths in its QR — same-LAN TLS with a pinned certificate, Tailscale, or
 * the PC's Cloudflare Tunnel — tried in the order [RouteSelector] picks for the phone's current
 * network ([networks]).
 */
class HomebaseApi(
    private val client: OkHttpClient = defaultClient,
    private val networks: () -> LocalNetworks? = { null },
) {
    private val quick: OkHttpClient = client.newBuilder()
        .connectTimeout(3, TimeUnit.SECONDS)
        .readTimeout(5, TimeUnit.SECONDS)
        .build()

    private val pinnedClients = ConcurrentHashMap<String, OkHttpClient>()

    /** The HTTP client for [e]: a pinned TLS client for LAN, the normal client otherwise. */
    private fun clientFor(e: Endpoint, base: OkHttpClient): OkHttpClient {
        val pin = e.certSha256 ?: return base
        val key = "${System.identityHashCode(base)}|$pin|${e.host}"
        return pinnedClients.getOrPut(key) { PinnedTls.client(base, pin, e.host) }
    }

    suspend fun info(pc: PcEntry): Pair<PcInfo, Endpoint> = withContext(Dispatchers.IO) {
        var last: Exception = IOException("no endpoints")
        for (e in pc.orderedEndpoints(networks())) {
            try {
                val req = Request.Builder().url(e.url("/api/v1/info")).build()
                clientFor(e, quick).newCall(req).execute().use { res ->
                    if (!res.isSuccessful) throw IOException("HTTP ${res.code}")
                    return@withContext PcInfo.parse(res.body!!.string()) to e
                }
            } catch (ex: Exception) {
                last = ex
            }
        }
        throw last
    }

    /** Tries each QR path until one answers; returns the pairing result and the path that worked. */
    suspend fun pair(offer: PairingOffer, deviceName: String, deviceModel: String): Pair<PairResponse, Endpoint> =
        withContext(Dispatchers.IO) {
            val body = JSONObject()
                .put("code", offer.code)
                .put("deviceName", deviceName)
                .put("deviceModel", deviceModel)
                .toString()
                .toRequestBody(JSON)
            val failures = mutableListOf<String>()
            for (e in RouteSelector.order(offer.endpoints, networks(), null)) {
                val res: Response = try {
                    clientFor(e, quick).newCall(Request.Builder().url(e.url("/api/v1/pair")).post(body).build()).execute()
                } catch (ex: IOException) {
                    failures += "${e.label}: ${describe(ex)}"
                    continue
                }
                res.use {
                    val text = it.body?.string()
                    if (it.isSuccessful) return@withContext PairResponse.parse(text!!) to e
                    throw PairException(errorMessage(text) ?: "페어링 실패 (HTTP ${it.code})")
                }
            }
            throw IOException(
                "PC에 연결할 수 없습니다. 같은 Wi-Fi인지, 또는 PC의 Tailscale·Cloudflare 경로가 켜져 있는지 확인하세요." +
                    if (failures.isEmpty()) "" else "\n" + failures.joinToString("\n"),
            )
        }

    /**
     * Registers this phone's UnifiedPush endpoint and keys with [pc] (`POST /api/v1/push`), trying
     * each path in order. Throws [UnauthorizedException] when the PC no longer knows this phone and
     * [IOException] when no path answered or the PC is too old for push (404).
     */
    suspend fun registerPush(pc: PcEntry, subscription: PushSubscription): PushSubscriptionResponse =
        pushCall(pc, Request.Builder().post(subscription.toJson().toString().toRequestBody(JSON)))

    /** Removes this phone's subscription from [pc] (`DELETE /api/v1/push`). */
    suspend fun unregisterPush(pc: PcEntry): PushSubscriptionResponse = pushCall(pc, Request.Builder().delete())

    private suspend fun pushCall(pc: PcEntry, builder: Request.Builder): PushSubscriptionResponse = withContext(Dispatchers.IO) {
        var last: Exception = IOException("no endpoints")
        for (e in pc.orderedEndpoints(networks())) {
            val req = builder.url(e.url("/api/v1/push")).header("Authorization", "Bearer ${pc.token}").build()
            val res = try {
                clientFor(e, quick).newCall(req).execute()
            } catch (ex: IOException) {
                last = IOException("${e.label}: ${describe(ex)}")
                continue
            }
            res.use {
                val text = it.body?.string()
                when {
                    it.isSuccessful -> return@withContext PushSubscriptionResponse.parse(text!!)
                    it.code == 401 -> throw UnauthorizedException()
                    it.code == 404 -> throw IOException("이 PC의 Homebase가 푸시 알림을 지원하지 않는 버전입니다")
                    else -> throw IOException(errorMessage(text) ?: "푸시 등록 실패 (HTTP ${it.code})")
                }
            }
        }
        throw last
    }

    /**
     * Tells [pc] this phone's state and returns every device paired with it (DECISIONS M-27).
     * Null when the PC predates the route (404). Throws [UnauthorizedException] on 401, [IOException]
     * when no path answered.
     */
    suspend fun presence(pc: PcEntry, report: PresenceReport): DevicesResponse? =
        authedCall(pc, "/api/v1/presence", Request.Builder().post(report.toJson().toString().toRequestBody(JSON))) { code, text ->
            when {
                code in 200..299 -> DevicesResponse.parse(text!!)
                code == 404 -> null
                else -> throw IOException(errorMessage(text) ?: "상태 보고 실패 (HTTP $code)")
            }
        }

    /**
     * Forwards one of this phone's notifications to [pc] (DECISIONS M-28). False when the PC can't
     * relay it (too old: 404, or Windows notification forwarding is off: 503).
     */
    suspend fun sendNotification(pc: PcEntry, n: DeviceNotification): Boolean =
        authedCall(pc, "/api/v1/notifications", Request.Builder().post(n.toJson().toString().toRequestBody(JSON))) { code, text ->
            when {
                code in 200..299 -> true
                code == 404 || code == 503 -> false
                else -> throw IOException(errorMessage(text) ?: "알림 전달 실패 (HTTP $code)")
            }
        }

    /** One authenticated call over [pc]'s paths in order; the first path that answers decides. */
    private suspend fun <T> authedCall(
        pc: PcEntry,
        path: String,
        builder: Request.Builder,
        handle: (code: Int, body: String?) -> T,
    ): T = withContext(Dispatchers.IO) {
        var last: Exception = IOException("no endpoints")
        for (e in pc.orderedEndpoints(networks())) {
            val req = builder.url(e.url(path)).header("Authorization", "Bearer ${pc.token}").build()
            val res = try {
                clientFor(e, quick).newCall(req).execute()
            } catch (ex: IOException) {
                last = IOException("${e.label}: ${describe(ex)}")
                continue
            }
            res.use {
                if (it.code == 401) throw UnauthorizedException()
                return@withContext handle(it.code, it.body?.string())
            }
        }
        throw last
    }

    /** The PC's current endpoints (null when the PC predates the route, or on any failure). */
    private fun fetchEndpoints(pc: PcEntry, e: Endpoint): List<Endpoint>? = try {
        val req = Request.Builder().url(e.url("/api/v1/endpoints")).header("Authorization", "Bearer ${pc.token}").build()
        clientFor(e, quick).newCall(req).execute().use { res ->
            if (res.isSuccessful) parseEndpointsResponse(res.body!!.string()) else null
        }
    } catch (_: Exception) {
        null
    }

    /**
     * Live snapshots while collected. Reconnects with backoff on network loss (re-choosing the
     * path for the network the phone is on now); stops on 401 or a schema mismatch. Cancelling
     * the collector closes the socket, which makes the PC stop sampling. After the first
     * snapshot on a path, the PC's current endpoint list is fetched (on every reconnect) and handed to
     * [onEndpoints] so DHCP or Quick Tunnel address changes follow automatically. Each attempt starts
     * from [latest] (the stored entry), so paths the PC pushed while this was retrying are used (M-23).
     */
    fun live(
        pc: PcEntry,
        onGoodEndpoint: (Endpoint) -> Unit,
        onEndpoints: (List<Endpoint>) -> Unit = {},
        latest: () -> PcEntry? = { null },
    ): Flow<LiveState> = flow {
        var attempt = 0
        var current = pc
        while (true) {
            latest()?.let { current = it }
            emit(LiveState.Connecting)
            var failure: Throwable? = null
            for (e in current.orderedEndpoints(networks())) {
                try {
                    var first = true
                    socketFlow(e, current).collect { snapshot ->
                        if (first) {
                            first = false
                            attempt = 0
                            onGoodEndpoint(e)
                            current = current.copy(lastGood = e.key)
                            // Every (re)connect: a PC restart is exactly when a Quick Tunnel address or LAN IP changes.
                            fetchEndpoints(current, e)?.takeIf { it.isNotEmpty() && it != current.endpoints }?.let {
                                current = current.copy(endpoints = it)
                                onEndpoints(it)
                            }
                        }
                        emit(LiveState.Live(snapshot, e))
                    }
                    failure = IOException("연결이 닫혔습니다")
                } catch (ex: UnauthorizedException) {
                    emit(LiveState.Unauthorized)
                    return@flow
                } catch (ex: ProtocolException) {
                    emit(LiveState.Incompatible(ex.message ?: "호환되지 않는 버전"))
                    return@flow
                } catch (ex: IOException) {
                    failure = IOException("${e.label}: ${describe(ex)}")
                    continue
                }
                break // got snapshots from this path, then lost it → back off and re-pick
            }
            val wait = BACKOFF_SECONDS[minOf(attempt++, BACKOFF_SECONDS.lastIndex)]
            emit(LiveState.Retrying(failure?.message ?: "연결 실패", wait))
            delay(wait * 1000L)
        }
    }.flowOn(Dispatchers.IO)

    private fun socketFlow(e: Endpoint, pc: PcEntry): Flow<LiveSnapshot> = callbackFlow {
        val request = Request.Builder()
            .url(e.url("/api/v1/live"))
            .header("Authorization", "Bearer ${pc.token}")
            .build()
        val ws = clientFor(e, client).newWebSocket(request, object : WebSocketListener() {
            override fun onMessage(webSocket: WebSocket, text: String) {
                try {
                    trySend(LiveSnapshot.parse(text))
                } catch (ex: ProtocolException) {
                    close(ex)
                } catch (ex: Exception) {
                    close(IOException("잘못된 응답: ${ex.message}"))
                }
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                close(if (response?.code == 401) UnauthorizedException() else (t as? IOException ?: IOException(t.message, t)))
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

        /** Short reason for a failed path; a certificate pin mismatch is called out as such. */
        fun describe(e: Throwable): String {
            var c: Throwable? = e
            while (c != null) {
                if (c is PinMismatchException) return c.message ?: "인증서 불일치"
                c = c.cause
            }
            return if (e is SSLException) "TLS 오류 (${e.message})" else e.message ?: e.javaClass.simpleName
        }
    }
}
