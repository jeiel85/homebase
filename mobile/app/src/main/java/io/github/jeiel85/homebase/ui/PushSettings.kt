package io.github.jeiel85.homebase.ui

import android.Manifest
import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.PowerManager
import android.provider.Settings
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.repeatOnLifecycle
import io.github.jeiel85.homebase.data.PcStore
import io.github.jeiel85.homebase.net.HomebaseApi
import io.github.jeiel85.homebase.push.PushNotifier
import io.github.jeiel85.homebase.push.PushRegistrar
import io.github.jeiel85.homebase.push.PushState
import io.github.jeiel85.homebase.push.UnifiedPush
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/** What the push row shows, re-read from preferences while the list is visible (the receiver writes them). */
private data class PushView(
    val distributors: List<String>,
    val distributor: String?,
    val registering: Boolean,
    val endpoint: String?,
    val userDisabled: Boolean,
    val registeredPcs: Int,
    val canNotify: Boolean,
    val error: String?,
    /** The distributor keeps its own connection; without a battery-optimization exemption Doze cuts it. */
    val distributorOptimized: Boolean,
)

private fun readView(context: Context, store: PcStore): PushView {
    val state = PushState(context)
    val pcIds = store.all().map { it.pcId }.toSet()
    return PushView(
        distributors = UnifiedPush.distributors(context),
        distributor = state.distributor,
        registering = state.token != null && state.endpoint == null,
        endpoint = state.endpoint,
        userDisabled = state.userDisabled,
        registeredPcs = state.registeredPcs.count { it in pcIds },
        canNotify = PushNotifier.canNotify(context),
        error = state.lastError,
        distributorOptimized = state.distributor?.let {
            !context.getSystemService(PowerManager::class.java).isIgnoringBatteryOptimizations(it)
        } ?: false,
    )
}

/**
 * "푸시 알림" settings. Push is turned on automatically the first time a PC is paired and exactly
 * one distributor is installed; with none installed it explains how to get ntfy; with several it
 * asks which one. Per-app muting lives in Android's notification settings (channels per PC and app).
 */
@Composable
fun PushSettings(store: PcStore, api: HomebaseApi, pcCount: Int) {
    val context = LocalContext.current
    val hb = Hb.colors
    val scope = rememberCoroutineScope()
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    var view by remember { mutableStateOf(readView(context, store)) }
    var picking by remember { mutableStateOf(false) }
    var askedPermission by rememberSaveable { mutableStateOf(false) }
    val permission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) {
        view = readView(context, store)
    }

    fun enable(distributor: String) {
        enablePush(context, distributor)
        if (Build.VERSION.SDK_INT >= 33 && !PushNotifier.canNotify(context)) {
            askedPermission = true
            permission.launch(Manifest.permission.POST_NOTIFICATIONS)
        }
        view = readView(context, store)
    }

    // Re-read while visible: the receiver writes the state, and the user may come back from
    // installing ntfy or from Android's settings. Turning push on by itself is [PushAutoSetup]'s job.
    LaunchedEffect(Unit) {
        lifecycle.repeatOnLifecycle(Lifecycle.State.STARTED) {
            while (true) {
                view = readView(context, store)
                delay(2_000)
            }
        }
    }

    val on = view.distributor != null
    Spacer(Modifier.height(12.dp))
    SettingsSwitchRow(
        title = "푸시 알림",
        description = when {
            view.distributors.isEmpty() ->
                "PC 경보와 Windows 알림을 받으려면 UnifiedPush 배포자 앱이 필요합니다. ntfy(무료, 계정 없음)를 설치한 뒤 돌아오세요. " +
                    "Google Play 서비스가 없는 휴대폰은 F-Droid판을 쓰세요."
            !on -> "꺼져 있습니다. 켜면 PC 경보와 Windows 알림이 이 휴대폰으로 옵니다."
            view.registering -> "${UnifiedPush.label(context, view.distributor!!)}에 등록하는 중…"
            else -> "${UnifiedPush.label(context, view.distributor!!)} 경유 · PC ${view.registeredPcs}/$pcCount 대에 등록됨. " +
                "내용은 종단 암호화(RFC 8291)되어 중계 서버는 읽을 수 없습니다."
        },
        checked = on,
        onCheckedChange = { checked ->
            if (checked) {
                when (view.distributors.size) {
                    0 -> Unit
                    1 -> enable(view.distributors.single())
                    else -> picking = true
                }
            } else {
                PushState(context).userDisabled = true
                UnifiedPush.unregister(context)
                scope.launch {
                    PushRegistrar.unregisterAll(context.applicationContext, api, store)
                    view = readView(context, store)
                }
                view = readView(context, store)
            }
        },
    )
    view.error?.takeIf { on || view.distributors.isNotEmpty() }?.let {
        Text(it, style = MaterialTheme.typography.bodySmall, color = hb.danger)
    }
    if (view.distributors.isEmpty()) {
        Row {
            TextButton(onClick = { openStore(context, "market://details?id=${UnifiedPush.NTFY_PACKAGE}",
                "https://play.google.com/store/apps/details?id=${UnifiedPush.NTFY_PACKAGE}") }) { Text("Play 스토어에서 ntfy") }
            Spacer(Modifier.width(4.dp))
            TextButton(onClick = { openStore(context, "https://f-droid.org/packages/${UnifiedPush.NTFY_PACKAGE}/", null) }) {
                Text("F-Droid에서 ntfy")
            }
        }
    }
    if (on && !view.canNotify) {
        Text("Android 알림 권한이 꺼져 있어 푸시를 표시하지 못합니다.", style = MaterialTheme.typography.bodySmall, color = hb.danger)
        TextButton(onClick = {
            if (Build.VERSION.SDK_INT >= 33 && !askedPermission) {
                askedPermission = true
                permission.launch(Manifest.permission.POST_NOTIFICATIONS)
            } else {
                openNotificationSettings(context)
            }
        }) { Text("알림 허용") }
    }
    if (on && view.endpoint != null && view.distributorOptimized) {
        Text(
            "${UnifiedPush.label(context, view.distributor!!)}이(가) 배터리 최적화 대상이라 휴대폰이 절전(Doze) 중일 때 알림이 늦게 올 수 있습니다. " +
                "Google Play 서비스가 없는 휴대폰(F-Droid판 ntfy)이라면 배터리 최적화 예외를 켜 주세요.",
            style = MaterialTheme.typography.bodySmall,
            color = hb.danger,
        )
        TextButton(onClick = {
            context.startActivity(Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
        }) { Text("배터리 최적화 설정 열기") }
    }
    if (on) {
        TextButton(onClick = { openNotificationSettings(context) }) { Text("PC·앱별 알림 켜고 끄기") }
    }

    if (picking) {
        AlertDialog(
            onDismissRequest = { picking = false },
            title = { Text("배포자 앱 선택") },
            text = {
                Column {
                    Text("어느 앱으로 푸시를 받을까요?", style = MaterialTheme.typography.bodyMedium)
                    Spacer(Modifier.height(8.dp))
                    view.distributors.forEach { pkg ->
                        TextButton(onClick = {
                            picking = false
                            enable(pkg)
                        }, modifier = Modifier.fillMaxWidth()) { Text(UnifiedPush.label(context, pkg)) }
                    }
                }
            },
            confirmButton = {},
            dismissButton = { TextButton(onClick = { picking = false }) { Text("취소") } },
        )
    }
    Spacer(Modifier.padding(bottom = 4.dp))
}

