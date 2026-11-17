package io.github.jeiel85.homebase.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.selectableGroup
import androidx.compose.foundation.selection.toggleable
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.RadioButton
import androidx.compose.material3.RadioButtonDefaults
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import io.github.jeiel85.homebase.push.AlertLevel
import io.github.jeiel85.homebase.push.AlertProfile
import io.github.jeiel85.homebase.push.NotificationGroup
import io.github.jeiel85.homebase.push.NotificationGroups
import io.github.jeiel85.homebase.push.NotificationPriority
import io.github.jeiel85.homebase.push.NotificationRuleStore
import io.github.jeiel85.homebase.push.NotificationRules
import io.github.jeiel85.homebase.push.PushHistory
import io.github.jeiel85.homebase.push.PushHistoryRules
import io.github.jeiel85.homebase.push.PushNotifier
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/** What the editor dialog is open for. */
private sealed interface Editing {
    data class Group(val group: NotificationGroup) : Editing
    data class App(val app: NotificationPriority.AppSource) : Editing
}

/**
 * Settings → 알림 우선순위 (DECISIONS M-37): how each kind of notification — and, for app
 * notifications, each app — appears on this phone: popup, sound, vibration, bundled or one by one,
 * or kept in the notification center only. Applies from the next notification; the PC is not told.
 */
