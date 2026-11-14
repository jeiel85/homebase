package io.github.jeiel85.homebase.ui

import android.os.SystemClock
import androidx.compose.animation.animateContentSize
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyListScope
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.KeyboardArrowDown
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import io.github.jeiel85.homebase.data.PcEntry
import io.github.jeiel85.homebase.data.PcStore
import io.github.jeiel85.homebase.net.AppEntry
import io.github.jeiel85.homebase.net.HomebaseApi
import io.github.jeiel85.homebase.push.PushRegistrar
import io.github.jeiel85.homebase.net.LiveSnapshot
import io.github.jeiel85.homebase.net.WorkItem
import io.github.jeiel85.homebase.net.LiveState
import io.github.jeiel85.homebase.net.ProcessEntry
import io.github.jeiel85.homebase.net.WindowTitle
import java.time.Duration
import java.time.OffsetDateTime

private const val HISTORY = 60

/**
 * One PC, live. The WebSocket is open only while this screen is at least STARTED
 * (`collectAsStateWithLifecycle`), so leaving the screen or turning the display off closes it
 * and the PC stops sampling.
 *
 * Layout: an always-dark instrument panel (CPU/RAM arc gauges, 60 s history, I/O and network
 * traces), then a sticky selector for apps / background processes / work sessions.
 */
@OptIn(ExperimentalFoundationApi::class)
@Composable
fun PcDetailScreen(pc: PcEntry, store: PcStore, api: HomebaseApi, onBack: () -> Unit) {
    val context = LocalContext.current
    val flow = remember(pc.pcId) {
        api.live(
            pc,
            onGoodEndpoint = { e ->
                store.rememberGood(pc.pcId, e)
                // Every (re)connect re-tells the PC this phone's push endpoint (a PC may have lost it).
                PushRegistrar.ensureAsync(context, api, store.get(pc.pcId) ?: pc)
            },
            onEndpoints = { list -> store.updateEndpoints(pc.pcId, list) },
            latest = { store.get(pc.pcId) },
        )
    }
    val state by flow.collectAsStateWithLifecycle(initialValue = LiveState.Connecting)
    var last by remember { mutableStateOf<LiveSnapshot?>(null) }
    (state as? LiveState.Live)?.let { last = it.snapshot }
    var tab by rememberSaveable { mutableIntStateOf(0) }
    val history = remember(pc.pcId) { History() }
    var appSort by remember { mutableStateOf(AppSortSetting.load(context)) }
    val appOrder = remember(pc.pcId) { AppOrder() }

    LaunchedEffect(last?.ts) {
        last?.let {
            history.add(it)
            LastSeen.record(pc.pcId, it)
        }
    }

    val hb = Hb.colors
    Column(Modifier.fillMaxSize().background(hb.background)) {
        ConsoleBar(
            title = pc.name,
            onBack = onBack,
            status = { LiveStatus(state) },
        )
        when (val s = state) {
            LiveState.Unauthorized -> Notice("이 PC에서 휴대폰 연결이 해제되었습니다. 목록에서 삭제한 뒤 QR로 다시 연결하세요.")
            is LiveState.Incompatible -> Notice(s.reason)
            else -> Unit
        }
        val snapshot = last
        if (snapshot == null) {
            if (state is LiveState.Connecting || state is LiveState.Retrying) Waiting(state, pc.endpoints.any { it.isQuickTunnel })
            return@Column
        }
        // Keyed on the snapshot, so each second's list is ordered once, and a sort change re-ranks at once.
        val apps = remember(snapshot, appSort) { appOrder.arrange(snapshot.apps, appSort, SystemClock.elapsedRealtime()) }
        LazyColumn(
            Modifier.fillMaxSize(),
            contentPadding = PaddingValues(bottom = 24.dp),
        ) {
            item(key = "panel") { InstrumentPanel(snapshot, history) }
            stickyHeader(key = "tabs") {
                TabSelector(
                    tab,
                    listOf(
                        "앱" to if (snapshot.sessionAvailable) snapshot.apps.size.toString() else "—",
                        "프로세스" to snapshot.processes.size.toString(),
                        "작업" to snapshot.workItems.size.toString(),
                    ),
                ) { tab = it }
            }
            when (tab) {
                0 -> appItems(snapshot, apps, appSort) {
                    appSort = it
                    AppSortSetting.save(context, it)
                }
                1 -> processItems(snapshot)
                else -> sessionItems(snapshot)
            }
            item(key = "pad") { Spacer(Modifier.navigationBarsPadding()) }
        }
    }
}

