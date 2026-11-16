package io.github.jeiel85.homebase.net

import org.json.JSONObject

/*
 * The device group (DECISIONS M-47). Wire shape: docs/mobile-protocol/group.v1.json
 * (`GET /api/v1/group`): the answering PC and the PCs it is linked with; `token` = it holds that PC's
 * token, so it can introduce it.
 */

data class GroupMember(val pcId: String, val pcName: String, val token: Boolean)

data class GroupInfo(val pcId: String, val pcName: String, val pcs: List<GroupMember>) {
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
            )
        }
    }
}

/** `POST /api/v1/group/join` body: the offer (raw JSON as the other PC gave it). */
internal fun groupJoinBody(offerJson: String): String =
    JSONObject().put("schema", SCHEMA).put("offer", JSONObject(offerJson)).toString()

internal fun groupIntroduceBody(pcId: String): String =
    JSONObject().put("schema", SCHEMA).put("pcId", pcId).toString()
