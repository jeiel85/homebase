package io.github.jeiel85.homebase.ui

import android.content.Context
import android.os.SystemClock
import android.widget.Toast
import androidx.compose.foundation.background
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
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.KeyboardArrowRight
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExtendedFloatingActionButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.draw.clip
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LifecycleResumeEffect
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.repeatOnLifecycle
import io.github.jeiel85.homebase.data.PcEntry
import io.github.jeiel85.homebase.data.PcStore
import io.github.jeiel85.homebase.mesh.MeshSync
import io.github.jeiel85.homebase.mesh.PresenceScheduler
import androidx.compose.runtime.collectAsState
import io.github.jeiel85.homebase.net.Endpoint
import io.github.jeiel85.homebase.net.HomebaseApi
import io.github.jeiel85.homebase.push.PushHistory
import io.github.jeiel85.homebase.push.PushNotifier
import io.github.jeiel85.homebase.push.PushRegistrar
import io.github.jeiel85.homebase.push.PushState
import io.github.jeiel85.homebase.update.UpdateSurface
import kotlinx.coroutines.Job
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull

private sealed interface PcStatus {
    data object Checking : PcStatus
    data class Online(val version: String, val endpoint: Endpoint) : PcStatus
    data object Offline : PcStatus
}

@OptIn(ExperimentalMaterial3Api::class) // PullToRefreshBox is experimental in Material3 1.3.
@Composable
fun PcListScreen(
    store: PcStore,
    api: HomebaseApi,
    onOpen: (PcEntry) -> Unit,
    onAdd: () -> Unit,
    onSettings: () -> Unit,
    onNotifications: () -> Unit,
) {
    var pcs by remember { mutableStateOf(store.all()) }
    val status = remember { mutableStateMapOf<String, PcStatus>() }
    var confirmRemove by remember { mutableStateOf<PcEntry?>(null) }
    var renaming by remember { mutableStateOf<PcEntry?>(null) }
    var refreshing by remember { mutableStateOf(false) }
    var refreshWork by remember { mutableStateOf<Job?>(null) }
    var pollRounds by remember { mutableIntStateOf(0) }
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    // Re-read on every return to this screen: the user may have fixed it in Settings or Android's settings.
    var needsAttention by remember { mutableStateOf(false) }
    LifecycleResumeEffect(Unit) {
        needsAttention = pushNeedsAttention(context)
        onPauseOrDispose { }
    }

    // One /info round over every listed PC. [forcePresence]: report this phone's state even if unchanged.
    suspend fun pollAll(listedPcs: List<PcEntry>, forcePresence: Boolean = false) {
        var renamed = false
        coroutineScope {
            listedPcs.map { listed ->
                async {
                    // Paths may have been refreshed by the live screen since this list was built.
                    val pc = store.get(listed.pcId) ?: listed
                    if (status[pc.pcId] == null) status[pc.pcId] = PcStatus.Checking
                    status[pc.pcId] = try {
                        val (info, endpoint) = api.info(pc)
                        store.rememberGood(pc.pcId, endpoint)
                        if (syncReportedName(context, store, pc, info.pcName)) renamed = true
                        PushRegistrar.ensureAsync(context, api, pc)
                        // This phone's state to the PC, and the PC's device list back (DECISIONS M-27);
                        // unchanged state goes at most once a minute, not on every 15 s round.
                        launch { MeshSync.reportIfDue(context, api, pc, force = forcePresence) }
                        PcStatus.Online(info.agentVersion, endpoint)
                    } catch (_: Exception) {
                        PcStatus.Offline
                    }
                }
            }.awaitAll()
        }
        // A PC renamed in Windows: its card and the alias dialog show the new name.
        if (renamed) pcs = store.all()
    }

    // Poll /info for each PC while the list is visible (every 15 s; nothing in the background). Keyed on
    // which PCs are listed, not on their names, so a rename the poll itself found doesn't restart it.
    val pcIds = pcs.map { it.pcId }
    LaunchedEffect(pcIds) {
        lifecycle.repeatOnLifecycle(Lifecycle.State.STARTED) {
            while (true) {
                pollAll(pcs)
                pollRounds++
                delay(15_000)
            }
        }
    }

    // Pull down: re-read the list, poll every PC now and check for an app update now (skipping the
    // once-a-day rule); the update result is one toast. The indicator stays until both are done or
    // for REFRESH_INDICATOR_MS, whichever is first: a PC that is off takes up to 3 s per path to give
    // up, and its status still lands when it does. A pull while that work still runs doesn't start it twice.
    fun refresh() {
        if (refreshing) return
        refreshing = true
        val work = refreshWork?.takeIf { it.isActive } ?: scope.launch {
            pcs = store.all()
            val updateMessage = coroutineScope {
                launch { pollAll(pcs, forcePresence = true) }
                UpdateSurface.checkNow(context)
            }
            updateMessage?.let { Toast.makeText(context, it, Toast.LENGTH_SHORT).show() }
        }.also { refreshWork = it }
        scope.launch {
            try {
                withTimeoutOrNull(REFRESH_INDICATOR_MS) { work.join() }
            } finally {
                refreshing = false
            }
        }
    }

    // Unread count for the bell; the file is read off the main thread once per process.
    LaunchedEffect(Unit) { withContext(Dispatchers.IO) { PushHistory.ensureLoaded(context) } }
    val history by PushHistory.state.collectAsState()

    val hubs by MeshSync.hubs.collectAsState()
    // Recomputed after every poll round too, so "online" from a PC that went quiet expires (FRESH_MS).
    val others = remember(hubs, pcs, pollRounds) { MeshSync.otherDevices(hubs, pcs) }
    val hb = Hb.colors
    val online = pcs.count { status[it.pcId] is PcStatus.Online }
    Box(Modifier.fillMaxSize().background(hb.background)) {
        Column(Modifier.fillMaxSize()) {
            ConsoleBar(
                title = "Homebase",
                eyebrow = "PC 모니터",
                actions = {
                    if (pcs.isNotEmpty()) {
                        Row(
                            Modifier
                                .padding(end = 8.dp)
                                .clip(RoundedCornerShape(4.dp))
                                .background(hb.slabRaised)
                                .padding(horizontal = 10.dp, vertical = 6.dp)
                                .semantics(mergeDescendants = true) { contentDescription = "${pcs.size}대 중 ${online}대 온라인" },
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            Led(if (online > 0) hb.ledOn else hb.ledOff, size = 6.dp)
                            Text("$online/${pcs.size}", style = HbType.readout.copy(fontSize = 14.sp), color = hb.onSlab)
                            Text(" 온라인", style = HbType.label.copy(fontSize = 10.sp), color = hb.onSlabSoft)
                        }
                    }
                    NotificationCenterButton(unread = history.unread, onClick = onNotifications)
                    SettingsButton(attention = needsAttention, onClick = onSettings)
                },
            )
            PullToRefreshBox(
                isRefreshing = refreshing,
                onRefresh = ::refresh,
                modifier = Modifier.fillMaxSize(),
            ) {
                LazyColumn(
                    modifier = Modifier.fillMaxSize(),
                    contentPadding = PaddingValues(horizontal = 14.dp),
                ) {
                    item { UpdateSurface.Banner(Modifier.padding(top = 12.dp)) }
                    if (pcs.isEmpty()) {
                        item { EmptyBay(onAdd) }
                    } else {
                        item { SectionLabel("내 PC", trailing = "${pcs.size}대") }
                    }
                    items(pcs, key = { it.pcId }) { pc ->
                        PcBay(
                            pc,
                            status[pc.pcId] ?: PcStatus.Checking,
                            onClick = { onOpen(pc) },
                            onRename = { renaming = pc },
                            onRemove = { confirmRemove = pc },
                        )
                        Spacer(Modifier.height(12.dp))
                    }
                    if (others.isNotEmpty()) {
                        item { SectionLabel("다른 기기", trailing = "${others.count { it.entry.online }}/${others.size} 온라인") }
                        items(others, key = { "dev:" + it.entry.kind + it.entry.deviceId }) { OtherDeviceCard(it) }
                    }
                    item {
                        Spacer(Modifier.navigationBarsPadding().height(104.dp))
                    }
                }
            }
        }
        if (pcs.isNotEmpty()) {
            ExtendedFloatingActionButton(
                onClick = onAdd,
                icon = { Icon(Icons.Filled.Add, contentDescription = null) },
                text = { Text("기기 추가", style = HbType.label.copy(fontSize = 14.sp, letterSpacing = 0.4.sp)) },
                containerColor = hb.action,
                contentColor = hb.onAction,
                shape = RoundedCornerShape(8.dp),
                modifier = Modifier.align(Alignment.BottomEnd).navigationBarsPadding().padding(18.dp),
            )
        }
    }

    renaming?.let { pc ->
        AliasDialog(
            pc,
            onSave = { input ->
                store.setAlias(pc.pcId, input)
                store.get(pc.pcId)?.let { PushNotifier.renamePc(context, it.pcId, it.displayName) }
                pcs = store.all()
                renaming = null
            },
            onDismiss = { renaming = null },
        )
    }

    confirmRemove?.let { pc ->
        AlertDialog(
            onDismissRequest = { confirmRemove = null },
            title = { Text("PC 삭제") },
            text = { Text("${pc.displayName}을(를) 목록에서 지울까요? PC 쪽 연결 해제는 PC의 \"설정…\" → \"휴대폰\" 탭에서 합니다.") },
            confirmButton = {
                TextButton(onClick = {
                    // Best effort: stop this PC pushing to the phone, then drop its notification channels.
                    PushRegistrar.scope.launch { runCatching { api.unregisterPush(pc) } }
                    PushRegistrar.forget(pc.pcId)
                    PushState(context).markRegistered(pc.pcId, false)
                    PushNotifier.forgetPc(context, pc.pcId)
                    PushHistory.forgetPc(context, pc.pcId)
                    MeshSync.forget(pc.pcId)
                    store.remove(pc.pcId)
                    PresenceScheduler.sync(context) // the last PC gone: stop the background report
                    pcs = store.all()
                    confirmRemove = null
                }) { Text("삭제") }
            },
            dismissButton = { TextButton(onClick = { confirmRemove = null }) { Text("취소") } },
        )
    }
}

