package io.github.jeiel85.homebase.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Card
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExtendedFloatingActionButton
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.repeatOnLifecycle
import io.github.jeiel85.homebase.BuildConfig
import io.github.jeiel85.homebase.data.PcEntry
import io.github.jeiel85.homebase.data.PcStore
import io.github.jeiel85.homebase.net.HomebaseApi
import io.github.jeiel85.homebase.update.UpdateSurface
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay

private sealed interface PcStatus {
    data object Checking : PcStatus
    data class Online(val version: String, val host: String) : PcStatus
    data object Offline : PcStatus
}

@OptIn(ExperimentalMaterial3Api::class)
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

    // Poll /info for each PC while the list is visible (every 15 s; nothing in the background).
    LaunchedEffect(pcs) {
        lifecycle.repeatOnLifecycle(Lifecycle.State.STARTED) {
            while (true) {
                coroutineScope {
                    pcs.map { pc ->
                        async {
                            if (status[pc.pcId] == null) status[pc.pcId] = PcStatus.Checking
                            status[pc.pcId] = try {
                                val (info, host) = api.info(pc)
                                store.rememberGoodHost(pc.pcId, host)
                                PcStatus.Online(info.agentVersion, host)
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

    Scaffold(
        topBar = { TopAppBar(title = { Text("Homebase") }) },
        floatingActionButton = {
            ExtendedFloatingActionButton(
                onClick = onAdd,
                icon = { Icon(Icons.Filled.Add, contentDescription = null) },
                text = { Text("PC 추가") },
            )
        },
    ) { padding ->
        LazyColumn(
            modifier = Modifier.fillMaxSize().padding(padding).padding(horizontal = 16.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            item { UpdateSurface.Banner(Modifier.padding(top = 4.dp)) }
            if (pcs.isEmpty()) {
                item {
                    Text(
                        "아직 등록된 PC가 없습니다.\nPC 트레이 메뉴의 \"Connect Phone…\"에서 QR 코드를 띄운 뒤 'PC 추가'를 누르세요.\n휴대폰과 PC 모두 Tailscale에 연결되어 있어야 합니다.",
                        style = MaterialTheme.typography.bodyMedium,
                        modifier = Modifier.padding(vertical = 24.dp),
                    )
                }
            }
            items(pcs, key = { it.pcId }) { pc ->
                PcCard(pc, status[pc.pcId] ?: PcStatus.Checking, onClick = { onOpen(pc) }, onRemove = { confirmRemove = pc })
            }
            item {
                HorizontalDivider(Modifier.padding(top = 16.dp))
                UpdateSurface.SettingsRows()
                Text(
                    "앱 버전 ${BuildConfig.VERSION_NAME}",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(bottom = 96.dp),
                )
            }
        }
    }

    confirmRemove?.let { pc ->
        AlertDialog(
            onDismissRequest = { confirmRemove = null },
            title = { Text("PC 삭제") },
            text = { Text("${pc.name}을(를) 목록에서 지울까요? PC 쪽 연결 해제는 PC의 \"Connect Phone…\" 창에서 합니다.") },
            confirmButton = {
                TextButton(onClick = {
                    store.remove(pc.pcId)
                    pcs = store.all()
                    confirmRemove = null
                }) { Text("삭제") }
            },
            dismissButton = { TextButton(onClick = { confirmRemove = null }) { Text("취소") } },
        )
    }
}

@Composable
private fun PcCard(pc: PcEntry, status: PcStatus, onClick: () -> Unit, onRemove: () -> Unit) {
    var menu by remember { mutableStateOf(false) }
    Card(Modifier.fillMaxWidth().clickable(onClick = onClick)) {
        Row(Modifier.padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
            val (color, label) = when (status) {
                PcStatus.Checking -> Color.Gray to "확인 중…"
                is PcStatus.Online -> Color(0xFF2E7D32) to "온라인 · v${status.version} · ${status.host}"
                PcStatus.Offline -> Color(0xFFE60012) to "오프라인 또는 Tailscale 연결 안 됨"
            }
            Surface(color = color, shape = CircleShape, modifier = Modifier.size(12.dp)) {}
            Column(Modifier.weight(1f).padding(start = 14.dp)) {
                Text(pc.name, style = MaterialTheme.typography.titleMedium)
                Spacer(Modifier.height(2.dp))
                Text(label, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            Box {
                IconButton(onClick = { menu = true }) { Icon(Icons.Filled.MoreVert, contentDescription = "메뉴") }
                DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                    DropdownMenuItem(text = { Text("삭제") }, onClick = { menu = false; onRemove() })
                }
            }
        }
    }
}
