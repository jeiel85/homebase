package io.github.jeiel85.homebase.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Notifications
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.FilterChip
import androidx.compose.material3.FilterChipDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.jeiel85.homebase.data.PcStore
import io.github.jeiel85.homebase.push.HistoryEntry
import io.github.jeiel85.homebase.push.PushHistory
import io.github.jeiel85.homebase.push.PushHistoryRules
import io.github.jeiel85.homebase.push.PushNotifier
import io.github.jeiel85.homebase.push.PushState
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter

/**
 * The notification center (DECISIONS M-31): every push this phone received, newest first, grouped
 * by day, filterable by kind and by PC. Opening it marks everything read; entries that were new on
 * opening (or arrive while it is open) keep the amber rail so they stand out.
 */
@Composable
fun NotificationCenterScreen(store: PcStore, onBack: () -> Unit, onOpenPc: (String) -> Unit, onSettings: () -> Unit) {
    val hb = Hb.colors
    val context = LocalContext.current
    val history by PushHistory.state.collectAsState()
    // What counted as read when the screen opened; null until the file is loaded.
    var seenBefore by remember { mutableStateOf<Long?>(null) }
    LaunchedEffect(Unit) {
        withContext(Dispatchers.IO) { PushHistory.ensureLoaded(context) }
        seenBefore = PushHistory.state.value.lastSeenAt
    }
    // Opening the center reads everything — also what arrives while it stays open.
    LaunchedEffect(seenBefore, history.entries) {
        if (seenBefore != null) withContext(Dispatchers.IO) { PushHistory.markAllRead(context) }
    }

    var groupFilter by rememberSaveable { mutableStateOf<String?>(null) }
    var pcFilter by rememberSaveable { mutableStateOf<String?>(null) }
    var expanded by rememberSaveable { mutableStateOf<String?>(null) }
    var confirmClear by remember { mutableStateOf(false) }
    val pushOn = remember { PushState(context).endpoint != null }
    val pairedPcs = remember { store.all().map { it.pcId }.toSet() }

    val entries = history.entries
    val groups = remember(entries) { entries.map { PushHistoryRules.categoryLabel(it.category) }.distinct() }
    // Latest name per PC, in the order they last sent something.
    val pcs = remember(entries) { entries.distinctBy { it.pcId }.map { it.pcId to it.pcName } }
    val shown = remember(entries, groupFilter, pcFilter) {
        entries.filter { e ->
            (groupFilter == null || PushHistoryRules.categoryLabel(e.category) == groupFilter) &&
                (pcFilter == null || e.pcId == pcFilter)
        }
    }
    val zone = remember { ZoneId.systemDefault() }
    val now = remember(entries) { System.currentTimeMillis() }
    val byDay = remember(shown, now) { shown.groupBy { PushHistoryRules.dayOf(it.receivedAt, zone) }.toList() }

    Column(Modifier.fillMaxSize().background(hb.background)) {
        ConsoleBar(
            title = "알림 센터",
            onBack = onBack,
            eyebrow = "받은 푸시 알림",
            actions = {
                IconButton(onClick = { confirmClear = true }, enabled = entries.isNotEmpty()) {
                    Icon(
                        Icons.Filled.Delete,
                        contentDescription = "알림 이력 모두 지우기",
                        tint = if (entries.isNotEmpty()) hb.onSlab else hb.onSlabSoft.copy(alpha = 0.4f),
                    )
                }
            },
        )
        LazyColumn(
            modifier = Modifier.fillMaxSize(),
            contentPadding = PaddingValues(horizontal = 14.dp),
        ) {
            if (seenBefore != null && entries.isEmpty()) {
                item { EmptyCenter(pushOn, onSettings) }
            }
            if (groups.size > 1 || pcs.size > 1) {
                item {
                    Column(Modifier.padding(top = 12.dp)) {
                        if (groups.size > 1) {
                            ChipRow(
                                options = groups.map { it to it },
                                selected = groupFilter,
                                onSelect = { groupFilter = it },
                            )
                        }
                        if (pcs.size > 1) {
                            ChipRow(options = pcs, selected = pcFilter, onSelect = { pcFilter = it })
                        }
                    }
                }
            }
            if (entries.isNotEmpty() && shown.isEmpty()) {
                item {
                    Text(
                        "고른 조건에 맞는 알림이 없습니다.",
                        style = MaterialTheme.typography.bodyMedium,
                        color = hb.inkSoft,
                        modifier = Modifier.padding(top = 24.dp),
                    )
                }
            }
            byDay.forEach { (day, dayEntries) ->
                item(key = "day:$day") {
                    SectionLabel(
                        PushHistoryRules.dayLabel(dayEntries.first().receivedAt, now, zone),
                        trailing = "${dayEntries.size}건",
                    )
                }
                items(dayEntries, key = { it.key }) { e ->
                    HistoryCard(
                        entry = e,
                        unread = seenBefore.let { it != null && e.receivedAt > it },
                        expanded = expanded == e.key,
                        canOpenPc = e.pcId in pairedPcs,
                        onToggle = { expanded = if (expanded == e.key) null else e.key },
                        onOpenPc = { onOpenPc(e.pcId) },
                        onDelete = {
                            PushHistory.remove(context, e)
                            if (expanded == e.key) expanded = null
                        },
                        zone = zone,
                    )
                    Spacer(Modifier.height(10.dp))
                }
            }
            if (entries.isNotEmpty()) {
                item {
                    Text(
                        "최근 30일, 최대 ${PushHistoryRules.MAX_ENTRIES}건을 이 휴대폰에만 암호화해 보관합니다. " +
                            "끈 범주의 알림은 받지 않으므로 여기에도 남지 않습니다.",
                        style = MaterialTheme.typography.bodySmall,
                        color = hb.inkSoft,
                        modifier = Modifier.padding(top = 8.dp),
                    )
                }
            }
            item { Spacer(Modifier.navigationBarsPadding().height(24.dp)) }
        }
    }

    if (confirmClear) {
        AlertDialog(
            onDismissRequest = { confirmClear = false },
            title = { Text("알림 이력 지우기") },
            text = { Text("받은 알림 ${entries.size}건을 이 휴대폰에서 모두 지울까요? 되돌릴 수 없습니다. PC와 Android 알림 창에는 영향이 없습니다.") },
            confirmButton = {
                TextButton(onClick = {
                    PushHistory.clear(context)
                    expanded = null
                    groupFilter = null
                    pcFilter = null
                    confirmClear = false
                }) { Text("모두 지우기") }
            },
            dismissButton = { TextButton(onClick = { confirmClear = false }) { Text("취소") } },
        )
    }
}