/** Rolling 60-sample buffers for the panel; kept per screen, nothing persisted. */
private class History {
    val cpu = mutableStateListOf<Float>()
    val mem = mutableStateListOf<Float>()
    val io = mutableStateListOf<Float>()
    val rx = mutableStateListOf<Float>()
    val tx = mutableStateListOf<Float>()

    fun add(s: LiveSnapshot) {
        val sys = s.system
        push(cpu, sys.cpuPct.toFloat())
        push(mem, memPct(s).toFloat())
        push(io, sys.ioBps.toFloat())
        push(rx, sys.netRxBps.toFloat())
        push(tx, sys.netTxBps.toFloat())
    }

    private fun push(list: MutableList<Float>, v: Float) {
        list.add(v)
        while (list.size > HISTORY) list.removeAt(0)
    }
}

private fun memPct(s: LiveSnapshot): Double =
    if (s.system.memTotalBytes > 0) s.system.memUsedBytes * 100.0 / s.system.memTotalBytes else 0.0

/** Status line under the PC name (the slot where the connection route is also shown). */
@Composable
private fun LiveStatus(state: LiveState) {
    val hb = Hb.colors
    Row(verticalAlignment = Alignment.CenterVertically) {
        val (color, pulse) = when (state) {
            is LiveState.Live -> hb.ledOn to true
            LiveState.Connecting -> hb.accent to false
            else -> hb.ledOff to false
        }
        Led(color, size = 7.dp, pulse = pulse)
        Spacer(Modifier.width(2.dp))
        Text(
            statusLine(state),
            style = HbType.readout.copy(fontSize = 12.sp, fontWeight = FontWeight.Normal),
            color = hb.onSlabSoft,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
    }
}

private fun statusLine(state: LiveState): String = when (state) {
    LiveState.Connecting -> "연결 중…"
    is LiveState.Live -> "실시간 · ${state.endpoint.label} · ${state.endpoint.host}"
    is LiveState.Retrying -> "연결 끊김 — ${state.inSeconds}초 후 다시 시도"
    LiveState.Unauthorized -> "연결 해제됨"
    is LiveState.Incompatible -> "버전 불일치"
}

/** Before the first snapshot: say what is happening and what to check, not just a spinner. */
@Composable
private fun Waiting(state: LiveState, quickTunnel: Boolean) {
    val hb = Hb.colors
    val retrying = state as? LiveState.Retrying
    Column(Modifier.fillMaxWidth().background(hb.slab).padding(horizontal = 20.dp, vertical = 28.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Led(if (retrying == null) hb.accent else hb.ledOff, size = 9.dp, pulse = retrying == null)
            Spacer(Modifier.width(6.dp))
            Text(
                if (retrying == null) "PC에 연결하는 중" else "PC에 닿지 않습니다",
                style = HbType.display.copy(fontSize = 19.sp),
                color = hb.onSlab,
            )
        }
        Spacer(Modifier.height(14.dp))
        SegmentMeter(0.0, segments = 24, height = 10.dp)
        Spacer(Modifier.height(14.dp))
        Text(
            if (retrying == null) {
                "실시간 연결을 여는 중입니다."
            } else {
                "${retrying.inSeconds}초 뒤 자동으로 다시 시도합니다. PC가 켜져 있는지, 휴대폰이 PC와 같은 Wi-Fi에 있거나 PC의 Tailscale·Cloudflare 경로에 닿는지 확인하세요."
            },
            style = androidx.compose.material3.MaterialTheme.typography.bodyMedium,
            color = hb.onSlabSoft,
        )
        if (retrying != null && quickTunnel) {
            Text(
                "PC가 다시 시작되면 Cloudflare 임시 주소가 바뀝니다. 이 앱의 푸시 알림이 켜져 있으면 PC가 새 주소를 보내 줘서 곧 다시 연결되고, 꺼져 있으면 PC의 \"설정…\" → \"휴대폰\" 탭 QR로 다시 연결해야 합니다.",
                style = androidx.compose.material3.MaterialTheme.typography.bodySmall,
                color = hb.onSlabSoft,
                modifier = Modifier.padding(top = 10.dp),
            )
        }
        if (retrying != null && retrying.reason.isNotBlank()) {
            Text(
                retrying.reason,
                style = HbType.readout.copy(fontSize = 11.sp, fontWeight = FontWeight.Normal),
                color = hb.onSlabSoft.copy(alpha = 0.7f),
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.padding(top = 10.dp),
            )
        }
    }
}

@Composable
private fun Notice(text: String) {
    val hb = Hb.colors
    Row(
        Modifier.fillMaxWidth().padding(12.dp).clip(RoundedCornerShape(6.dp)).background(hb.danger.copy(alpha = 0.14f)).padding(14.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Led(hb.ledOff, size = 8.dp)
        Spacer(Modifier.width(8.dp))
        Text(text, style = androidx.compose.material3.MaterialTheme.typography.bodyMedium, color = hb.ink)
    }
}

@Composable
private fun InstrumentPanel(s: LiveSnapshot, h: History) {
    val hb = Hb.colors
    val sys = s.system
    Column(Modifier.fillMaxWidth().background(hb.slab).padding(horizontal = 16.dp, vertical = 14.dp)) {
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            Column(Modifier.weight(1f)) {
                ArcGauge(sys.cpuPct, "CPU", Fmt.pctShort(sys.cpuPct), null, Modifier.fillMaxWidth().aspectRatio(1.3f))
                HistoryStrip(h.cpu, capacity = HISTORY, height = 22.dp)
            }
            Column(Modifier.weight(1f)) {
                val mp = memPct(s)
                ArcGauge(mp, "RAM", Fmt.pctShort(mp), "${Fmt.gb(sys.memUsedBytes)} / ${Fmt.gb(sys.memTotalBytes)}", Modifier.fillMaxWidth().aspectRatio(1.3f))
                HistoryStrip(h.mem, capacity = HISTORY, height = 22.dp)
            }
        }
        Text(
            "최근 60초",
            style = HbType.label.copy(fontSize = 9.sp),
            color = hb.onSlabSoft.copy(alpha = 0.7f),
            modifier = Modifier.align(Alignment.End).padding(top = 3.dp),
        )
        Spacer(Modifier.height(12.dp))
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            RateCell("디스크", sys.ioBps, h.io, hb.meterOk, Modifier.weight(1f))
            RateCell("받기 ↓", sys.netRxBps, h.rx, Color(0xFF6FD3C1), Modifier.weight(1f))
            RateCell("보내기 ↑", sys.netTxBps, h.tx, hb.accent, Modifier.weight(1f))
        }
        Spacer(Modifier.height(12.dp))
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text("가동", style = HbType.label, color = hb.onSlabSoft)
            Spacer(Modifier.width(8.dp))
            Text(Fmt.uptime(sys.uptimeSec), style = HbType.readout.copy(fontSize = 13.sp), color = hb.onSlab)
            if (!s.netPerProcess) {
                Spacer(Modifier.width(10.dp))
                Text("· 앱별 네트워크 없음", style = androidx.compose.material3.MaterialTheme.typography.bodySmall, color = hb.onSlabSoft)
            }
        }
    }
}

