package io.github.jeiel85.homebase.push

import io.github.jeiel85.homebase.net.Endpoint
import io.github.jeiel85.homebase.net.ProtocolException
import io.github.jeiel85.homebase.net.SCHEMA
import org.json.JSONObject
import java.security.MessageDigest
import java.time.OffsetDateTime
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

/**
 * Push wire models mirroring `LocalOpsBot.Core.Push.PushContracts` (docs/MOBILE_PROTOCOL.md,
 * fixtures `push-subscription.v1.json`, `push-subscription-response.v1.json`, `push-message.v1.json`).
 */

/** What this phone registers with each PC: its UnifiedPush endpoint and RFC 8291 keys (base64url). */
data class PushSubscription(val endpoint: String, val p256dh: String, val auth: String) {
    fun toJson(): JSONObject = JSONObject()
        .put("schema", SCHEMA)
        .put("endpoint", endpoint)
        .put("p256dh", p256dh)
        .put("auth", auth)
}

data class PushSubscriptionResponse(val deviceId: String, val registered: Boolean) {
    companion object {
        fun parse(json: String): PushSubscriptionResponse {
            val o = JSONObject(json)
            if (o.optInt("schema") != SCHEMA) throw ProtocolException("지원하지 않는 응답 형식 (schema ${o.optInt("schema")})")
            return PushSubscriptionResponse(o.getString("deviceId"), o.getBoolean("registered"))
        }
    }
}

/**
 * A PC's current paths, pushed when a new one appears (DECISIONS M-23, fixture `push-endpoints.v1.json`).
 * Without it a PC restart that changes its Quick Tunnel address strands a phone that can only reach the
 * PC that way: the app learns new paths from `/api/v1/endpoints`, which needs a working path first.
 */
data class PushEndpointsUpdate(val pcId: String, val tsEpochMillis: Long, val endpoints: List<Endpoint>) {
    companion object {
        const val KIND = "endpoints"

        /** `kind` of a decrypted push, or null when it is a notification (or not JSON). */
        fun kindOf(json: String): String? = try {
            JSONObject(json).optString("kind").ifEmpty { null }
        } catch (_: Exception) {
            null
        }

        /** `pcId` of the envelope, to look up which token verifies it. */
        fun pcIdOf(json: String): String = JSONObject(json).optString("pcId")

        /**
         * Checks the MAC with [token] (this phone's bearer token for that PC) and parses the signed part.
         * All PCs share this phone's push keys, so decryption alone doesn't prove which PC sent it; the
         * MAC does. Throws [ProtocolException] for a bad MAC, a pcId mismatch or a malformed payload.
         */
        fun verify(json: String, token: String): PushEndpointsUpdate {
            val o = try {
                JSONObject(json)
            } catch (e: Exception) {
                throw ProtocolException("푸시 내용이 JSON이 아닙니다")
            }
            if (o.optInt("schema") != SCHEMA || o.optString("kind") != KIND) throw ProtocolException("경로 푸시 형식이 아닙니다")
            val data = try {
                WebPush.unb64url(o.getString("data"))
            } catch (e: Exception) {
                throw ProtocolException("경로 푸시 data가 잘못되었습니다")
            }
            val mac = try {
                WebPush.unb64url(o.getString("mac"))
            } catch (e: Exception) {
                throw ProtocolException("경로 푸시 mac이 잘못되었습니다")
            }
            val key = MessageDigest.getInstance("SHA-256").digest(token.toByteArray(Charsets.UTF_8))
            val expected = Mac.getInstance("HmacSHA256").apply { init(SecretKeySpec(key, "HmacSHA256")) }.doFinal(data)
            if (!MessageDigest.isEqual(expected, mac)) throw ProtocolException("경로 푸시 서명이 맞지 않습니다")

            val inner = try {
                JSONObject(data.toString(Charsets.UTF_8))
            } catch (e: Exception) {
                throw ProtocolException("경로 푸시 본문이 JSON이 아닙니다")
            }
            if (inner.optInt("schema") != SCHEMA) throw ProtocolException("지원하지 않는 경로 푸시 (schema ${inner.optInt("schema")})")
            val pcId = inner.optString("pcId")
            if (pcId.isEmpty() || pcId != o.optString("pcId")) throw ProtocolException("경로 푸시의 PC가 맞지 않습니다")
            val ts = try {
                OffsetDateTime.parse(inner.getString("ts")).toInstant().toEpochMilli()
            } catch (e: Exception) {
                throw ProtocolException("경로 푸시 시각이 잘못되었습니다")
            }
            val endpoints = Endpoint.parseList(inner.optJSONArray("endpoints"))
            if (endpoints.isEmpty()) throw ProtocolException("경로 푸시에 쓸 수 있는 경로가 없습니다")
            return PushEndpointsUpdate(pcId, ts, endpoints)
        }
    }
}

/**
 * One decrypted notification. [source] is the Windows app for forwarded toasts and the alert kind
 * otherwise; with [pcId] it picks the Android notification channel, so each can be muted alone.
 */
data class PushMessage(
    val id: String,
    val pcId: String,
    val pcName: String,
    val category: String,
    val priority: String,
    val title: String,
    val body: String?,
    val source: String,
    val count: Int,
    val ts: String,
) {
    val isToast: Boolean get() = category == "windows-toast"

    companion object {
        /** Throws [ProtocolException] for another schema or a missing field — never a half-filled message. */
        fun parse(json: String): PushMessage {
            val o = try {
                JSONObject(json)
            } catch (e: Exception) {
                throw ProtocolException("푸시 내용이 JSON이 아닙니다")
            }
            if (o.optInt("schema") != SCHEMA) throw ProtocolException("지원하지 않는 푸시 형식 (schema ${o.optInt("schema")})")
            return try {
                PushMessage(
                    id = o.getString("id"),
                    pcId = o.getString("pcId"),
                    pcName = o.getString("pcName"),
                    category = o.getString("category"),
                    priority = o.getString("priority"),
                    title = o.getString("title"),
                    body = if (o.isNull("body")) null else o.optString("body").ifEmpty { null },
                    source = o.getString("source"),
                    count = o.optInt("count", 1).coerceAtLeast(1),
                    ts = o.getString("ts"),
                )
            } catch (e: Exception) {
                throw ProtocolException("푸시 필드가 빠졌습니다: ${e.message}")
            }
        }
    }
}