/** "전체" plus one chip per option; scrolls sideways when the names don't fit. */
@Composable
private fun ChipRow(options: List<Pair<String, String>>, selected: String?, onSelect: (String?) -> Unit) {
    val hb = Hb.colors
    val colors = FilterChipDefaults.filterChipColors(
        selectedContainerColor = hb.slab,
        selectedLabelColor = hb.onSlab,
        labelColor = hb.ink,
    )
    Row(
        Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
        horizontalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        FilterChip(selected = selected == null, onClick = { onSelect(null) }, label = { Text("전체") }, colors = colors)
        options.forEach { (value, label) ->
            FilterChip(
                selected = selected == value,
                onClick = { onSelect(if (selected == value) null else value) },
                label = { Text(label, maxLines = 1, overflow = TextOverflow.Ellipsis) },
                colors = colors,
            )
        }
    }
}

@Composable
private fun HistoryCard(
    entry: HistoryEntry,
    unread: Boolean,
    expanded: Boolean,
    canOpenPc: Boolean,
    onToggle: () -> Unit,
    onOpenPc: () -> Unit,
    onDelete: () -> Unit,
    zone: ZoneId,
) {
    val hb = Hb.colors
    val time = remember(entry.receivedAt) { TIME.format(Instant.ofEpochMilli(entry.receivedAt).atZone(zone)) }
    val source = if (entry.category == "windows-toast") entry.source else PushNotifier.CATEGORY_LABELS[entry.source] ?: entry.source
    Plate(
        rail = unread,
        onClick = onToggle,
        onClickLabel = if (expanded) "접기" else "펼치기",
    ) {
        Column(Modifier.padding(horizontal = 14.dp, vertical = 12.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Tag(
                    PushHistoryRules.categoryLabel(entry.category),
                    background = if (unread) hb.accent else hb.slabRaised,
                    foreground = if (unread) Brand.Carbon else hb.onSlab,
                )
                Spacer(Modifier.width(8.dp))
                Text(
                    "${entry.pcName} · $source",
                    style = HbType.label,
                    color = hb.inkSoft,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.weight(1f),
                )
                Spacer(Modifier.width(8.dp))
                Text(time, style = HbType.readout.copy(fontSize = 12.sp), color = hb.inkSoft)
            }
            Text(
                entry.title,
                style = MaterialTheme.typography.titleSmall.copy(fontWeight = FontWeight.Bold),
                color = hb.ink,
                maxLines = if (expanded) Int.MAX_VALUE else 2,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.padding(top = 6.dp),
            )
            entry.body?.let {
                Text(
                    it,
                    style = MaterialTheme.typography.bodyMedium,
                    color = hb.inkSoft,
                    maxLines = if (expanded) Int.MAX_VALUE else 3,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.padding(top = 2.dp),
                )
            }
            if (entry.count > 1) {
                Text(
                    "같은 출처의 알림 ${entry.count}건을 묶은 것",
                    style = MaterialTheme.typography.bodySmall,
                    color = hb.inkSoft,
                    modifier = Modifier.padding(top = 4.dp),
                )
            }
            if (!entry.shown) {
                Text(
                    "Android 알림이 꺼져 있어 알림 창에는 뜨지 않았습니다",
                    style = MaterialTheme.typography.bodySmall,
                    color = hb.danger,
                    modifier = Modifier.padding(top = 4.dp),
                )
            }
            if (expanded) {
                Row(Modifier.padding(top = 4.dp), verticalAlignment = Alignment.CenterVertically) {
                    Text(
                        "PC 발송 ${FULL.format(Instant.ofEpochMilli(entry.sentAt).atZone(zone))}",
                        style = MaterialTheme.typography.bodySmall,
                        color = hb.inkSoft,
                        modifier = Modifier.weight(1f),
                    )
                    if (canOpenPc) TextButton(onClick = onOpenPc) { Text("PC 열기") }
                    TextButton(onClick = onDelete) { Text("삭제") }
                }
            }
        }
    }
}

