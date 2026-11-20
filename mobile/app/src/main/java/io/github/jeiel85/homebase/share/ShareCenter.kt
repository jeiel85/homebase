package io.github.jeiel85.homebase.share

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.Uri
import android.os.Build
import android.util.Size
import android.widget.Toast
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.animateDpAsState
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.IntrinsicSize
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.selection.toggleable
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Close
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.minimumInteractiveComponentSize
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size as GeoSize
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.jeiel85.homebase.clip.ClipShare
import io.github.jeiel85.homebase.data.PcEntry
import io.github.jeiel85.homebase.data.PcMark
import io.github.jeiel85.homebase.data.PcMarks
import io.github.jeiel85.homebase.data.PcStore
import io.github.jeiel85.homebase.net.HomebaseApi
import io.github.jeiel85.homebase.net.NetworkProbe
import io.github.jeiel85.homebase.net.RelayAnswer
import io.github.jeiel85.homebase.net.RelayPhone
import io.github.jeiel85.homebase.net.RelayTarget
import io.github.jeiel85.homebase.net.ShareRelay
import io.github.jeiel85.homebase.net.TransferProtocol
import io.github.jeiel85.homebase.net.UnauthorizedException
import io.github.jeiel85.homebase.ui.Brand
import io.github.jeiel85.homebase.ui.ConsoleBar
import io.github.jeiel85.homebase.ui.Hb
import io.github.jeiel85.homebase.ui.HbType
import io.github.jeiel85.homebase.ui.Led
import io.github.jeiel85.homebase.ui.PcBadge
import io.github.jeiel85.homebase.ui.SectionLabel
import io.github.jeiel85.homebase.ui.SignalButton
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/** What the share center found out about a PC while it was open. */
private enum class Reach { CHECKING, ONLINE, OFFLINE, REVOKED }

/** A PC's answer to `GET /api/v1/share/phones`: reachable or not, and the phones it relays to (null: it doesn't). */
private class Probe(val reach: Reach, val phones: List<RelayPhone>?)

/**
 * The share center (docs/SHARE_CONVENIENCE.md §9.2): what another app shared into Homebase — files ([shared], more
 * can be added with the system pickers, [TransferProtocol.MAX_FILES] at most) or [text] — and where it goes: any of
 * the paired PCs, and other phones a PC passes things on to (§4.8), checked at will. [presetPcId] is the PC of a share
 * sheet shortcut (§4.6): only it starts checked. Otherwise the last share's targets do.
 *
 * Nothing is sent until [공유 시작]: any app can open this with any file or text — and with any shortcut id — and only
 * the user decides that it leaves the phone. Then files go as one upload job per PC (each PC has its own key and
 * transfer; to phones, one per relaying PC), asked about once on mobile data over [FileShare.METERED_ASK_BYTES], and
 * text is sent on in the background; the screen closes at once.
 */
