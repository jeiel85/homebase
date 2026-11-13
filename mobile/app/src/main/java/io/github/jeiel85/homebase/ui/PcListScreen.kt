package io.github.jeiel85.homebase.ui

import android.os.SystemClock
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
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExtendedFloatingActionButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
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
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.repeatOnLifecycle
import io.github.jeiel85.homebase.BuildConfig
import io.github.jeiel85.homebase.data.PcEntry
import io.github.jeiel85.homebase.data.PcStore
import io.github.jeiel85.homebase.net.Endpoint
import io.github.jeiel85.homebase.net.HomebaseApi
import io.github.jeiel85.homebase.push.PushNotifier
import io.github.jeiel85.homebase.push.PushRegistrar
import io.github.jeiel85.homebase.push.PushState
import io.github.jeiel85.homebase.update.UpdateSurface
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

private sealed interface PcStatus {
    data object Checking : PcStatus
    data class Online(val version: String, val endpoint: Endpoint) : PcStatus
    data object Offline : PcStatus
}

@Composable
fun PcListScreen(
    store: PcStore,
    api: HomebaseApi,
    onOpen: (PcEntry) -> Unit,
    onAdd: () -> Unit,
) {
    var pcs by remember { mutableStateOf(store.all()) }
    val status = remember { mutableStateMapOf<String, PcStatus>() }
    var confirmRemove by remember { mutableStateOf<PcEntry?>(null) }
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    val context = LocalContext.current

    // Poll /info for each PC while the list is visible (every 15 s; nothing in the background).
    LaunchedEffect(pcs) {
        lifecycle.repeatOnLifecycle(Lifecycle.State.STARTED) {
            while (true) {
                coroutineScope {
                    pcs.map { listed ->
                        async {
                            // Paths may have been refreshed by the live screen since this list was built.
                            val pc = store.get(listed.pcId) ?: listed
                            if (status[pc.pcId] == null) status[pc.pcId] = PcStatus.Checking
                            status[pc.pcId] = try {
                                val (info, endpoint) = api.info(pc)
                                store.rememberGood(pc.pcId, endpoint)
                                PushRegistrar.ensureAsync(context, api, pc)
                                PcStatus.Online(info.agentVersion, endpoint)
                            } catch (_: Exception) {
                                PcStatus.Offline
                            }
                        }
                    }.awaitAll()
                }
                delay(15_000)
            }
        }
    }

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
                },
            )
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
                    PcBay(pc, status[pc.pcId] ?: PcStatus.Checking, onClick = { onOpen(pc) }, onRemove = { confirmRemove = pc })
                    Spacer(Modifier.height(12.dp))
                }
                item {
                    SectionLabel("앱")
                    Plate {
                        Column(Modifier.padding(horizontal = 16.dp, vertical = 4.dp)) {
                            PushSettings(store, api, pcs.size)
                            UpdateSurface.SettingsRows()
                            Text(
                                "앱 버전 ${BuildConfig.VERSION_NAME}",
                                style = MaterialTheme.typography.bodySmall,
                                color = hb.inkSoft,
                                modifier = Modifier.padding(bottom = 14.dp),
                            )
                        }
                    }
                    Spacer(Modifier.navigationBarsPadding().height(104.dp))
                }
            }
        }
        if (pcs.isNotEmpty()) {
            ExtendedFloatingActionButton(
                onClick = onAdd,
                icon = { Icon(Icons.Filled.Add, contentDescription = null) },
                text = { Text("PC 추가", style = HbType.label.copy(fontSize = 14.sp, letterSpacing = 0.4.sp)) },
                containerColor = hb.action,
                contentColor = hb.onAction,
                shape = RoundedCornerShape(8.dp),
                modifier = Modifier.align(Alignment.BottomEnd).navigationBarsPadding().padding(18.dp),
            )
        }
    }

    confirmRemove?.let { pc ->
        AlertDialog(
            onDismissRequest = { confirmRemove = null },
            title = { Text("PC 삭제") },
            text = { Text("${pc.name}을(를) 목록에서 지울까요? PC 쪽 연결 해제는 PC의 \"휴대폰 연결…\" 창에서 합니다.") },
            confirmButton = {
                TextButton(onClick = {
                    // Best effort: stop this PC pushing to the phone, then drop its notification channels.
                    PushRegistrar.scope.launch { runCatching { api.unregisterPush(pc) } }
                    PushRegistrar.forget(pc.pcId)
                    PushState(context).markRegistered(pc.pcId, false)
                    PushNotifier.forgetPc(context, pc.pcId)
                    store.remove(pc.pcId)
                    pcs = store.all()
                    confirmRemove = null
                }) { Text("삭제") }
            },
            dismissButton = { TextButton(onClick = { confirmRemove = null }) { Text("취소") } },
        )
    }
}

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
            "PC 트레이 아이콘 메뉴의 \"휴대폰 연결…\"에서 QR 코드를 띄운 뒤 아래 버튼을 누르세요. 같은 Wi-Fi면 바로 연결되고, 밖에서는 PC의 Tailscale 또는 Cloudflare 경로로 자동 연결됩니다.",
            style = MaterialTheme.typography.bodyMedium,
            color = hb.inkSoft,
        )
        Spacer(Modifier.height(16.dp))
        SignalButton("PC 추가", onAdd)
    }
}

/**
 * One PC as a hardware bay: a carbon faceplate (lamp, name, load meters) over a light label
 * plate. The plate's first line is the connection status — the slot where the route is shown.
 */
@Composable
private fun PcBay(pc: PcEntry, status: PcStatus, onClick: () -> Unit, onRemove: () -> Unit) {
    val hb = Hb.colors
    var menu by remember { mutableStateOf(false) }
    val reading = LastSeen.get(pc.pcId)
    Plate(onClick = onClick, onClickLabel = "${pc.name} 실시간 보기") {
        val showMeters = reading != null && status is PcStatus.Online
        Column(Modifier.fillMaxWidth().background(hb.slab).padding(start = 12.dp, end = 4.dp, top = 4.dp, bottom = if (showMeters) 12.dp else 4.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                val (led, pulse) = when (status) {
                    PcStatus.Checking -> hb.accent to false
                    is PcStatus.Online -> hb.ledOn to true
                    PcStatus.Offline -> hb.ledOff to false
                }
                Led(led, size = 8.dp, pulse = pulse)
                Spacer(Modifier.width(4.dp))
                Text(
                    pc.name.uppercase(),
                    style = HbType.display.copy(fontSize = 19.sp),
                    color = hb.onSlab,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.weight(1f),
                )
                Box {
                    IconButton(onClick = { menu = true }) {
                        Icon(Icons.Filled.MoreVert, contentDescription = "${pc.name} 메뉴", tint = hb.onSlabSoft)
                    }
                    DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
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
