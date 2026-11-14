package io.github.jeiel85.homebase.ui

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import io.github.jeiel85.homebase.data.PcStore
import io.github.jeiel85.homebase.net.HomebaseApi
import io.github.jeiel85.homebase.push.NotificationGroups
import io.github.jeiel85.homebase.push.PushRegistrar
import io.github.jeiel85.homebase.push.PushState
import kotlinx.coroutines.launch

/**
 * "받을 알림": which kinds of PC notification come to this phone (DECISIONS M-25). One switch per
 * [NotificationGroups] group plus two presets. The choice applies to every paired PC and is sent with
 * the push subscription, so the PC does not send what is off (and spends none of the relay's daily
 * quota on it). Muting single apps or single PCs stays in Android's notification settings.
 */
@Composable
fun NotificationCategories(store: PcStore, api: HomebaseApi) {
    val context = LocalContext.current
    val hb = Hb.colors
    val state = remember(context) { PushState(context) }
    var muted by remember { mutableStateOf(state.mutedCategories) }

    fun apply(next: Set<String>) {
        if (next == muted) return
        state.mutedCategories = next
        muted = next
        // Tell every PC now; one that is offline hears it on the next contact (the sync key includes the choice).
        PushRegistrar.forgetSynced()
        val app = context.applicationContext
        PushRegistrar.scope.launch { PushRegistrar.ensureAll(app, api, store) }
    }

    Column(Modifier.padding(vertical = 6.dp)) {
        Text("받을 알림", style = MaterialTheme.typography.titleSmall, color = hb.ink, modifier = Modifier.padding(top = 6.dp))
        Text(
            "끈 종류는 PC가 이 휴대폰으로 보내지 않습니다. 앱 하나나 PC 하나만 끄려면 Android 알림 설정에서 끕니다.",
            style = MaterialTheme.typography.bodySmall,
            color = hb.inkSoft,
        )
        Row {
            TextButton(onClick = { apply(emptySet()) }) { Text("모두 받기") }
            TextButton(onClick = { apply(NotificationGroups.ALL_BUT_TOASTS) }) { Text("앱 알림만") }
        }
        NotificationGroups.ALL.forEach { group ->
            val on = group.categories.none { it in muted }
            SettingsSwitchRow(
                title = group.label,
                description = group.description,
                checked = on,
                onCheckedChange = { checked ->
                    apply(if (checked) muted - group.categories.toSet() else muted + group.categories)
                },
            )
        }
    }
}