@Composable
internal fun ShareCenterFlow(shared: List<Uri>, text: String?, presetPcId: String?, onDone: () -> Unit) {
    val context = LocalContext.current
    val app = context.applicationContext
    val hb = Hb.colors
    val api = remember { HomebaseApi(networks = NetworkProbe(app)::current) }
    val forFiles = text == null

    // What goes: the shared files, those added with a picker, less those taken out (kept across rotation as strings).
    val sharedOk = remember(shared) { shared.filter { FileShare.acceptable(context, it) } }
    var added by rememberSaveable { mutableStateOf(listOf<String>()) }
    var removed by rememberSaveable { mutableStateOf(listOf<String>()) }
    val uris = remember(sharedOk, added, removed) {
        (sharedOk + added.map(Uri::parse)).distinct().filterNot { it.toString() in removed }
    }
    val described = remember { mutableStateMapOf<Uri, OutgoingFile>() }
    var files by remember { mutableStateOf<List<OutgoingFile>?>(null) }

    // Where it goes.
    var pcs by remember { mutableStateOf<List<PcEntry>?>(null) }
    val reach = remember { mutableStateMapOf<String, Reach>() }
    var answers by remember { mutableStateOf<List<RelayAnswer>?>(null) }
    var checked by rememberSaveable { mutableStateOf(setOf<String>()) }
    var initialized by rememberSaveable { mutableStateOf(false) }
    var askMetered by remember { mutableStateOf<Long?>(null) }
    var starting by remember { mutableStateOf(false) }

    val phones = remember(answers, forFiles) { answers?.let { ShareRelay.targets(it, forFiles) } ?: emptyList() }
    val pcColors = remember(pcs) { pcs.orEmpty().mapNotNull { it.color } }
    val chosenPcs = pcs.orEmpty().filter { ShareChoice.pcKey(it.pcId) in checked }
    val chosenPhones = phones.filter { it.key in checked }
    val problem = if (forFiles) files?.let { TransferProtocol.problem(it.map { f -> f.toTransferFile() }) } else null
    val ready = !starting && (chosenPcs.isNotEmpty() || chosenPhones.isNotEmpty()) &&
        (!forFiles || (files?.size == uris.size && problem == null))

    fun toggle(key: String, on: Boolean) {
        checked = if (on) checked + key else checked - key
    }

    fun addPicked(picked: List<Uri>) {
        if (picked.isEmpty()) return
        val have = uris.map { it.toString() }.toSet()
        val fresh = picked.filter { FileShare.acceptable(context, it) }.map { it.toString() }.distinct().filter { it !in have }
        val room = (TransferProtocol.MAX_FILES - uris.size).coerceAtLeast(0)
        if (fresh.size > room) {
            toast(context, "한 번에 ${TransferProtocol.MAX_FILES}개까지 보낼 수 있습니다 — ${fresh.size - room}개는 넣지 않았습니다")
        }
        val taken = fresh.take(room)
        added = added + taken
        removed = removed - taken.toSet()
    }

    val pickMedia = rememberLauncherForActivityResult(ActivityResultContracts.PickMultipleVisualMedia(TransferProtocol.MAX_FILES)) { addPicked(it) }
    val pickFiles = rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { addPicked(it) }

    /** Remembered for next time: these targets, and the PCs' rank among the share sheet's shortcuts. */
    fun saveChoice(allPcs: List<PcEntry>, pcTargets: List<PcEntry>, phoneTargets: List<RelayTarget>) {
        SharePrefs(app).setLastTargets(pcTargets.map { ShareChoice.pcKey(it.pcId) }.toSet() + phoneTargets.map { it.key })
        pcTargets.firstOrNull()?.let { ClipShare.remember(app, it.pcId) }
        ShareShortcuts.used(app, allPcs, pcTargets.map { it.pcId })
    }

    fun startFiles(list: List<OutgoingFile>) {
        val allPcs = pcs ?: return
        val byRelay = chosenPhones.groupBy { it.pcId }
        val names = mutableListOf<String>()
        var failed = 0
        for (pc in chosenPcs) {
            if (FileShare.schedule(context, pc, list) != null) names += pc.displayName else failed++
        }
        for ((pcId, targets) in byRelay) {
            val relay = allPcs.firstOrNull { it.pcId == pcId } ?: continue
            val started = FileShare.schedule(context, relay, list, targets.map { it.phone.deviceId }, targets.map { it.phone.name })
            if (started != null) names += ShareRelay.names(targets.map { it.phone.name }) else failed++
        }
        saveChoice(allPcs, chosenPcs, chosenPhones)
        val where = names.singleOrNull() ?: "${names.size}곳"
        toast(
            context,
            when {
                names.isEmpty() -> "보내기를 시작하지 못했습니다. 다시 시도하세요."
                failed > 0 -> "${where}에 보내는 중 — ${failed}곳은 시작하지 못했습니다. 다시 보내세요."
                else -> "${where}에 보내는 중 — 진행 상황은 알림에서 볼 수 있습니다"
            },
        )
        onDone()
    }

    fun startText(content: String) {
        val allPcs = pcs ?: return
        val pcTargets = chosenPcs
        val byRelay = chosenPhones.groupBy { it.pcId }
            .mapNotNull { (pcId, targets) -> allPcs.firstOrNull { it.pcId == pcId }?.let { pc -> pc to targets.map { t -> t.phone } } }
        saveChoice(allPcs, pcTargets, chosenPhones)
        // A second or two over the network: done after the screen is gone, and said in a toast.
        UploadJobs.scope.launch {
            val sendApi = HomebaseApi(networks = NetworkProbe(app)::current)
            val lines = coroutineScope {
                val direct = pcTargets.map { pc -> async { ClipShare.send(app, sendApi, pc, content, sensitive = false) } }
                val relayed = byRelay.map { (pc, targets) -> async { ClipShare.sendRelayed(app, sendApi, pc, content, targets) } }
                (direct + relayed).awaitAll()
            }
            withContext(Dispatchers.Main) {
                Toast.makeText(app, lines.filter { it.isNotBlank() }.joinToString("\n"), Toast.LENGTH_LONG).show()
            }
        }
        onDone()
    }

    fun start() {
        if (!ready) return
        if (text != null) {
            starting = true
            startText(text)
            return
        }
        val list = files ?: return
        // Every PC (and every relaying PC) gets its own upload: that much goes over the network.
        val uploads = chosenPcs.size + chosenPhones.map { it.pcId }.distinct().size
        val bytes = list.sumOf { it.size } * uploads
        if (FileShare.isMetered(context) && bytes > FileShare.METERED_ASK_BYTES) {
            askMetered = bytes
        } else {
            starting = true
            startFiles(list)
        }
    }

    // The paired PCs, what starts checked, and then — every PC at once — who answers and which phones each relays to.
    LaunchedEffect(Unit) {
        if (text != null) {
            ClipShare.problem(text)?.let {
                toast(context, it)
                onDone()
                return@LaunchedEffect
            }
        } else if (sharedOk.isEmpty()) {
            toast(context, "보낼 수 있는 파일이 없습니다")
            onDone()
            return@LaunchedEffect
        }
        // The PC list is decrypted with a Keystore key: off the main thread.
        val list = withContext(Dispatchers.IO) { PcStore(app).all() }
        if (list.isEmpty()) {
            toast(context, "연결된 PC가 없습니다. 먼저 앱에서 PC를 추가하세요.")
            onDone()
            return@LaunchedEffect
        }
        pcs = list
        if (!initialized) {
            val prefs = SharePrefs(app)
            checked = ShareChoice.preselect(list.map { it.pcId }, prefs.lastTargets(), ClipShare.lastPcId(app), presetPcId)
            initialized = true
        }
        list.forEach { reach[it.pcId] = Reach.CHECKING }
        val found = coroutineScope {
            list.map { pc ->
                async {
                    val p = probe(api, pc)
                    reach[pc.pcId] = p.reach
                    pc to p
                }
            }.awaitAll()
        }
        // In the order of the PC list: a phone two PCs know goes through the first of them.
        answers = found.mapNotNull { (pc, p) -> p.phones?.let { RelayAnswer(pc.pcId, pc.displayName, it) } }
    }

    // Name, size and type of each file; one that can't be read is taken out (and said so) rather than stopping it all.
    LaunchedEffect(uris) {
        if (!forFiles) return@LaunchedEffect
        // Until every file now in the list is read, there is nothing to send: [공유 시작] waits rather than sending the
        // previous list without the ones just added (Codex review on #103).
        if (uris.any { it !in described }) files = null
        for ((index, uri) in uris.withIndex()) {
            if (uri in described) continue
            try {
                described[uri] = withContext(Dispatchers.IO) { FileShare.describeOne(app, uri, index) }
            } catch (e: UnreadableFileException) {
                toast(context, "'${e.name}'${FileShare.objectParticle(e.name)} 읽지 못해 뺐습니다")
                removed = removed + uri.toString()
            }
        }
        files = uris.mapNotNull { described[it] }
    }

    val chosenCount = chosenPcs.size + chosenPhones.size
    Column(Modifier.fillMaxSize().background(hb.background)) {
        ConsoleBar(title = "공유 센터", onBack = onDone, eyebrow = "Homebase")
        LazyColumn(
            modifier = Modifier.weight(1f).fillMaxWidth(),
            contentPadding = PaddingValues(bottom = 16.dp),
        ) {
            // What goes: a dark instrument panel continuing the masthead, with the files or the text large on it.
            item(key = "payload") {
                val described0 = files
                PayloadPanel(
                    summary = when {
                        text != null -> "텍스트 ${String.format(java.util.Locale.ROOT, "%,d", text.length)}자"
                        described0 != null && described0.isNotEmpty() -> FileShare.summary(described0.map { it.toTransferFile() })
                        described0 == null -> "확인하는 중…"
                        else -> "보낼 파일 없음"
                    },
                ) {
                    if (text != null) {
                        TextPreview(text)
                    } else {
                        FilesPreview(
                            files = files,
                            count = uris.size,
                            canAdd = !starting && uris.size < TransferProtocol.MAX_FILES,
                            onRemove = { uri -> removed = removed + uri.toString() },
                            onAddMedia = { pickMedia.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageAndVideo)) },
                            onAddFiles = { pickFiles.launch(arrayOf("*/*")) },
                        )
                    }
                }
            }
            item(key = "to") {
                SectionLabel("보낼 곳", Modifier.padding(horizontal = 14.dp), trailing = if (chosenCount == 0) null else "${chosenCount}곳 고름")
            }
            val list = pcs
            if (list == null) {
                item(key = "loading") { TileGrid(listOf(Unit, Unit)) { _, m -> PlaceholderTile(m) } }
            } else {
                // Two tiles a row: a PC is picked by its badge and name at a glance, not read off a checklist.
                items(list.chunked(2), key = { row -> "pcs:" + row.joinToString(",") { it.pcId } }) { row ->
                    TileGrid(row) { pc, m ->
                        val key = ShareChoice.pcKey(pc.pcId)
                        val state = reach[pc.pcId] ?: Reach.CHECKING
                        TargetTile(
                            checked = key in checked,
                            onToggle = { toggle(key, it) },
                            mark = pc.mark,
                            kind = DeviceKind.PC,
                            name = pc.displayName,
                            hint = when (state) {
                                Reach.CHECKING -> "확인 중…"
                                Reach.ONLINE -> "연결됨"
                                Reach.OFFLINE -> if (forFiles) "지금 닿지 않음 · 연결되면 이어서 보냅니다" else "지금 닿지 않음"
                                Reach.REVOKED -> "연결이 해제됨 · 다시 페어링하세요"
                            },
                            led = when (state) {
                                Reach.CHECKING -> null
                                Reach.ONLINE -> hb.ledOn
                                Reach.OFFLINE, Reach.REVOKED -> hb.ledOff
                            },
                            enabled = !starting,
                            modifier = m,
                        )
                    }
                }
                if (answers == null) {
                    item(key = "phones-wait") {
                        Hint("PC를 거쳐 보낼 수 있는 다른 휴대폰을 찾는 중…", Modifier.padding(horizontal = 14.dp))
                    }
                } else if (phones.isNotEmpty()) {
                    item(key = "phones") { SectionLabel("다른 휴대폰 · PC 경유", Modifier.padding(horizontal = 14.dp)) }
                    items(phones.chunked(2), key = { row -> "phones:" + row.joinToString(",") { it.key } }) { row ->
                        TileGrid(row) { t, m ->
                            TargetTile(
                                checked = t.key in checked,
                                onToggle = { toggle(t.key, it) },
                                mark = PcMarks.unpaired(t.phone.name, pcColors),
                                kind = DeviceKind.PHONE,
                                name = t.phone.name,
                                hint = "${t.pcName} 경유",
                                led = null,
                                enabled = !starting,
                                modifier = m,
                            )
                        }
                    }
                }
            }
        }
        ShareDock(
            marks = chosenPcs.map { it.mark } + chosenPhones.map { PcMarks.unpaired(it.phone.name, pcColors) },
            names = chosenPcs.map { it.displayName } + chosenPhones.map { it.phone.name },
            problem = problem,
            label = when {
                starting -> "보내는 중…"
                chosenCount == 0 -> "공유 시작"
                else -> "공유 시작 · ${chosenCount}곳"
            },
            enabled = ready,
            onStart = { start() },
        )
    }

    askMetered?.let { bytes ->
        val uploads = chosenPcs.size + chosenPhones.map { it.pcId }.distinct().size
        AlertDialog(
            onDismissRequest = { askMetered = null },
            title = { Text("모바일 데이터") },
            text = {
                Text(
                    "모바일 데이터로 ${FileShare.bytes(bytes)}를 보낼까요?" +
                        if (uploads > 1) " 보낼 곳마다 따로 올립니다(${uploads}곳)." else "",
                )
            },
            confirmButton = {
                TextButton(onClick = {
                    askMetered = null
                    val list = files
                    if (list != null && !starting) {
                        starting = true
                        startFiles(list)
                    }
                }) { Text("보내기") }
            },
            dismissButton = { TextButton(onClick = { askMetered = null }) { Text("취소") } },
        )
    }
}