@Composable
private fun RateCell(label: String, bps: Long, trace: List<Float>, color: Color, modifier: Modifier) {
    val hb = Hb.colors
    Column(
        modifier
            .clip(RoundedCornerShape(6.dp))
            .background(hb.slabRaised)
            .padding(horizontal = 10.dp, vertical = 8.dp)
            .semantics(mergeDescendants = true) { contentDescription = "$label ${Fmt.rate(bps)}" },
    ) {
        Text(label, style = HbType.label.copy(fontSize = 10.sp), color = hb.onSlabSoft, maxLines = 1)
        Text(Fmt.rate(bps), style = HbType.readout.copy(fontSize = 13.sp), color = hb.onSlab, maxLines = 1)
        Spacer(Modifier.height(4.dp))
        RateTrace(trace, color, capacity = HISTORY, height = 26.dp)
    }
}

/** Console-style segmented selector; sticks under the bar while the list scrolls. */
@Composable
private fun TabSelector(selected: Int, tabs: List<Pair<String, String>>, onSelect: (Int) -> Unit) {
    val hb = Hb.colors
    Row(
        Modifier.fillMaxWidth().background(hb.background).padding(horizontal = 12.dp, vertical = 10.dp),
        horizontalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        tabs.forEachIndexed { i, (label, count) ->
            val on = i == selected
            Row(
                Modifier
                    .weight(1f)
                    .heightIn(min = 48.dp)
                    .clip(RoundedCornerShape(6.dp))
                    .background(if (on) hb.slab else hb.plate)
                    .drawBehind {
                        if (on) drawRect(hb.accent, topLeft = Offset(0f, size.height - 3.dp.toPx()), size = Size(size.width, 3.dp.toPx()))
                    }
                    .clickable(role = Role.Tab) { onSelect(i) }
                    .semantics { this.selected = on }
                    .padding(horizontal = 8.dp),
                horizontalArrangement = Arrangement.Center,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(label, style = HbType.label.copy(fontSize = 13.sp, letterSpacing = 0.4.sp), color = if (on) hb.onSlab else hb.ink, maxLines = 1)
                Spacer(Modifier.width(6.dp))
                Text(
                    count,
                    style = HbType.readout.copy(fontSize = 11.sp),
                    color = if (on) hb.slab else hb.inkSoft,
                    modifier = Modifier.clip(RoundedCornerShape(3.dp)).background(if (on) hb.accent else hb.hairline).padding(horizontal = 5.dp, vertical = 1.dp),
                )
            }
        }
    }
}

