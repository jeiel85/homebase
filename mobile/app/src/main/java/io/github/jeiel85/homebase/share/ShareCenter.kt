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
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
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
import androidx.compose.material.icons.filled.Close
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Checkbox
import androidx.compose.material3.CheckboxDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
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
import io.github.jeiel85.homebase.ui.Plate
import io.github.jeiel85.homebase.ui.QuietButton
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

    Column(Modifier.fillMaxSize().background(hb.background)) {
        ConsoleBar(title = "공유 센터", onBack = onDone, eyebrow = "Homebase")
        LazyColumn(
            modifier = Modifier.weight(1f).fillMaxWidth(),
            contentPadding = PaddingValues(horizontal = 14.dp),
        ) {
            item {
                val described0 = files
                SectionLabel(
                    "보낼 것",
                    trailing = when {
                        text != null -> "텍스트 ${String.format(java.util.Locale.ROOT, "%,d", text.length)}자"
                        described0 != null && described0.isNotEmpty() -> FileShare.summary(described0.map { it.toTransferFile() })
                        else -> null
                    },
                )
                if (text != null) {
                    TextPreview(text)
                } else {
                    FilesPreview(
                        files = files,
                        canAdd = !starting && uris.size < TransferProtocol.MAX_FILES,
                        onRemove = { uri -> removed = removed + uri.toString() },
                        onAddMedia = { pickMedia.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageAndVideo)) },
                        onAddFiles = { pickFiles.launch(arrayOf("*/*")) },
                    )
                }
                SectionLabel("보낼 곳", trailing = if (checked.isEmpty()) null else "${chosenPcs.size + chosenPhones.size}곳 고름")
            }
            val list = pcs
            if (list == null) {
                item { Hint("연결된 PC를 읽는 중…") }
            } else {
                items(list, key = { "pc:" + it.pcId }) { pc ->
                    val key = ShareChoice.pcKey(pc.pcId)
                    val state = reach[pc.pcId] ?: Reach.CHECKING
                    TargetRow(
                        checked = key in checked,
                        onToggle = { toggle(key, it) },
                        mark = pc.mark,
                        name = pc.displayName,
                        hint = when (state) {
                            Reach.CHECKING -> "확인 중…"
                            Reach.ONLINE -> "연결됨"
                            Reach.OFFLINE -> if (forFiles) "지금 닿지 않음 — 연결되면 이어서 보냅니다" else "지금 닿지 않음"
                            Reach.REVOKED -> "이 휴대폰의 연결이 해제됨 — 다시 페어링하세요"
                        },
                        led = when (state) {
                            Reach.CHECKING -> null
                            Reach.ONLINE -> hb.ledOn
                            Reach.OFFLINE, Reach.REVOKED -> hb.ledOff
                        },
                        enabled = !starting,
                    )
                }
                if (answers == null) {
                    item { Hint("PC를 거쳐 보낼 수 있는 다른 휴대폰을 찾는 중…") }
                } else if (phones.isNotEmpty()) {
                    item { SectionLabel("다른 휴대폰 (PC 경유)") }
                    items(phones, key = { it.key }) { t ->
                        TargetRow(
                            checked = t.key in checked,
                            onToggle = { toggle(t.key, it) },
                            mark = PcMarks.unpaired(t.phone.name, pcColors),
                            name = t.phone.name,
                            hint = "${t.pcName} 경유",
                            led = null,
                            enabled = !starting,
                        )
                    }
                }
            }
            item { Spacer(Modifier.height(12.dp)) }
        }
        Column(
            Modifier
                .fillMaxWidth()
                .background(hb.plate)
                .navigationBarsPadding()
                .padding(horizontal = 14.dp, vertical = 12.dp),
        ) {
            problem?.let {
                Text(it, style = MaterialTheme.typography.bodySmall, color = hb.danger, modifier = Modifier.padding(bottom = 8.dp))
            }
            SignalButton("공유 시작", onClick = { start() }, enabled = ready)
            Spacer(Modifier.height(8.dp))
            QuietButton("취소", onClick = onDone, enabled = !starting)
        }
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
private fun Hint(text: String) {
    Text(
        text,
        style = MaterialTheme.typography.bodySmall,
        color = Hb.colors.inkSoft,
        modifier = Modifier.padding(vertical = 8.dp),
    )
}

/** The shared text, as much as fits in a few lines. */
@Composable
private fun TextPreview(text: String) {
    val hb = Hb.colors
    Plate {
        Text(
            text.take(PREVIEW_CHARS),
            style = MaterialTheme.typography.bodyMedium,
            color = hb.ink,
            maxLines = 6,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.padding(horizontal = 14.dp, vertical = 12.dp),
        )
    }
}

