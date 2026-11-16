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
import androidx.compose.material.icons.filled.KeyboardArrowDown
import androidx.compose.material.icons.filled.KeyboardArrowUp
import androidx.compose.material.icons.filled.Notifications
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Badge
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
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.jeiel85.homebase.data.PcStore
import io.github.jeiel85.homebase.push.HistoryEntry
import io.github.jeiel85.homebase.push.NotificationPriority
import io.github.jeiel85.homebase.push.NotificationRuleStore
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
    // The one bundle opened to its entries (DECISIONS M-37); bundles follow the priority settings.
    var openStack by rememberSaveable { mutableStateOf<String?>(null) }
    val rules = remember { NotificationRuleStore(context).rules() }
    var confirmClear by remember { mutableStateOf(false) }
    val pushOn = remember { PushState(context).endpoint != null }
    // Paired PCs; an entry is drawn under the PC's alias (M-36), else the name it was sent with.
    val paired = remember { store.all().associateBy { it.pcId } }
    fun nameOf(pcId: String, sent: String) = paired[pcId]?.shownName(sent) ?: sent

    val entries = history.entries
    val groups = remember(entries) { entries.map { PushHistoryRules.categoryLabel(it.category) }.distinct() }
    // Latest name per PC, in the order they last sent something.
    val pcs = remember(entries) { entries.distinctBy { it.pcId }.map { it.pcId to nameOf(it.pcId, it.pcName) } }
    // A filter whose last entry was deleted would hide everything with its chip row (and "전체") gone: drop it.
    LaunchedEffect(groups, pcs) {
        if (groupFilter != null && groupFilter !in groups) groupFilter = null
        if (pcFilter != null && pcs.none { it.first == pcFilter }) pcFilter = null
    }
    val shown = remember(entries, groupFilter, pcFilter) {
        entries.filter { e ->
            (groupFilter == null || PushHistoryRules.categoryLabel(e.category) == groupFilter) &&
                (pcFilter == null || e.pcId == pcFilter)
        }
    }
    val zone = remember { ZoneId.systemDefault() }
    val now = remember(entries) { System.currentTimeMillis() }
    val byDay = remember(shown, now) {
        shown.groupBy { PushHistoryRules.dayOf(it.receivedAt, zone) }.toList().map { (day, dayEntries) ->
            Triple(day, dayEntries, PushHistoryRules.stack(dayEntries) { rules.resolve(it.category, it.source).bundle })
        }
    }

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
            fun isUnread(e: HistoryEntry) = seenBefore.let { it != null && e.receivedAt > it }
            byDay.forEach { (day, dayEntries, rows) ->
                item(key = "day:$day") {
                    SectionLabel(
                        PushHistoryRules.dayLabel(dayEntries.first().receivedAt, now, zone),
                        trailing = "${dayEntries.size}건",
                    )
                }
                rows.forEach { row ->
                    val stackKey = "stack:$day:${row.first().bundleKey}"
                    val open = row.size == 1 || openStack == stackKey
                    if (row.size > 1) {
                        item(key = stackKey) {
                            StackCard(
                                entries = row,
                                unread = row.any(::isUnread),
                                open = open,
                                pcName = nameOf(row.first().pcId, row.first().pcName),
                                onToggle = { openStack = if (open) null else stackKey },
                                zone = zone,
                            )
                            Spacer(Modifier.height(if (open) 6.dp else 10.dp))
                        }
                    }
                    if (open) {
                        items(row, key = { it.key }) { e ->
                            HistoryCard(
                                entry = e,
                                unread = isUnread(e),
                                expanded = expanded == e.key,
                                pcName = nameOf(e.pcId, e.pcName),
                                canOpenPc = e.pcId in paired,
                                onToggle = { expanded = if (expanded == e.key) null else e.key },
                                onOpenPc = { onOpenPc(e.pcId) },
                                onDelete = {
                                    PushHistory.remove(context, e)
                                    if (expanded == e.key) expanded = null
                                },
                                zone = zone,
                                // Entries of an opened bundle sit indented under it.
                                modifier = if (row.size > 1) Modifier.padding(start = 14.dp) else Modifier,
                            )
                            Spacer(Modifier.height(if (row.size > 1 && e !== row.last()) 6.dp else 10.dp))
                        }
                    }
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
    pcName: String,
    canOpenPc: Boolean,
    onToggle: () -> Unit,
    onOpenPc: () -> Unit,
    onDelete: () -> Unit,
    zone: ZoneId,
    modifier: Modifier = Modifier,
) {
    val hb = Hb.colors
    val time = remember(entry.receivedAt) { TIME.format(Instant.ofEpochMilli(entry.receivedAt).atZone(zone)) }
    val source = sourceLabel(entry)
    // The header line already names the source; the PC sends titles as "카카오톡: 홍길동", so drop the repeat.
    val title = if (expanded) entry.title else entry.title.removePrefix("${entry.source}: ").ifBlank { entry.title }
    Plate(
        modifier = modifier,
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
                    // The tag already names the kind; repeat the source only when it adds something (an app, a monitor).
                    if (source == PushHistoryRules.categoryLabel(entry.category)) pcName else "$pcName · $source",
                    style = HbType.label,
                    color = hb.inkSoft,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.weight(1f),
                )
                Spacer(Modifier.width(8.dp))
                Text(time, style = HbType.readout.copy(fontSize = 12.sp), color = hb.inkSoft)
            }
            // Collapsed cards stay short (one title line, two body lines) so a busy day doesn't scroll forever.
            Text(
                title,
                style = MaterialTheme.typography.titleSmall.copy(fontWeight = FontWeight.Bold),
                color = hb.ink,
                maxLines = if (expanded) Int.MAX_VALUE else 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.padding(top = 6.dp),
            )
            entry.body?.let {
                Text(
                    it,
                    style = MaterialTheme.typography.bodyMedium,
                    color = hb.inkSoft,
                    maxLines = if (expanded) Int.MAX_VALUE else 2,
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
            if (entry.quiet) {
                Text(
                    "알림 우선순위가 '센터에만'이라 알림 창에는 띄우지 않았습니다",
                    style = MaterialTheme.typography.bodySmall,
                    color = hb.inkSoft,
                    modifier = Modifier.padding(top = 4.dp),
                )
            } else if (!entry.shown) {
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

/** An app for forwarded notifications, a Korean label for alert kinds. */
private fun sourceLabel(entry: HistoryEntry): String =
    if (NotificationPriority.hasApps(entry.category)) entry.source else PushNotifier.CATEGORY_LABELS[entry.source] ?: entry.source

/** Entries a closed bundle previews, one line each. */
private const val STACK_PREVIEW = 3

/**
 * Same-source entries of one day folded into one card (DECISIONS M-37): the newest few as one line
 * each and how many there are. Tapping it lists them all, indented, below it.
 */
@Composable
private fun StackCard(
    entries: List<HistoryEntry>,
    unread: Boolean,
    open: Boolean,
    pcName: String,
    onToggle: () -> Unit,
    zone: ZoneId,
) {
    val hb = Hb.colors
    val newest = entries.first()
    val time = remember(newest.receivedAt) { TIME.format(Instant.ofEpochMilli(newest.receivedAt).atZone(zone)) }
    val source = sourceLabel(newest)
    val kind = PushHistoryRules.categoryLabel(newest.category)
    Plate(
        rail = unread,
        onClick = onToggle,
        onClickLabel = if (open) "묶음 접기" else "묶음 펼치기",
    ) {
        Column(Modifier.padding(horizontal = 14.dp, vertical = 12.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Tag(
                    kind,
                    background = if (unread) hb.accent else hb.slabRaised,
                    foreground = if (unread) Brand.Carbon else hb.onSlab,
                )
                Spacer(Modifier.width(8.dp))
                Text(
                    if (source == kind) pcName else "$pcName · $source",
                    style = HbType.label,
                    color = hb.inkSoft,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.weight(1f),
                )
                Spacer(Modifier.width(8.dp))
                Text(time, style = HbType.readout.copy(fontSize = 12.sp), color = hb.inkSoft)
            }
            if (!open) {
                entries.take(STACK_PREVIEW).forEachIndexed { i, e ->
                    Text(
                        NotificationPriority.shortLine(e.title, e.body, e.source),
                        style = if (i == 0) {
                            MaterialTheme.typography.bodyMedium.copy(fontWeight = FontWeight.SemiBold)
                        } else {
                            MaterialTheme.typography.bodyMedium
                        },
                        color = if (i == 0) hb.ink else hb.inkSoft,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.padding(top = if (i == 0) 6.dp else 2.dp),
                    )
                }
            }
            Row(Modifier.padding(top = 6.dp), verticalAlignment = Alignment.CenterVertically) {
                Text(
                    when {
                        open -> "${entries.size}건 · 접기"
                        entries.size > STACK_PREVIEW -> "외 ${entries.size - STACK_PREVIEW}건 · 모두 ${entries.size}건 보기"
                        else -> "${entries.size}건 모두 보기"
                    },
                    style = MaterialTheme.typography.bodySmall.copy(fontWeight = FontWeight.SemiBold),
                    color = hb.ink,
                    modifier = Modifier.weight(1f),
                )
                Icon(
                    if (open) Icons.Filled.KeyboardArrowUp else Icons.Filled.KeyboardArrowDown,
                    contentDescription = null,
                    tint = hb.inkSoft,
                    modifier = Modifier.size(20.dp),
                )
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
    // IconButton clips its content to a 40dp circle, so a badge drawn inside it and pushed past the
    // bell loses its top-right corner to the arc. Draw it as a sibling over the 48dp touch target
    // instead; it has no pointer input, so taps on it still reach the button underneath.
    Box {
        IconButton(
            onClick = onClick,
            modifier = Modifier.semantics {
                contentDescription = if (unread > 0) "알림 센터 — 새 알림 ${unread}건" else "알림 센터"
            },
        ) {
            Icon(Icons.Filled.Notifications, contentDescription = null, tint = hb.onSlab)
        }
        if (unread > 0) {
            // Badge keeps a 16dp minimum on both axes and centres its text: a circle for one digit,
            // a pill for "99+". Anchored by its start so longer counts grow away from the bell.
            Badge(
                containerColor = hb.accent,
                contentColor = Brand.Carbon,
                modifier = Modifier
                    .align(Alignment.TopStart)
                    .offset(x = 24.dp, y = 6.dp)
                    .clearAndSetSemantics {},
            ) {
                Text(
                    if (unread > 99) "99+" else unread.toString(),
                    style = HbType.readout.copy(fontSize = 10.sp),
                    maxLines = 1,
                )
            }
        }
    }
}

private val TIME: DateTimeFormatter = DateTimeFormatter.ofPattern("HH:mm")
private val FULL: DateTimeFormatter = DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm:ss")
