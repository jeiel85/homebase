package io.github.jeiel85.homebase.net

import org.json.JSONObject

/**
 * Text and files from this phone to another phone through a PC both are paired with (docs/SHARE_CONVENIENCE.md §4.8
 * and §9, fixture `share-relay.v1.json`):
 * - `GET /api/v1/share/phones` lists the other phones that PC can pass things on to ([RelayPhone]); a PC that answers
 *   404 is too old to relay, and nothing is relayed through it.
 * - Text: the usual `POST /api/v1/clip` with `to` in the sealed [ClipContent]. The PC then doesn't put the text on its
 *   own clipboard; it pushes it on and answers which phones it reached ([Forwarded]).
 * - Files: the usual upload (2a) with `to` in the sealed [TransferMeta]. `done` answers `saved: "forwarded"`, and the PC
 *   sends the phones in `to` an ordinary `share` push (2b).
 * - What a phone receives that way carries `from`, the sending phone's name ([ClipContent.from], [ShareNotice.from]).
 *
 * Each leg is sealed with the key of that device–PC pair, so the PC opens what it passes on (it is the user's own PC,
 * the same trust as the notification relay of M-28).
 */
object ShareRelay {
    const val PHONES_PATH = "/api/v1/share/phones"

    /** `saved` of `POST /api/v1/transfer/done` when the PC passes the files on to the phones in `to`. */
    const val SAVED_FORWARDED = "forwarded"

    /** "집 PC에서", or for something another phone sent through that PC "Galaxy S24에서(집 PC 경유)". */
    fun fromLine(pcName: String, from: String?): String =
        if (from.isNullOrBlank()) "${pcName}에서" else "${from.trim()}에서(${pcName} 경유)"

    /** "Galaxy Tab S9", "Galaxy Tab S9, iPad", "Galaxy Tab S9 외 2대". */
    fun names(names: List<String>): String = when {
        names.isEmpty() -> "휴대폰"
        names.size <= 2 -> names.joinToString(", ")
        else -> "${names.first()} 외 ${names.size - 1}대"
    }

    /**
     * The phones to offer, from each PC's answer in [answers] (in the order the PCs are preferred as relays): those
     * that can take [forFiles] (files) or text, each once — a phone paired with two PCs is listed by both, under a
     * different device id each, so it is known by its name — through the first PC that lists it.
     */
    fun targets(answers: List<RelayAnswer>, forFiles: Boolean): List<RelayTarget> {
        val seen = mutableSetOf<String>()
        val result = mutableListOf<RelayTarget>()
        for (answer in answers) {
            for (phone in answer.phones) {
                val takes = if (forFiles) phone.files else phone.clip
                if (!takes) continue
                if (!seen.add(phone.name.trim().lowercase())) continue
                result += RelayTarget(phone, answer.pcId, answer.pcName)
            }
        }
        return result
    }
}

/** One phone a PC can pass things on to: [clip] when it takes clipboard text, [files] when it takes files. */
data class RelayPhone(val deviceId: String, val name: String, val clip: Boolean, val files: Boolean) {
    companion object {
        /**
         * The phones of a `GET /api/v1/share/phones` answer; null when it is not of this schema or not readable. An
         * entry without a device id is left out; an unnamed one is called "휴대폰". Unknown fields are ignored.
         */
        fun parseList(json: String?): List<RelayPhone>? = try {
            val o = JSONObject(json ?: "")
            if (o.optInt("schema") != SCHEMA) {
                null
            } else {
                o.optJSONArray("phones").objects().mapNotNull { p ->
                    val id = p.optString("deviceId").trim()
                    if (id.isEmpty()) {
                        null
                    } else {
                        RelayPhone(
                            deviceId = id,
                            name = p.optString("name").trim().ifEmpty { "휴대폰" },
                            clip = p.optBoolean("clip", false),
                            files = p.optBoolean("files", false),
                        )
                    }
                }
            }
        } catch (_: Exception) {
            null
        }
    }
}

/** One PC's answer to `GET /api/v1/share/phones`. */
data class RelayAnswer(val pcId: String, val pcName: String, val phones: List<RelayPhone>)

/** A phone offered in the share center and the PC that passes things on to it. */
data class RelayTarget(val phone: RelayPhone, val pcId: String, val pcName: String) {
    /** The device id is the relaying PC's own name for that phone, so the PC is part of the key. */
    val key: String get() = "phone:$pcId/${phone.deviceId}"
}

/** What the PC said about one phone in `to` of relayed text: pushed on ([ok]) or not, and why ([error]). */
data class Forwarded(val deviceId: String, val ok: Boolean, val error: String? = null) {
    companion object {
        /** The `forwarded` list of a `POST /api/v1/clip` answer; null when the answer has none (not relayed). */
        fun parseList(body: String?): List<Forwarded>? = try {
            val o = JSONObject(body ?: "")
            val list = o.optJSONArray("forwarded")
            if (list == null) {
                null
            } else {
                list.objects().map { f ->
                    Forwarded(
                        deviceId = f.optString("deviceId"),
                        ok = f.optBoolean("ok", false),
                        error = if (f.isNull("error")) null else f.optString("error").ifBlank { null },
                    )
                }
            }
        } catch (_: Exception) {
            null
        }
    }
}