/** Thumbnails (pictures, videos) or names of the files, each with a way to take it out, and the pickers to add more. */
@Composable
private fun FilesPreview(
    files: List<OutgoingFile>?,
    canAdd: Boolean,
    onRemove: (Uri) -> Unit,
    onAddMedia: () -> Unit,
    onAddFiles: () -> Unit,
) {
    val hb = Hb.colors
    Plate {
        Column(Modifier.padding(horizontal = 12.dp, vertical = 10.dp)) {
            when {
                files == null -> Text("보낼 파일을 확인하는 중…", style = MaterialTheme.typography.bodySmall, color = hb.inkSoft)
                files.isEmpty() -> Text("보낼 파일이 없습니다. 아래에서 고르세요.", style = MaterialTheme.typography.bodySmall, color = hb.inkSoft)
                else -> LazyRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    items(files, key = { it.uri.toString() }) { f -> Thumb(f, onRemove = { onRemove(f.uri) }) }
                }
            }
            Row(Modifier.padding(top = 4.dp)) {
                TextButton(onClick = onAddMedia, enabled = canAdd) { Text("+ 사진·동영상") }
                TextButton(onClick = onAddFiles, enabled = canAdd) { Text("+ 파일") }
            }
        }
    }
}

@Composable
private fun Thumb(file: OutgoingFile, onRemove: () -> Unit) {
    val hb = Hb.colors
    val context = LocalContext.current
    val visual = file.mime?.let { it.startsWith("image/") || it.startsWith("video/") } == true
    val bitmap by produceState<ImageBitmap?>(null, file.uri) {
        if (visual) {
            value = withContext(Dispatchers.IO) { Thumbnails.load(context, file.uri, file.mime) }?.asImageBitmap()
        }
    }
    Column(Modifier.width(THUMB.dp)) {
        Box(Modifier.size(THUMB.dp).clip(RoundedCornerShape(6.dp)).background(hb.slabRaised)) {
            val image = bitmap
            if (image != null) {
                Image(image, contentDescription = null, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize())
            } else {
                Text(
                    typeLabel(file),
                    style = HbType.label,
                    color = hb.onSlab,
                    maxLines = 1,
                    modifier = Modifier.align(Alignment.Center).padding(horizontal = 4.dp),
                )
            }
            Box(
                Modifier
                    .align(Alignment.TopEnd)
                    .padding(3.dp)
                    .size(24.dp)
                    .clip(CircleShape)
                    .background(Color(0x99000000))
                    .clickable(onClickLabel = "빼기", role = Role.Button, onClick = onRemove),
                contentAlignment = Alignment.Center,
            ) {
                Icon(Icons.Filled.Close, contentDescription = "${file.name} 빼기", tint = Color.White, modifier = Modifier.size(14.dp))
            }
        }
        Text(
            file.name,
            style = MaterialTheme.typography.bodySmall,
            color = hb.inkSoft,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.padding(top = 2.dp),
        )
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

/** One PC or phone: a checkbox row with its badge, name and a hint (reachable, or which PC it goes through). */
@Composable
private fun TargetRow(
    checked: Boolean,
    onToggle: (Boolean) -> Unit,
    mark: PcMark,
    name: String,
    hint: String,
    led: Color?,
    enabled: Boolean,
) {
    val hb = Hb.colors
    Plate(Modifier.padding(bottom = 8.dp)) {
        Row(
            Modifier
                .fillMaxWidth()
                .toggleable(value = checked, enabled = enabled, role = Role.Checkbox, onValueChange = onToggle)
                .padding(horizontal = 8.dp, vertical = 8.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Checkbox(
                checked = checked,
                onCheckedChange = null,
                enabled = enabled,
                colors = CheckboxDefaults.colors(checkedColor = hb.accent, checkmarkColor = Brand.Carbon, uncheckedColor = hb.inkSoft),
            )
            Spacer(Modifier.width(8.dp))
            PcBadge(mark, size = 28.dp)
            Spacer(Modifier.width(10.dp))
            Column(Modifier.weight(1f)) {
                Text(name, style = MaterialTheme.typography.titleSmall, color = hb.ink, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Row(verticalAlignment = Alignment.CenterVertically) {
                    if (led != null) {
                        Led(led, size = 6.dp)
                        Spacer(Modifier.width(5.dp))
                    }
                    Text(hint, style = MaterialTheme.typography.bodySmall, color = hb.inkSoft, maxLines = 2, overflow = TextOverflow.Ellipsis)
                }
            }
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

private const val THUMB = 76
private const val PREVIEW_CHARS = 1_000

private fun toast(context: Context, text: String) = Toast.makeText(context.applicationContext, text, Toast.LENGTH_SHORT).show()
