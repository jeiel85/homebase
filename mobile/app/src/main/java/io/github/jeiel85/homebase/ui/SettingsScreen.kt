package io.github.jeiel85.homebase.ui

import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.widget.Toast
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.selectableGroup
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.RadioButton
import androidx.compose.material3.RadioButtonDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.unit.dp
import io.github.jeiel85.homebase.BuildConfig
import io.github.jeiel85.homebase.data.PcStore
import io.github.jeiel85.homebase.net.HomebaseApi
import io.github.jeiel85.homebase.share.QuickSendMode
import io.github.jeiel85.homebase.share.SharePrefs
import io.github.jeiel85.homebase.update.UpdateSurface

/** Project links. One place, so a moved repository is a one-line change. */
internal object Links {
    const val REPO = "https://github.com/jeiel85/homebase"
    const val LATEST_RELEASE = "$REPO/releases/latest"
    const val APACHE_2 = "https://www.apache.org/licenses/LICENSE-2.0"
    const val APP_LICENSE = "$REPO/blob/main/LICENSE"
    fun releaseNotes(version: String) = "$REPO/releases/tag/v$version"
}

/**
 * Settings, apart from the PC list (the list is the dashboard). Sections: notifications, display,
 * updates, about. The pull-to-refresh on the list is the manual update check, so there is no
 * "check now" button here.
 */
@Composable
fun SettingsScreen(store: PcStore, api: HomebaseApi, onBack: () -> Unit, onOpenPriority: () -> Unit) {
    val hb = Hb.colors
    val context = LocalContext.current
    val pcCount = remember { store.all().size }
    var showLicenses by remember { mutableStateOf(false) }

    Column(Modifier.fillMaxSize().background(hb.background)) {
        ConsoleBar(title = "설정", onBack = onBack, eyebrow = "Homebase")
        LazyColumn(
            modifier = Modifier.fillMaxSize(),
            contentPadding = PaddingValues(horizontal = 14.dp),
        ) {
            item {
                SectionLabel("알림")
                SettingsPlate {
                    PushSettings(store, api, pcCount)
                    Divider()
                    NotificationCategories(store, api)
                    Divider()
                    LinkRow("알림 우선순위", "팝업·소리·진동과 묶어 보기를 종류별·앱별로 정합니다", onOpenPriority)
                }
            }
            item {
                SectionLabel("다른 기기")
                SettingsPlate { DeviceSharingSettings() }
            }
            item {
                SectionLabel("공유 메뉴")
                SettingsPlate { QuickSendChoice() }
            }
            item {
                SectionLabel("파일 받기")
                SettingsPlate { FileReceiveSettings() }
            }
            item {
                SectionLabel("화면")
                SettingsPlate { ThemeChoice() }
            }
            // GitHub build: the in-app updater (M-22). Play build: "a new version is on Play" (M-57); Play installs it.
            item {
                SectionLabel("업데이트")
                SettingsPlate {
                    UpdateSurface.SettingsRows()
                    Text(
                        "새 버전은 PC 목록을 아래로 당기면 바로 확인합니다.",
                        style = MaterialTheme.typography.bodySmall,
                        color = hb.inkSoft,
                        modifier = Modifier.padding(bottom = 14.dp),
                    )
                }
            }
            item {
                SectionLabel("정보", trailing = "v${BuildConfig.VERSION_NAME}")
                SettingsPlate {
                    LinkRow("이 버전의 변경 내용", "v${BuildConfig.VERSION_NAME} 릴리스 노트") {
                        context.openUrl(Links.releaseNotes(BuildConfig.VERSION_NAME))
                    }
                    Divider()
                    LinkRow("PC 프로그램 받기", "다른 PC에 설치할 Windows 설치 파일(최신 릴리스)") {
                        context.openUrl(Links.LATEST_RELEASE)
                    }
                    Divider()
                    LinkRow("GitHub 저장소", "소스 코드와 설명서") { context.openUrl(Links.REPO) }
                    Divider()
                    LinkRow("문제 신고·제안", "GitHub 이슈를 엽니다. 앱·Android 버전이 미리 채워집니다") {
                        context.openUrl(newIssueUrl())
                    }
                    Divider()
                    LinkRow("오픈소스 라이선스", "이 앱과 사용한 라이브러리의 라이선스") { showLicenses = true }
                    Text(
                        "이 앱은 연결한 PC와 직접 통신합니다. 광고·분석·사용 기록 수집이 없고, " +
                            "외부로 나가는 것은 업데이트 확인(${UpdateSurface.CHECK_SOURCE})과 종단 암호화된 푸시(배포자 앱의 중계 서버)뿐입니다.",
                        style = MaterialTheme.typography.bodySmall,
                        color = hb.inkSoft,
                        modifier = Modifier.padding(top = 4.dp, bottom = 14.dp),
                    )
                }
                Spacer(Modifier.navigationBarsPadding().height(24.dp))
            }
        }
    }

    if (showLicenses) LicensesDialog(onDismiss = { showLicenses = false })
}

