package io.github.jeiel85.homebase.update

import android.app.Activity
import android.app.Application
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.util.Log
import androidx.annotation.RequiresApi
import io.github.jeiel85.homebase.BuildConfig
import java.io.File
import java.util.concurrent.atomic.AtomicBoolean

/**
 * 앱을 쓰지 않는 동안 새 버전을 확인 없이 설치한다 (DECISIONS M-56).
 *
 * 백그라운드 확인 잡([UpdateCheckJobService])이 새 버전을 찾았을 때 [UpdateDecision.shouldInstallSilently]가
 * 허락하면, 알림 대신 여기서 받아 `PackageInstaller` 세션으로 넘긴다. Android 12+는 이 앱이 지금 설치본의
 * 설치자(installer of record)이면 `USER_ACTION_NOT_REQUIRED` 세션을 확인 없이 설치한다. 처음 자체
 * 업데이트(브라우저·파일 앱으로 설치한 경우)나 다른 이유로 시스템이 확인을 요구하면, 세션을 버리고
 * 지금까지의 알림으로 돌아간다([UpdateInstallReceiver]).
 *
 * 앱을 연 채 사용자가 누르는 설치는 그대로 [UpdateInstaller](`ACTION_INSTALL_PACKAGE`)다. 거기 적힌
 * "세션으로 자기 자신을 업데이트하면 앱이 말없이 닫힌다"는 문제는, 화면에 앱이 없을 때만 이 경로를
 * 쓰는 것으로 피한다([AppVisibility]).
 *
 * 받은 파일은 manifest의 SHA-256과 대조하고(같은 [UpdateDownloader]), 패키지 이름·versionCode가
 * manifest와 같은지 본다. 서명은 OS가 확인한다 — 다른 키의 APK는 세션이 실패로 끝난다.
 */
internal object SilentInstaller {
    private const val TAG = "HbSilentUpdate"
    const val ACTION_STATUS = "io.github.jeiel85.homebase.update.INSTALL_STATUS"
    const val EXTRA_VERSION_CODE = "versionCode"

    /** [UpdateDecision.shouldInstallSilently]에 지금 상태를 채워 묻는다. */
    fun shouldTry(context: Context, preferences: UpdatePreferences, manifest: UpdateManifest): Boolean =
        UpdateDecision.shouldInstallSilently(
            autoInstall = preferences.isAutoInstallEnabled(),
            deviceSdk = Build.VERSION.SDK_INT,
            appVisible = AppVisibility.visible,
            canUpdateInPlace = UpdateSigner.canUpdateInPlace(context),
            canRequestInstalls = context.packageManager.canRequestPackageInstalls(),
            offeredVersionCode = manifest.versionCode,
            needsConfirmVersionCode = preferences.needsConfirmVersionCode(),
            failedVersionCode = preferences.silentFailedVersionCode(),
            failedAttempts = preferences.silentFailures(),
        )

    /**
     * Input : 컨텍스트, 설치할 manifest, 잡이 중단됐는지
     * Output: 세션을 커밋했는가. 결과는 나중에 [UpdateInstallReceiver]로 온다. false면 호출부가 알림으로 알린다.
     *
     * 블로킹 다운로드가 있으므로 작업 스레드에서 부른다. 받는 사이 앱이 화면에 올라왔거나 잡이
     * 멈췄으면 커밋하지 않는다 — 다음 확인에서 다시 한다.
     */
    fun install(context: Context, manifest: UpdateManifest, isStopped: () -> Boolean): Boolean {
        if (Build.VERSION.SDK_INT < UpdateDecision.SILENT_INSTALL_MIN_SDK) return false
        // 사용자가 대화상자에서 받는 중이면 같은 파일을 쓰게 된다 — 그쪽에 맡긴다.
        if (!UpdateDownloads.inProgress.compareAndSet(false, true)) return false
        val preferences = UpdatePreferences(context)
        val file = File(UpdateDownloads.dir(context).apply { mkdirs() }, UpdateDownloads.FILE_NAME)
        try {
            val downloaded = UpdateDownloader().download(manifest.apkUrl, file, manifest.sha256, isStopped)
            if (downloaded == UpdateDownloader.Result.Stopped) {
                // 잡이 멈췄다 — 실패가 아니다. 다음 확인에서 다시 받는다.
                Log.i(TAG, "download of ${manifest.versionCode} stopped with the job")
                return false
            }
            if (downloaded !is UpdateDownloader.Result.Success) {
                Log.w(TAG, "download or checksum failed for ${manifest.versionCode}")
                preferences.addSilentFailure(manifest.versionCode)
                preferences.recordAutoInstall(AutoInstallResult.Outcome.DOWNLOAD_FAILED, manifest.versionCode)
                return false
            }
            if (!isExpectedArchive(context, file, manifest.versionCode)) {
                Log.w(TAG, "downloaded APK is not ${context.packageName} ${manifest.versionCode}")
                preferences.addSilentFailure(manifest.versionCode)
                preferences.recordAutoInstall(AutoInstallResult.Outcome.INSTALL_FAILED, manifest.versionCode, status = 4)
                return false
            }
            // 받는 사이 앱이 화면에 올라왔거나 잡이 멈췄거나, 사용자가 설정에서 "새 버전 자동 설치"를
            // 껐을 수 있다. 끈 경우엔 그 선택대로 알림만 남긴다(false → 호출부가 알린다).
            if (isStopped() || AppVisibility.visible || !preferences.isAutoInstallEnabled()) return false
            return commit(context, file, manifest.versionCode, preferences)
        } catch (e: Exception) {
            Log.w(TAG, "silent install failed to start", e)
            preferences.addSilentFailure(manifest.versionCode)
            preferences.recordAutoInstall(AutoInstallResult.Outcome.INSTALL_FAILED, manifest.versionCode)
            return false
        } finally {
            // 세션이 바이트를 이미 복사했다 — 파일은 더 쓸 데가 없다.
            file.delete()
            UpdateDownloads.inProgress.set(false)
        }
    }