/** Reachability, and the phones [pc] passes things on to; an unreachable PC is said so, never an error. */
private suspend fun probe(api: HomebaseApi, pc: PcEntry): Probe = try {
    Probe(Reach.ONLINE, api.sharePhones(pc))
} catch (e: CancellationException) {
    throw e
} catch (_: UnauthorizedException) {
    Probe(Reach.REVOKED, null)
} catch (_: Exception) {
    Probe(Reach.OFFLINE, null)
}

@Composable
private fun Hint(text: String, modifier: Modifier = Modifier) {
    Text(
        text,
        style = MaterialTheme.typography.bodySmall,
        color = Hb.colors.inkSoft,
        modifier = modifier.padding(vertical = 8.dp),
    )
}

/** The carbon panel under the masthead: "보낼 것", the summary as a readout, and the preview ([content]). */
@Composable
private fun PayloadPanel(summary: String, content: @Composable ColumnScope.() -> Unit) {
    val hb = Hb.colors
    // No side padding of its own: the parts keep [PANEL_GUTTER] themselves, and a thumbnail strip runs to the edges.
    Column(Modifier.fillMaxWidth().background(hb.slab).padding(top = 14.dp, bottom = 18.dp)) {
        Text("보낼 것", style = HbType.label, color = hb.accent, modifier = Modifier.padding(horizontal = PANEL_GUTTER).semantics { heading() })
        Spacer(Modifier.height(2.dp))
        Text(
            summary,
            style = HbType.readout.copy(fontSize = 20.sp, letterSpacing = (-0.2).sp),
            color = hb.onSlab,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.padding(horizontal = PANEL_GUTTER),
        )
        Spacer(Modifier.height(14.dp))
        content()
    }
}

