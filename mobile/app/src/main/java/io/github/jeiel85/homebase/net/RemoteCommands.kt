package io.github.jeiel85.homebase.net

import org.json.JSONObject
import java.time.Instant
import java.time.OffsetDateTime

/**
 * Commands this phone asks a PC to carry out (DECISIONS M-69, docs/REMOTE_COMMANDS.md), mirroring
 * `LocalOpsBot.Core.Mobile.RemoteCommands` (fixture `commands.v1.json`): a fixed list the PC turns on one by one,
 * never a shell.
 * - `GET /api/v1/commands`: what the PC turned on, in the order to show ([CommandList]); 404 on an older PC.
 * - `POST /api/v1/commands` and `/cancel`: a [CommandRequest] sealed like clipboard text — a [ClipEnvelope] under the
 *   key this phone agreed with that PC (M-62), so the Cloudflare edge, which sees the Bearer token, can neither make
 *   nor replay one — answered with a [CommandOutcome].
 */
object RemoteCommandProtocol {
    const val PATH = "/api/v1/commands"
    const val CANCEL_PATH = "/api/v1/commands/cancel"

    /** A restart waits this long, with a countdown and [취소] on the PC. */
    const val RESTART_DELAY_SECONDS = 60
}

/** The commands there are. A custom one is `custom:<id>`, registered on the PC by its user. */
object RemoteCommandNames {
    const val LOCK = "lock"
    const val SCREEN_OFF = "screen_off"
    const val MUTE = "mute"
    const val VOLUME_UP = "volume_up"
    const val VOLUME_DOWN = "volume_down"
    const val PLAY_PAUSE = "media_play_pause"
    const val NEXT = "media_next"
    const val PREVIOUS = "media_previous"
    const val RESTART = "restart"
    const val SLEEP = "sleep"
    const val CUSTOM_PREFIX = "custom:"

    fun isSystem(name: String): Boolean = name == RESTART || name == SLEEP

    fun isCustom(name: String): Boolean = name.startsWith(CUSTOM_PREFIX) && name.length > CUSTOM_PREFIX.length

    /** The kind the PC gives [name] ([CommandKinds]); for a list entry that names none. */
    fun kindOf(name: String): String = when {
        isSystem(name) -> CommandKinds.SYSTEM
        isCustom(name) -> CommandKinds.CUSTOM
        else -> CommandKinds.SESSION
    }

    /** The PC's own label for [name] (the list carries it; this is for calls made without the list). */
    fun label(name: String): String = when (name) {
        LOCK -> "잠그기"
        SCREEN_OFF -> "화면 끄기"
        MUTE -> "음소거"
        VOLUME_UP -> "소리 크게"
        VOLUME_DOWN -> "소리 작게"
        PLAY_PAUSE -> "재생/일시정지"
        NEXT -> "다음"
        PREVIOUS -> "이전"
        RESTART -> "다시 시작"
        SLEEP -> "절전"
        else -> name.removePrefix(CUSTOM_PREFIX)
    }
}

object CommandKinds {
    /** Run by the PC's Tray in the signed-in user's session. */
    const val SESSION = "session"

    /** Run by the Agent (restart, sleep). */
    const val SYSTEM = "system"

    /** A program the PC's user registered, run as that user. */
    const val CUSTOM = "custom"
}

object CommandStates {
    const val DONE = "done"

    /** A restart counting down on the PC, at [CommandOutcome.Ok.at]. */
    const val SCHEDULED = "scheduled"
    const val CANCELLED = "cancelled"
}

object CommandErrors {
    /** Not turned on on that PC (or not a command at all). */
    const val NOT_ALLOWED = "not_allowed"

    /** Doesn't open with the agreed key, or not a request of this schema. */
    const val INVALID = "invalid_command"

    /** This id was carried out already, or the request's time is more than 2 minutes off the PC's clock. */
    const val REPLAYED = "replayed"

    /** Too many requests in a minute. */
    const val BUSY = "busy"

    /** A cancel with nothing to cancel. */
    const val NOTHING_PENDING = "nothing_pending"
    const val FAILED = "failed"

    /** Nobody is signed in to the PC to run a session command (as for the clipboard). */
    const val NO_USER_SESSION = "no_user_session"

    /** The PC holds no clipboard key for this phone (as for the clipboard). */
    const val KEY_NEEDED = "clip_key_needed"
}

/**
 * One command the PC turned on. [label] is the PC's (a custom command's is its user's name for it). [confirm]: the PC
 * asks the phone to confirm and unlock first.
 */
data class CommandInfo(val name: String, val label: String, val kind: String, val confirm: Boolean) {
    /**
     * Whether this phone asks to confirm and unlock before sending: when the PC says so, and always for restart, sleep
     * and custom commands, or a kind this version doesn't know — a PC can't talk the phone out of it.
     */
    val asks: Boolean
        get() = confirm || kind != CommandKinds.SESSION || RemoteCommandNames.isSystem(name) || RemoteCommandNames.isCustom(name)
}

/** A command under way on the PC — a restart counting down — due at [at]. */
data class CommandPending(val name: String, val at: String) {
    val atMillis: Long? get() = epochMillis(at)
}

