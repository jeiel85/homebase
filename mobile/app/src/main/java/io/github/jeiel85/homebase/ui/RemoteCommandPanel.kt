package io.github.jeiel85.homebase.ui

import android.app.Activity
import android.app.KeyguardManager
import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.ActivityResultLauncher
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.MutableState
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.Stable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.jeiel85.homebase.command.CommandLayout
import io.github.jeiel85.homebase.command.CommandPrefs
import io.github.jeiel85.homebase.command.CommandText
import io.github.jeiel85.homebase.command.RemoteCommands
import io.github.jeiel85.homebase.command.WakeSender
import io.github.jeiel85.homebase.data.PcEntry
import io.github.jeiel85.homebase.net.CommandErrors
import io.github.jeiel85.homebase.net.CommandInfo
import io.github.jeiel85.homebase.net.CommandList
import io.github.jeiel85.homebase.net.CommandOutcome
import io.github.jeiel85.homebase.net.CommandPending
import io.github.jeiel85.homebase.net.HomebaseApi
import io.github.jeiel85.homebase.net.RemoteCommandNames
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import kotlin.coroutines.cancellation.CancellationException

/**
 * The PC screen's remote commands (DECISIONS M-69, docs/REMOTE_COMMANDS.md §4.4): what the PC turned on, the question
 * and the device unlock before a restart, sleep or custom command, a restart counting down, and [깨우기] for a PC that
 * went to sleep. Requests run in [RemoteCommands]' own scope, so leaving the screen doesn't lose an answer.
 */
@Stable
internal class CommandPanel(
    private val app: Context,
    private val api: HomebaseApi,
    private val pcId: String,
    /** A command sent once the device credential screen answers — "name\nlabel" — kept across activity recreation. */
    private val awaiting: MutableState<String?>,
) {
    /** What the PC turned on; null until it answered, and for a PC older than remote commands. */
    var list by mutableStateOf<CommandList?>(null)

    /** The PC's MAC addresses for Wake-on-LAN, as last listed. */
    var macs by mutableStateOf<List<String>>(emptyList())

    /** The command under way; the buttons wait for it. */
    var sending by mutableStateOf<String?>(null)

    /** What came of the last command, under the buttons. */
    var lastLine by mutableStateOf<String?>(null)

    /** The command whose question is up. */
    var confirming by mutableStateOf<CommandInfo?>(null)

    /** Bumped to ask the PC for its list again (a restart scheduled or called off). */
    var refreshes by mutableIntStateOf(0)

    /** The device credential screen's launcher, set by [rememberCommandPanel]. */
    var unlockLauncher: ActivityResultLauncher<Intent>? = null

    fun tap(info: CommandInfo) {
        if (sending != null) return
        if (info.asks) confirming = info else send(info.name, info.label)
    }

    /**
     * The question was answered yes: the device credential (PIN, pattern, fingerprint) next, unless the phone has no
     * screen lock at all. Nothing is sent when that screen can't be shown.
     */
    fun confirmed(info: CommandInfo, pcName: String) {
        confirming = null
        val keyguard = app.getSystemService(KeyguardManager::class.java)
        if (keyguard == null || !keyguard.isDeviceSecure) {
            send(info.name, info.label)
            return
        }
        // BiometricPrompt would be a new dependency; this asks for the screen lock, fingerprint included, just as well.
        @Suppress("DEPRECATION")
        val intent = keyguard.createConfirmDeviceCredentialIntent("${info.label} 확인", CommandText.unlockDescription(pcName, info.label))
        val launcher = unlockLauncher
        if (intent == null || launcher == null) {
            refuse()
            return
        }
        awaiting.value = info.name + "\n" + info.label
        try {
            launcher.launch(intent)
        } catch (_: ActivityNotFoundException) {
            awaiting.value = null
            refuse()
        }
    }

    /** The device credential screen answered. */
    fun unlocked(ok: Boolean) {
        val saved = awaiting.value ?: return
        awaiting.value = null
        val name = saved.substringBefore('\n')
        val label = saved.substringAfter('\n', RemoteCommandNames.label(name))
        if (ok) send(name, label) else refuse()
    }

    fun cancelPending(pending: CommandPending, label: String) {
        if (sending == null) send(pending.name, label, cancel = true, restartAt = pending.atMillis)
    }

    fun wake() = WakeSender.wake(app, pcId)

    private fun refuse() {
        lastLine = CommandText.NOT_UNLOCKED
        RemoteCommands.toast(app, CommandText.NOT_UNLOCKED)
    }

    private fun send(name: String, label: String, cancel: Boolean = false, restartAt: Long? = null) {
        sending = name
        lastLine = if (cancel) "다시 시작을 취소하는 중…" else "'$label' 보내는 중…"
        RemoteCommands.launch(app, api, pcId, name, label, cancel, restartAt) { sent ->
            sending = null
            lastLine = sent.message
            RemoteCommands.toast(app, sent.message)
            val outcome = sent.outcome
            // A restart scheduled or called off changes what the PC has pending.
            val pendingChanged = (outcome is CommandOutcome.Ok && (cancel || name == RemoteCommandNames.RESTART)) ||
                (outcome is CommandOutcome.Refused && outcome.error == CommandErrors.NOTHING_PENDING)
            if (pendingChanged) refreshes++
        }
    }
}