/** The shared text, as much as fits in a few lines, set off by an amber quote rule. */
@Composable
private fun TextPreview(text: String) {
    val hb = Hb.colors
    Row(
        Modifier
            .padding(horizontal = PANEL_GUTTER)
            .fillMaxWidth()
            .height(IntrinsicSize.Min)
            .clip(RoundedCornerShape(8.dp))
            .background(hb.slabRaised),
    ) {
        Box(Modifier.width(3.dp).fillMaxHeight().background(hb.accent))
        Text(
            text.take(PREVIEW_CHARS),
            style = MaterialTheme.typography.bodyMedium,
            color = hb.onSlab,
            maxLines = 8,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.padding(horizontal = 14.dp, vertical = 12.dp),
        )
    }
}

/**
 * The files: one picture or video large, several as a strip of thumbnails (or names), each with a way to take it
 * out, and the pickers to add more under them.
 */
@Composable
private fun FilesPreview(
    files: List<OutgoingFile>?,
    count: Int,
    canAdd: Boolean,
    onRemove: (Uri) -> Unit,
    onAddMedia: () -> Unit,
    onAddFiles: () -> Unit,
) {
    val hb = Hb.colors
    val gutter = Modifier.padding(horizontal = PANEL_GUTTER)
    when {
        files == null -> Box(gutter.size(THUMB.dp).clip(RoundedCornerShape(8.dp)).background(hb.slabRaised))
        files.isEmpty() -> Text("보낼 파일이 없습니다. 아래에서 고르세요.", style = MaterialTheme.typography.bodySmall, color = hb.onSlabSoft, modifier = gutter)
        files.size == 1 -> LargeThumb(files.single(), onRemove = { onRemove(files.single().uri) }, modifier = gutter)
        else -> LazyRow(contentPadding = PaddingValues(horizontal = PANEL_GUTTER), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            items(files, key = { it.uri.toString() }) { f -> Thumb(f, onRemove = { onRemove(f.uri) }) }
        }
    }
    Spacer(Modifier.height(12.dp))
    Row(gutter, verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        SlabChip("사진·동영상", onClick = onAddMedia, enabled = canAdd)
        SlabChip("파일", onClick = onAddFiles, enabled = canAdd)
        Spacer(Modifier.weight(1f))
        Text("$count/${TransferProtocol.MAX_FILES}", style = HbType.readout.copy(fontSize = 12.sp), color = hb.onSlabSoft)
    }
}

