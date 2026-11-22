package io.github.jeiel85.homebase.update

import android.Manifest
import android.content.Intent
import android.os.Build
import android.provider.Settings
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.platform.LocalContext
import io.github.jeiel85.homebase.push.PushHistoryRules
import io.github.jeiel85.homebase.push.PushNotifier
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.concurrent.TimeUnit

/*
 * 두 채널의 설정 → "업데이트" 섹션이 함께 쓰는 조각. 무엇을 확인하는지(GitHub update.json / Google Play)는
 * 채널마다 다르지만(DECISIONS M-33, M-57), 확인이 실제로 도는지 보여 주는 줄과 알림 권한 안내는 같다.
 */

/** 12시간 주기 확인이 이만큼 없으면 "막혀 있을 수 있다"고 덧붙인다(최악 36시간의 두 배). */
private val STALE_CHECK_MILLIS = TimeUnit.DAYS.toMillis(3)

/**
 * "마지막 확인: 오늘 14:05". 성공한 확인만 기록된다. 며칠째 없으면 그 이유일 수 있는 것을 덧붙인다 —
 * 배터리 정책이 백그라운드 잡을 미루거나 건너뛰는 기기가 있다.
 */
internal fun lastUpdateCheckLine(at: Long, now: Long): String {
    if (at <= 0L) return "마지막 확인: 아직 없음"
    val line = "마지막 확인: ${updateTimeLabel(at, now)}"
    return if (now - at > STALE_CHECK_MILLIS) {
        "$line · 며칠째 확인하지 못했습니다. 배터리 최적화가 백그라운드 확인을 막고 있을 수 있습니다."
    } else {
        line
    }
}

/** "오늘 14:05", "11월 12일 (목) 09:30" — 업데이트 설정의 시각 표기. */
internal fun updateTimeLabel(at: Long, now: Long, zone: ZoneId = ZoneId.systemDefault()): String {
    val time = DateTimeFormatter.ofPattern("HH:mm").format(Instant.ofEpochMilli(at).atZone(zone))
    return "${PushHistoryRules.dayLabel(at, now, zone)} $time"
}

/**
 * 알림 권한이 없으면 확인은 돌아도 알림이 뜨지 않는다(목록 위 배너만 남는다). 그 사실을 이 자리에서
 * 말하고 고칠 길을 준다. 처음엔 권한을 묻고, 이미 거부돼 다시 물을 수 없으면(두 번째 누름) 앱 알림
 * 설정을 연다. 권한이 있으면 아무것도 그리지 않는다.
 */
@Composable
internal fun UpdateNotificationPermissionRow() {
    val context = LocalContext.current
    var canNotify by remember { mutableStateOf(PushNotifier.canNotify(context)) }
    var askedPermission by rememberSaveable { mutableStateOf(false) }
    val permission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) {
        canNotify = PushNotifier.canNotify(context)
    }
    // 알림 설정 화면에서 돌아오면 다시 읽는다.
    val notificationSettings = rememberLauncherForActivityResult(ActivityResultContracts.StartActivityForResult()) {
        canNotify = PushNotifier.canNotify(context)
    }
    if (canNotify) return
    TextButton(
        onClick = {
            if (Build.VERSION.SDK_INT >= 33 && !askedPermission) {
                askedPermission = true
                permission.launch(Manifest.permission.POST_NOTIFICATIONS)
            } else {
                notificationSettings.launch(
                    Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS)
                        .putExtra(Settings.EXTRA_APP_PACKAGE, context.packageName),
                )
            }
        },
    ) {
        Text("알림이 꺼져 있어 새 버전을 알림으로 받지 못합니다 — 허용하기")
    }
}