@Composable
fun NotificationPriorityScreen(onBack: () -> Unit) {
    val hb = Hb.colors
    val context = LocalContext.current
    val store = remember { NotificationRuleStore(context) }
    var rules by remember { mutableStateOf(store.rules()) }
    var labels by remember { mutableStateOf(store.appLabels()) }
    val history by PushHistory.state.collectAsState()
    LaunchedEffect(Unit) { withContext(Dispatchers.IO) { PushHistory.ensureLoaded(context) } }
    val apps = remember(history.entries, labels) { NotificationPriority.apps(history.entries, labels) }
    var editing by remember { mutableStateOf<Editing?>(null) }
    var confirmReset by remember { mutableStateOf(false) }

    fun reload() {
        rules = store.rules()
        labels = store.appLabels()
        editing = null
    }

    Column(Modifier.fillMaxSize().background(hb.background)) {
        ConsoleBar(title = "알림 우선순위", onBack = onBack, eyebrow = "팝업·소리·진동·묶음")
        LazyColumn(
            modifier = Modifier.fillMaxSize(),
            contentPadding = PaddingValues(horizontal = 14.dp),
        ) {
            item {
                Text(
                    "PC가 보낸 알림이 이 휴대폰에 어떻게 뜰지 정합니다. 앱별로 정한 것이 종류별 설정보다 먼저 적용되고, " +
                        "바꾼 설정은 다음 알림부터 적용됩니다.",
                    style = MaterialTheme.typography.bodySmall,
                    color = hb.inkSoft,
                    modifier = Modifier.padding(top = 14.dp),
                )
            }
            item {
                SectionLabel("종류별")
                Plate {
                    Column(Modifier.padding(horizontal = 16.dp, vertical = 4.dp)) {
                        NotificationGroups.ALL.forEachIndexed { i, group ->
                            if (i > 0) HorizontalDivider(color = hb.hairline)
                            val chosen = rules.groups[group.key]
                            PriorityRow(
                                title = group.label,
                                tag = if (chosen == null) "기본값" else null,
                                detail = NotificationPriority.describe(chosen ?: NotificationPriority.defaultFor(group.key)),
                                onClick = { editing = Editing.Group(group) },
                            )
                        }
                    }
                }
            }
            item {
                SectionLabel("앱별", trailing = if (apps.isEmpty()) null else "${apps.size}개")
                if (apps.isEmpty()) {
                    Text(
                        "Windows 앱 알림이나 다른 기기 알림을 받으면 보낸 앱이 여기에 나옵니다(알림 센터의 최근 30일 기준).",
                        style = MaterialTheme.typography.bodySmall,
                        color = hb.inkSoft,
                    )
                } else {
                    Plate {
                        Column(Modifier.padding(horizontal = 16.dp, vertical = 4.dp)) {
                            apps.forEachIndexed { i, app ->
                                if (i > 0) HorizontalDivider(color = hb.hairline)
                                val own = rules.sources[app.key]
                                PriorityRow(
                                    title = app.label,
                                    tag = if (app.category == NotificationGroups.DEVICE) "다른 기기" else "Windows",
                                    detail = own?.let { "이 앱만: ${NotificationPriority.describe(it)}" }
                                        ?: "종류 설정 따름 — ${NotificationPriority.summary(groupProfileOf(app, rules))}",
                                    onClick = { editing = Editing.App(app) },
                                )
                            }
                        }
                    }
                }
            }
            item {
                Text(
                    "Android 알림 설정에서 끈 앱·PC는 여기서 바꿔도 꺼진 채로 둡니다. 여기서 우선순위를 바꾸면 그 앱의 " +
                        "Android 알림 채널을 새로 만들므로, Android 설정에서 따로 바꿔 둔 소리 같은 세부 설정은 처음 상태로 " +
                        "돌아갑니다. 묶은 알림은 PC·앱과 상관없이 알림 창의 Homebase 묶음 하나에 모이고, 거기에는 출처별로 최근 " +
                        "${PushNotifier.MAX_IN_BUNDLE}건(모두 합쳐 ${PushNotifier.MAX_IN_STACK}건)까지 두며, 전부는 알림 센터에 남습니다. " +
                        "PC 트레이의 '테스트' 알림은 경로 확인용이라 늘 팝업으로 뜹니다.",
                    style = MaterialTheme.typography.bodySmall,
                    color = hb.inkSoft,
                    modifier = Modifier.padding(top = 14.dp),
                )
                TextButton(onClick = { confirmReset = true }, modifier = Modifier.padding(top = 4.dp)) { Text("모두 기본값으로") }
                Spacer(Modifier.navigationBarsPadding().height(24.dp))
            }
        }
    }

    when (val e = editing) {
        is Editing.Group -> ProfileDialog(
            title = e.group.label,
            subtitle = e.group.description,
            initial = rules.groups[e.group.key] ?: NotificationPriority.defaultFor(e.group.key),
            resetLabel = "기본값으로",
            onReset = {
                store.setGroup(e.group.key, null)
                reload()
            },
            onSave = {
                store.setGroup(e.group.key, it)
                reload()
            },
            onDismiss = { editing = null },
        )
        is Editing.App -> ProfileDialog(
            title = e.app.label,
            subtitle = "${PushHistoryRules.categoryLabel(e.app.category)} 중 이 앱만 따로 정합니다. " +
                "지금 종류 설정: ${NotificationPriority.summary(groupProfileOf(e.app, rules))}",
            initial = rules.sources[e.app.key] ?: groupProfileOf(e.app, rules),
            resetLabel = "종류 설정 따르기",
            onReset = {
                store.setApp(e.app.key, e.app.label, null)
                reload()
            },
            onSave = {
                store.setApp(e.app.key, e.app.label, it)
                reload()
            },
            onDismiss = { editing = null },
        )
        null -> Unit
    }

    if (confirmReset) {
        AlertDialog(
            onDismissRequest = { confirmReset = false },
            title = { Text("모두 기본값으로") },
            text = { Text("종류별·앱별로 정한 알림 우선순위를 모두 지우고 기본값으로 돌릴까요?") },
            confirmButton = {
                TextButton(onClick = {
                    store.clear()
                    reload()
                    confirmReset = false
                }) { Text("기본값으로") }
            },
            dismissButton = { TextButton(onClick = { confirmReset = false }) { Text("취소") } },
        )
    }
}

/** The group's profile an app follows when it has no choice of its own. */
private fun groupProfileOf(app: NotificationPriority.AppSource, rules: NotificationRules): AlertProfile =
    rules.copy(sources = emptyMap()).resolve(app.category, app.label)

@Composable
private fun PriorityRow(title: String, tag: String?, detail: String, onClick: () -> Unit) {
    val hb = Hb.colors
    Column(
        Modifier
            .fillMaxWidth()
            .heightIn(min = 56.dp)
            .clickable(role = Role.Button, onClickLabel = "바꾸기", onClick = onClick)
            .padding(vertical = 10.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(
                title,
                style = MaterialTheme.typography.titleSmall,
                color = hb.ink,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.weight(1f, fill = false),
            )
            if (tag != null) {
                Spacer(Modifier.width(8.dp))
                Tag(tag, hb.hairline, hb.inkSoft)
            }
        }
        Text(detail, style = MaterialTheme.typography.bodySmall, color = hb.inkSoft)
    }
}