/** A pill on the slab that adds files: "+ 사진·동영상", "+ 파일". */
@Composable
private fun SlabChip(text: String, onClick: () -> Unit, enabled: Boolean) {
    val hb = Hb.colors
    val shape = RoundedCornerShape(50)
    Row(
        Modifier
            .minimumInteractiveComponentSize()
            .clip(shape)
            .border(1.dp, if (enabled) hb.onSlabSoft.copy(alpha = 0.55f) else hb.meterOff, shape)
            .clickable(enabled = enabled, onClickLabel = "$text 더 고르기", role = Role.Button, onClick = onClick)
            .heightIn(min = 36.dp)
            .padding(start = 10.dp, end = 14.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(Icons.Filled.Add, contentDescription = null, tint = if (enabled) hb.accent else hb.meterOff, modifier = Modifier.size(16.dp))
        Spacer(Modifier.width(4.dp))
        Text(text, style = HbType.label.copy(fontSize = 12.sp), color = if (enabled) hb.onSlab else hb.onSlabSoft.copy(alpha = 0.5f))
    }
}

/** A file's thumbnail (pictures, videos; [Thumbnails]), loaded off the main thread; null until then or without one. */
@Composable
private fun rememberThumbnail(file: OutgoingFile): ImageBitmap? {
    val context = LocalContext.current
    var bitmap by remember(file.uri) { mutableStateOf<ImageBitmap?>(null) }
    LaunchedEffect(file.uri) {
        if (isVisual(file)) bitmap = withContext(Dispatchers.IO) { Thumbnails.load(context, file.uri, file.mime) }?.asImageBitmap()
    }
    return bitmap
}

private fun isVisual(file: OutgoingFile) = file.mime?.let { it.startsWith("image/") || it.startsWith("video/") } == true

/**
 * The one shared file, the width of the panel: a picture or video as a wide crop, anything else as its type. No
 * name under it — the summary above is the file's name and size.
 */
@Composable
private fun LargeThumb(file: OutgoingFile, onRemove: () -> Unit, modifier: Modifier = Modifier) {
    val hb = Hb.colors
    val image = rememberThumbnail(file)
    Box(
        modifier
            .fillMaxWidth()
            .height(if (isVisual(file)) 200.dp else 120.dp)
            .clip(RoundedCornerShape(10.dp))
            .background(hb.slabRaised),
    ) {
        if (image != null) {
            Image(image, contentDescription = null, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize())
        } else {
            FileGlyph(typeLabel(file), Modifier.align(Alignment.Center))
        }
        RemoveButton(file.name, onRemove, Modifier.align(Alignment.TopEnd))
    }
}

@Composable
private fun Thumb(file: OutgoingFile, onRemove: () -> Unit) {
    val hb = Hb.colors
    val image = rememberThumbnail(file)
    Column(Modifier.width(THUMB.dp)) {
        Box(Modifier.size(THUMB.dp).clip(RoundedCornerShape(8.dp)).background(hb.slabRaised)) {
            if (image != null) {
                Image(image, contentDescription = null, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize())
            } else {
                FileGlyph(typeLabel(file), Modifier.align(Alignment.Center), small = true)
            }
            RemoveButton(file.name, onRemove, Modifier.align(Alignment.TopEnd))
        }
        Text(
            file.name,
            style = MaterialTheme.typography.bodySmall,
            color = hb.onSlabSoft,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.padding(top = 4.dp),
        )
    }
}

/** A sheet with a folded corner and the file's type on it, for a file without a thumbnail. */
@Composable
private fun FileGlyph(label: String, modifier: Modifier = Modifier, small: Boolean = false) {
    val hb = Hb.colors
    val w = if (small) 40.dp else 56.dp
    Box(modifier.size(width = w, height = w * 1.25f), contentAlignment = Alignment.Center) {
        Canvas(Modifier.matchParentSize()) {
            val fold = size.width * 0.3f
            val sheet = Path().apply {
                moveTo(0f, 0f)
                lineTo(size.width - fold, 0f)
                lineTo(size.width, fold)
                lineTo(size.width, size.height)
                lineTo(0f, size.height)
                close()
            }
            drawPath(sheet, hb.onSlabSoft.copy(alpha = 0.16f))
            drawPath(sheet, hb.onSlabSoft.copy(alpha = 0.6f), style = Stroke(width = 1.5.dp.toPx()))
            val corner = Path().apply {
                moveTo(size.width - fold, 0f)
                lineTo(size.width - fold, fold)
                lineTo(size.width, fold)
            }
            drawPath(corner, hb.onSlabSoft.copy(alpha = 0.6f), style = Stroke(width = 1.5.dp.toPx()))
        }
        Text(
            label,
            style = HbType.label.copy(fontSize = if (small) 10.sp else 12.sp, letterSpacing = 0.4.sp),
            color = hb.accent,
            maxLines = 1,
            modifier = Modifier.padding(top = w * 0.2f),
        )
    }
}

/** The × on a thumbnail: a 22 dp dot in a 36 dp target. */
@Composable
private fun RemoveButton(name: String, onRemove: () -> Unit, modifier: Modifier = Modifier) {
    Box(
        modifier
            .size(36.dp)
            .clickable(onClickLabel = "빼기", role = Role.Button, onClick = onRemove),
        contentAlignment = Alignment.Center,
    ) {
        Box(Modifier.size(22.dp).clip(CircleShape).background(Color(0xB3000000)), contentAlignment = Alignment.Center) {
            Icon(Icons.Filled.Close, contentDescription = "$name 빼기", tint = Color.White, modifier = Modifier.size(14.dp))
        }
    }
}

/** What stands in for a file without a thumbnail: its extension ("PDF"), else its kind. */
private fun typeLabel(file: OutgoingFile): String {
    val ext = file.name.substringAfterLast('.', "").take(4).uppercase(java.util.Locale.ROOT)
    return when {
        ext.isNotEmpty() -> ext
        file.mime?.startsWith("video/") == true -> "동영상"
        file.mime?.startsWith("image/") == true -> "사진"
        else -> "파일"
    }
}

/** What a target is, for the small glyph on its badge. */
private enum class DeviceKind { PC, PHONE }

/** [row] (one or two items) side by side as equal tiles of the same height, the page's gutters around them. */
@Composable
private fun <T> TileGrid(row: List<T>, tile: @Composable (T, Modifier) -> Unit) {
    Row(
        Modifier.fillMaxWidth().padding(start = 14.dp, end = 14.dp, bottom = 10.dp).height(IntrinsicSize.Min),
        horizontalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        row.forEach { tile(it, Modifier.weight(1f).fillMaxHeight()) }
        repeat(2 - row.size) { Spacer(Modifier.weight(1f)) }
    }
}

/**
 * One PC or phone to send to: badge, name and a hint (reachable, or which PC it goes through), checked by a tap.
 * A faceplate like [io.github.jeiel85.homebase.ui.Plate] — depth is the bevel along the bottom — that grows an
 * amber frame and a check when picked.
 */
@Composable
private fun TargetTile(
    checked: Boolean,
    onToggle: (Boolean) -> Unit,
    mark: PcMark,
    kind: DeviceKind,
    name: String,
    hint: String,
    led: Color?,
    enabled: Boolean,
    modifier: Modifier = Modifier,
) {
    val hb = Hb.colors
    val frame by animateColorAsState(if (checked) hb.accent else hb.plateEdge, label = "frame")
    val edge by animateDpAsState(if (checked) 2.dp else 0.dp, label = "edge")
    val shape = RoundedCornerShape(10.dp)
    Box(
        modifier
            .clip(shape)
            .background(frame)
            .toggleable(value = checked, enabled = enabled, role = Role.Checkbox, onValueChange = onToggle)
            .padding(start = edge, end = edge, top = edge, bottom = edge + 2.dp),
    ) {
        Column(
            Modifier
                .fillMaxSize()
                .clip(RoundedCornerShape(if (checked) 8.dp else 10.dp))
                .background(hb.plate)
                .background(if (checked) hb.accent.copy(alpha = if (hb.dark) 0.10f else 0.12f) else Color.Transparent)
                .padding(12.dp),
        ) {
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.Top) {
                DeviceBadge(mark, kind)
                Spacer(Modifier.weight(1f))
                CheckDot(checked)
            }
            Spacer(Modifier.height(10.dp))
            Text(
                name,
                style = MaterialTheme.typography.titleSmall.copy(fontWeight = FontWeight.Bold),
                color = hb.ink,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
            )
            Spacer(Modifier.height(4.dp))
            Row(verticalAlignment = Alignment.Top) {
                if (led != null) {
                    Led(led, size = 6.dp, modifier = Modifier.padding(top = 2.dp))
                    Spacer(Modifier.width(2.dp))
                }
                Text(hint, style = MaterialTheme.typography.bodySmall, color = hb.inkSoft, maxLines = 3, overflow = TextOverflow.Ellipsis)
            }
        }
    }
}

