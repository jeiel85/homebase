package io.github.jeiel85.homebase.ui

import android.widget.Toast
import androidx.compose.foundation.background
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.interaction.DragInteraction
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
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.KeyboardArrowDown
import androidx.compose.material.icons.filled.KeyboardArrowUp
import androidx.compose.material.icons.filled.Notifications
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.SnackbarDuration
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.SnackbarResult
import androidx.compose.material3.SwipeToDismissBox
import androidx.compose.material3.SwipeToDismissBoxValue
import androidx.compose.material3.rememberSwipeToDismissBoxState
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
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.CustomAccessibilityAction
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.customActions
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.jeiel85.homebase.data.PcMark
import io.github.jeiel85.homebase.data.PcStore
import io.github.jeiel85.homebase.push.CommandHistory
import io.github.jeiel85.homebase.clip.PhoneClipboard
import io.github.jeiel85.homebase.push.ClipHistory
import io.github.jeiel85.homebase.push.FileHistory
import io.github.jeiel85.homebase.push.HistoryEntry
import io.github.jeiel85.homebase.push.NotificationGroups
import io.github.jeiel85.homebase.push.NotificationLook
import io.github.jeiel85.homebase.push.NotificationPriority
import io.github.jeiel85.homebase.push.NotificationRuleStore
import io.github.jeiel85.homebase.push.PushHistory
import io.github.jeiel85.homebase.push.PushHistoryRules
import io.github.jeiel85.homebase.push.PushNotifier
import io.github.jeiel85.homebase.push.PushState
import io.github.jeiel85.homebase.share.ReceivedStore
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
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
fun NotificationCenterScreen(
    store: PcStore,
    onBack: () -> Unit,
    onOpenPc: (String) -> Unit,
    onSettings: () -> Unit,
    focus: CenterFocus? = null,
    onFocused: () -> Unit = {},
) {
    val hb = Hb.colors
    val context = LocalContext.current
    val history by PushHistory.state.collectAsState()
    // What counted as read when the screen opened; null until the file is loaded.
    var seenBefore by remember { mutableStateOf<Long?>(null) }
    // The history file could not be read and started over (said once, #41).
    var restarted by remember { mutableStateOf(false) }
    LaunchedEffect(Unit) {
        restarted = withContext(Dispatchers.IO) { PushHistory.takeRestartNotice(context) }
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
    val listState = rememberLazyListState()
    val rules = remember { NotificationRuleStore(context).rules() }
    var confirmClear by remember { mutableStateOf(false) }
    val pushOn = remember { PushState(context).endpoint != null }
    // Paired PCs; an entry is drawn under the PC's alias (M-36), else the name it was sent with.
    val paired = remember { store.all().associateBy { it.pcId } }
    fun nameOf(pcId: String, sent: String) = paired[pcId]?.shownName(sent) ?: sent
    // The badge its Android notification carried (M-38), in the colour it wore then (#57).
    val pcColors = remember(paired) { paired.values.mapNotNull { it.color } }
    fun markOf(e: HistoryEntry) =
        NotificationLook.mark(e.category, e.source, e.pcId, nameOf(e.pcId, e.pcName), paired[e.pcId], e.color, pcColors)

    val entries = history.entries
    val groups = remember(entries) { entries.map { PushHistoryRules.categoryLabel(it.category) }.distinct() }
    // Latest name per PC, in the order they last sent something.
    val pcs = remember(entries) { entries.distinctBy { it.pcId }.map { it.pcId to nameOf(it.pcId, it.pcName) } }
    // A filter whose last entry was deleted would hide everything with its chip row (and "전체") gone: drop it.
    LaunchedEffect(groups, pcs) {
        if (groupFilter != null && groupFilter !in groups) groupFilter = null
        if (pcFilter != null && pcs.none { it.first == pcFilter }) pcFilter = null
    }
    // The entry a tapped notification opened, scrolled to once the list is laid out with its filter (#109).
    var scrollTo by remember { mutableStateOf<String?>(null) }
    // A transfer's notification tapped while it was still under way: its entry, opened when it is written (#109).
    var awaitedCategory by rememberSaveable { mutableStateOf<String?>(null) }
    var awaitedId by rememberSaveable { mutableStateOf<String?>(null) }
    var awaitedPc by rememberSaveable { mutableStateOf<String?>(null) }
    // The user took over (picked a filter, opened an entry or a stack, dragged the list): a transfer that finishes
    // later doesn't move the screen away from what they are reading.
    fun stopAwaiting() {
        awaitedCategory = null
        awaitedId = null
        awaitedPc = null
    }
    LaunchedEffect(listState) {
        listState.interactionSource.interactions.collect { if (it is DragInteraction.Start) stopAwaiting() }
    }
    // A tapped upload notification: once the history is loaded (an empty list would drop the filter), its kind's
    // filter and its entry open.
    LaunchedEffect(focus, seenBefore) {
        val wanted = focus ?: return@LaunchedEffect
        if (seenBefore == null) return@LaunchedEffect
        val view = viewOf(PushHistory.state.value.entries, wanted.category, wanted.id, wanted.pcId)
        groupFilter = view.group
        pcFilter = null
        openStack = null
        expanded = view.expanded
        awaitedCategory = if (view.expanded == null && wanted.id != null) wanted.category else null
        awaitedId = if (awaitedCategory != null) wanted.id else null
        awaitedPc = if (awaitedCategory != null) wanted.pcId else null
        if (view.expanded != null) scrollTo = view.expanded else listState.scrollToItem(0)
        // Last: taking the focus restarts this effect, which would cut the scroll short.
        onFocused()
    }
    // The awaited transfer's entry came in while the center is open: its kind's filter, it open and in view.
    LaunchedEffect(history.entries, awaitedId) {
        val category = awaitedCategory ?: return@LaunchedEffect
        val id = awaitedId ?: return@LaunchedEffect
        val view = viewOf(history.entries, category, id, awaitedPc)
        val key = view.expanded ?: return@LaunchedEffect
        groupFilter = view.group
        pcFilter = null
        openStack = null
        expanded = key
        scrollTo = key
        awaitedCategory = null
        awaitedId = null
        awaitedPc = null
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
            // Each transfer and each command sent is a card of its own: they are not bundled like notifications of one source.
            Triple(day, dayEntries, PushHistoryRules.stack(dayEntries) { !isOwnRecord(it.category) && rules.resolve(it.category, it.source).bundle })
        }
    }
    // What the list starts with before the days, in [LazyColumn]'s order: the restart notice, the empty notice, the
    // chips and the "no match" line. [scrollTo] counts from them.
    val showRestarted = restarted
    val showEmpty = seenBefore != null && entries.isEmpty()
    val showChips = groups.size > 1 || pcs.size > 1
    val showNoMatch = entries.isNotEmpty() && shown.isEmpty()
    val leading = listOf(showRestarted, showEmpty, showChips, showNoMatch).count { it }
    LaunchedEffect(scrollTo, byDay, openStack) {
        val key = scrollTo ?: return@LaunchedEffect
        // A tapped push may sit in a bundle (M-37): unfold it first, so the entry itself is shown open (M-81).
        CenterLayout.stackOf(key, byDay.map { (day, _, rows) -> rows.map { it to "stack:$day:${it.first().bundleKey}" } })
            ?.takeIf { it != openStack }
            ?.let {
                openStack = it
                return@LaunchedEffect // runs again with the bundle open
            }
        CenterLayout.indexOf(key, leading, byDay.map { (day, _, rows) -> rows.map { it to "stack:$day:${it.first().bundleKey}" } }, openStack)
            ?.let { listState.scrollToItem(it) }
        scrollTo = null
    }
    val scope = rememberCoroutineScope()
    // A received file entry opens the first of its files that is still there (docs/SHARE_CONVENIENCE.md §4.5).
    fun openFiles(e: HistoryEntry) {
        scope.launch {
            val uri = withContext(Dispatchers.IO) { ReceivedStore.firstExisting(context, e.uris) }
            val message = when {
                uri == null -> "파일이 없습니다 — 지워졌거나 옮겨졌습니다"
                !ReceivedStore.view(context, uri) -> "이 파일을 열 앱이 없습니다"
                else -> null
            }
            message?.let { Toast.makeText(context, it, Toast.LENGTH_SHORT).show() }
        }
    }

    // A kept text goes back on the clipboard (M-70); sensitive text was never kept, so it has no button.
    fun copyText(e: HistoryEntry) {
        val text = e.body ?: return
        val message = if (PhoneClipboard.write(context, text, sensitive = false)) "클립보드에 복사했습니다" else "클립보드에 넣지 못했습니다"
        Toast.makeText(context, message, Toast.LENGTH_SHORT).show()
    }

    // A delete says what went and offers it back for a few seconds (DECISIONS M-81): a swipe is easy to make by mistake.
    val snackbar = remember { SnackbarHostState() }
    fun delete(gone: List<HistoryEntry>) {
        if (gone.isEmpty()) return
        PushHistory.removeAll(context, gone)
        if (gone.any { it.key == expanded }) expanded = null
        scope.launch {
            snackbar.currentSnackbarData?.dismiss()
            val result = snackbar.showSnackbar(
                if (gone.size == 1) "알림을 지웠습니다" else "알림 ${gone.size}건을 지웠습니다",
                actionLabel = "되돌리기",
                duration = SnackbarDuration.Short,
            )
            if (result == SnackbarResult.ActionPerformed) PushHistory.restore(context, gone)
        }
    }
    // With a kind or a PC picked, the bin empties just what is shown (M-81); otherwise everything.
    val filterLabel = listOfNotNull(groupFilter, pcFilter?.let { id -> pcs.firstOrNull { it.first == id }?.second }).joinToString(" · ")
    val clearsShown = filterLabel.isNotEmpty()
    val canClear = if (clearsShown) shown.isNotEmpty() else entries.isNotEmpty()

    Box(Modifier.fillMaxSize()) {
        Column(Modifier.fillMaxSize().background(hb.background)) {
            ConsoleBar(
                title = "알림 센터",
                onBack = onBack,
                eyebrow = "받은 푸시 알림",
                actions = {
                    IconButton(onClick = { confirmClear = true }, enabled = canClear) {
                        Icon(
                            Icons.Filled.Delete,
                            contentDescription = if (clearsShown) "'$filterLabel' 알림 비우기" else "알림 이력 모두 지우기",
                            tint = if (canClear) hb.onSlab else hb.onSlabSoft.copy(alpha = 0.4f),
                        )
                    }
                },
            )
            LazyColumn(
                state = listState,
                modifier = Modifier.fillMaxSize(),
                contentPadding = PaddingValues(horizontal = 14.dp),
            ) {
                if (showRestarted) {
                    item {
                        Text(
                            "이전 알림 이력을 읽지 못해 새로 시작했습니다.",
                            style = MaterialTheme.typography.bodySmall,
                            color = hb.inkSoft,
                            modifier = Modifier.padding(top = 12.dp),
                        )
                    }
                }
                if (showEmpty) {
                    item { EmptyCenter(pushOn, onSettings) }
                }
                if (showChips) {
                    item {
                        Column(Modifier.padding(top = 12.dp)) {
                            if (groups.size > 1) {
                                ChipRow(
                                    options = groups.map { it to it },
                                    selected = groupFilter,
                                    onSelect = {
                                        groupFilter = it
                                        stopAwaiting()
                                    },
                                )
                            }
                            if (pcs.size > 1) {
                                ChipRow(options = pcs, selected = pcFilter, onSelect = {
                                    pcFilter = it
                                    stopAwaiting()
                                })
                            }
                        }
                    }
                }
                if (showNoMatch) {
                    item {
                        Text(
                            "고른 조건에 맞는 알림이 없습니다.",
                            style = MaterialTheme.typography.bodyMedium,
                            color = hb.inkSoft,
                            modifier = Modifier.padding(top = 24.dp),
                        )
                    }
                }
                fun isUnread(e: HistoryEntry) = seenBefore.let { it != null && PushHistoryRules.isUnread(e, it) }
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
                                SwipeToDelete(label = "묶음 ${row.size}건 지우기", onDelete = { delete(row) }) {
                                    StackCard(
                                        entries = row,
                                        unread = row.any(::isUnread),
                                        open = open,
                                        pcName = nameOf(row.first().pcId, row.first().pcName),
                                        mark = markOf(row.first()),
                                        onToggle = {
                                            openStack = if (open) null else stackKey
                                            stopAwaiting()
                                        },
                                        zone = zone,
                                    )
                                }
                                Spacer(Modifier.height(if (open) 6.dp else 10.dp))
                            }
                        }
                        if (open) {
                            items(row, key = { it.key }) { e ->
                                // Entries of an opened bundle sit indented under it.
                                SwipeToDelete(
                                    label = "이 알림 지우기",
                                    onDelete = { delete(listOf(e)) },
                                    modifier = if (row.size > 1) Modifier.padding(start = 14.dp) else Modifier,
                                ) {
                                HistoryCard(
                                    entry = e,
                                    unread = isUnread(e),
                                    expanded = expanded == e.key,
                                    pcName = nameOf(e.pcId, e.pcName),
                                    mark = markOf(e),
                                    canOpenPc = e.pcId in paired,
                                    onToggle = {
                                        expanded = if (expanded == e.key) null else e.key
                                        stopAwaiting()
                                    },
                                    onOpenPc = { onOpenPc(e.pcId) },
                                    onOpenFiles = if (e.category == FileHistory.RECEIVED && e.uris.isNotEmpty()) {
                                        { openFiles(e) }
                                    } else {
                                        null
                                    },
                                    onCopy = if (ClipHistory.isClip(e.category) && e.body != null) {
                                        { copyText(e) }
                                    } else {
                                        null
                                    },
                                    onDelete = { delete(listOf(e)) },
                                    zone = zone,
                                )
                                }
                                Spacer(Modifier.height(if (row.size > 1 && e !== row.last()) 6.dp else 10.dp))
                            }
                        }
                    }
                }
                if (entries.isNotEmpty()) {
                    item {
                        Text(
                            "카드를 옆으로 밀면 지웁니다(묶음은 통째로). " +
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
        SnackbarHost(snackbar, Modifier.align(Alignment.BottomCenter).navigationBarsPadding().padding(12.dp))
    }

    if (confirmClear) {
        AlertDialog(
            onDismissRequest = { confirmClear = false },
            title = { Text(if (clearsShown) "알림 비우기" else "알림 이력 지우기") },
            text = {
                Text(
                    if (clearsShown) {
                        "'$filterLabel' 알림 ${shown.size}건을 이 휴대폰에서 지울까요? 다른 알림은 그대로 둡니다. " +
                            "PC와 Android 알림 창에는 영향이 없습니다."
                    } else {
                        "받은 알림 ${entries.size}건을 이 휴대폰에서 모두 지울까요? 되돌릴 수 없습니다. PC와 Android 알림 창에는 영향이 없습니다."
                    },
                )
            },
            confirmButton = {
                TextButton(onClick = {
                    if (clearsShown) {
                        delete(shown)
                    } else {
                        PushHistory.clear(context)
                        expanded = null
                        groupFilter = null
                        pcFilter = null
                    }
                    confirmClear = false
                }) { Text(if (clearsShown) "비우기" else "모두 지우기") }
            },
            dismissButton = { TextButton(onClick = { confirmClear = false }) { Text("취소") } },
        )
    }
}

/**
 * A card that is deleted by swiping it either way (DECISIONS M-81), over a red strip saying so. [label] also names the
 * delete as an accessibility action, for those who cannot swipe.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun SwipeToDelete(label: String, onDelete: () -> Unit, modifier: Modifier = Modifier, content: @Composable () -> Unit) {
    val hb = Hb.colors
    val current by rememberUpdatedState(onDelete)
    val state = rememberSwipeToDismissBoxState(
        confirmValueChange = { value ->
            if (value == SwipeToDismissBoxValue.Settled) {
                false
            } else {
                current()
                true
            }
        },
    )
    SwipeToDismissBox(
        state = state,
        modifier = modifier.semantics {
            customActions = listOf(
                CustomAccessibilityAction(label) {
                    current()
                    true
                },
            )
        },
        backgroundContent = {
            val fromStart = state.dismissDirection == SwipeToDismissBoxValue.StartToEnd
            Box(
                Modifier.fillMaxSize().clip(PlateShape).background(hb.danger).padding(horizontal = 18.dp),
                contentAlignment = if (fromStart) Alignment.CenterStart else Alignment.CenterEnd,
            ) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Icon(Icons.Filled.Delete, contentDescription = null, tint = Color.White)
                    Spacer(Modifier.width(6.dp))
                    Text("지우기", color = Color.White, style = HbType.label)
                }
            }
        },
    ) { content() }
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
    mark: PcMark,
    canOpenPc: Boolean,
    onToggle: () -> Unit,
    onOpenPc: () -> Unit,
    onDelete: () -> Unit,
    zone: ZoneId,
    modifier: Modifier = Modifier,
    /** A received file entry: opens its file (shown folded too, so one tap opens it). */
    onOpenFiles: (() -> Unit)? = null,
    /** A clipboard entry with its text kept: copies it again (shown folded too). */
    onCopy: (() -> Unit)? = null,
) {
    val hb = Hb.colors
    val time = remember(entry.receivedAt) { TIME.format(Instant.ofEpochMilli(entry.receivedAt).atZone(zone)) }
    val kind = PushHistoryRules.categoryLabel(entry.category)
    // A file transfer, a text or a command this phone recorded: nothing about the shade applies.
    val file = isOwnRecord(entry.category)
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
                PcBadge(mark, size = 18.dp)
                Spacer(Modifier.width(6.dp))
                Tag(
                    kind,
                    background = if (unread) hb.accent else hb.slabRaised,
                    foreground = if (unread) Brand.Carbon else hb.onSlab,
                )
                Spacer(Modifier.width(8.dp))
                Text(
                    headerLine(entry, pcName, kind),
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
            // A file entry is a transfer, not a push: there is no shade it could have been kept out of.
            if (!file && entry.quiet) {
                Text(
                    "알림 우선순위가 '센터에만'이라 알림 창에는 띄우지 않았습니다",
                    style = MaterialTheme.typography.bodySmall,
                    color = hb.inkSoft,
                    modifier = Modifier.padding(top = 4.dp),
                )
            } else if (!file && !entry.shown) {
                Text(
                    "Android 알림이 꺼져 있어 알림 창에는 뜨지 않았습니다",
                    style = MaterialTheme.typography.bodySmall,
                    color = hb.danger,
                    modifier = Modifier.padding(top = 4.dp),
                )
            }
            if (expanded) {
                Row(Modifier.padding(top = 4.dp), verticalAlignment = Alignment.CenterVertically) {
                    val at = FULL.format(Instant.ofEpochMilli(entry.sentAt).atZone(zone))
                    Text(
                        when (entry.category) {
                            FileHistory.RECEIVED, ClipHistory.RECEIVED -> "받음 $at"
                            FileHistory.SENT, CommandHistory.SENT, ClipHistory.SENT -> "보냄 $at"
                            else -> "PC 발송 $at"
                        },
                        style = MaterialTheme.typography.bodySmall,
                        color = hb.inkSoft,
                        modifier = Modifier.weight(1f),
                    )
                    if (onOpenFiles != null) TextButton(onClick = onOpenFiles) { Text("열기") }
                    if (onCopy != null) TextButton(onClick = onCopy) { Text("복사") }
                    if (canOpenPc) TextButton(onClick = onOpenPc) { Text("PC 열기") }
                    TextButton(onClick = onDelete) { Text("삭제") }
                }
            } else if (onOpenFiles != null || onCopy != null) {
                Row(Modifier.fillMaxWidth().padding(top = 2.dp), horizontalArrangement = Arrangement.End) {
                    if (onOpenFiles != null) TextButton(onClick = onOpenFiles) { Text("열기") }
                    if (onCopy != null) TextButton(onClick = onCopy) { Text("복사") }
                }
            }
        }
    }
}