@Composable
private fun EmptyCenter(pushOn: Boolean, onSettings: () -> Unit) {
    val hb = Hb.colors
    Column(
        Modifier
            .padding(top = 28.dp)
            .fillMaxWidth()
            .clip(RoundedCornerShape(8.dp))
            .background(hb.slab)
            .padding(20.dp),
    ) {
        Icon(Icons.Filled.Notifications, contentDescription = null, tint = hb.accent, modifier = Modifier.size(28.dp))
        Text(
            "받은 알림이 없습니다",
            style = MaterialTheme.typography.titleMedium,
            color = hb.onSlab,
            modifier = Modifier.padding(top = 10.dp),
        )
        Text(
            if (pushOn) {
                "PC가 보낸 푸시 알림이 여기에 쌓입니다. Android 알림 창에서 지워도 여기서 다시 볼 수 있습니다."
            } else {
                "푸시 알림이 꺼져 있습니다. 설정에서 켜면 PC가 보낸 알림이 여기에 쌓입니다."
            },
            style = MaterialTheme.typography.bodyMedium,
            color = hb.onSlabSoft,
            modifier = Modifier.padding(top = 4.dp),
        )
        if (!pushOn) {
            TextButton(onClick = onSettings, modifier = Modifier.padding(top = 4.dp)) { Text("설정 열기", color = hb.accent) }
        }
    }
}

/** Bell on the list's console bar with the unread count. */
@Composable
fun NotificationCenterButton(unread: Int, onClick: () -> Unit) {
    val hb = Hb.colors
    IconButton(
        onClick = onClick,
        modifier = Modifier.semantics {
            contentDescription = if (unread > 0) "알림 센터 — 새 알림 ${unread}건" else "알림 센터"
        },
    ) {
        Box {
            Icon(Icons.Filled.Notifications, contentDescription = null, tint = hb.onSlab)
            if (unread > 0) {
                Text(
                    if (unread > 99) "99+" else unread.toString(),
                    style = HbType.readout.copy(fontSize = 9.sp),
                    color = Brand.Carbon,
                    maxLines = 1,
                    modifier = Modifier
                        .align(Alignment.TopEnd)
                        .offset(x = 10.dp, y = (-6).dp)
                        .heightIn(min = 14.dp)
                        .clip(RoundedCornerShape(7.dp))
                        .background(hb.accent)
                        .padding(horizontal = 4.dp),
                )
            }
        }
    }
}

private val TIME: DateTimeFormatter = DateTimeFormatter.ofPattern("HH:mm")
private val FULL: DateTimeFormatter = DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm:ss")