/**
 * Editor for one profile: a level (one tap), the three switches it sets for a combination of one's
 * own, and bundling, which the levels leave alone.
 */
@Composable
private fun ProfileDialog(
    title: String,
    subtitle: String,
    initial: AlertProfile,
    resetLabel: String,
    onReset: () -> Unit,
    onSave: (AlertProfile) -> Unit,
    onDismiss: () -> Unit,
) {
    val hb = Hb.colors
    var p by remember(initial) { mutableStateOf(initial) }
    val level = AlertLevel.of(p)
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(title, maxLines = 2, overflow = TextOverflow.Ellipsis) },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState())) {
                Text(subtitle, style = MaterialTheme.typography.bodySmall, color = hb.inkSoft)
                Column(Modifier.selectableGroup().padding(top = 6.dp)) {
                    AlertLevel.entries.forEach { l ->
                        Row(
                            Modifier
                                .fillMaxWidth()
                                .heightIn(min = 48.dp)
                                .selectable(selected = level == l, role = Role.RadioButton, onClick = { p = l.applyTo(p) }),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            RadioButton(
                                selected = level == l,
                                onClick = null,
                                colors = RadioButtonDefaults.colors(selectedColor = hb.accent),
                            )
                            Spacer(Modifier.width(12.dp))
                            Column {
                                Text(l.label, style = MaterialTheme.typography.titleSmall, color = hb.ink)
                                Text(l.description, style = MaterialTheme.typography.bodySmall, color = hb.inkSoft)
                            }
                        }
                    }
                }
                if (level == null) {
                    Text(
                        "사용자 지정 — 아래 스위치로 고른 조합입니다",
                        style = MaterialTheme.typography.bodySmall,
                        color = hb.ink,
                        modifier = Modifier.padding(top = 2.dp),
                    )
                }
                HorizontalDivider(color = hb.hairline, modifier = Modifier.padding(vertical = 6.dp))
                ToggleLine("화면 위에 띄우기 (팝업)", p.popup, enabled = p.shade) { p = p.copy(popup = it) }
                ToggleLine("소리", p.sound, enabled = p.shade) { p = p.copy(sound = it) }
                ToggleLine("진동", p.vibrate, enabled = p.shade) { p = p.copy(vibrate = it) }
                HorizontalDivider(color = hb.hairline, modifier = Modifier.padding(vertical = 6.dp))
                SettingsSwitchRow(
                    title = "같은 출처끼리 묶기",
                    description = "알림 창에서는 Homebase 묶음에 모으고, 알림 센터에서는 같은 출처끼리 한 묶음으로 접어 보여 줍니다. 끄면 한 건씩 따로 뜹니다.",
                    checked = p.bundle,
                    onCheckedChange = { p = p.copy(bundle = it) },
                )
            }
        },
        confirmButton = { TextButton(onClick = { onSave(p) }) { Text("저장") } },
        dismissButton = {
            Row {
                TextButton(onClick = onReset) { Text(resetLabel) }
                TextButton(onClick = onDismiss) { Text("취소") }
            }
        },
    )
}

/** A compact switch row for the dialog; the whole row toggles. */
@Composable
private fun ToggleLine(label: String, checked: Boolean, enabled: Boolean, onChange: (Boolean) -> Unit) {
    val hb = Hb.colors
    Row(
        Modifier
            .fillMaxWidth()
            .heightIn(min = 48.dp)
            .toggleable(value = checked, enabled = enabled, role = Role.Switch, onValueChange = onChange),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(
            label,
            style = MaterialTheme.typography.bodyMedium,
            color = if (enabled) hb.ink else hb.inkSoft,
            modifier = Modifier.weight(1f),
        )
        Spacer(Modifier.width(12.dp))
        Switch(
            checked = checked,
            onCheckedChange = null,
            enabled = enabled,
            colors = SwitchDefaults.colors(
                checkedTrackColor = hb.accent,
                checkedThumbColor = Brand.Carbon,
                checkedBorderColor = hb.accent,
            ),
        )
    }
}
