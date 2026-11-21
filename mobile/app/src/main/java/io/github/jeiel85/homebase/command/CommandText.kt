package io.github.jeiel85.homebase.command

import io.github.jeiel85.homebase.net.CommandErrors
import io.github.jeiel85.homebase.net.CommandInfo
import io.github.jeiel85.homebase.net.CommandOutcome
import io.github.jeiel85.homebase.net.CommandStates
import io.github.jeiel85.homebase.net.RemoteCommandNames
import io.github.jeiel85.homebase.net.RemoteCommandProtocol

/**
 * What the app says about remote commands (DECISIONS M-69): the result of each one, its line in the notification
 * center, and the question asked before a restart, sleep or custom command. Pure, so unit tests pin it.
 */
object CommandText {
    /** Where the PC turns commands on, in the words of the PC's own screens. */
    const val WHERE_TO_TURN_ON = "PC 트레이 메뉴의 \"설정…\" → \"휴대폰\" 탭 › 원격 명령에서 켤 수 있습니다."

    /** The PC answered the list but turned nothing on. */
    const val NONE_TURNED_ON = "이 PC에서 켠 원격 명령이 없습니다. $WHERE_TO_TURN_ON"

    /** The label of a cancel sent for a restart counting down, in the notification center. */
    const val CANCEL_LABEL = "다시 시작 취소"

    /** The unlock step was not passed. */
    const val NOT_UNLOCKED = "잠금 해제를 확인하지 않아 보내지 않았습니다"

    const val WAKE_SENT = "깨우기 신호를 보냈습니다 — 같은 Wi-Fi에 있어야 하고, PC에서 Wake-on-LAN이 켜져 있어야 합니다"
    const val WAKE_NO_LAN = "Wi-Fi에 연결돼 있지 않아 깨우기 신호를 보내지 못했습니다. PC와 같은 Wi-Fi에서 다시 하세요"
    const val WAKE_FAILED = "깨우기 신호를 보내지 못했습니다"

    /** What came of [label] ([name]) sent to [pc] — or, with [cancel], of calling off its restart — for a toast. */
    fun resultLine(pc: String, name: String, label: String, outcome: CommandOutcome, cancel: Boolean = false): String = when (outcome) {
        is CommandOutcome.Ok -> when {
            outcome.state == CommandStates.SCHEDULED ->
                "${pc}이(가) ${RemoteCommandProtocol.RESTART_DELAY_SECONDS}초 뒤 다시 시작합니다. 알림이나 이 화면에서 취소할 수 있습니다."
            outcome.state == CommandStates.CANCELLED || cancel -> "${pc}의 다시 시작을 취소했습니다"
            else -> doneLine(pc, name, label)
        }
        is CommandOutcome.Refused -> refusedLine(pc, label, outcome.error)
        CommandOutcome.Unsupported -> "${pc}의 Homebase가 원격 명령보다 오래된 버전입니다. PC를 업데이트하세요."
        CommandOutcome.Unpaired -> "${pc}에서 이 휴대폰의 연결이 해제되었습니다. 다시 페어링하세요."
        CommandOutcome.Unreachable -> "${pc}에 닿지 않습니다"
        CommandOutcome.Unconfirmed -> "${pc}이(가) 받았는지 확인하지 못했습니다. PC에서 확인하세요."
        is CommandOutcome.Failed -> "${pc}에 보내지 못했습니다: ${outcome.reason}"
    }

    private fun doneLine(pc: String, name: String, label: String): String = when {
        name == RemoteCommandNames.LOCK -> "${pc}을(를) 잠갔습니다"
        name == RemoteCommandNames.SCREEN_OFF -> "${pc}의 화면을 껐습니다"
        name == RemoteCommandNames.SLEEP ->
            "${pc}을(를) 절전 모드로 보냈습니다. 이제 휴대폰에서 닿지 않습니다 — PC에서 Wake-on-LAN을 켜 두었다면 같은 Wi-Fi에서 [깨우기]로 깨울 수 있습니다."
        name == RemoteCommandNames.RESTART -> "${pc}을(를) 다시 시작합니다"
        RemoteCommandNames.isCustom(name) -> "${pc}에서 '${label}'을(를) 실행했습니다"
        else -> "${pc}: $label"
    }

    private fun refusedLine(pc: String, label: String, error: String): String = when (error) {
        CommandErrors.NOT_ALLOWED -> "${pc}에서 '${label}' 명령이 꺼져 있습니다. $WHERE_TO_TURN_ON"
        CommandErrors.NO_USER_SESSION -> "${pc}에 로그인한 사용자가 없어 '${label}'을(를) 실행하지 못했습니다"
        CommandErrors.BUSY -> "${pc}에 명령을 너무 자주 보냈습니다. 1분쯤 뒤 다시 하세요"
        CommandErrors.REPLAYED -> "${pc}이(가) 요청을 거절했습니다 — 이미 처리했거나, 휴대폰과 PC의 시계가 2분 넘게 다릅니다"
        CommandErrors.INVALID, CommandErrors.KEY_NEEDED -> "${pc}와(과) 암호 키를 맞추지 못했습니다. 다시 하세요"
        CommandErrors.NOTHING_PENDING -> "${pc}에 취소할 다시 시작이 없습니다 — 이미 취소됐거나 다시 시작했습니다"
        CommandErrors.FAILED -> "${pc}에서 '${label}'을(를) 실행하지 못했습니다"
        else -> "${pc}이(가) '${label}'을(를) 거절했습니다 ($error)"
    }

