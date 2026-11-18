package io.github.jeiel85.homebase.update

import android.content.Context
import android.content.Intent
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import io.github.jeiel85.homebase.BuildConfig
import io.github.jeiel85.homebase.ui.SettingsSwitchRow
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * Google Play 빌드의 업데이트 이음매. `src/github/java`의 같은 이름 파일과 **배타적으로** 컴파일된다
 * (DECISIONS M-33).
 *
 * 설치는 Play 스토어가 한다. 이 빌드는 Play In-App Updates API로 새 버전이 있는지만 묻고(M-57), 있으면
 * 목록 위 배너와 알림으로 알려 누르면 Play 스토어의 Homebase 페이지를 연다. Google Play 정책은 Play로 받은
 * 앱이 Play 밖에서 자기 APK를 받아 설치하는 것을 금지하므로(기기·네트워크 악용 정책), 다운로드·설치 코드와
 * `REQUEST_INSTALL_PACKAGES` 권한은 이 빌드에 들어가지 않는다.
 */
internal object UpdateSurface {

    /** 업데이트를 어디에 묻는가. 설정 → 정보의 "외부로 나가는 것" 문구가 쓴다. */
    const val CHECK_SOURCE = "Google Play"

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    /** 이 프로세스에서 앱을 열 때 마지막으로 물은 시각 — 실패해도 연달아 열 때마다 다시 묻지 않게. */
    @Volatile private var lastOpenCheckAt = 0L

    /**
     * 백그라운드 확인 예약을 설정에 맞추고, 마지막 확인이 6시간 넘게 지났으면 지금 한 번 묻는다
     * (결과는 배너로). 앱이 열릴 때마다(MainActivity.onCreate) 부른다.
     */
    fun sync(context: Context) {
        val app = context.applicationContext
        StoreUpdateScheduler.sync(app)
        val preferences = StoreUpdatePreferences(app)
        if (!preferences.isEnabled()) return
        val now = System.currentTimeMillis()
        if (!StoreUpdateRules.isDueOnOpen(preferences.lastCheckedAt(), lastOpenCheckAt, now)) return
        lastOpenCheckAt = now
        scope.launch {
            StoreUpdateChecker.check(app)
            withContext(Dispatchers.Main) { refreshEpoch++ }
        }
    }

    /** 알림은 앱이 아니라 Play 스토어를 바로 연다 — 앱이 처리할 업데이트 탭이 없다. */
    @Suppress("UNUSED_PARAMETER")
    fun isUpdateTap(intent: Intent?): Boolean = false

    fun requestOpen() = Unit

    /** 확인이 끝날 때마다 하나씩 올라간다. [Banner]가 이 값으로 다시 그린다. */
    private var refreshEpoch by mutableIntStateOf(0)

    /**
     * 목록을 아래로 당겼을 때: 6시간 규칙 없이 지금 묻고, 결과를 한 줄로 돌려준다.
     * "새 버전 알림"을 꺼 두었으면 묻지 않고 아무 말도 하지 않는다(GitHub판과 같은 계약).
     */
    suspend fun checkNow(context: Context): String? {
        val app = context.applicationContext
        if (!StoreUpdatePreferences(app).isEnabled()) return null
        val result = withContext(Dispatchers.IO) { StoreUpdateChecker.check(app) }
        refreshEpoch++
        return when (result) {
            is StoreUpdateRules.Result.Available ->
                "새 버전 ${StoreUpdateRules.versionName(result.versionCode)}이 있습니다 — 배너를 눌러 Play 스토어에서 업데이트하세요"
            StoreUpdateRules.Result.UpToDate -> "최신 버전입니다 (${BuildConfig.VERSION_NAME})"
            // 직접 설치한 Play 빌드(개발용) 등 — "최신 버전입니다"라고 하면 사실이 아니다.
            StoreUpdateRules.Result.NotFromPlay -> "Play 스토어에서 설치한 앱에서만 새 버전을 확인할 수 있습니다"
            StoreUpdateRules.Result.Failed -> "업데이트를 확인하지 못했습니다 — 네트워크를 확인하세요"
        }
    }

    /**
     * PC 목록 위의 한 줄. 알릴 새 버전이 없으면 **아무것도 그리지 않는다.** 누르면 Play 스토어가 열린다.
     * 표시는 마지막 확인 결과(저장값)로 네트워크 없이 한다 — Play가 업데이트하면 앱이 다시 시작되고,
     * 설치된 versionCode가 그 값 이상이 되어 배너가 사라진다.
     */
    @Composable
    fun Banner(modifier: Modifier = Modifier) {
        val context = LocalContext.current
        var offered by remember { mutableStateOf<Int?>(null) }
        val epoch = refreshEpoch
        LaunchedEffect(epoch) {
            offered = withContext(Dispatchers.IO) { StoreUpdatePreferences(context).offeredVersionCode() }
            // 이미 업데이트했거나 알림을 껐다 — 남은 알림도 치운다.
            if (offered == null) StoreUpdateNotifier.cancel(context)
        }
        val versionCode = offered ?: return
        Surface(
            modifier = modifier.fillMaxWidth(),
            color = MaterialTheme.colorScheme.secondaryContainer,
            contentColor = MaterialTheme.colorScheme.onSecondaryContainer,
            shape = RoundedCornerShape(12.dp),
        ) {
            Text(
                text = "새 버전 ${StoreUpdateRules.versionName(versionCode)}이 있습니다 — 눌러서 Play 스토어에서 업데이트",
                style = MaterialTheme.typography.bodyMedium,
                modifier = Modifier
                    .fillMaxWidth()
                    .clickable { StoreLink.open(context) }
                    .padding(horizontal = 16.dp, vertical = 12.dp),
            )
        }
    }

    /** 설정 → 업데이트. 켜고 끄는 한 줄, 마지막 확인 시각, 알림 권한이 없으면 허용 버튼. */
    @Composable
    fun SettingsRows(titleModifier: Modifier = Modifier) {
        val context = LocalContext.current
        val preferences = remember(context) { StoreUpdatePreferences(context) }
        var enabled by remember { mutableStateOf(preferences.isEnabled()) }

        Spacer(Modifier.height(12.dp))
        SettingsSwitchRow(
            title = "새 버전 알림",
            titleModifier = titleModifier,
            description = "하루 두 번 Google Play에 새 버전이 있는지 묻고, 있으면 버전마다 한 번 알립니다. " +
                "설치는 Play 스토어가 합니다 — 알림이나 PC 목록 위 배너를 누르면 Play 스토어의 Homebase 페이지가 열립니다.",
            checked = enabled,
            onCheckedChange = { checked ->
                preferences.setEnabled(checked)
                enabled = checked
                StoreUpdateScheduler.sync(context)
                refreshEpoch++
            },
        )
        if (enabled) {
            val lastChecked = remember { preferences.lastCheckedAt() }
            Text(
                text = lastUpdateCheckLine(lastChecked, System.currentTimeMillis()),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(bottom = 4.dp),
            )
            UpdateNotificationPermissionRow()
        }
    }
}