/**
 * [pc]'s command panel for its screen: the list is asked for each time the live connection ([connected]) opens, and
 * the PC's MAC addresses are remembered from it so a sleeping PC can still be woken.
 */
@Composable
internal fun rememberCommandPanel(pc: PcEntry, api: HomebaseApi, connected: Boolean): CommandPanel {
    val context = LocalContext.current
    val awaiting = rememberSaveable(pc.pcId) { mutableStateOf<String?>(null) }
    val panel = remember(pc.pcId) { CommandPanel(context.applicationContext, api, pc.pcId, awaiting) }
    val unlock = rememberLauncherForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
        panel.unlocked(result.resultCode == Activity.RESULT_OK)
    }
    SideEffect { panel.unlockLauncher = unlock }
    LaunchedEffect(panel) {
        val stored = withContext(Dispatchers.IO) { CommandPrefs(context).wakeMacs(pc.pcId) }
        if (panel.list == null) panel.macs = stored
    }
    val refreshes = panel.refreshes
    LaunchedEffect(panel, connected, refreshes) {
        if (!connected) return@LaunchedEffect
        try {
            val list = RemoteCommands.list(context, api, pc)
            panel.list = list
            if (list != null) panel.macs = list.wakeMacs
        } catch (e: CancellationException) {
            throw e
        } catch (_: Exception) {
            // Keep what is shown; the next connect asks again.
        }
    }
    return panel
}

/**
 * Under the AI advice: the commands [CommandLayout] lays out, a restart counting down with [취소], what came of the
 * last one, and [깨우기] while the PC is [unreachable] and its MAC addresses are known. Nothing for a PC older than
 * remote commands.
 */
@Composable
internal fun RemoteCommandCard(panel: CommandPanel, unreachable: Boolean) {
    val hb = Hb.colors
    val list = panel.list
    // A restart counting down is shown until its time has passed.
    var now by remember { mutableLongStateOf(System.currentTimeMillis()) }
    val dueAt = list?.pending?.atMillis
    LaunchedEffect(dueAt) {
        if (dueAt != null) {
            val wait = dueAt - System.currentTimeMillis()
            if (wait > 0) delay(wait)
            now = System.currentTimeMillis()
        }
    }
    val canWake = unreachable && panel.macs.isNotEmpty()
    if (list == null && !canWake) return
    val pending = list?.pending?.takeIf { p -> p.atMillis.let { it != null && it > now } }
    val small = MaterialTheme.typography.bodySmall
    Plate(Modifier.padding(horizontal = 12.dp, vertical = 6.dp)) {
        Column(Modifier.padding(14.dp)) {
            Text("원격 명령", style = HbType.label, color = hb.inkSoft, modifier = Modifier.padding(bottom = 8.dp))
            if (list != null) {
                if (list.commands.isEmpty()) Text(CommandText.NONE_TURNED_ON, style = small, color = hb.inkSoft)
                CommandLayout.rows(list.commands).forEachIndexed { i, row ->
                    Row(
                        Modifier.fillMaxWidth().padding(top = if (i > 0) 8.dp else 0.dp),
                        horizontalArrangement = Arrangement.spacedBy(8.dp),
                    ) {
                        row.forEach { info ->
                            CommandButton(
                                info,
                                enabled = panel.sending == null && !unreachable,
                                onClick = { panel.tap(info) },
                                modifier = Modifier.weight(1f),
                            )
                        }
                    }
                }
            }
            if (list != null && pending != null) {
                val label = list.commands.firstOrNull { it.name == pending.name }?.label ?: RemoteCommandNames.label(pending.name)
                val at = pending.atMillis?.let { TIME.format(Instant.ofEpochMilli(it).atZone(ZoneId.systemDefault())) }
                Row(Modifier.fillMaxWidth().padding(top = 12.dp), verticalAlignment = Alignment.CenterVertically) {
                    Led(hb.accent, size = 7.dp, pulse = true)
                    Text(
                        if (at == null) "$label 예정" else "$label 예정 · $at",
                        style = MaterialTheme.typography.bodyMedium,
                        color = hb.ink,
                        modifier = Modifier.weight(1f).padding(start = 6.dp),
                    )
                    TextButton(onClick = { panel.cancelPending(pending, label) }, enabled = panel.sending == null) { Text("취소") }
                }
            }
            panel.lastLine?.let { Text(it, style = small, color = hb.inkSoft, modifier = Modifier.padding(top = 10.dp)) }
            if (canWake) WakeBlock(panel, Modifier.padding(top = 12.dp))
        }
    }
}