/** The PC's or phone's badge with a small PC or phone glyph on its corner. */
@Composable
private fun DeviceBadge(mark: PcMark, kind: DeviceKind) {
    val hb = Hb.colors
    Box(Modifier.size(46.dp)) {
        PcBadge(mark, size = 40.dp)
        Box(
            Modifier
                .align(Alignment.BottomEnd)
                .size(20.dp)
                .clip(CircleShape)
                .background(hb.plate)
                .padding(2.dp)
                .clip(CircleShape)
                .background(hb.slab),
            contentAlignment = Alignment.Center,
        ) {
            Canvas(Modifier.size(10.dp)) {
                val s = 1.2.dp.toPx()
                val c = hb.onSlab
                if (kind == DeviceKind.PC) {
                    // A monitor on a stand.
                    drawRoundRect(c, Offset(0f, 0f), GeoSize(size.width, size.height * 0.68f), CornerRadius(1.dp.toPx()), style = Stroke(s))
                    drawLine(c, Offset(size.width / 2, size.height * 0.68f), Offset(size.width / 2, size.height * 0.92f), s)
                    drawLine(c, Offset(size.width * 0.25f, size.height * 0.95f), Offset(size.width * 0.75f, size.height * 0.95f), s)
                } else {
                    // A phone.
                    val w = size.width * 0.62f
                    drawRoundRect(c, Offset((size.width - w) / 2, 0f), GeoSize(w, size.height), CornerRadius(1.5.dp.toPx()), style = Stroke(s))
                    drawCircle(c, radius = s * 0.6f, center = Offset(size.width / 2, size.height * 0.82f))
                }
            }
        }
    }
}

