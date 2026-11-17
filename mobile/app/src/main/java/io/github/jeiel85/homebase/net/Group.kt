package io.github.jeiel85.homebase.net

import org.json.JSONArray
import org.json.JSONObject
import java.time.Instant
import java.time.OffsetDateTime

/*
 * The device group (DECISIONS M-47). Wire shape: docs/mobile-protocol/group.v1.json
 * (`GET /api/v1/group`): the answering PC and the PCs it is linked with; `token` = it holds that PC's
 * token, so it can introduce it. Since v0.37.0 it also carries the group's aliases (DECISIONS M-50).
 */

data class GroupMember(val pcId: String, val pcName: String, val token: Boolean)

/**
 * One alias of the group (DECISIONS M-50): [key] is `pc:<pcId>` for a PC (phones are named on the PCs
 * only), [name] what the device reported, [alias] null when it was cleared, [atMillis] when it was set
 * or cleared — the latest wins everywhere.
 */
data class GroupAlias(val key: String, val name: String, val alias: String?, val atMillis: Long) {
    fun toJson(): JSONObject = JSONObject()
        .put("key", key)
        .put("name", name)
        .apply { alias?.let { put("alias", it) } }
        .put("at", Instant.ofEpochMilli(atMillis).toString())

    companion object {
        fun pcKey(pcId: String) = "pc:$pcId"

        /** null for an entry this app can't read (no key, a time it can't parse). */
        fun parse(o: JSONObject): GroupAlias? {
            val key = o.optString("key").ifEmpty { return null }
            val at = try {
                OffsetDateTime.parse(o.optString("at")).toInstant().toEpochMilli()
            } catch (_: Exception) {
                return null
            }
            val alias = if (o.isNull("alias")) null else o.optString("alias").ifEmpty { null }
            return GroupAlias(key, o.optString("name"), alias, at)
        }
    }
}

/**
 * [aliasesKnown]: the PC answered with an `aliases` list (v0.37.0 or later), so it takes them too —
 * an older PC is not sent any.
 */
data class GroupInfo(
    val pcId: String,
    val pcName: String,
    val pcs: List<GroupMember>,
    val aliases: List<GroupAlias> = emptyList(),
    val aliasesKnown: Boolean = false,
) {
    companion object {
        fun parse(json: String): GroupInfo {
            val o = JSONObject(json)
            val schema = o.optInt("schema")
            if (schema != SCHEMA) throw ProtocolException("Unsupported group schema $schema")
            return GroupInfo(
                pcId = o.getString("pcId"),
                pcName = o.optString("pcName"),
                pcs = o.optJSONArray("pcs").objects().map {
                    GroupMember(it.getString("pcId"), it.optString("pcName"), it.optBoolean("token"))
                },
                aliases = o.optJSONArray("aliases").objects().mapNotNull(GroupAlias::parse),
                aliasesKnown = o.has("aliases"),
            )
        }
    }
}

/** `POST /api/v1/group/join` body: the offer (raw JSON as the other PC gave it). */
internal fun groupJoinBody(offerJson: String): String =
    JSONObject().put("schema", SCHEMA).put("offer", JSONObject(offerJson)).toString()

internal fun groupIntroduceBody(pcId: String): String =
    JSONObject().put("schema", SCHEMA).put("pcId", pcId).toString()

/** `POST /api/v1/group/alias` body (DECISIONS M-50). */
internal fun groupAliasBody(entries: List<GroupAlias>): String =
    JSONObject().put("schema", SCHEMA).put("aliases", JSONArray().apply { entries.forEach { put(it.toJson()) } }).toString()