/** [깨우기] and what it needs, for a PC that doesn't answer and listed its MAC addresses before. */
@Composable
internal fun WakeBlock(panel: CommandPanel, modifier: Modifier = Modifier) {
    Column(modifier.fillMaxWidth()) {
        QuietButton("깨우기", onClick = panel::wake)
        Text(
            "절전 중인 PC를 Wake-on-LAN으로 깨웁니다. 휴대폰이 PC와 같은 Wi-Fi에 있어야 하고, PC에서 Wake-on-LAN이 켜져 있어야 합니다.",
            style = MaterialTheme.typography.bodySmall,
            color = Hb.colors.inkSoft,
            modifier = Modifier.padding(top = 6.dp),
        )
    }
}

/** The question before a restart, sleep or custom command; yes goes on to the device unlock. */
@Composable
internal fun CommandConfirmDialog(panel: CommandPanel, pcName: String) {
    val info = panel.confirming ?: return
    val context = LocalContext.current
    val secure = remember { context.getSystemService(KeyguardManager::class.java)?.isDeviceSecure == true }
    val question = CommandText.confirmText(pcName, info.name, info.label)
    AlertDialog(
        onDismissRequest = { panel.confirming = null },
        title = { Text(info.label) },
        text = { Text(if (secure) "$question\n\n${CommandText.UNLOCK_NOTE}" else question) },
        confirmButton = { TextButton(onClick = { panel.confirmed(info, pcName) }) { Text(info.label) } },
        dismissButton = { TextButton(onClick = { panel.confirming = null }) { Text("취소") } },
    )
}

/** A command's button: a symbol or icon over the PC's label, narrow enough for three in a row on a 360 dp phone. */
@Composable
private fun CommandButton(info: CommandInfo, enabled: Boolean, onClick: () -> Unit, modifier: Modifier = Modifier) {
    val hb = Hb.colors
    OutlinedButton(
        onClick = onClick,
        enabled = enabled,
        shape = RoundedCornerShape(6.dp),
        colors = ButtonDefaults.outlinedButtonColors(contentColor = hb.ink),
        border = BorderStroke(1.5.dp, if (enabled) hb.inkSoft else hb.hairline),
        contentPadding = PaddingValues(horizontal = 6.dp, vertical = 8.dp),
        modifier = modifier.heightIn(min = 56.dp),
    ) {
        Column(horizontalAlignment = Alignment.CenterHorizontally) {
            val icon = iconOf(info.name)
            val symbol = CommandLayout.symbol(info.name)
            if (icon != null) {
                Icon(icon, contentDescription = null, modifier = Modifier.size(18.dp))
            } else if (symbol != null) {
                // The label says it; a screen reader needn't read the symbol too.
                Text(symbol, style = HbType.readout.copy(fontSize = 16.sp), maxLines = 1, modifier = Modifier.clearAndSetSemantics { })
            }
            Text(
                info.label,
                style = HbType.label.copy(fontSize = 12.sp, letterSpacing = 0.2.sp),
                textAlign = TextAlign.Center,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
            )
        }
    }
}

/** Icons of material-icons-core only; the media and volume buttons carry a symbol instead ([CommandLayout.symbol]). */
private fun iconOf(name: String): ImageVector? = when {
    name == RemoteCommandNames.LOCK -> Icons.Filled.Lock
    name == RemoteCommandNames.RESTART -> Icons.Filled.Refresh
    RemoteCommandNames.isCustom(name) -> Icons.Filled.PlayArrow
    else -> null
}

private val TIME: DateTimeFormatter = DateTimeFormatter.ofPattern("HH:mm:ss")