/** `GET /api/v1/commands`. [wakeMacs]: the PC's adapters for Wake-on-LAN, normalized ([WakeOnLan.normalize]). */
data class CommandList(val commands: List<CommandInfo>, val wakeMacs: List<String>, val pending: CommandPending?) {
    companion object {
        /** Null for another schema or an unreadable answer; entries without a name and unreadable MACs are dropped. */
        fun parse(json: String?): CommandList? = try {
            val o = JSONObject(json ?: "")
            if (o.optInt("schema") != SCHEMA) {
                null
            } else {
                CommandList(
                    commands = o.optJSONArray("commands").objects().mapNotNull { c ->
                        val name = c.text("name") ?: return@mapNotNull null
                        val kind = c.text("kind") ?: RemoteCommandNames.kindOf(name)
                        CommandInfo(
                            name = name,
                            label = c.text("label") ?: RemoteCommandNames.label(name),
                            kind = kind,
                            // Absent: a system or custom command asks anyway (see [CommandInfo.asks]).
                            confirm = c.optBoolean("confirm", kind != CommandKinds.SESSION),
                        )
                    }.distinctBy { it.name },
                    wakeMacs = o.optJSONObject("wake")?.optJSONArray("macs")?.strings()
                        ?.mapNotNull(WakeOnLan::normalize)?.distinct() ?: emptyList(),
                    pending = o.optJSONObject("pending")?.let { p ->
                        val name = p.text("name")
                        val at = p.text("at")
                        if (name == null || at == null) null else CommandPending(name, at)
                    },
                )
            }
        } catch (_: Exception) {
            null
        }
    }
}

/** The sealed part of `POST /api/v1/commands` and `/cancel`: `{schema, command, ts}`, [ts] this phone's clock. */
data class CommandRequest(val command: String, val ts: String) {
    fun toJson(): JSONObject = JSONObject().put("schema", SCHEMA).put("command", command).put("ts", ts)

    /** Sealed with [key] (the one agreed with that PC) under a fresh [id] — one per request, never reused. [nonce]: tests only. */
    fun seal(key: ByteArray, id: String, nonce: ByteArray? = null): ClipEnvelope {
        val (n, data) = ClipCrypto.seal(key, toJson().toString().toByteArray(Charsets.UTF_8), nonce)
        return ClipEnvelope(id, n, data)
    }

    companion object {
        fun of(command: String, nowMillis: Long = System.currentTimeMillis()) = CommandRequest(command, Instant.ofEpochMilli(nowMillis).toString())

        /** Null for another schema or a malformed payload. */
        fun parse(json: String): CommandRequest? = try {
            val o = JSONObject(json)
            val command = o.text("command")
            val ts = o.text("ts")
            if (o.optInt("schema") != SCHEMA || command == null || ts == null) null else CommandRequest(command, ts)
        } catch (_: Exception) {
            null
        }

        /** The request in [envelope], or null when it doesn't open with [key]. */
        fun open(key: ByteArray, envelope: ClipEnvelope): CommandRequest? {
            if (!ClipProtocol.isValidId(envelope.id)) return null
            val plain = ClipCrypto.open(key, envelope.nonce, envelope.data) ?: return null
            return parse(plain.toString(Charsets.UTF_8))
        }
    }
}

/** What came of a command: the PC's answer (`{schema, ok, state, at}` or `{schema, ok: false, error}`), or why there is none. */
sealed interface CommandOutcome {
    /** Carried out ([CommandStates.DONE]), a restart counting down ([CommandStates.SCHEDULED], due at [at]), or called off. */
    data class Ok(val state: String, val at: String? = null) : CommandOutcome {
        val atMillis: Long? get() = at?.let(::epochMillis)
    }

    /** The PC said no: [error] is a [CommandErrors] code. */
    data class Refused(val error: String) : CommandOutcome

    /** That PC's Homebase is older than remote commands (404) or clipboard keys. */
    data object Unsupported : CommandOutcome

    /** The PC no longer knows this phone (401). */
    data object Unpaired : CommandOutcome

    /** No path to the PC answered. */
    data object Unreachable : CommandOutcome

    /** Cut off by its time budget: it may or may not have reached the PC. */
    data object Unconfirmed : CommandOutcome

    data class Failed(val reason: String) : CommandOutcome

    /** The PC can't open it with the key it holds for this phone: agree a new key and send once more (new id). */
    val needsNewKey: Boolean
        get() = (this as? Refused)?.error.let { it == CommandErrors.KEY_NEEDED || it == CommandErrors.INVALID }

    companion object {
        /** What the PC's answer [code] / [body] means. */
        fun of(code: Int, body: String?): CommandOutcome {
            val o = try {
                JSONObject(body ?: "")
            } catch (_: Exception) {
                null
            }
            val error = o?.text("error")
            if (code in 200..299) {
                if (o == null) return Failed("PC의 응답을 읽지 못했습니다")
                return if (o.optBoolean("ok", true)) Ok(o.text("state") ?: CommandStates.DONE, o.text("at")) else Refused(error ?: CommandErrors.FAILED)
            }
            return when {
                code == 404 || code == 405 -> Unsupported
                error != null -> Refused(error)
                code == 403 -> Refused(CommandErrors.NOT_ALLOWED)
                code == 429 -> Refused(CommandErrors.BUSY)
                code == 400 -> Refused(CommandErrors.INVALID)
                else -> Failed(errorMessage(body) ?: "HTTP $code")
            }
        }
    }
}

/** A string field, null when absent, JSON null or blank (`optString` would turn JSON null into "null"). */
private fun JSONObject.text(key: String): String? = if (isNull(key)) null else optString(key).trim().ifEmpty { null }

private fun epochMillis(ts: String): Long? = try {
    OffsetDateTime.parse(ts).toInstant().toEpochMilli()
} catch (_: Exception) {
    null
}