    /** How it went, in a few words, for the notification center's "보낸 명령". */
    fun historyLine(outcome: CommandOutcome): String = when (outcome) {
        is CommandOutcome.Ok -> when (outcome.state) {
            CommandStates.SCHEDULED -> "${RemoteCommandProtocol.RESTART_DELAY_SECONDS}초 뒤 다시 시작 예약됨"
            CommandStates.CANCELLED -> "취소됨"
            else -> "완료"
        }
        is CommandOutcome.Refused -> when (outcome.error) {
            CommandErrors.NOT_ALLOWED -> "거절 — PC에서 꺼져 있음"
            CommandErrors.NO_USER_SESSION -> "거절 — PC에 로그인한 사용자 없음"
            CommandErrors.BUSY -> "거절 — 너무 자주 보냄"
            CommandErrors.REPLAYED -> "거절 — 이미 처리했거나 시계가 다름"
            CommandErrors.INVALID, CommandErrors.KEY_NEEDED -> "거절 — 암호 키 불일치"
            CommandErrors.NOTHING_PENDING -> "취소할 것 없음"
            CommandErrors.FAILED -> "PC에서 실행 실패"
            else -> "거절 — ${outcome.error}"
        }
        CommandOutcome.Unsupported -> "PC 버전이 낮음"
        CommandOutcome.Unpaired -> "연결 해제됨"
        CommandOutcome.Unreachable -> "PC에 닿지 않음"
        CommandOutcome.Unconfirmed -> "받았는지 확인 못 함"
        is CommandOutcome.Failed -> "보내지 못함 — ${outcome.reason}"
    }

    /** The question before a command that [CommandInfo.asks]. */
    fun confirmText(pc: String, name: String, label: String): String = when {
        name == RemoteCommandNames.RESTART ->
            "${pc}을(를) 다시 시작할까요? 저장하지 않은 작업은 사라질 수 있습니다. PC에는 ${RemoteCommandProtocol.RESTART_DELAY_SECONDS}초 카운트다운과 취소가 뜹니다."
        name == RemoteCommandNames.SLEEP ->
            "${pc}을(를) 절전 모드로 보낼까요? 절전하는 동안에는 휴대폰에서 닿지 않습니다. PC에서 Wake-on-LAN을 켜 두었다면 같은 Wi-Fi에서 [깨우기]로 깨울 수 있습니다."
        RemoteCommandNames.isCustom(name) ->
            "${pc}에서 '${label}'을(를) 실행할까요? PC에 등록해 둔 프로그램이 그 PC 사용자의 권한으로 실행됩니다."
        else -> "${pc}에서 '${label}'을(를) 실행할까요?"
    }

    /** Under the question when the phone is locked with a PIN, pattern or fingerprint. */
    const val UNLOCK_NOTE = "확인하면 휴대폰 잠금 해제(지문·PIN 등)를 한 번 더 묻습니다."

    /** The device credential screen's description. */
    fun unlockDescription(pc: String, label: String): String = "${pc}에 '${label}'을(를) 보내려면 잠금을 해제하세요"
}

/**
 * How the PC screen's "원격 명령" section lays the listed commands out (DECISIONS M-69): lock and screen off, then
 * media [이전] [재생/일시정지] [다음], volume [음소거] [소리 작게] [소리 크게], restart and sleep, then custom
 * commands — and any a newer PC adds — in the PC's order, two to a row. Only what the PC listed; a row with nothing
 * listed is left out. Pure, so unit tests pin it.
 */
object CommandLayout {
    private val BASIC = listOf(RemoteCommandNames.LOCK, RemoteCommandNames.SCREEN_OFF)
    private val MEDIA = listOf(RemoteCommandNames.PREVIOUS, RemoteCommandNames.PLAY_PAUSE, RemoteCommandNames.NEXT)
    private val VOLUME = listOf(RemoteCommandNames.MUTE, RemoteCommandNames.VOLUME_DOWN, RemoteCommandNames.VOLUME_UP)
    private val SYSTEM = listOf(RemoteCommandNames.RESTART, RemoteCommandNames.SLEEP)
    private val PLACED = (BASIC + MEDIA + VOLUME + SYSTEM).toSet()

    /** Commands of the last rows (custom ones, and names this version doesn't know) in a row. */
    const val PER_ROW = 2

    fun rows(commands: List<CommandInfo>): List<List<CommandInfo>> {
        val byName = commands.associateBy { it.name }
        val fixed = listOf(BASIC, MEDIA, VOLUME, SYSTEM).map { row -> row.mapNotNull { byName[it] } }
        val rest = commands.filter { it.name !in PLACED }.chunked(PER_ROW)
        return (fixed + rest).filter { it.isNotEmpty() }
    }
}
