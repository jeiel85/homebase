package io.github.jeiel85.homebase.update

import android.app.Application
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.provider.Settings
import android.text.format.Formatter
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.DialogProperties
import io.github.jeiel85.homebase.BuildConfig
import io.github.jeiel85.homebase.ui.SettingsSwitchRow
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File

/**
 * GitHub(사이드로드) 빌드가 얻는 이음매. `src/play/java`의 같은 이름 파일과 **배타적으로**
 * 컴파일된다(DECISIONS M-33, product flavor `channel`).
 *
 * 공통 코드(`main`)는 업데이트 기능에서 이 객체만 부른다. 그래서 MainActivity·PC 목록·설정 화면은
 * 업데이트 기능의 존재를 몰라도 되고, Play 빌드에는 그 기능의 코드·권한·서비스가 아예 들어가지 않는다.
 */
internal object UpdateSurface {

    /** 업데이트를 어디에 묻는가. 설정 → 정보의 "외부로 나가는 것" 문구가 쓴다. */
    const val CHECK_SOURCE = "GitHub"

    /**
     * 백그라운드 업데이트 확인(DECISIONS M-22) 예약을 설정에 맞추고, 확인 없는 설치(M-56)가 화면에 떠 있는
     * 앱을 교체하지 않도록 화면 표시를 세기 시작한다. 앱이 열릴 때마다(MainActivity.onCreate) 부른다.
     */
    fun sync(context: Context) {
        (context.applicationContext as? Application)?.let(AppVisibility::register)
        UpdateCheckScheduler.sync(context)
    }

    /** Play판은 화면으로 돌아올 때 다시 묻는다(#77). 이 판의 확인은 잡과 배너의 새로 고침이 맡는다. */
    @Suppress("UNUSED_PARAMETER")
    fun onResume(context: Context) = Unit

    /** 업데이트 알림을 눌러 앱이 열렸는가. */
    fun isUpdateTap(intent: Intent?): Boolean =
        intent?.getBooleanExtra(UpdateNotifier.EXTRA_OPEN_UPDATE, false) == true

    /**
     * 설정 → 앱 섹션에 붙는 행들.
     *
     * Input : [titleModifier] — 설정 검색이 이 행으로 이동할 때 쓰는 앵커(#517). 상태는 [UpdatePreferences]에서 읽는다
     * Output: 기본 켜짐 토글 한 줄과 그 아래 설명(알림 권한이 없으면 허용 버튼 한 줄)
     *
     * 핵심 로직: 켜는 순간 무엇이 달라지는지를 **같은 화면에서** 말한다. F-Droid 포함 정책은
     * 추가 실행 파일을 받는 동작에 옵트인을 요구하면서, 사용자가 "F-Droid의 검사를 우회하는
     * 선택을 하고 있다"는 것을 분명히 알 수 있게 하라고 적는다. 설명을 다른 화면으로 미루면
     * 그 요구를 형식적으로만 만족시킨다.
     */
    @Composable
    fun SettingsRows(titleModifier: Modifier = Modifier) {
        val context = LocalContext.current
        val preferences = remember(context) { UpdatePreferences(context) }
        var enabled by remember { mutableStateOf(preferences.isEnabled()) }
        var autoInstall by remember { mutableStateOf(preferences.isAutoInstallEnabled()) }

        Spacer(Modifier.height(12.dp))
        SettingsSwitchRow(
            title = "업데이트 확인",
            titleModifier = titleModifier,
            description = "하루 한 번 GitHub Releases에서 새 버전을 확인합니다. 받은 APK는 SHA-256을 대조하고, Android가 같은 서명인지 확인한 뒤에만 설치됩니다.",
            checked = enabled,
            onCheckedChange = { checked ->
                preferences.setEnabled(checked)
                enabled = checked
                UpdateCheckScheduler.sync(context)
            },
        )
        // 백그라운드 확인이 실제로 도는지 보이게 한다 — 배터리 정책이 잡을 미루거나 건너뛰는 기기가 있다.
        if (enabled) {
            val lastChecked = remember { preferences.lastCheckedAt() }
            val now = System.currentTimeMillis()
            Text(
                text = lastUpdateCheckLine(lastChecked, now),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(bottom = 4.dp),
            )
            // 마지막 자동 설치가 어떻게 끝났는지(#74) — 실패는 이 화면 말고는 남는 곳이 없다.
            // 그 버전을 이미 (손으로) 설치했으면 지난 실패는 더 할 일이 없으니 보이지 않는다.
            val autoResult = remember { preferences.lastAutoInstall() }?.takeIf {
                it.outcome == AutoInstallResult.Outcome.INSTALLED || it.versionCode > BuildConfig.VERSION_CODE
            }
            if (autoInstall && autoResult != null) {
                val gaveUp = autoResult.outcome == AutoInstallResult.Outcome.DOWNLOAD_FAILED ||
                    autoResult.outcome == AutoInstallResult.Outcome.INSTALL_FAILED
                Text(
                    text = AutoInstallResult.line(
                        autoResult,
                        updateTimeLabel(autoResult.at, now),
                        gaveUp = gaveUp && preferences.silentFailedVersionCode() == autoResult.versionCode &&
                            preferences.silentFailures() >= UpdateDecision.MAX_SILENT_ATTEMPTS,
                    ),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(bottom = 4.dp),
                )
            }
            UpdateNotificationPermissionRow()
        }
        // 확인 없는 설치(DECISIONS M-56). 확인을 끄면 받을 새 버전도 모르므로 함께 숨긴다.
        if (enabled) {
            SettingsSwitchRow(
                title = "새 버전 자동 설치",
                description = if (Build.VERSION.SDK_INT >= UpdateDecision.SILENT_INSTALL_MIN_SDK) {
                    "앱을 쓰지 않는 동안 새 버전을 받아 확인 없이 설치하고, 설치한 뒤 알림으로 알립니다. " +
                        "처음 한 번은 Android가 설치 확인을 물을 수 있습니다 — 그때는 알림을 눌러 설치하세요. " +
                        "끄면 새 버전을 알림으로만 알리고, 설치는 내가 누를 때 진행됩니다."
                } else {
                    "이 휴대폰의 Android(12 미만)는 확인 없는 설치를 지원하지 않아, 새 버전을 알림으로 알립니다."
                },
                checked = autoInstall,
                onCheckedChange = { checked ->
                    preferences.setAutoInstallEnabled(checked)
                    autoInstall = checked
                },
            )
        }
    }