/** [apps] is [s].apps already in display order ([AppOrder]). */
private fun LazyListScope.appItems(s: LiveSnapshot, apps: List<AppEntry>, sort: AppSort, onSort: (AppSort) -> Unit) {
    if (!s.sessionAvailable) {
        item(key = "noapps") {
            EmptyNote("창 목록을 받을 수 없습니다", "PC에 사용자가 로그인해 있고 Homebase 트레이 앱이 실행 중이어야 합니다. '프로세스'는 계속 볼 수 있습니다.")
        }
        return
    }
    if (apps.isEmpty()) {
        item(key = "noapps") { EmptyNote("열린 창이 없습니다", null) }
        return
    }
    item(key = "asort") { SortBar(sort, onSort) }
    items(apps, key = { "a${it.pid}" }) { AppCard(it, s.system.memTotalBytes) }
}

/** Which order the cards are in, said in words, with the three choices next to it. */
@Composable
private fun SortBar(sort: AppSort, onSort: (AppSort) -> Unit) {
    val hb = Hb.colors
    Column(Modifier.fillMaxWidth().padding(start = 16.dp, end = 12.dp, bottom = 2.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text("정렬", style = HbType.label.copy(fontSize = 11.sp, letterSpacing = 0.4.sp), color = hb.inkSoft, modifier = Modifier.padding(end = 6.dp))
            AppSort.entries.forEach { s ->
                val on = s == sort
                // 48 dp touch target around a smaller pill.
                Box(
                    Modifier
                        .heightIn(min = 48.dp)
                        .clickable(role = Role.RadioButton) { onSort(s) }
                        .semantics { this.selected = on }
                        .padding(horizontal = 3.dp),
                    contentAlignment = Alignment.Center,
                ) {
                    Text(
                        s.label,
                        style = HbType.label.copy(fontSize = 12.sp, letterSpacing = 0.3.sp),
                        color = if (on) hb.slab else hb.ink,
                        maxLines = 1,
                        modifier = Modifier
                            .clip(RoundedCornerShape(5.dp))
                            .background(if (on) hb.accent else hb.plate)
                            .padding(horizontal = 12.dp, vertical = 6.dp),
                    )
                }
            }
        }
        Text(
            sort.description,
            style = androidx.compose.material3.MaterialTheme.typography.bodySmall,
            color = hb.inkSoft,
        )
    }
}

