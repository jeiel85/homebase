package io.github.jeiel85.homebase.push

import io.github.jeiel85.homebase.net.ProtocolException
import io.github.jeiel85.homebase.net.SCHEMA
import org.json.JSONObject

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