    /** 받은 파일이 이 앱의 바로 그 versionCode인가. 서명은 세션에서 OS가 본다. */
    @RequiresApi(31)
    private fun isExpectedArchive(context: Context, apk: File, versionCode: Int): Boolean {
        val pm = context.packageManager
        val info = if (Build.VERSION.SDK_INT >= 33) {
            pm.getPackageArchiveInfo(apk.path, PackageManager.PackageInfoFlags.of(0))
        } else {
            @Suppress("DEPRECATION")
            pm.getPackageArchiveInfo(apk.path, 0)
        } ?: return false
        return info.packageName == context.packageName && info.longVersionCode == versionCode.toLong()
    }

    @RequiresApi(31)
    private fun commit(context: Context, apk: File, versionCode: Int, preferences: UpdatePreferences): Boolean {
        val installer = context.packageManager.packageInstaller
        // 지난 시도가 남긴 세션(확인을 기다리다 버려지지 않은 것 등)을 치운다.
        installer.mySessions.forEach { runCatching { installer.abandonSession(it.sessionId) } }
        val params = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL).apply {
            setAppPackageName(context.packageName)
            setSize(apk.length())
            setInstallReason(PackageManager.INSTALL_REASON_USER)
            setRequireUserAction(PackageInstaller.SessionParams.USER_ACTION_NOT_REQUIRED)
            // 다른 설치 앱이 이 앱을 몰래 바꾸지 못하게 한다(그때는 사용자 확인). API 34+.
            if (Build.VERSION.SDK_INT >= 34) setRequestUpdateOwnership(true)
        }
        val id = installer.createSession(params)
        try {
            installer.openSession(id).use { session ->
                session.openWrite("base.apk", 0, apk.length()).use { out ->
                    apk.inputStream().use { it.copyTo(out) }
                    session.fsync(out)
                }
                preferences.setSilentInstallingVersionCode(versionCode)
                val status = PendingIntent.getBroadcast(
                    context,
                    id,
                    Intent(context, UpdateInstallReceiver::class.java)
                        .setAction(ACTION_STATUS)
                        .putExtra(EXTRA_VERSION_CODE, versionCode),
                    // 시스템이 결과 extra를 채워야 하므로 mutable이다. 명시적 인텐트라 API 34+에서도 허용된다.
                    PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_MUTABLE,
                )
                session.commit(status.intentSender)
            }
            Log.i(TAG, "committed silent install of $versionCode")
            return true
        } catch (e: Exception) {
            runCatching { installer.abandonSession(id) }
            throw e
        }
    }
}

/**
 * 설치 세션의 결과와 "이 앱이 방금 교체됐다"(`MY_PACKAGE_REPLACED`)를 받는다 (DECISIONS M-56).
 * 매니페스트에서 exported=false — 결과는 이 앱의 PendingIntent로, 교체 알림은 시스템이 보낸다.
 */
class UpdateInstallReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        when (intent.action) {
            Intent.ACTION_MY_PACKAGE_REPLACED -> onReplaced(context)
            SilentInstaller.ACTION_STATUS -> {
                val versionCode = intent.getIntExtra(SilentInstaller.EXTRA_VERSION_CODE, 0)
                val status = intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE)
                if (status == PackageInstaller.STATUS_SUCCESS) return // 새 프로세스에서 MY_PACKAGE_REPLACED가 온다.
                val preferences = UpdatePreferences(context)
                preferences.setSilentInstallingVersionCode(0)
                if (status == PackageInstaller.STATUS_PENDING_USER_ACTION) {
                    // 처음 자체 업데이트(다른 설치자가 설치한 앱)·확인을 요구하는 기기. 확인 화면을 갑자기
                    // 띄우지 않고 세션을 버린 뒤, 지금까지처럼 알림으로 알린다. 이 버전은 다시 받지 않는다.
                    val sessionId = intent.getIntExtra(PackageInstaller.EXTRA_SESSION_ID, -1)
                    if (sessionId >= 0) runCatching { context.packageManager.packageInstaller.abandonSession(sessionId) }
                    preferences.setNeedsConfirmVersionCode(versionCode)
                    preferences.recordAutoInstall(AutoInstallResult.Outcome.NEEDS_CONFIRM, versionCode)
                    Log.i(TAG, "system asks to confirm $versionCode; falling back to the notification")
                } else {
                    preferences.addSilentFailure(versionCode)
                    preferences.recordAutoInstall(AutoInstallResult.Outcome.INSTALL_FAILED, versionCode, status)
                    Log.w(TAG, "silent install of $versionCode failed: $status ${intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE)}")
                }
                // 새 버전이 있다는 사실은 알려야 한다. 캐시된 manifest로만 판단한다(네트워크 없음).
                val pending = goAsync()
                Thread({
                    try {
                        UpdateRefresh.offeredFromCache(preferences)?.let { UpdateCheckJobService.notifyIfDue(context, it) }
                    } finally {
                        pending.finish()
                    }
                }, "hb-update-fallback").start()
            }
        }
    }

    /** 교체된 뒤 새 프로세스에서 한 번. 확인 없이 설치한 버전이면 "업데이트했습니다"를 알린다. */
    private fun onReplaced(context: Context) {
        val preferences = UpdatePreferences(context)
        val installed = preferences.silentInstallingVersionCode()
        if (installed != 0) preferences.setSilentInstallingVersionCode(0)
        UpdateDownloads.dir(context).listFiles()?.forEach { it.delete() }
        if (installed == BuildConfig.VERSION_CODE) {
            preferences.recordAutoInstall(AutoInstallResult.Outcome.INSTALLED, installed)
            UpdateNotifier.showUpdated(context, BuildConfig.VERSION_NAME)
        } else {
            // 사용자가 직접 설치했다 — "새 버전이 있습니다" 알림이 남아 있으면 치운다.
            UpdateNotifier.cancel(context)
        }
    }

    private companion object {
        const val TAG = "HbSilentUpdate"
    }
}

/**
 * 이 앱의 화면이 보이는가 — 시작된(onStart~onStop) 액티비티 수. QR 스캐너(zxing `CaptureActivity`)도
 * 이 앱 화면이므로 MainActivity 하나가 아니라 모든 액티비티를 센다. 프로세스가 없던 상태에서 잡이
 * 깨우면 0이라 보이지 않는 것으로 맞다.
 */
internal object AppVisibility {
    @Volatile
    var visible: Boolean = false
        private set

    private var started = 0
    private val registered = AtomicBoolean(false)

    /** 처음 만들어진 액티비티(MainActivity.onCreate)에서 부른다 — 그 액티비티의 onStart부터 센다. */
    fun register(application: Application) {
        if (!registered.compareAndSet(false, true)) return
        application.registerActivityLifecycleCallbacks(object : Application.ActivityLifecycleCallbacks {
            override fun onActivityStarted(activity: Activity) {
                started++
                visible = started > 0
            }

            override fun onActivityStopped(activity: Activity) {
                started = (started - 1).coerceAtLeast(0)
                visible = started > 0
            }

            override fun onActivityCreated(activity: Activity, savedInstanceState: Bundle?) = Unit
            override fun onActivityResumed(activity: Activity) = Unit
            override fun onActivityPaused(activity: Activity) = Unit
            override fun onActivitySaveInstanceState(activity: Activity, outState: Bundle) = Unit
            override fun onActivityDestroyed(activity: Activity) = Unit
        })
    }
}

/**
 * 업데이트 APK를 받는 자리와, 프로세스 전체에서 하나만 받게 하는 가드. 사용자가 누른 받기
 * ([UpdateSurface])와 백그라운드 받기([SilentInstaller])가 같은 파일을 동시에 쓰지 않게 한다.
 */
internal object UpdateDownloads {
    val inProgress = AtomicBoolean(false)
    const val FILE_NAME = "update.apk"

    // 외부 저장소 권한을 피하려고 앱 캐시에 받는다. Android가 캐시를 비울 때 같이 지워져도 된다.
    fun dir(context: Context): File = File(context.cacheDir, "updates")
}