/**
 * Gear on the console bar; an amber dot when push is on but Android blocks its notifications.
 * Same pattern as [NotificationCenterButton]: IconButton clips its content to a 40dp circle, so the
 * dot is drawn as a sibling over the 48dp touch target (where the icon's top-right corner is), not
 * inside the button where a dot moved past the icon would lose its edge to the arc.
 */
@Composable
private fun SettingsButton(attention: Boolean, onClick: () -> Unit) {
    val hb = Hb.colors
    Box {
        IconButton(
            onClick = onClick,
            modifier = Modifier.semantics { contentDescription = if (attention) "설정 — 알림 권한 확인 필요" else "설정" },
        ) {
            Icon(Icons.Filled.Settings, contentDescription = null, tint = hb.onSlab)
        }
        if (attention) {
            // The 24dp icon sits at 12..36dp of the 48dp target; the dot covers its top-right corner.
            Box(
                Modifier
                    .align(Alignment.TopStart)
                    .offset(x = 28.dp, y = 12.dp)
                    .size(8.dp)
                    .clip(RoundedCornerShape(4.dp))
                    .background(hb.accent),
            )
        }
    }
}

/**
 * Stores the name [pc] reports now ([PcStore.updateName]) and, when no alias covers it, renames the
 * PC's group in Android's notification settings to match. True when the stored name changed.
 */