private fun enablePush(context: Context, distributor: String) {
    PushState(context).userDisabled = false
    PushRegistrar.forgetSynced()
    UnifiedPush.register(context, distributor)
}

/**
 * Push housekeeping that must not depend on the Settings screen being open, so it runs from the
 * activity on every screen but pairing:
 * - first run: one distributor installed, a PC paired, push never turned off → turn it on;
 * - the chosen distributor was uninstalled → clear the registration and say why;
 * - push is on but Android notifications are blocked → ask for the permission once.
 */
@Composable
fun PushAutoSetup(store: PcStore) {
    val context = LocalContext.current
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    var askedPermission by rememberSaveable { mutableStateOf(false) }
    val permission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) {}

    LaunchedEffect(Unit) {
        lifecycle.repeatOnLifecycle(Lifecycle.State.STARTED) {
            while (true) {
                val v = readView(context, store)
                val pcCount = store.all().size
                var justEnabled = false
                if (pcCount > 0 && !v.userDisabled && v.distributor == null && v.distributors.size == 1) {
                    enablePush(context, v.distributors.single())
                    justEnabled = true
                } else if (v.distributor != null && v.distributor !in v.distributors) {
                    PushState(context).apply { clear(); lastError = "배포자 앱이 제거되었습니다" }
                }
                if (pcCount > 0 && (justEnabled || v.endpoint != null) && Build.VERSION.SDK_INT >= 33 &&
                    !PushNotifier.canNotify(context) && !askedPermission
                ) {
                    askedPermission = true
                    permission.launch(Manifest.permission.POST_NOTIFICATIONS)
                }
                delay(2_000)
            }
        }
    }
}

/** True when push is on but Android blocks its notifications — the settings button shows a dot. */
fun pushNeedsAttention(context: Context): Boolean =
    PushState(context).distributor != null && !PushNotifier.canNotify(context)

private fun openStore(context: Context, primary: String, fallback: String?) {
    try {
        context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(primary)).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
    } catch (_: ActivityNotFoundException) {
        fallback?.let { context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(it)).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) }
    }
}

private fun openNotificationSettings(context: Context) {
    context.startActivity(
        Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS)
            .putExtra(Settings.EXTRA_APP_PACKAGE, context.packageName)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
    )
}