/** The tile's check: an empty ring, or a filled amber dot with a check. Decorative — the tile is the checkbox. */
@Composable
private fun CheckDot(checked: Boolean) {
    val hb = Hb.colors
    val fill by animateColorAsState(if (checked) hb.accent else Color.Transparent, label = "check")
    Box(
        Modifier
            .size(24.dp)
            .clip(CircleShape)
            .background(fill)
            .border(1.5.dp, if (checked) hb.accent else hb.inkSoft.copy(alpha = 0.6f), CircleShape),
        contentAlignment = Alignment.Center,
    ) {
        if (checked) Icon(Icons.Filled.Check, contentDescription = null, tint = Brand.Carbon, modifier = Modifier.size(16.dp))
    }
}

/** A tile's shape while the PC list is read. */
@Composable
private fun PlaceholderTile(modifier: Modifier) {
    val hb = Hb.colors
    Column(
        modifier
            .clip(RoundedCornerShape(10.dp))
            .background(hb.plate)
            .padding(12.dp),
    ) {
        Box(Modifier.size(40.dp).clip(CircleShape).background(hb.hairline))
        Spacer(Modifier.height(12.dp))
        Box(Modifier.fillMaxWidth(0.7f).height(12.dp).clip(RoundedCornerShape(3.dp)).background(hb.hairline))
        Spacer(Modifier.height(8.dp))
        Box(Modifier.fillMaxWidth(0.45f).height(10.dp).clip(RoundedCornerShape(3.dp)).background(hb.hairline))
    }
}