internal fun syncReportedName(context: Context, store: PcStore, pc: PcEntry, reported: String): Boolean {
    // Almost every round reports the name already known; then the store (which decrypts every entry) isn't read.
    if (reported.trim().let { it.isEmpty() || it == pc.name }) return false
    if (!store.updateName(pc.pcId, reported)) return false
    store.get(pc.pcId)?.takeIf { it.alias == null }?.let { PushNotifier.renamePc(context, it.pcId, it.name) }
    return true
}

/** Longest the pull-to-refresh indicator stays; slower PCs' results still land when they arrive. */
private const val REFRESH_INDICATOR_MS = 4_000L

/** First run: what the app is for and the one thing to do next. */
@Composable
private fun EmptyBay(onAdd: () -> Unit) {
    val hb = Hb.colors
    Column(Modifier.padding(top = 28.dp)) {
        Box(
            Modifier.fillMaxWidth().clip(RoundedCornerShape(8.dp)).background(hb.slab).padding(20.dp),
        ) {
            Column {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    repeat(3) { i -> Led(if (i == 0) hb.accent else hb.meterOff, size = 7.dp) }
                }
                Spacer(Modifier.height(10.dp))
                Text("아직 연결된 PC가 없습니다", style = HbType.display.copy(fontSize = 20.sp), color = hb.onSlab)
                Spacer(Modifier.height(6.dp))
                Text(
                    "PC를 연결하면 켜져 있는지, CPU·메모리를 얼마나 쓰는지, 어떤 창이 열려 있는지 실시간으로 봅니다.",
                    style = MaterialTheme.typography.bodyMedium,
                    color = hb.onSlabSoft,
                )
                Spacer(Modifier.height(14.dp))
                SegmentMeter(0.0, segments = 24, height = 10.dp)
            }
        }
        Spacer(Modifier.height(16.dp))
        Text(
            "PC 트레이 아이콘 메뉴의 \"설정…\" → \"휴대폰\" 탭에서 QR 코드를 띄운 뒤 아래 버튼을 누르세요. 같은 Wi-Fi면 바로 연결되고, 밖에서는 PC의 Tailscale 또는 Cloudflare 경로로 자동 연결됩니다.",
            style = MaterialTheme.typography.bodyMedium,
            color = hb.inkSoft,
        )
        Spacer(Modifier.height(16.dp))
        SignalButton("기기 추가", onAdd)
        val context = LocalContext.current
        TextButton(onClick = { context.openUrl(Links.LATEST_RELEASE) }, modifier = Modifier.padding(top = 4.dp)) {
            Text("PC에 Homebase가 없나요? Windows 설치 파일 받기")
        }
    }
}

