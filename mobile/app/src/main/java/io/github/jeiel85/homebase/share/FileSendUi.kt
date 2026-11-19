package io.github.jeiel85.homebase.share

import android.content.Context
import android.net.Uri
import android.widget.Toast
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import io.github.jeiel85.homebase.clip.ClipShare
import io.github.jeiel85.homebase.data.PcEntry
import io.github.jeiel85.homebase.data.PcStore
import io.github.jeiel85.homebase.net.TransferProtocol
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/**
 * Sending [uris] to a PC (DECISIONS M-64): reads what they are, checks the limits, picks the PC — [presetPcId]
 * (a PC card's "파일 보내기"), else the one the tile would use when [pickAlways] is off, else the user's tap —
 * asks before a large send over mobile data, then hands it to the upload job and calls [onDone]. The share sheet
 * always asks for the PC: another app can open it with any file, and only a tap should send that to a PC.
 */
@Composable
internal fun FileSendFlow(uris: List<Uri>, presetPcId: String?, pickAlways: Boolean, onDone: () -> Unit) {
    val context = LocalContext.current
    var files by remember { mutableStateOf<List<OutgoingFile>?>(null) }
    var choices by remember { mutableStateOf<List<PcEntry>?>(null) }
    var askMetered by remember { mutableStateOf<PcEntry?>(null) }

    fun start(pc: PcEntry) {
        val list = files ?: return
        val transferId = FileShare.schedule(context, pc, list)
        if (transferId == null) {
            toast(context, "보내기를 시작하지 못했습니다. 다시 시도하세요.")
        } else {
            ClipShare.remember(context, pc.pcId)
            toast(context, "${pc.displayName}에 보내는 중 — 진행 상황은 알림에서 볼 수 있습니다")
        }
        onDone()
    }

    fun choose(pc: PcEntry) {
        val list = files ?: return
        if (FileShare.isMetered(context) && list.sumOf { it.size } > FileShare.METERED_ASK_BYTES) askMetered = pc else start(pc)
    }

    LaunchedEffect(Unit) {
        val usable = uris.filter { FileShare.acceptable(context, it) }
        if (usable.isEmpty()) {
            toast(context, "보낼 수 있는 파일이 없습니다")
            onDone()
            return@LaunchedEffect
        }
        val described = try {
            FileShare.describe(context, usable)
        } catch (e: UnreadableFileException) {
            toast(context, "'${e.name}'${FileShare.objectParticle(e.name)} 읽지 못했습니다")
            onDone()
            return@LaunchedEffect
        }
        TransferProtocol.problem(described.map { it.toTransferFile() })?.let {
            toast(context, it)
            onDone()
            return@LaunchedEffect
        }
        // The PC list is decrypted with a Keystore key: off the main thread.
        val pcs = withContext(Dispatchers.IO) { PcStore(context).all() }
        if (pcs.isEmpty()) {
            toast(context, "연결된 PC가 없습니다. 먼저 앱에서 PC를 추가하세요.")
            onDone()
            return@LaunchedEffect
        }
        files = described
        val last = ClipShare.lastPcId(context)
        val direct = when {
            presetPcId != null -> pcs.firstOrNull { it.pcId == presetPcId }
            pickAlways -> null
            else -> ClipShare.autoTarget(pcs, last)
        }
        if (direct != null) choose(direct) else choices = ClipShare.ordered(pcs, last)
    }

    val list = choices
    val described = files
    val metered = askMetered
    when {
        metered != null && described != null -> AlertDialog(
            onDismissRequest = onDone,
            title = { Text("모바일 데이터") },
            text = { Text("모바일 데이터로 ${FileShare.bytes(described.sumOf { it.size })}를 보낼까요?") },
            confirmButton = { TextButton(onClick = { start(metered) }) { Text("보내기") } },
            dismissButton = { TextButton(onClick = onDone) { Text("취소") } },
        )
        list != null && described != null -> AlertDialog(
            onDismissRequest = onDone,
            title = { Text("PC로 보내기") },
            text = {
                Column {
                    Text(
                        FileShare.summary(described.map { it.toTransferFile() }),
                        style = MaterialTheme.typography.bodySmall,
                        maxLines = 2,
                        overflow = TextOverflow.Ellipsis,
                    )
                    Spacer(Modifier.height(12.dp))
                    Column(Modifier.heightIn(max = 320.dp).verticalScroll(rememberScrollState())) {
                        list.forEach { pc ->
                            TextButton(onClick = { choose(pc) }, modifier = Modifier.fillMaxWidth()) { Text(pc.displayName) }
                        }
                    }
                }
            },
            confirmButton = {},
            dismissButton = { TextButton(onClick = onDone) { Text("취소") } },
        )
        described == null -> AlertDialog(onDismissRequest = {}, text = { Text("보낼 파일을 확인하는 중…") }, confirmButton = {})
    }
}

private fun toast(context: Context, text: String) = Toast.makeText(context.applicationContext, text, Toast.LENGTH_SHORT).show()