/**
 * Who sent it, then what: "작업실 · 카카오톡", or for a relayed notification the device first and the
 * PC it came through last. The tag already names the kind, so a monitor's name is repeated only when
 * it adds something.
 */
private fun headerLine(entry: HistoryEntry, pcName: String, kind: String): String {
    if (FileHistory.isFile(entry.category)) return FileHistory.header(entry, pcName)
    if (ClipHistory.isClip(entry.category)) return ClipHistory.header(entry, pcName)
    if (CommandHistory.isCommand(entry.category)) return pcName
    val source = sourceLabel(entry)
    return when {
        entry.category == NotificationGroups.DEVICE -> "${entry.source} · $pcName 경유"
        source == kind -> pcName
        else -> "$pcName · $source"
    }
}

/**
 * What the notification center opens on: the kind [category] filtered, with the entry [id] of it open; or, for a tapped
 * push ([pcId] set, M-81), everything, with that PC's entry [id] open and its bundle unfolded.
 */
data class CenterFocus(val category: String, val id: String?, val pcId: String? = null)

/** How the center opens for a focus: a tapped push's entry ([pcId] set, M-81), or a kind's filter with its entry. */
private fun viewOf(entries: List<HistoryEntry>, category: String, id: String?, pcId: String?): PushHistoryRules.Focus =
    if (pcId != null) PushHistoryRules.focusEntry(entries, pcId, id) else PushHistoryRules.focus(entries, category, id)