    /**
     * 노트 목록 위의 한 줄짜리 알림. 새 버전이 없으면 **아무것도 그리지 않는다.**
     *
     * Input : 없음
     * Output: 새 버전이 있을 때만 눌러서 모달을 여는 배너
     *
     * 핵심 로직: 확인은 백그라운드에서, 표시는 목록에서만 한다. 앱 시작 화면을 모달로 막지
     * 않는 것이 UX 계약이다 — 업데이트는 사용자가 지금 하려던 일이 아니다.
     *
     * 상태를 화면에 들고 있는 이유: 확인 자체는 하루 1회지만 배너는 그 사이에도 떠 있어야 하고,
     * 그 판단은 캐시된 원문으로 네트워크 없이 재현된다([UpdateRefresh.run] 참조).
     */
    @Composable
    fun Banner(modifier: Modifier = Modifier) {
        val context = LocalContext.current
        val preferences = remember(context) { UpdatePreferences(context) }
        var offered by remember { mutableStateOf<UpdateManifest?>(null) }
        var dialogOpen by remember { mutableStateOf(false) }
        // 설치 권한을 주고 돌아왔는데 대화상자가 사라졌으면(앱이 다시 시작됨) 받기를 바로 이어 간다.
        var resumeDownload by remember { mutableStateOf(false) }
        val openRequested = openRequest
        val epoch = refreshEpoch

        // 업데이트 알림을 눌러 들어왔으면(openRequested) 판단을 다시 하고 모달을 바로 연다. 그사이
        // 설치했거나 건너뛰어 더 알릴 것이 없으면 모달 없이 요청만 지운다. 당겨서 새로고침으로
        // 확인을 마치면(epoch) 방금 저장된 결과로 배너를 다시 그린다.
        LaunchedEffect(openRequested, epoch) {
            offered = withContext(Dispatchers.IO) {
                // 지난번 설치 앱에 넘긴 APK는 여기서 치운다(`UpdateInstaller.install` 참조).
                // 설치 앱은 뜨자마자 파일을 자기 쪽으로 복사하므로, 사용자가 이 화면으로 다시
                // 돌아올 무렵에는 이미 다 읽은 뒤다. 내려받는 중이면 그 파일이므로 건드리지 않는다.
                if (!downloadInProgress.get()) {
                    UpdateInstaller.deleteLeftovers(UpdateDownloads.dir(context))
                }
                UpdateRefresh.run(preferences)
            }
            // 알릴 것이 없으면(설치를 마쳤거나 건너뛰었거나 확인을 껐다) 남은 알림도 치운다.
            if (offered == null) UpdateNotifier.cancel(context)
            if (openRequested) {
                if (offered != null) dialogOpen = true
                openRequest = false
            }
            // "업데이트"를 눌렀다가 '알 수 없는 앱 설치' 권한을 주러 설정에 다녀왔다. 권한을 바꾸면 Android가
            // 앱 프로세스를 다시 시작하는 기기가 있어 대화상자와 그 결과 콜백이 사라진다 — 사용자가 다시
            // 누르지 않아도 그 버전을 이어서 받는다. 권한이 그대로 없거나 오래된 요청이면 지우기만 한다.
            val current = offered
            if (current != null && preferences.pendingInstallVersionCode() != 0) {
                val resume = UpdateDecision.shouldResumeInstall(
                    pendingVersionCode = preferences.pendingInstallVersionCode(),
                    pendingAt = preferences.pendingInstallAt(),
                    offeredVersionCode = current.versionCode,
                    now = System.currentTimeMillis(),
                    canInstall = context.packageManager.canRequestPackageInstalls(),
                )
                preferences.clearPendingInstall()
                if (resume) {
                    resumeDownload = true
                    dialogOpen = true
                }
            }
        }

        val manifest = offered ?: return

        // `Surface(onClick = ...)`가 아니라 `Modifier.clickable`인 이유: 그 오버로드는 Material3
        // 버전에 따라 실험 API라 opt-in이 필요해질 수 있다. 눌리는 영역과 모양은 동일하다.
        Surface(
            modifier = modifier.fillMaxWidth(),
            color = MaterialTheme.colorScheme.secondaryContainer,
            contentColor = MaterialTheme.colorScheme.onSecondaryContainer,
            shape = RoundedCornerShape(12.dp),
        ) {
            Text(
                text = "새 버전 ${manifest.versionName}이 있습니다 — 눌러서 업데이트",
                style = MaterialTheme.typography.bodyMedium,
                modifier = Modifier
                    .fillMaxWidth()
                    .clickable { dialogOpen = true }
                    .padding(horizontal = 16.dp, vertical = 12.dp),
            )
        }

        if (dialogOpen) {
            UpdateDialog(
                manifest = manifest,
                startNow = resumeDownload,
                onDismiss = {
                    dialogOpen = false
                    resumeDownload = false
                },
                onSkip = {
                    preferences.setSkippedVersionCode(manifest.versionCode)
                    UpdateNotifier.cancel(context)
                    offered = null
                    dialogOpen = false
                    resumeDownload = false
                },
            )
        }
    }