/**
 * Collapsed, every card has the same five rows whatever the numbers do: name, one window title,
 * the two bars, and a footer that always holds disk (and network) plus the window / child counts.
 * Rows that came and went with the values (I/O at zero, a second title, the expand hint) made the
 * list jump under the reader every second.
 */
@Composable
private fun AppCard(app: AppEntry, memTotal: Long) {
    val hb = Hb.colors
    var expanded by rememberSaveable(app.pid) { mutableStateOf(false) }
    // Expandable when there is more to show: child processes, or window titles beyond the one shown.
    val canExpand = app.children.isNotEmpty() || app.windows.size > 1
    Plate(
        modifier = Modifier.padding(horizontal = 12.dp, vertical = 5.dp).animateContentSize(),
        // Also while expanded, so a card whose extra windows closed can still be folded.
        onClick = if (canExpand || expanded) ({ expanded = !expanded }) else null,
        onClickLabel = if (expanded) "창·하위 프로세스 접기" else "창·하위 프로세스 펼치기",
        rail = app.foreground,
    ) {
        Column(Modifier.padding(start = 14.dp, end = 14.dp, top = 12.dp, bottom = 12.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Row(Modifier.weight(1f), verticalAlignment = Alignment.CenterVertically) {
                    Text(
                        app.process,
                        style = HbType.display.copy(fontSize = 15.sp, letterSpacing = 0.sp),
                        color = hb.ink,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.weight(1f, fill = false),
                    )
                    if (app.foreground) {
                        Spacer(Modifier.width(8.dp))
                        Tag("사용 중", hb.accent, hb.slab)
                    }
                }
                Spacer(Modifier.width(8.dp))
                Text(Fmt.pctShort(app.usage.cpuPct), style = HbType.readout.copy(fontSize = 15.sp), color = hb.ink)
            }
            // Collapsed: exactly one title line — the window in use, else the first.
            val titles = if (expanded) app.windows.take(8) else listOfNotNull(app.windows.firstOrNull { it.foreground } ?: app.windows.firstOrNull())
            if (titles.isEmpty()) {
                WindowLine(WindowTitle("", false), expanded)
            } else {
                titles.forEach { WindowLine(it, expanded) }
            }
            Spacer(Modifier.height(10.dp))
            UsageBars(app.usage.cpuPct, app.usage.memBytes, memTotal)
            CardFooter(app, canExpand || expanded, expanded)
            if (expanded && app.children.isNotEmpty()) {
                Spacer(Modifier.height(8.dp))
                Box(Modifier.fillMaxWidth().height(1.dp).background(hb.hairline))
                app.children.forEach { ProcessRow(it, memTotal, compact = true) }
            }
        }
    }
}

@Composable
private fun WindowLine(w: WindowTitle, expanded: Boolean) {
    val hb = Hb.colors
    Text(
        w.title.ifBlank { "(제목 없음)" },
        style = androidx.compose.material3.MaterialTheme.typography.bodyMedium,
        fontWeight = if (w.foreground) FontWeight.SemiBold else FontWeight.Normal,
        color = if (w.title.isBlank()) hb.inkSoft else hb.ink,
        maxLines = if (expanded) 3 else 1,
        overflow = TextOverflow.Ellipsis,
        modifier = Modifier.padding(top = 2.dp),
    )
}

/**
 * Disk (and per-app network where the PC measures it) on the left, always shown — "0 B/s" is an
 * answer too, and a line that vanishes at zero resizes the card. Counts and the chevron on the right;
 * the chevron's slot is kept even when there is nothing to expand, for the same reason.
 */