/**
 * The bottom of the screen: who is picked (their badges, overlapping, and names), what is wrong with the files if
 * anything, and [공유 시작].
 */
@Composable
private fun ShareDock(
    marks: List<PcMark>,
    names: List<String>,
    problem: String?,
    label: String,
    enabled: Boolean,
    onStart: () -> Unit,
) {
    val hb = Hb.colors
    Column(Modifier.fillMaxWidth().background(hb.plate).navigationBarsPadding()) {
        Box(Modifier.fillMaxWidth().height(1.dp).background(hb.hairline))
        Column(Modifier.fillMaxWidth().padding(start = 14.dp, end = 14.dp, top = 10.dp, bottom = 12.dp)) {
            problem?.let {
                Text(it, style = MaterialTheme.typography.bodySmall, color = hb.danger, modifier = Modifier.padding(bottom = 8.dp))
            }
            Row(Modifier.fillMaxWidth().heightIn(min = 28.dp).padding(bottom = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                if (marks.isEmpty()) {
                    Text("보낼 곳을 골라 주세요", style = MaterialTheme.typography.bodySmall, color = hb.inkSoft)
                } else {
                    Row(horizontalArrangement = Arrangement.spacedBy((-8).dp)) {
                        marks.take(DOCK_BADGES).forEach { mark ->
                            Box(Modifier.clip(CircleShape).background(hb.plate).padding(2.dp)) { PcBadge(mark, size = 24.dp) }
                        }
                    }
                    Spacer(Modifier.width(8.dp))
                    Text(
                        names.joinToString(", "),
                        style = MaterialTheme.typography.bodySmall.copy(fontWeight = FontWeight.Medium),
                        color = hb.ink,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.weight(1f),
                    )
                }
            }
            SignalButton(label, onClick = onStart, enabled = enabled)
        }
    }
}

/**
 * Small pictures of shared photos and videos for the share center. Android 10+ asks the provider for a thumbnail
 * (videos too); before that a picture is decoded at a reduced size and a video gets none. Null whenever it can't.
 */
internal object Thumbnails {
    private const val PX = 256

    /** Blocking: call it off the main thread. */
    fun load(context: Context, uri: Uri, mime: String?): Bitmap? = try {
        if (Build.VERSION.SDK_INT >= 29) {
            context.contentResolver.loadThumbnail(uri, Size(PX, PX), null)
        } else if (mime?.startsWith("image/") == true) {
            decodeSmall(context, uri)
        } else {
            null
        }
    } catch (_: Exception) {
        null
    } catch (_: OutOfMemoryError) {
        null
    }

    private fun decodeSmall(context: Context, uri: Uri): Bitmap? {
        val resolver = context.contentResolver
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        resolver.openInputStream(uri)?.use { BitmapFactory.decodeStream(it, null, bounds) }
        if (bounds.outWidth <= 0 || bounds.outHeight <= 0) return null
        var sample = 1
        while (bounds.outWidth / (sample * 2) >= PX && bounds.outHeight / (sample * 2) >= PX) sample *= 2
        val options = BitmapFactory.Options().apply { inSampleSize = sample }
        return resolver.openInputStream(uri)?.use { BitmapFactory.decodeStream(it, null, options) }
    }
}

private const val THUMB = 96
private const val DOCK_BADGES = 5
private val PANEL_GUTTER = 16.dp
private const val PREVIEW_CHARS = 1_000

private fun toast(context: Context, text: String) = Toast.makeText(context.applicationContext, text, Toast.LENGTH_SHORT).show()