    /**
     * 이 모달의 상태 기계. `Offer`가 시작점이고, 성공하면 [onDismiss]로 빠져나가며 다시
     * 이 상태로 돌아오지 않는다 — 설치가 넘어간 뒤에 이 모달이 여전히 무언가를 보여줄 이유가
     * 없다(그 다음은 시스템 설치 앱의 몫).
     */
    /**
     * 업데이트 알림을 눌러 앱이 열렸다는 표시. [MainActivity]가 켜고 [Banner]가 소비한다.
     * 프로세스 전역 상태인 이유: 알림은 액티비티가 이미 떠 있을 때(onNewIntent)도 눌릴 수 있고,
     * 그때 배너는 이미 컴포지션에 있다 — 값이 바뀌면 배너의 LaunchedEffect가 다시 돈다.
     */
    private var openRequest by mutableStateOf(false)

    /** 업데이트 알림을 눌러 들어왔을 때 부른다. 목록 화면의 배너가 모달을 연다. */
    fun requestOpen() {
        openRequest = true
    }

    /** [checkNow]가 끝날 때마다 하나씩 올라간다. [Banner]가 이 값으로 판단을 다시 한다. */
    private var refreshEpoch by mutableIntStateOf(0)

    /**
     * Input : [context]
     * Output: 사용자에게 한 줄로 알릴 결과 문구, 알릴 것이 없으면 null
     *
     * 핵심 로직: 목록을 아래로 당겼을 때 부른다. 하루 1회 규칙 없이 지금 확인하고
     * ([UpdateRefresh.runNow]), 새 버전이 있으면 배너가 뜨도록 [Banner]를 다시 돌린다.
     * "업데이트 확인"을 꺼 두었으면 확인하지 않고 아무 말도 하지 않는다.
     */
    suspend fun checkNow(context: Context): String? {
        val result = withContext(Dispatchers.IO) { UpdateRefresh.runNow(UpdatePreferences(context)) }
        refreshEpoch++
        return when (result) {
            is UpdateRefresh.ManualResult.Offer -> "새 버전 ${result.manifest.versionName}이 있습니다"
            // "최신 버전입니다"는 사실이 아니다 — 새 버전이 있지만 이 휴대폰에는 설치할 수 없다.
            is UpdateRefresh.ManualResult.NeedsNewerAndroid ->
                "새 버전 ${result.manifest.versionName}은 Android API ${result.manifest.minSdk} 이상이 필요해 " +
                    "이 휴대폰(API ${Build.VERSION.SDK_INT})에는 설치할 수 없습니다"
            UpdateRefresh.ManualResult.UpToDate -> "최신 버전입니다 (${BuildConfig.VERSION_NAME})"
            UpdateRefresh.ManualResult.Failed -> "업데이트를 확인하지 못했습니다 — 네트워크를 확인하세요"
            UpdateRefresh.ManualResult.Disabled -> null
        }
    }