@Composable
private fun CardFooter(app: AppEntry, canExpand: Boolean, expanded: Boolean) {
    val hb = Hb.colors
    val u = app.usage
    val io = buildString {
        append("디스크 ").append(Fmt.rate(u.ioBps))
        if (u.netRxBps != null || u.netTxBps != null) append("  ·  ↓").append(Fmt.rate(u.netRxBps)).append(" ↑").append(Fmt.rate(u.netTxBps))
    }
    val counts = if (app.children.isNotEmpty()) "창 ${app.windows.size} · 하위 ${app.children.size}" else "창 ${app.windows.size}"
    val angle by animateFloatAsState(if (expanded) 180f else 0f, label = "chevron")
    Row(Modifier.fillMaxWidth().padding(top = 6.dp), verticalAlignment = Alignment.CenterVertically) {
        Text(
            io,
            style = HbType.readout.copy(fontSize = 11.sp, fontWeight = FontWeight.Normal),
            color = hb.inkSoft,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.weight(1f),
        )
        Spacer(Modifier.width(8.dp))
        Text(counts, style = HbType.label.copy(fontSize = 11.sp, letterSpacing = 0.3.sp), color = hb.inkSoft, maxLines = 1)
        Box(Modifier.size(18.dp)) {
            if (canExpand) {
                Icon(Icons.Filled.KeyboardArrowDown, contentDescription = null, tint = hb.inkSoft, modifier = Modifier.size(18.dp).rotate(angle))
            }
        }
    }
}

/** CPU share and RAM share as two labelled bars — the numbers stay, the bars carry the glance. */
@Composable
private fun UsageBars(cpuPct: Double, memBytes: Long, memTotal: Long) {
    val hb = Hb.colors
    val memShare = if (memTotal > 0) memBytes.toFloat() / memTotal else 0f
    // On white plates periwinkle is too faint for a 4 dp bar, so the cool tone is chrome indigo.
    val cool = if (hb.dark) hb.meterOk else Brand.ChromeIndigo
    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
        Row(Modifier.weight(1f), verticalAlignment = Alignment.CenterVertically) {
            Text("CPU", style = HbType.label.copy(fontSize = 10.sp), color = hb.inkSoft, maxLines = 1, softWrap = false, modifier = Modifier.widthIn(min = 28.dp).padding(end = 6.dp))
            ShareBar((cpuPct / 100).toFloat(), if (cpuPct >= 75) hb.load(cpuPct) else cool, Modifier.weight(1f))
        }
        Row(Modifier.weight(1f), verticalAlignment = Alignment.CenterVertically) {
            Text("RAM", style = HbType.label.copy(fontSize = 10.sp), color = hb.inkSoft, maxLines = 1, softWrap = false, modifier = Modifier.widthIn(min = 28.dp).padding(end = 6.dp))
            ShareBar(memShare, cool, Modifier.weight(1f))
            Spacer(Modifier.width(6.dp))
            Text(Fmt.bytes(memBytes), style = HbType.readout.copy(fontSize = 11.sp, fontWeight = FontWeight.Normal), color = hb.inkSoft, maxLines = 1)
        }
    }
}

private fun LazyListScope.processItems(s: LiveSnapshot) {
    item(key = "phead") {
        Text(
            "CPU 사용량 상위 ${s.processes.size}개 · 창이 없는 백그라운드 프로세스 포함",
            style = androidx.compose.material3.MaterialTheme.typography.bodySmall,
            color = Hb.colors.inkSoft,
            modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp),
        )
    }
    item(key = "plist") {
        Plate(Modifier.padding(horizontal = 12.dp, vertical = 5.dp)) {
            Column(Modifier.padding(horizontal = 14.dp, vertical = 6.dp)) {
                s.processes.forEachIndexed { i, p ->
                    if (i > 0) Box(Modifier.fillMaxWidth().height(1.dp).background(Hb.colors.hairline))
                    ProcessRow(p, s.system.memTotalBytes, compact = false)
                }
            }
        }
    }
}

/** Name + pid on the left, CPU as a bar with its value on the right, RAM underneath. */
@Composable
private fun ProcessRow(p: ProcessEntry, memTotal: Long, compact: Boolean) {
    val hb = Hb.colors
    Column(Modifier.fillMaxWidth().padding(vertical = if (compact) 6.dp else 9.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Row(Modifier.weight(1f), verticalAlignment = Alignment.CenterVertically) {
                Text(
                    p.name,
                    style = androidx.compose.material3.MaterialTheme.typography.bodyMedium.copy(fontWeight = FontWeight.SemiBold),
                    color = hb.ink,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.weight(1f, fill = false),
                )
                Spacer(Modifier.width(6.dp))
                Text("${p.pid}", style = HbType.readout.copy(fontSize = 11.sp, fontWeight = FontWeight.Normal), color = hb.inkSoft)
            }
            Spacer(Modifier.width(8.dp))
            Text(Fmt.pctShort(p.usage.cpuPct), style = HbType.readout.copy(fontSize = 13.sp), color = hb.ink)
        }
        Spacer(Modifier.height(5.dp))
        UsageBars(p.usage.cpuPct, p.usage.memBytes, memTotal)
    }
}