/**
 * Files a PC (or another phone through it) sends are fetched as soon as they come (docs/SHARE_CONVENIENCE.md §9.3);
 * over mobile data, more than 25 MB waits for [받기] unless this is on.
 */
@Composable
private fun FileReceiveSettings() {
    val hb = Hb.colors
    val context = LocalContext.current
    val prefs = remember { SharePrefs(context) }
    var onMetered by remember { mutableStateOf(prefs.receiveOnMetered()) }
    Column(Modifier.padding(vertical = 6.dp)) {
        SettingsSwitchRow(
            title = "모바일 데이터에서도 자동으로 받기",
            description = "PC가 보낸 파일은 알림이 오면 바로 받습니다. 끄면 모바일 데이터에서 25MB가 넘는 파일은 [받기]를 눌러야 받습니다.",
            checked = onMetered,
            onCheckedChange = {
                onMetered = it
                prefs.setReceiveOnMetered(it)
            },
        )
        Text(
            "사진·동영상은 Pictures/Homebase(갤러리), 그 밖의 파일은 Download/Homebase에 저장합니다(Android 10 이상). " +
                "받은 파일과 보낸 파일은 알림 센터에 이름·크기만 남습니다.",
            style = MaterialTheme.typography.bodySmall,
            color = hb.inkSoft,
            modifier = Modifier.padding(top = 2.dp, bottom = 10.dp),
        )
    }
}

/**
 * "PC 바로가기로 공유하면 바로 보내기" (docs/QUICK_SEND.md §4.1): what a share sheet PC shortcut sends without the share
 * center. One radio row per [QuickSendMode]; the whole row selects.
 */
@Composable
private fun QuickSendChoice() {
    val hb = Hb.colors
    val context = LocalContext.current
    val prefs = remember { SharePrefs(context) }
    var mode by remember { mutableStateOf(prefs.quickMode()) }
    Column(Modifier.selectableGroup().padding(vertical = 6.dp)) {
        Text(
            "PC 바로가기로 공유하면 바로 보내기",
            style = MaterialTheme.typography.titleSmall,
            color = hb.ink,
            modifier = Modifier.padding(top = 6.dp),
        )
        QuickSendMode.entries.forEach { option ->
            Row(
                Modifier
                    .fillMaxWidth()
                    .heightIn(min = 48.dp)
                    .selectable(
                        selected = mode == option,
                        role = Role.RadioButton,
                        onClick = {
                            mode = option
                            prefs.setQuickMode(option)
                        },
                    ),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                RadioButton(
                    selected = mode == option,
                    onClick = null,
                    colors = RadioButtonDefaults.colors(selectedColor = hb.accent),
                )
                Spacer(Modifier.width(12.dp))
                Text(option.label, style = MaterialTheme.typography.bodyMedium, color = hb.ink)
            }
        }
        Text(
            "공유 메뉴의 PC 바로가기를 누르면 확인 화면 없이 보냅니다. 다른 앱도 이 바로가기로 보낼 수 있어 " +
                "PC의 Homebase 설정 › 휴대폰에서 바로 클립보드에 넣을 범위를 함께 정해 두세요. 결과는 토스트와 알림으로 알리므로 " +
                "Homebase 알림이 꺼져 있으면 공유 센터가 열립니다. Homebase 아이콘으로 공유해도 지금처럼 공유 센터가 열립니다.",
            style = MaterialTheme.typography.bodySmall,
            color = hb.inkSoft,
            modifier = Modifier.padding(top = 2.dp, bottom = 10.dp),
        )
    }
}

@Composable
private fun SettingsPlate(content: @Composable ColumnScope.() -> Unit) {
    Plate { Column(Modifier.padding(horizontal = 16.dp, vertical = 4.dp), content = content) }
}

@Composable
private fun Divider() = HorizontalDivider(color = Hb.colors.hairline)