/** Where an entry sits in the notification center's list — pure, so tests pin it (#109). */
internal object CenterLayout {
    /** The bundle key of the row holding entry [key] when that row is a bundle of several, else null. */
    fun stackOf(key: String, days: List<List<Pair<List<HistoryEntry>, String>>>): String? =
        days.asSequence().flatten().firstOrNull { (row, _) -> row.size > 1 && row.any { it.key == key } }?.second

    /**
     * The list index of the entry [key], or its bundle's card when that bundle is closed; null when it isn't shown.
     * [leading]: items before the first day. [days]: each day's rows (newest first) with the row's bundle key, laid
     * out as the screen does — a day heading, then per row a bundle card when it has more than one entry, and its
     * entries when it has one or is [openStack].
     */
    fun indexOf(key: String, leading: Int, days: List<List<Pair<List<HistoryEntry>, String>>>, openStack: String?): Int? {
        var i = leading
        days.forEach { rows ->
            i++ // the day's heading
            rows.forEach { (row, stackKey) ->
                val open = row.size == 1 || openStack == stackKey
                val at = row.indexOfFirst { it.key == key }
                if (row.size > 1) {
                    if (at >= 0 && !open) return i
                    i++ // the bundle card
                }
                if (open) {
                    if (at >= 0) return i + at
                    i += row.size
                }
            }
        }
        return null
    }
}

/** Entries this phone made itself — files and text received and sent, commands sent — rather than pushes it was sent. */
private fun isOwnRecord(category: String): Boolean =
    FileHistory.isFile(category) || ClipHistory.isClip(category) || CommandHistory.isCommand(category)

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
    mark: PcMark,
    onToggle: () -> Unit,
    zone: ZoneId,
) {
    val hb = Hb.colors
    val newest = entries.first()
    val time = remember(newest.receivedAt) { TIME.format(Instant.ofEpochMilli(newest.receivedAt).atZone(zone)) }
    val kind = PushHistoryRules.categoryLabel(newest.category)
    Plate(
        rail = unread,
        onClick = onToggle,
        onClickLabel = if (open) "묶음 접기" else "묶음 펼치기",
    ) {
        Column(Modifier.padding(horizontal = 14.dp, vertical = 12.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                PcBadge(mark, size = 18.dp)
                Spacer(Modifier.width(6.dp))
                Tag(
                    kind,
                    background = if (unread) hb.accent else hb.slabRaised,
                    foreground = if (unread) Brand.Carbon else hb.onSlab,
                )
                Spacer(Modifier.width(8.dp))
                Text(
                    headerLine(newest, pcName, kind),
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