/**
 * One PC as a hardware bay: a carbon faceplate (lamp, name, load meters) over a light label
 * plate. The plate's first line is the connection status — the slot where the route is shown.
 */
@Composable
private fun PcBay(pc: PcEntry, status: PcStatus, onClick: () -> Unit, onRename: () -> Unit, onRemove: () -> Unit) {
    val hb = Hb.colors
    var menu by remember { mutableStateOf(false) }
    val reading = LastSeen.get(pc.pcId)
    Plate(onClick = onClick, onClickLabel = "${pc.displayName} 실시간 보기") {
        val showMeters = reading != null && status is PcStatus.Online
        Column(Modifier.fillMaxWidth().background(hb.slab).padding(start = 12.dp, end = 4.dp, top = 4.dp, bottom = if (showMeters) 12.dp else 4.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                val (led, pulse) = when (status) {
                    PcStatus.Checking -> hb.accent to false
                    is PcStatus.Online -> hb.ledOn to true
                    PcStatus.Offline -> hb.ledOff to false
                }
                Led(led, size = 8.dp, pulse = pulse)
                Spacer(Modifier.width(8.dp))
                // The same badge its notifications carry (M-38).
                PcBadge(pc.mark, size = 22.dp)
                Spacer(Modifier.width(8.dp))
                Text(
                    pc.displayName.uppercase(),
                    style = HbType.display.copy(fontSize = 19.sp),
                    color = hb.onSlab,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.weight(1f),
                )
                Box {
                    IconButton(onClick = { menu = true }) {
                        Icon(Icons.Filled.MoreVert, contentDescription = "${pc.displayName} 메뉴", tint = hb.onSlabSoft)
                    }
                    DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                        DropdownMenuItem(
                            text = { Text(if (pc.alias == null) "별칭 지정" else "별칭 바꾸기") },
                            onClick = { menu = false; onRename() },
                        )
                        DropdownMenuItem(text = { Text("목록에서 삭제") }, onClick = { menu = false; onRemove() })
                    }
                }
            }
            if (showMeters && reading != null) {
                Spacer(Modifier.height(2.dp))
                Row(Modifier.padding(end = 12.dp), horizontalArrangement = Arrangement.spacedBy(14.dp)) {
                    MiniMeter("CPU", reading.cpuPct, Modifier.weight(1f))
                    MiniMeter("RAM", reading.memPct, Modifier.weight(1f))
                }
            }
        }
        Row(Modifier.fillMaxWidth().padding(start = 14.dp, end = 8.dp, top = 10.dp, bottom = 10.dp), verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text(
                    when (status) {
                        PcStatus.Checking -> "확인 중…"
                        is PcStatus.Online -> "온라인 · ${status.endpoint.label}"
                        PcStatus.Offline -> "응답 없음 — 연결할 수 있는 경로가 없음"
                    },
                    style = MaterialTheme.typography.bodyMedium.copy(fontWeight = FontWeight.SemiBold),
                    color = if (status is PcStatus.Offline) hb.danger else hb.ink,
                    maxLines = 2,
                )
                val meta = buildList {
                    // With an alias the PC's own name stays visible, so it can still be told apart in the PC's own lists.
                    if (pc.alias != null) add(pc.name)
                    if (status is PcStatus.Online && status.version.isNotBlank()) add("Agent v${status.version}")
                    if (reading != null && status is PcStatus.Online) add("부하 ${Fmt.age(SystemClock.elapsedRealtime() - reading.atElapsedMs)} 확인")
                }
                if (meta.isNotEmpty()) {
                    Text(meta.joinToString(" · "), style = MaterialTheme.typography.bodySmall, color = hb.inkSoft)
                }
            }
            Icon(Icons.Filled.KeyboardArrowRight, contentDescription = null, tint = hb.inkSoft, modifier = Modifier.size(24.dp))
        }
    }
}

