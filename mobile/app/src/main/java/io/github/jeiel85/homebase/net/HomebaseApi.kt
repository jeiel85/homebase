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
/** A call cut off by its time budget: it may or may not have reached the PC. */
class UnconfirmedException : IOException("PC의 응답을 기다리다 시간이 다 됐습니다.")

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

    /** For the PC's local AI (DECISIONS M-60): a model loading cold can take most of a minute to answer. */
    private val patient: OkHttpClient = client.newBuilder()
        .connectTimeout(3, TimeUnit.SECONDS)
        .readTimeout(90, TimeUnit.SECONDS)
        .build()

    /** Clipboard text (DECISIONS M-62): up to ~1 MB on the way up, which a slow mobile link needs time for. */
    private val clipClient: OkHttpClient = client.newBuilder()
        .connectTimeout(3, TimeUnit.SECONDS)
        .writeTimeout(30, TimeUnit.SECONDS)
        .readTimeout(15, TimeUnit.SECONDS)
        .build()

    /**
     * File transfer calls (DECISIONS M-64): a 4 MiB chunk on a slow mobile link takes a while to go up, and the PC
     * waits up to 120 s for it.
     */
    private val transferClient: OkHttpClient = client.newBuilder()
        .connectTimeout(5, TimeUnit.SECONDS)
        .writeTimeout(120, TimeUnit.SECONDS)
        .readTimeout(60, TimeUnit.SECONDS)
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
                // The token isn't needed for /info, but over the Cloudflare Tunnel an anonymous caller
                // gets an empty PC name (M-39), and the list keeps the stored name in sync with it.
                val req = Request.Builder().url(e.url("/api/v1/info")).header("Authorization", "Bearer ${pc.token}").build()
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

    /** [pc]'s device group (DECISIONS M-47), or null for a PC older than v0.35.0. */
    suspend fun group(pc: PcEntry): GroupInfo? =
        authedCall(pc, "/api/v1/group", Request.Builder().get()) { code, text ->
            when {
                code in 200..299 -> GroupInfo.parse(text!!)
                code == 404 || code == 405 -> null
                else -> throw IOException(errorMessage(text) ?: "그룹 확인 실패 (HTTP $code)")
            }
        }

    /** A fresh one-time pairing offer of [pc] (raw JSON), to hand to another of the group's PCs. */
    suspend fun groupOffer(pc: PcEntry): String? =
        authedCall(pc, "/api/v1/group/offer", Request.Builder().post("{\"schema\":1}".toRequestBody(JSON))) { code, text ->
            if (code in 200..299) text else null
        }

    /** Asks [pc] to link to the PC behind [offerJson]. */
    suspend fun groupJoin(pc: PcEntry, offerJson: String): Boolean =
        authedCall(pc, "/api/v1/group/join", Request.Builder().post(groupJoinBody(offerJson).toRequestBody(JSON))) { code, text ->
            code in 200..299 && JSONObject(text ?: "{}").let { it.optBoolean("linked") || it.optBoolean("already") }
        }

    /** Hands the group's aliases this phone decided to [pc] (DECISIONS M-51); false for a PC older than v0.38.0. */
    suspend fun groupAlias(pc: PcEntry, entries: List<GroupAlias>): Boolean =
        authedCall(pc, "/api/v1/group/alias", Request.Builder().post(groupAliasBody(entries).toRequestBody(JSON))) { code, _ ->
            code in 200..299
        }

    /** A fresh offer of [pcId] (raw JSON) from [pc], which holds its token; null when it can't. */
    suspend fun groupIntroduce(pc: PcEntry, pcId: String): String? =
        authedCall(pc, "/api/v1/group/introduce", Request.Builder().post(groupIntroduceBody(pcId).toRequestBody(JSON))) { code, text ->
            if (code in 200..299) text else null
        }

    /**
     * Answers a Claude Code permission prompt on [pc] (DECISIONS M-48). True when the PC took it,
     * false when the prompt is gone (answered elsewhere, withdrawn or timed out: 410) or the PC is too
     * old to know approvals (404).
     */
    suspend fun answerApproval(pc: PcEntry, id: String, decision: String, budgetMillis: Long): Boolean {
        val body = JSONObject().put("schema", 1).put("id", id).put("decision", decision).toString()
        return authedCall(pc, "/api/v1/approval", Request.Builder().post(body.toRequestBody(JSON)), budgetMillis) { code, text ->
            when {
                code in 200..299 -> true
                code == 404 || code == 410 -> false
                else -> throw IOException(errorMessage(text) ?: "승인 응답 실패 (HTTP $code)")
            }
        }
    }

    /**
     * Asks [pc]'s local AI for advice about its current state (DECISIONS M-60). Null when the PC
     * predates the route (404/405: v0.45.0 and older). Advice that couldn't be produced (Ollama not
     * running, model not pulled …) is a reply with `ok = false`, not an exception.
     */
    suspend fun advise(pc: PcEntry): AdviceReply? =
        authedCall(pc, "/api/v1/advise", Request.Builder().post("{\"schema\":1}".toRequestBody(JSON)), base = patient) { code, text ->
            when {
                code in 200..299 -> try {
                    AdviceReply.parse(text ?: "")
                } catch (_: org.json.JSONException) {
                    throw IOException("PC의 응답을 읽지 못했습니다.")
                }
                code == 404 || code == 405 -> null
                else -> throw IOException(errorMessage(text) ?: "조언 요청 실패 (HTTP $code)")
            }
        }

    /**
     * Agrees a fresh clipboard key with [pc] (`POST /api/v1/clip/key`, DECISIONS M-62): an ECDH exchange
     * with a P-256 pair made for this call only; the PC replaces any key it held for this phone. Returns the
     * 32-byte key, or null when the PC is too old to know the route (404/405). Throws
     * [UnauthorizedException] on 401 and [IOException] when no path answered or the answer is unusable.
     */
    suspend fun agreeClipKey(pc: PcEntry): ByteArray? {
        val agreement = ClipKeyAgreement()
        val body = agreement.requestJson().toString().toRequestBody(JSON)
        return authedCall(pc, ClipProtocol.KEY_PATH, Request.Builder().post(body)) { code, answer ->
            when {
                code in 200..299 -> agreement.finish(answer) ?: throw IOException("PC의 클립보드 키를 읽지 못했습니다.")
                code == 404 || code == 405 -> null
                else -> throw IOException(errorMessage(answer) ?: "클립보드 키 교환 실패 (HTTP $code)")
            }
        }
    }

    /**
     * Puts [text] on [pc]'s clipboard (DECISIONS M-62), sealed with [key] (agreed with that PC), so the
     * Cloudflare edge never reads it. With [to] (device ids from [sharePhones]) the PC passes it on to those
     * phones instead (docs/SHARE_CONVENIENCE.md §4.8) and answers [ClipSendResult.Relayed]. Throws
     * [UnauthorizedException] on 401 and [IOException] when no path answered; everything the PC said is a
     * [ClipSendResult].
     */
    suspend fun sendClip(pc: PcEntry, key: ByteArray, text: String, sensitive: Boolean, to: List<String> = emptyList()): ClipSendResult {
        val envelope = ClipEnvelope.seal(key, ClipProtocol.newId(), ClipContent.of(text, sensitive, to = to))
        val body = envelope.toJson().toString().toRequestBody(JSON)
        return authedCall(pc, ClipProtocol.PATH, Request.Builder().post(body), base = clipClient) { code, answer ->
            ClipSendResult.of(code, answer)
        }
    }

    /**
     * What [pc] lets this phone ask it to do (`GET /api/v1/commands`, DECISIONS M-69), in the order to show. Null when
     * the PC is older than remote commands (404/405). Throws [UnauthorizedException] on 401 and [IOException] when no
     * path answered or the answer is unreadable.
     */
    suspend fun commands(pc: PcEntry): CommandList? =
        authedCall(pc, RemoteCommandProtocol.PATH, Request.Builder().get()) { code, text ->
            when {
                code in 200..299 -> CommandList.parse(text) ?: throw IOException("PC의 원격 명령 목록을 읽지 못했습니다.")
                code == 404 || code == 405 -> null
                else -> throw IOException(errorMessage(text) ?: "원격 명령 목록 확인 실패 (HTTP $code)")
            }
        }

    /**
     * Asks [pc] to carry out [command] (DECISIONS M-69) — or, with [cancel], to call it off (a restart counting down) —
     * sealed with [key] (agreed with that PC) under a fresh id and this phone's clock, so the Cloudflare edge can't make
     * or replay one. With [budgetMillis] the call ends in [UnconfirmedException] once that is spent (see [authedCall]).
     * Throws [UnauthorizedException] on 401 and [IOException] when no path answered; everything the PC said is a
     * [CommandOutcome].
     */
    suspend fun sendCommand(pc: PcEntry, key: ByteArray, command: String, cancel: Boolean = false, budgetMillis: Long? = null): CommandOutcome {
        val envelope = CommandRequest.of(command).seal(key, ClipProtocol.newId())
        val body = envelope.toJson().toString().toRequestBody(JSON)
        val path = if (cancel) RemoteCommandProtocol.CANCEL_PATH else RemoteCommandProtocol.PATH
        return authedCall(pc, path, Request.Builder().post(body), budgetMillis, base = clipClient) { code, answer ->
            CommandOutcome.of(code, answer)
        }
    }

    /**
     * The other phones [pc] can pass text and files on to (`GET /api/v1/share/phones`, docs/SHARE_CONVENIENCE.md §4.8),
     * this phone not among them. Null when the PC is too old to relay (404/405) or its answer is unreadable — nothing
     * is relayed through it then. Throws [UnauthorizedException] on 401 and [IOException] when no path answered.
     */
    suspend fun sharePhones(pc: PcEntry): List<RelayPhone>? =
        authedCall(pc, ShareRelay.PHONES_PATH, Request.Builder().get()) { code, text ->
            when {
                code in 200..299 -> RelayPhone.parseList(text)
                code == 404 || code == 405 -> null
                else -> throw IOException(errorMessage(text) ?: "휴대폰 목록 확인 실패 (HTTP $code)")
            }
        }

    /**
     * Text [pc] kept for this phone because it was too long for a push (DECISIONS M-62), sealed with [key].
     * Null when it is gone (more than 10 minutes passed, or the PC restarted). Throws like [sendClip].
     */
    suspend fun fetchClip(pc: PcEntry, key: ByteArray, id: String): ClipContent? =
        authedCall(pc, "${ClipProtocol.PATH}?id=$id", Request.Builder().get(), base = clipClient) { code, body ->
            when {
                code in 200..299 -> ClipEnvelope.parse(body ?: "")?.open(key)
                    ?: throw IOException("PC가 보낸 텍스트를 열지 못했습니다.")
                code == 404 || code == 405 -> null
                else -> throw IOException(errorMessage(body) ?: "텍스트 받기 실패 (HTTP $code)")
            }
        }

    /**
     * One call of a file transfer (DECISIONS M-64) over [pc]'s paths, [prefer] first when given — the path that
     * answered the last call, so a transfer of many chunks doesn't wait for a dead path's timeout on each one.
     * Returns what the PC answered and the path it came over. Throws [UnauthorizedException] on 401 and
     * [IOException] when no path answered.
     */
    suspend fun transfer(
        pc: PcEntry,
        path: String,
        builder: Request.Builder,
        prefer: Endpoint? = null,
        // A chunk a PC sends (2b, DECISIONS M-65): a 2xx body is kept as bytes in [TransferAnswer.bytes].
        binary: Boolean = false,
    ): TransferAnswer =
        withContext(Dispatchers.IO) {
            val ordered = pc.orderedEndpoints(networks())
            val paths = if (prefer != null && prefer in ordered) listOf(prefer) + ordered.filter { it != prefer } else ordered
            var last: Exception = IOException("no endpoints")
            for (e in paths) {
                val req = builder.url(e.url(path)).header("Authorization", "Bearer ${pc.token}").build()
                val res = try {
                    clientFor(e, transferClient).newCall(req).execute()
                } catch (ex: IOException) {
                    last = IOException("${e.label}: ${describe(ex)}")
                    continue
                }
                res.use {
                    if (it.code == 401) throw UnauthorizedException()
                    if (binary && it.isSuccessful) return@withContext TransferAnswer(it.code, null, e, it.body?.bytes())
                    return@withContext TransferAnswer(it.code, it.body?.string(), e)
                }
            }
            throw last
        }

    /**
     * One authenticated call over [pc]'s paths in order; the first path that answers decides.
     * With [budgetMillis] every attempt is bounded by what is left of it (OkHttp's call timeout, which
     * really stops a blocking call), and no further path is tried once it is spent. A call cut off by
     * the budget may already have reached the PC, so it ends in [UnconfirmedException], not a retry.
     */
    private suspend fun <T> authedCall(
        pc: PcEntry,
        path: String,
        builder: Request.Builder,
        budgetMillis: Long? = null,
        base: OkHttpClient = quick,
        handle: (code: Int, body: String?) -> T,
    ): T = withContext(Dispatchers.IO) {
        val deadline = budgetMillis?.let { System.currentTimeMillis() + it }
        var last: Exception = IOException("no endpoints")
        for (e in pc.orderedEndpoints(networks())) {
            val remaining = deadline?.let { it - System.currentTimeMillis() }
            if (remaining != null && remaining <= 0) break
            val req = builder.url(e.url(path)).header("Authorization", "Bearer ${pc.token}").build()
            val pathClient = clientFor(e, base)
            val client = if (remaining != null) pathClient.newBuilder().callTimeout(remaining, TimeUnit.MILLISECONDS).build() else pathClient
            val res = try {
                client.newCall(req).execute()
            } catch (ex: IOException) {
                if (deadline != null && System.currentTimeMillis() >= deadline) throw UnconfirmedException()
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