/** One radio row per [ThemeMode]; the whole row selects. */
@Composable
private fun ThemeChoice() {
    val hb = Hb.colors
    val context = LocalContext.current
    Column(Modifier.selectableGroup().padding(vertical = 6.dp)) {
        Text("테마", style = MaterialTheme.typography.titleSmall, color = hb.ink, modifier = Modifier.padding(top = 6.dp))
        ThemeMode.entries.forEach { mode ->
            Row(
                Modifier
                    .fillMaxWidth()
                    .heightIn(min = 48.dp)
                    .selectable(
                        selected = ThemeSetting.mode == mode,
                        role = Role.RadioButton,
                        onClick = { ThemeSetting.set(context, mode) },
                    ),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                RadioButton(
                    selected = ThemeSetting.mode == mode,
                    onClick = null,
                    colors = RadioButtonDefaults.colors(selectedColor = hb.accent),
                )
                Spacer(Modifier.width(12.dp))
                Text(mode.label, style = MaterialTheme.typography.bodyMedium, color = hb.ink)
            }
        }
    }
}

/** A tappable row that leaves the app (browser) or opens a dialog. */
@Composable
private fun LinkRow(title: String, description: String, onClick: () -> Unit) {
    val hb = Hb.colors
    Row(
        Modifier
            .fillMaxWidth()
            .heightIn(min = 56.dp)
            .clickable(role = Role.Button, onClick = onClick)
            .padding(vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(Modifier.weight(1f)) {
            Text(title, style = MaterialTheme.typography.titleSmall, color = hb.ink)
            Text(description, style = MaterialTheme.typography.bodySmall, color = hb.inkSoft)
        }
        Icon(
            Icons.AutoMirrored.Filled.KeyboardArrowRight,
            contentDescription = null,
            tint = hb.inkSoft,
            modifier = Modifier.size(24.dp),
        )
    }
}

/** What ships in the APK besides our own code, with its license. Kept by hand: the dependency list is short. */
private val THIRD_PARTY = listOf(
    "AndroidX (Activity, Lifecycle, Compose, Material 3)" to "Apache License 2.0",
    "Kotlin 표준 라이브러리, kotlinx.coroutines" to "Apache License 2.0",
    "OkHttp, Okio" to "Apache License 2.0",
    "ZXing" to "Apache License 2.0",
    "ZXing Android Embedded" to "Apache License 2.0",
)

@Composable
private fun LicensesDialog(onDismiss: () -> Unit) {
    val context = LocalContext.current
    val hb = Hb.colors
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("오픈소스 라이선스") },
        text = {
            Column {
                Text("Homebase", style = MaterialTheme.typography.titleSmall, color = hb.ink)
                Text("MIT License", style = MaterialTheme.typography.bodySmall, color = hb.inkSoft)
                Spacer(Modifier.height(10.dp))
                THIRD_PARTY.forEach { (name, license) ->
                    Text(name, style = MaterialTheme.typography.titleSmall, color = hb.ink)
                    Text(license, style = MaterialTheme.typography.bodySmall, color = hb.inkSoft)
                    Spacer(Modifier.height(8.dp))
                }
                Row {
                    TextButton(onClick = { context.openUrl(Links.APP_LICENSE) }) { Text("MIT 전문") }
                    TextButton(onClick = { context.openUrl(Links.APACHE_2) }) { Text("Apache 2.0 전문") }
                }
            }
        },
        confirmButton = { TextButton(onClick = onDismiss) { Text("닫기") } },
    )
}

/** A new GitHub issue with the versions filled in — nothing else about the phone or its PCs. */
private fun newIssueUrl(): String = Uri.parse(Links.REPO).buildUpon()
    .appendPath("issues").appendPath("new")
    .appendQueryParameter(
        "body",
        "앱 버전: ${BuildConfig.VERSION_NAME}\nAndroid: ${Build.VERSION.RELEASE} (API ${Build.VERSION.SDK_INT})\n\n" +
            "무슨 일이 있었나요? / 무엇을 바라나요?\n\n",
    )
    .build().toString()

/** Hands a link to the browser; says so when there is no app to open it. */
internal fun Context.openUrl(url: String) {
    try {
        startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(url)).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
    } catch (_: ActivityNotFoundException) {
        Toast.makeText(this, "링크를 열 앱이 없습니다: $url", Toast.LENGTH_LONG).show()
    }
}