/** Names [pc] on this phone; an empty field (or "원래 이름") goes back to the PC's own name. */
@Composable
private fun AliasDialog(pc: PcEntry, onSave: (String?) -> Unit, onDismiss: () -> Unit) {
    var text by remember(pc.pcId) { mutableStateOf(pc.alias ?: "") }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("별칭") },
        text = {
            Column {
                Text(
                    "이 휴대폰에서만 ${pc.name} 대신 보일 이름입니다. 비워 두면 원래 이름으로 보입니다.",
                    style = MaterialTheme.typography.bodyMedium,
                )
                Spacer(Modifier.height(12.dp))
                OutlinedTextField(
                    value = text,
                    onValueChange = { v -> if (v.codePointCount(0, v.length) <= PcStore.ALIAS_MAX) text = v },
                    singleLine = true,
                    placeholder = { Text(pc.name) },
                    supportingText = { Text("${text.codePointCount(0, text.length)}/${PcStore.ALIAS_MAX}") },
                    modifier = Modifier.fillMaxWidth(),
                )
            }
        },
        confirmButton = { TextButton(onClick = { onSave(text) }) { Text("저장") } },
        dismissButton = {
            Row {
                if (pc.alias != null) TextButton(onClick = { onSave(null) }) { Text("원래 이름") }
                TextButton(onClick = onDismiss) { Text("취소") }
            }
        },
    )
}

@Composable
private fun MiniMeter(label: String, pct: Double, modifier: Modifier) {
    val hb = Hb.colors
    Column(modifier) {
        Row(verticalAlignment = Alignment.Bottom) {
            Text(label, style = HbType.label.copy(fontSize = 10.sp), color = hb.onSlabSoft, modifier = Modifier.weight(1f))
            Text(Fmt.pctShort(pct), style = HbType.readout.copy(fontSize = 12.sp), color = hb.onSlab)
        }
        Spacer(Modifier.height(3.dp))
        SegmentMeter(pct, segments = 16, height = 8.dp, description = "$label ${Fmt.pctShort(pct)}")
    }
}
