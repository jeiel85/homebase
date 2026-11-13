package io.github.jeiel85.homebase.ui

import androidx.compose.animation.animateContentSize
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Tab
import androidx.compose.material3.TabRow
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import io.github.jeiel85.homebase.data.PcEntry
import io.github.jeiel85.homebase.data.PcStore
import io.github.jeiel85.homebase.net.AppEntry
import io.github.jeiel85.homebase.net.HomebaseApi
import io.github.jeiel85.homebase.net.LiveSnapshot
import io.github.jeiel85.homebase.net.LiveState
import io.github.jeiel85.homebase.net.ProcessEntry
import io.github.jeiel85.homebase.net.Usage
import java.time.Duration
import java.time.OffsetDateTime

/**
 * One PC, live. The WebSocket is open only while this screen is at least STARTED
 * (`collectAsStateWithLifecycle`), so leaving the screen or turning the display off closes it
 * and the PC stops sampling.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun PcDetailScreen(pc: PcEntry, store: PcStore, api: HomebaseApi, onBack: () -> Unit) {
    val flow = remember(pc.pcId) {
        api.live(
            pc,
            onGoodEndpoint = { e -> store.rememberGood(pc.pcId, e) },
            onEndpoints = { list -> store.updateEndpoints(pc.pcId, list) },
        )
    }
    val state by flow.collectAsStateWithLifecycle(initialValue = LiveState.Connecting)
    var last by remember { mutableStateOf<LiveSnapshot?>(null) }
    (state as? LiveState.Live)?.let { last = it.snapshot }
    var tab by rememberSaveable { mutableIntStateOf(0) }

    Scaffold(
        topBar = {
            TopAppBar(
                title = {
                    Column {
                        Text(pc.name, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        Text(statusLine(state), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                },
                navigationIcon = {
                    IconButton(onClick = onBack) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "뒤로") }
                },
            )
        },
    ) { padding ->
        Column(Modifier.fillMaxSize().padding(padding)) {
            when (val s = state) {
                LiveState.Unauthorized -> Notice("이 PC에서 휴대폰 연결이 해제되었습니다. 목록에서 삭제한 뒤 QR로 다시 연결하세요.")
                is LiveState.Incompatible -> Notice(s.reason)
                else -> Unit
            }
            val snapshot = last
            if (snapshot == null) {
                if (state is LiveState.Connecting || state is LiveState.Retrying) LinearProgressIndicator(Modifier.fillMaxWidth())
                return@Column
            }
            SystemCard(snapshot)
            TabRow(selectedTabIndex = tab) {
                Tab(selected = tab == 0, onClick = { tab = 0 }, text = { Text("창 ${snapshot.apps.size}") })
                Tab(selected = tab == 1, onClick = { tab = 1 }, text = { Text("프로세스") })
                Tab(selected = tab == 2, onClick = { tab = 2 }, text = { Text("Claude ${snapshot.claudeSessions.size}") })
            }
            when (tab) {
                0 -> AppsTab(snapshot)
                1 -> ProcessesTab(snapshot)
                else -> ClaudeTab(snapshot)
            }
        }
    }
}

private fun statusLine(state: LiveState): String = when (state) {
    LiveState.Connecting -> "연결 중…"
    is LiveState.Live -> "실시간 · ${state.endpoint.label} · ${state.endpoint.host}"
    is LiveState.Retrying -> "연결 끊김 — ${state.inSeconds}초 후 재시도 (${state.reason})"
    LiveState.Unauthorized -> "연결 해제됨"
    is LiveState.Incompatible -> "버전 불일치"
}

@Composable
private fun Notice(text: String) {
    Card(
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.errorContainer),
        modifier = Modifier.fillMaxWidth().padding(12.dp),
    ) { Text(text, Modifier.padding(12.dp), color = MaterialTheme.colorScheme.onErrorContainer) }
}

@Composable
private fun SystemCard(s: LiveSnapshot) {
    val sys = s.system
    Card(Modifier.fillMaxWidth().padding(12.dp)) {
        Column(Modifier.padding(14.dp)) {
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                Metric("CPU", Fmt.pct(sys.cpuPct))
                Metric("RAM", "${Fmt.bytes(sys.memUsedBytes)} / ${Fmt.bytes(sys.memTotalBytes)}")
            }
            Spacer(Modifier.height(8.dp))
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                Metric("I/O", Fmt.rate(sys.ioBps))
                Metric("네트워크 ↓", Fmt.rate(sys.netRxBps))
                Metric("↑", Fmt.rate(sys.netTxBps))
            }
            Spacer(Modifier.height(6.dp))
            Text(
                "가동 ${Fmt.uptime(sys.uptimeSec)}" +
                    (if (!s.netPerProcess) " · 프로세스별 네트워크 사용 불가" else "") +
                    (if (!s.sessionAvailable) " · 사용자 세션 없음(트레이 미실행/로그아웃)" else ""),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}

@Composable
private fun Metric(label: String, value: String) {
    Column {
        Text(label, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        Text(value, style = MaterialTheme.typography.titleSmall)
    }
}

@Composable
private fun UsageLine(u: Usage) {
    Text(
        "CPU ${Fmt.pct(u.cpuPct)} · RAM ${Fmt.bytes(u.memBytes)} · I/O ${Fmt.rate(u.ioBps)} · ↓${Fmt.rate(u.netRxBps)} ↑${Fmt.rate(u.netTxBps)}",
        style = MaterialTheme.typography.bodySmall,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
    )
}

@Composable
private fun AppsTab(s: LiveSnapshot) {
    if (!s.sessionAvailable) {
        Text(
            "창 목록을 받을 수 없습니다. PC에 사용자가 로그인해 있고 Homebase 트레이 앱이 실행 중이어야 합니다. 아래 '프로세스' 탭은 계속 볼 수 있습니다.",
            Modifier.padding(16.dp),
        )
        return
    }
    LazyColumn(contentPadding = PaddingValues(12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        items(s.apps, key = { it.pid }) { AppCard(it) }
    }
}

@Composable
private fun AppCard(app: AppEntry) {
    var expanded by remember { mutableStateOf(false) }
    val border = if (app.foreground) CardDefaults.outlinedCardBorder() else null
    Card(
        modifier = Modifier.fillMaxWidth().clickable { expanded = !expanded }.animateContentSize(),
        border = border,
    ) {
        Column(Modifier.padding(12.dp)) {
            Row {
                Text(app.process, style = MaterialTheme.typography.titleSmall, fontWeight = FontWeight.Bold)
                if (app.foreground) Text("  · 활성", style = MaterialTheme.typography.titleSmall, color = MaterialTheme.colorScheme.primary)
            }
            app.windows.forEach { w ->
                Text(
                    w.title,
                    style = MaterialTheme.typography.bodyMedium,
                    fontWeight = if (w.foreground) FontWeight.SemiBold else FontWeight.Normal,
                    maxLines = if (expanded) 4 else 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
            Spacer(Modifier.height(4.dp))
            UsageLine(app.usage)
            if (app.children.isNotEmpty()) {
                if (expanded) {
                    Spacer(Modifier.height(6.dp))
                    app.children.forEach { ProcessRow(it, indent = true) }
                } else {
                    Text("하위 프로세스 ${app.children.size}개 — 눌러서 보기", style = MaterialTheme.typography.labelSmall)
                }
            }
        }
    }
}

@Composable
private fun ProcessRow(p: ProcessEntry, indent: Boolean = false) {
    Row(Modifier.fillMaxWidth().padding(start = if (indent) 8.dp else 0.dp, top = 4.dp)) {
        Text("${p.name} (${p.pid})", style = MaterialTheme.typography.bodySmall, modifier = Modifier.width(150.dp), maxLines = 1, overflow = TextOverflow.Ellipsis)
        Text(
            "${Fmt.pct(p.usage.cpuPct)} · ${Fmt.bytes(p.usage.memBytes)} · ${Fmt.rate(p.usage.ioBps)} · ↓${Fmt.rate(p.usage.netRxBps)} ↑${Fmt.rate(p.usage.netTxBps)}",
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

@Composable
private fun ProcessesTab(s: LiveSnapshot) {
    LazyColumn(contentPadding = PaddingValues(12.dp)) {
        items(s.processes, key = { it.pid }) { ProcessRow(it) }
    }
}

@Composable
private fun ClaudeTab(s: LiveSnapshot) {
    if (s.claudeSessions.isEmpty()) {
        Text("최근 10분 안에 활동한 Claude Code 세션이 없습니다.", Modifier.padding(16.dp))
        return
    }
    LazyColumn(contentPadding = PaddingValues(12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        items(s.claudeSessions) { c ->
            Card(Modifier.fillMaxWidth()) {
                Column(Modifier.padding(12.dp)) {
                    Text(c.title, style = MaterialTheme.typography.titleSmall)
                    c.project?.let { Text(it, style = MaterialTheme.typography.bodySmall, maxLines = 1, overflow = TextOverflow.Ellipsis) }
                    Text(ago(c.lastActivity, s.ts), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
        }
    }
}

private fun ago(at: String, now: String): String = try {
    val secs = Duration.between(OffsetDateTime.parse(at), OffsetDateTime.parse(now)).seconds.coerceAtLeast(0)
    if (secs < 60) "방금 활동" else "${secs / 60}분 전 활동"
} catch (_: Exception) {
    ""
}