    /** [WrongSigner]: 이 설치본이 릴리스 키로 서명되지 않아(디버그 빌드 등) 업데이트를 덮어 설치할 수 없다. */
    private enum class DownloadFlowState { Offer, WrongSigner, NeedsInstallPermission, Downloading, Failed }

    // 프로세스 전체에서 하나만 도는 다운로드/설치를 보장하는 재진입 가드. `UpdateDialog`가
    // 닫혔다 다시 열리거나 백그라운드 설치([SilentInstaller])가 받는 중이어도 같은 캐시 파일
    // 경로를 두 흐름이 동시에 쓰는 경합이 생기지 않게 한다.
    private val downloadInProgress get() = UpdateDownloads.inProgress

    /**
     * [startNow]: 열리자마자 받기를 시작한다 — 설치 권한을 주고 돌아왔는데 앱이 다시 시작돼
     * 이전 대화상자가 사라진 경우([Banner] 참조).
     */
    @Composable
    private fun UpdateDialog(
        manifest: UpdateManifest,
        startNow: Boolean,
        onDismiss: () -> Unit,
        onSkip: () -> Unit,
    ) {
        val context = LocalContext.current
        val preferences = remember(context) { UpdatePreferences(context) }
        val size = remember(manifest) { Formatter.formatShortFileSize(context, manifest.apkSizeBytes) }
        // 다른 키로 서명된 설치본이면 받기 전에 그 사실부터 말한다 — 받아 봐야 시스템이 설치를 거부한다.
        var flowState by remember(manifest) {
            mutableStateOf(if (UpdateSigner.canUpdateInPlace(context)) DownloadFlowState.Offer else DownloadFlowState.WrongSigner)
        }
        // LaunchedEffect의 재실행 열쇠다. 0은 "아직 시작 안 함"이고, 그 뒤로는 시도할 때마다
        // 하나씩 올린다 — "다시 시도"를 눌러도 같은 값이면 LaunchedEffect가 다시 돌지 않는다.
        var downloadEpoch by remember(manifest) { mutableIntStateOf(0) }

        fun startDownload() {
            // 설정에 다녀온 뒤의 "이어서 받기" 표시는 어느 길로 왔든 여기서 끝난다.
            preferences.clearPendingInstall()
            if (!UpdateSigner.canUpdateInPlace(context)) {
                flowState = DownloadFlowState.WrongSigner
                return
            }
            // compareAndSet이 false를 반환하면 이미 다른 흐름이 돌고 있다는 뜻 — 조용히
            // 무시한다. 정상 경로에서는 canDismiss가 그 상태로의 재진입 자체를 막으므로
            // 여기 걸릴 일이 없어야 하지만, 그 가정이 깨지더라도 파일 경합만은 막는다.
            if (!downloadInProgress.compareAndSet(false, true)) return
            // 매번 다시 묻는다 — Settings에서 돌아온 뒤 다시 확인하는 것과 같은 이유로,
            // 사용자가 그사이 권한을 바꿨을 수도 있는 사실을 이 함수가 직접 안다고 가정하지 않는다.
            flowState = if (context.packageManager.canRequestPackageInstalls()) {
                downloadEpoch++
                DownloadFlowState.Downloading
            } else {
                downloadInProgress.set(false)
                DownloadFlowState.NeedsInstallPermission
            }
        }

        // `ACTION_MANAGE_UNKNOWN_APP_SOURCES`는 API 26(=minSdk)에 생겼다 — 버전 분기가 필요 없다.
        val installPermissionLauncher = rememberLauncherForActivityResult(
            ActivityResultContracts.StartActivityForResult()
        ) {
            // Settings 화면은 결과 코드로 승인 여부를 알려주지 않는다. 돌아온 뒤 PackageManager에
            // 직접 다시 물어야 한다.
            startDownload()
        }

        // 앱이 다시 시작된 뒤 [Banner]가 이어 달라고 연 경우. 위 콜백도 같이 올 수 있지만
        // `downloadInProgress`가 두 번째 시작을 막는다.
        LaunchedEffect(Unit) {
            if (startNow) startDownload()
        }

        LaunchedEffect(downloadEpoch) {
            if (downloadEpoch == 0) return@LaunchedEffect
            try {
                val destination =
                    File(UpdateDownloads.dir(context).apply { mkdirs() }, UpdateDownloads.FILE_NAME)
                val downloaded = withContext(Dispatchers.IO) {
                    UpdateDownloader().download(manifest.apkUrl, destination, manifest.sha256)
                }
                // 설치 앱은 메인 스레드에서 띄운다 — 이 LaunchedEffect의 기본 디스패처다.
                val installed = when (downloaded) {
                    is UpdateDownloader.Result.Success -> UpdateInstaller.install(context, downloaded.file)
                    // 이 대화상자는 멈추라고 하지 않는다(isStopped 없음) — 여기 오지 않는다.
                    UpdateDownloader.Result.Failed, UpdateDownloader.Result.Stopped -> false
                }
                // 성공하면 이 모달이 더 할 일이 없다 — 그다음(확인, 진행, 완료/열기)은 시스템 설치
                // 앱이 그린다. 실패하면 "다시 시도"와 "브라우저에서 열기" 중 고르게 한다(디자인
                // 문서의 "실패 복구").
                if (installed) onDismiss() else flowState = DownloadFlowState.Failed
            } finally {
                // `UpdateDownloader.download()`는 취소 체크포인트가 없는 순수 블로킹 함수라, 이
                // LaunchedEffect가 취소돼도 이미 시작했다면 끝까지 실행된 뒤에야 여기 도달한다 — 그래서 가드 해제를 finally에 둬도
                // "아직 실행 중인데 풀렸다"는 일이 없다.
                downloadInProgress.set(false)
            }
        }

        // Downloading 동안은 닫을 방법을 아예 주지 않는다. 이 상태에서 시작된 네트워크 읽기는
        // 취소 체크포인트가 없는 블로킹 호출이라 다이얼로그를 나가도 실제로는 멈추지 않는다 —
        // "닫았다"고 믿었는데 잠시 뒤 설치 확인 화면이 뜨는 혼란과,
        // 그 틈에 재시도가 같은 캐시 파일을 다시 써서 생기는 경합(위 `downloadInProgress` 참조)을
        // 애초에 만들지 않는 편이 낫다.
        val canDismiss = flowState != DownloadFlowState.Downloading

        AlertDialog(
            onDismissRequest = onDismiss,
            properties = DialogProperties(
                dismissOnBackPress = canDismiss,
                dismissOnClickOutside = canDismiss,
            ),
            title = { Text("업데이트") },
            text = {
                when (flowState) {
                    DownloadFlowState.Offer -> Column {
                        Text(
                            text = "Homebase ${manifest.versionName} ($size)을 받아 설치할까요?",
                            style = MaterialTheme.typography.bodyMedium,
                        )
                        // 릴리스 노트는 설치된 앱의 CHANGELOG가 아니라 manifest의 링크를 쓴다.
                        // 지금 깔려 있는 앱은 **새 버전의** 변경 내용을 알 수 없다.
                        TextButton(onClick = { context.open(manifest.releaseNotesUrl) }) {
                            Text("변경 내용 보기")
                        }
                        TextButton(onClick = onSkip) {
                            Text("이 버전 건너뛰기")
                        }
                    }
                    DownloadFlowState.WrongSigner -> Column {
                        Text(
                            text = "새 버전 ${manifest.versionName}이 있지만, 지금 설치된 앱은 릴리스 키가 아닌 다른 키" +
                                "(개발용 빌드 등)로 서명되어 있어 그 위에 업데이트를 설치할 수 없습니다. " +
                                "이 앱을 삭제한 뒤 릴리스 페이지의 APK를 새로 설치하세요. 삭제하면 연결한 PC는 다시 연결해야 합니다.",
                            style = MaterialTheme.typography.bodyMedium,
                        )
                        TextButton(onClick = onSkip) {
                            Text("이 버전 건너뛰기")
                        }
                    }
                    DownloadFlowState.NeedsInstallPermission -> Text(
                        text = "업데이트를 설치하려면 이 앱에 '알 수 없는 앱 설치' 권한이 필요합니다. 설정에서 허용한 뒤 돌아오세요.",
                        style = MaterialTheme.typography.bodyMedium,
                    )
                    DownloadFlowState.Downloading -> Row(verticalAlignment = Alignment.CenterVertically) {
                        CircularProgressIndicator(modifier = Modifier.size(24.dp))
                        Spacer(Modifier.width(12.dp))
                        Text(
                            text = "내려받는 중…",
                            style = MaterialTheme.typography.bodyMedium,
                        )
                    }
                    DownloadFlowState.Failed -> Column {
                        Text(
                            text = "내려받기 또는 검증에 실패했습니다. 다시 시도하거나 브라우저에서 직접 받으세요.",
                            style = MaterialTheme.typography.bodyMedium,
                        )
                        // 최종 수단: 이 버튼이 눌리면 B단계와 같은 경로(브라우저에 위임)로
                        // 돌아간다. 앱이 직접 받는 경로가 실패해도 사용자는 막다른 길에 있지
                        // 않다(`docs/UPDATE_STRATEGY_EVALUATION.md` "C단계 구현 세부").
                        TextButton(
                            onClick = {
                                context.open(manifest.apkUrl)
                                onDismiss()
                            }
                        ) {
                            Text("브라우저에서 열기")
                        }
                    }
                }
            },
            confirmButton = {
                when (flowState) {
                    DownloadFlowState.Offer -> TextButton(onClick = ::startDownload) {
                        Text("업데이트")
                    }
                    // 릴리스 페이지에 APK가 붙어 있다. 설치는 이 앱을 지운 뒤 사용자가 한다.
                    DownloadFlowState.WrongSigner -> TextButton(onClick = { context.open(manifest.releaseNotesUrl) }) {
                        Text("릴리스 페이지 열기")
                    }
                    DownloadFlowState.NeedsInstallPermission -> TextButton(
                        onClick = {
                            // 권한을 바꾸면 앱이 다시 시작되는 기기가 있다 — 돌아온 뒤 이어 받을 수 있게 남겨 둔다.
                            preferences.setPendingInstall(manifest.versionCode, System.currentTimeMillis())
                            installPermissionLauncher.launch(
                                Intent(
                                    Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,
                                    Uri.parse("package:${context.packageName}"),
                                )
                            )
                        }
                    ) {
                        Text("설정 열기")
                    }
                    // 내려받는 동안은 누를 만한 주 동작이 없다 — 이 상태에서는 애초에 닫을
                    // 방법도 없다(위 `canDismiss` 참조), 기다리는 것 외엔 할 게 없다.
                    DownloadFlowState.Downloading -> Unit
                    DownloadFlowState.Failed -> TextButton(onClick = ::startDownload) {
                        Text("다시 시도")
                    }
                }
            },
            dismissButton = {
                // Downloading 상태에서는 위 `canDismiss`가 뒤로가기·바깥 탭을 이미 막았고,
                // 이 버튼도 같은 이유로 아예 그리지 않는다 — 닫을 방법이 하나라도 남아 있으면
                // 안 된다.
                if (canDismiss) {
                    TextButton(onClick = onDismiss) {
                        Text("닫기")
                    }
                }
            },
        )
    }

    /** 링크는 OS에 넘긴다. 앱이 직접 여는 브라우저는 없다 — 실패해도 조용히 넘어간다. */
    private fun Context.open(url: String) {
        runCatching { startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(url))) }
    }
}