/**
 * The work tab (DECISIONS M-26): AI coding sessions (Claude Code, Codex) with their last activity,
 * then the foreground window and the other editors, documents and browser tabs. A PC older than
 * v0.23.0 sends only Claude sessions, which [LiveSnapshot.workItems] turns into the same list.
 */
private fun LazyListScope.sessionItems(s: LiveSnapshot) {
    val work = s.workItems
    if (work.isEmpty()) {
        item(key = "nosess") {
            EmptyNote(
                "지금 보이는 작업이 없습니다",
                "최근 10분 안의 Claude Code·Codex 세션과 열린 문서(Word·Excel·한글 등), 코드 편집기, 브라우저 탭을 보여 줍니다.",
            )
        }
        return
    }
    // Positional keys: the protocol has no item id, and app + title can repeat (two untitled
    // sessions in one folder), which would crash LazyColumn with a duplicate key.
    itemsIndexed(work, key = { i, _ -> "w$i" }) { _, w ->
        val hb = Hb.colors
        val secs = w.lastActivity?.let { agoSeconds(it, s.ts) }
        val active = if (w.kind == WorkItem.AGENT) secs != null && secs < 60 else w.foreground
        Plate(Modifier.padding(horizontal = 12.dp, vertical = 5.dp), rail = active) {
            Column(Modifier.padding(14.dp)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Led(if (active) hb.ledOn else hb.meterOk, size = 7.dp, pulse = active)
                    Text(
                        when {
                            w.kind == WorkItem.AGENT -> ago(secs)
                            w.foreground -> "지금 보는 창"
                            else -> workKindLabel(w.kind)
                        },
                        style = HbType.label.copy(fontSize = 11.sp, letterSpacing = 0.3.sp),
                        color = hb.inkSoft,
                        modifier = Modifier.weight(1f).padding(start = 6.dp),
                    )
                    Tag(w.app, hb.hairline, hb.inkSoft)
                }
                Text(
                    w.title,
                    style = androidx.compose.material3.MaterialTheme.typography.titleSmall,
                    color = hb.ink,
                    maxLines = 3,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.padding(top = 4.dp),
                )
                w.detail?.let {
                    Text(
                        it,
                        style = HbType.readout.copy(fontSize = 11.sp, fontWeight = FontWeight.Normal),
                        color = hb.inkSoft,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.padding(top = 2.dp),
                    )
                }
            }
        }
    }
}

private fun workKindLabel(kind: String): String = when (kind) {
    WorkItem.BROWSER -> "브라우저 탭"
    WorkItem.DOCUMENT -> "문서"
    WorkItem.EDITOR -> "코드 편집기"
    else -> "작업"
}

@Composable
private fun EmptyNote(title: String, body: String?) {
    val hb = Hb.colors
    Column(Modifier.fillMaxWidth().padding(horizontal = 24.dp, vertical = 28.dp), horizontalAlignment = Alignment.CenterHorizontally) {
        Text(title, style = androidx.compose.material3.MaterialTheme.typography.titleSmall, color = hb.ink)
        if (body != null) {
            Text(
                body,
                style = androidx.compose.material3.MaterialTheme.typography.bodySmall,
                color = hb.inkSoft,
                modifier = Modifier.padding(top = 6.dp),
                textAlign = androidx.compose.ui.text.style.TextAlign.Center,
            )
        }
    }
}

private fun agoSeconds(at: String, now: String): Long? = try {
    Duration.between(OffsetDateTime.parse(at), OffsetDateTime.parse(now)).seconds.coerceAtLeast(0)
} catch (_: Exception) {
    null
}

private fun ago(secs: Long?): String = when {
    secs == null -> ""
    secs < 60 -> "방금 활동"
    else -> "${secs / 60}분 전 활동"
}
