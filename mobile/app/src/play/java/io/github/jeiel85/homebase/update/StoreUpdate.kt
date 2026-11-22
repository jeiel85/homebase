package io.github.jeiel85.homebase.update

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.job.JobInfo
import android.app.job.JobParameters
import android.app.job.JobScheduler
import android.app.job.JobService
import android.content.ActivityNotFoundException
import android.content.BroadcastReceiver
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.util.Log
import android.widget.Toast
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import com.google.android.play.core.appupdate.AppUpdateInfo
import com.google.android.play.core.appupdate.AppUpdateManagerFactory
import com.google.android.play.core.install.InstallException
import com.google.android.play.core.install.model.InstallErrorCode
import com.google.android.play.core.install.model.UpdateAvailability
import io.github.jeiel85.homebase.BuildConfig
import io.github.jeiel85.homebase.R
import io.github.jeiel85.homebase.push.PushNotifier
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withTimeoutOrNull
import java.util.concurrent.TimeUnit
import kotlin.coroutines.resume

/*
 * Google Play 빌드의 "새 버전 알림"(DECISIONS M-57). 설치는 Play 스토어가 한다.
 *
 * Play In-App Updates API로 이 사용자가 받을 수 있는 트랙(내부·비공개 테스트 포함)에 더 높은 versionCode가
 * 있는지만 묻고, 있으면 목록 위 배너·알림으로 알린다. 누르면 Play 스토어의 Homebase 페이지가 열린다.
 * APK를 받거나 설치하는 코드와 `REQUEST_INSTALL_PACKAGES`는 여전히 이 빌드에 없다(M-33).
 */

/** 판단만 모은 곳 — 기기 없이 단위 테스트로 고정한다(`StoreUpdateRulesTest`). */
internal object StoreUpdateRules {

    /** 앱을 열 때 마지막 확인에서 이만큼 지났으면 다시 묻는다. 그 사이는 백그라운드 잡(12시간)이 맡는다. */
    val RECHECK_ON_OPEN_MILLIS = TimeUnit.HOURS.toMillis(6)

    /**
     * Play가 이 설치본을 모른다는 뜻의 오류: Play에서 받지 않은 설치본(직접 설치한 디버그·GitHub판에서 바꾼 흔적 등),
     * Play 스토어 앱이 없거나 API를 쓸 수 없는 기기. 네트워크 문제와 달리 다시 해도 같다.
     */
    private val NOT_FROM_PLAY = setOf(
        InstallErrorCode.ERROR_APP_NOT_OWNED,
        InstallErrorCode.ERROR_API_NOT_AVAILABLE,
        InstallErrorCode.ERROR_PLAY_STORE_NOT_FOUND,
    )

    sealed interface Result {
        data class Available(val versionCode: Int) : Result
        data object UpToDate : Result
        data object NotFromPlay : Result
        data object Failed : Result
    }

    /** versionCode(주×10000 + 부×100 + 수, `app/build.gradle.kts`)를 화면에 보일 "0.42.0"으로 되돌린다. */
    fun versionName(code: Int): String = "${code / 10000}.${code / 100 % 100}.${code % 100}"

    /** Play가 돌려준 값 → 결과. 더 높은 버전이라고 해도 지금 설치본보다 높지 않으면(Play 쪽 캐시가 늦음) 최신으로 본다. */
    fun fromInfo(availability: Int, availableVersionCode: Int, installedVersionCode: Int): Result = when (availability) {
        UpdateAvailability.UPDATE_AVAILABLE ->
            if (availableVersionCode > installedVersionCode) Result.Available(availableVersionCode) else Result.UpToDate
        // 개발자가 시작한 앱 안 설치 흐름이 진행 중 — 이 앱은 그 흐름을 쓰지 않으므로 알릴 것이 없다.
        UpdateAvailability.UPDATE_NOT_AVAILABLE, UpdateAvailability.DEVELOPER_TRIGGERED_UPDATE_IN_PROGRESS -> Result.UpToDate
        else -> Result.Failed
    }

    /**
     * [errorCode]: `InstallException.errorCode`, 그 밖의 예외면 null. [installedByPlay]: 이 설치본을 Play 스토어가
     * 설치했는가. Play 서비스에 붙지 못한 실패는 라이브러리 내부 예외로 와서 오류 코드가 없으므로, Play가
     * 설치하지 않은 설치본이면 오류 종류와 상관없이 "Play에서 받은 앱이 아님"으로 본다(네트워크 탓이라고 하지 않는다).
     */
    fun fromError(errorCode: Int?, installedByPlay: Boolean): Result =
        if (!installedByPlay || errorCode in NOT_FROM_PLAY) Result.NotFromPlay else Result.Failed

    /** 배너·알림으로 알릴 versionCode. 확인을 껐거나 이미 그 버전 이상이면(Play가 업데이트함) null. */
    fun offeredVersionCode(enabled: Boolean, cachedAvailableVersionCode: Int, installedVersionCode: Int): Int? =
        cachedAvailableVersionCode.takeIf { enabled && it > installedVersionCode }

    /** 알림은 버전마다 한 번. 권한·채널([visible])은 마지막에 본다 — 못 띄웠으면 알린 것으로 치지 않는다. */
    fun shouldNotify(stopped: Boolean, enabled: Boolean, offeredVersionCode: Int, notifiedVersionCode: Int, visible: () -> Boolean): Boolean =
        !stopped && enabled && offeredVersionCode > notifiedVersionCode && visible()

    /**
     * 설정 "마지막 확인" 아래 줄(#77): 마지막 성공([lastCheckedAt]) 뒤에 실패한 확인([failure])이 있으면 그 시각과 이유.
     * 성공이 더 나중이면 null. Play 밖에서 설치한 앱은 확인이 계속 실패하므로 그 사실을 여기서 말한다.
     */
    fun failureLine(failure: Failure?, lastCheckedAt: Long, now: Long): String? {
        if (failure == null || failure.at <= lastCheckedAt) return null
        val why = when (failure.result) {
            Result.NotFromPlay -> "Play 스토어에서 설치한 앱이 아니라 새 버전을 확인할 수 없습니다"
            else -> "Google Play에 묻지 못했습니다(네트워크 등). 다음 확인 때 다시 묻습니다"
        }
        return "마지막 시도: ${updateTimeLabel(failure.at, now)} · $why"
    }

    /** 실패한 확인 하나: 결과([Result.NotFromPlay]·[Result.Failed])와 시각. */
    data class Failure(val result: Result, val at: Long)

    /** 앱을 열 때 다시 물을까. 시계가 뒤로 갔으면(마지막 값이 미래) 묻는다. */
    fun isDueOnOpen(lastCheckedAt: Long, lastAttemptAt: Long, now: Long): Boolean {
        val last = maxOf(lastCheckedAt, lastAttemptAt)
        return last > now || now - last >= RECHECK_ON_OPEN_MILLIS
    }
}

/** "새 버전 알림"이 기억하는 네 값. 이 빌드에만 의미가 있어 GitHub판의 `UpdatePreferences`와 따로 둔다. */
internal class StoreUpdatePreferences(context: Context) {
    private val prefs = context.applicationContext.getSharedPreferences(NAME, Context.MODE_PRIVATE)

    /** 기본 켜짐. 설정 → 업데이트 → "새 버전 알림"에서 끈다. */
    fun isEnabled(): Boolean = prefs.getBoolean(KEY_ENABLED, true)
    fun setEnabled(enabled: Boolean) = prefs.edit().putBoolean(KEY_ENABLED, enabled).apply()

    /** 마지막으로 Play에 묻기에 **성공한** 시각. 설정 화면이 "마지막 확인"으로 보여 준다. */
    fun lastCheckedAt(): Long = prefs.getLong(KEY_LAST_CHECKED_AT, 0L)

    /** 마지막 성공한 확인에서 Play가 알려 준 더 높은 versionCode, 없었으면 0. 배너가 네트워크 없이 다시 그린다. */
    fun availableVersionCode(): Int = prefs.getInt(KEY_AVAILABLE, 0)

    fun notifiedVersionCode(): Int = prefs.getInt(KEY_NOTIFIED, 0)
    fun setNotifiedVersionCode(code: Int) = prefs.edit().putInt(KEY_NOTIFIED, code).apply()

    fun recordCheck(at: Long, availableVersionCode: Int) {
        prefs.edit().putLong(KEY_LAST_CHECKED_AT, at).putInt(KEY_AVAILABLE, availableVersionCode).apply()
    }

    /** 실패한 확인을 기억한다 — "마지막 확인"과 배너는 그대로 두고 설정 화면의 한 줄만 쓴다(#77). */
    fun recordFailure(at: Long, result: StoreUpdateRules.Result) {
        val kind = if (result == StoreUpdateRules.Result.NotFromPlay) FAILURE_NOT_FROM_PLAY else FAILURE_OTHER
        prefs.edit().putLong(KEY_FAILED_AT, at).putString(KEY_FAILURE, kind).apply()
    }

    fun lastFailure(): StoreUpdateRules.Failure? {
        val at = prefs.getLong(KEY_FAILED_AT, 0L).takeIf { it > 0L } ?: return null
        val result = if (prefs.getString(KEY_FAILURE, null) == FAILURE_NOT_FROM_PLAY) {
            StoreUpdateRules.Result.NotFromPlay
        } else {
            StoreUpdateRules.Result.Failed
        }
        return StoreUpdateRules.Failure(result, at)
    }

    fun offeredVersionCode(): Int? =
        StoreUpdateRules.offeredVersionCode(isEnabled(), availableVersionCode(), BuildConfig.VERSION_CODE)

    private companion object {
        const val NAME = "store_update"
        const val KEY_ENABLED = "enabled"
        const val KEY_LAST_CHECKED_AT = "last_checked_at"
        const val KEY_AVAILABLE = "available_version_code"
        const val KEY_NOTIFIED = "notified_version_code"
        const val KEY_FAILED_AT = "failed_at"
        const val KEY_FAILURE = "failure"
        const val FAILURE_NOT_FROM_PLAY = "not_from_play"
        const val FAILURE_OTHER = "failed"
    }
}

/** Play에 한 번 묻고 결과를 기억한다. */
internal object StoreUpdateChecker {
    private const val TAG = "HbStoreUpdate"
    private val TIMEOUT_MILLIS = TimeUnit.SECONDS.toMillis(30)

    suspend fun check(context: Context): StoreUpdateRules.Result {
        val app = context.applicationContext
        val result = withTimeoutOrNull(TIMEOUT_MILLIS) { request(app) } ?: run {
            Log.i(TAG, "Play update check timed out")
            StoreUpdateRules.Result.Failed
        }
        // 실패는 배너·"마지막 확인"을 바꾸지 않는다 — 잠깐 오프라인이었다고 배너가 사라지지 않게. 그 아래 한 줄로만 남긴다.
        when (result) {
            is StoreUpdateRules.Result.Available ->
                StoreUpdatePreferences(app).recordCheck(System.currentTimeMillis(), result.versionCode)
            StoreUpdateRules.Result.UpToDate -> {
                StoreUpdatePreferences(app).recordCheck(System.currentTimeMillis(), 0)
                StoreUpdateNotifier.cancel(app)
            }
            StoreUpdateRules.Result.NotFromPlay, StoreUpdateRules.Result.Failed ->
                StoreUpdatePreferences(app).recordFailure(System.currentTimeMillis(), result)
        }
        return result
    }

    private suspend fun request(context: Context): StoreUpdateRules.Result = suspendCancellableCoroutine { cont ->
        AppUpdateManagerFactory.create(context).appUpdateInfo
            .addOnSuccessListener { info: AppUpdateInfo ->
                cont.resume(
                    StoreUpdateRules.fromInfo(info.updateAvailability(), info.availableVersionCode(), BuildConfig.VERSION_CODE),
                )
            }
            .addOnFailureListener { e ->
                // Play에서 받지 않은 설치본은 여기로 온다(ERROR_APP_NOT_OWNED). 배경 확인에서는 알릴 일이 아니다.
                Log.i(TAG, "Play update check unavailable (${e.javaClass.name}, code ${(e as? InstallException)?.errorCode}): ${e.message}")
                cont.resume(StoreUpdateRules.fromError((e as? InstallException)?.errorCode, installedByPlay(context)))
            }
    }

    /** 실패를 설명할 때만 쓴다 — 확인 자체를 켜고 끄는 데 쓰지 않는다(M-33). */
    private fun installedByPlay(context: Context): Boolean {
        val installer = runCatching {
            if (Build.VERSION.SDK_INT >= 30) {
                context.packageManager.getInstallSourceInfo(context.packageName).installingPackageName
            } else {
                @Suppress("DEPRECATION")
                context.packageManager.getInstallerPackageName(context.packageName)
            }
        }.getOrNull()
        return installer == StoreLink.PLAY_STORE_PACKAGE
    }
}

/** 이 앱의 Play 스토어 페이지. 스토어 앱이 없으면 웹 페이지. */
internal object StoreLink {
    const val PLAY_STORE_PACKAGE = "com.android.vending"

    fun intent(context: Context): Intent {
        val pkg = context.packageName
        val market = Intent(Intent.ACTION_VIEW, Uri.parse("market://details?id=$pkg")).setPackage(PLAY_STORE_PACKAGE)
        val target = if (market.resolveActivity(context.packageManager) != null) {
            market
        } else {
            Intent(Intent.ACTION_VIEW, Uri.parse("https://play.google.com/store/apps/details?id=$pkg"))
        }
        return target.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
    }

    fun open(context: Context) {
        try {
            context.startActivity(intent(context))
        } catch (_: ActivityNotFoundException) {
            // Play 스토어도 브라우저도 없는 기기.
            Toast.makeText(context, "Play 스토어를 열 수 없습니다", Toast.LENGTH_SHORT).show()
        }
    }
}

/** 새 버전 알림. 누르면 Play 스토어로 간다. 채널·ID는 GitHub판과 같다(두 판은 함께 설치될 수 없다). */
internal object StoreUpdateNotifier {
    private const val CHANNEL_ID = "hb.app.update"
    private const val NOTIFICATION_ID = 0x0b_0001

    /** 권한이 없거나 이 채널이 꺼져 있으면 false — 그 버전을 "알렸다"고 기억하지 않는다. */
    fun canShow(context: Context): Boolean {
        if (!PushNotifier.canNotify(context)) return false
        ensureChannel(context)
        val channel = context.getSystemService(NotificationManager::class.java).getNotificationChannel(CHANNEL_ID)
        return channel != null && channel.importance != NotificationManager.IMPORTANCE_NONE
    }

    fun show(context: Context, versionCode: Int): Boolean {
        if (!canShow(context)) return false
        val open = PendingIntent.getActivity(
            context,
            NOTIFICATION_ID,
            StoreLink.intent(context),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        val notification = NotificationCompat.Builder(context, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle("Homebase ${StoreUpdateRules.versionName(versionCode)} 업데이트")
            .setContentText("눌러서 Play 스토어에서 업데이트하세요.")
            .setCategory(NotificationCompat.CATEGORY_RECOMMENDATION)
            .setPriority(NotificationCompat.PRIORITY_DEFAULT)
            .setOnlyAlertOnce(true)
            .setContentIntent(open)
            .setAutoCancel(true)
            .build()
        return try {
            NotificationManagerCompat.from(context).notify(NOTIFICATION_ID, notification)
            true
        } catch (_: SecurityException) {
            // 확인과 게시 사이에 권한이 회수됐다.
            false
        }
    }

    fun cancel(context: Context) {
        NotificationManagerCompat.from(context).cancel(NOTIFICATION_ID)
    }

    private fun ensureChannel(context: Context) {
        val nm = context.getSystemService(NotificationManager::class.java)
        if (nm.getNotificationChannel(CHANNEL_ID) != null) return
        nm.createNotificationChannel(
            NotificationChannel(CHANNEL_ID, "앱 업데이트", NotificationManager.IMPORTANCE_DEFAULT).apply {
                description = "Homebase 새 버전이 나왔을 때 한 번 알립니다"
            },
        )
    }
}

/**
 * 앱을 열지 않아도 새 버전을 알게 하는 백그라운드 확인. GitHub판(M-22)과 같은 플랫폼 `JobScheduler`,
 * 같은 12시간 주기다(WorkManager를 들이지 않는다, M-3).
 */
class StoreUpdateJobService : JobService() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    @Volatile private var running: Job? = null
    @Volatile private var stopped = false

    override fun onStartJob(params: JobParameters): Boolean {
        stopped = false
        running = scope.launch {
            try {
                val context = applicationContext
                val preferences = StoreUpdatePreferences(context)
                if (!preferences.isEnabled()) return@launch
                val result = StoreUpdateChecker.check(context)
                if (result !is StoreUpdateRules.Result.Available) return@launch
                val offered = preferences.offeredVersionCode() ?: return@launch
                // 확인하는 사이 사용자가 "새 버전 알림"을 껐거나 잡이 멈췄으면 알리지 않는다.
                val post = StoreUpdateRules.shouldNotify(
                    stopped = stopped,
                    enabled = preferences.isEnabled(),
                    offeredVersionCode = offered,
                    notifiedVersionCode = preferences.notifiedVersionCode(),
                    visible = { StoreUpdateNotifier.canShow(context) },
                )
                if (post && StoreUpdateNotifier.show(context, offered)) preferences.setNotifiedVersionCode(offered)
            } finally {
                if (!stopped) jobFinished(params, false)
            }
        }
        return true
    }

    // 주기 잡이라 다시 예약할 필요가 없다 — 다음 주기에 돈다.
    override fun onStopJob(params: JobParameters): Boolean {
        stopped = true
        running?.cancel()
        return false
    }
}

/**
 * Play가 이 앱을 업데이트하고 나면(`MY_PACKAGE_REPLACED`, 새 프로세스) 남은 "새 버전" 알림을 치운다(#77) — 설치된
 * versionCode가 알린 버전 이상이면. 그러지 않으면 다음에 앱을 열어 배너가 판단할 때까지 알림이 남는다.
 */
class StoreUpdateReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != Intent.ACTION_MY_PACKAGE_REPLACED) return
        if (StoreUpdatePreferences(context).offeredVersionCode() == null) StoreUpdateNotifier.cancel(context)
    }
}

/** [StoreUpdateJobService]의 예약을 "새 버전 알림" 설정에 맞춘다. 앱이 열릴 때와 설정을 바꿀 때 부른다. */
internal object StoreUpdateScheduler {
    // GitHub판 업데이트 확인 잡과 같은 번호 — 두 판은 한 기기에 함께 있을 수 없다. PresenceJob은 0x0b_0002.
    private const val JOB_ID = 0x0b_0001
    private val PERIOD_MILLIS = TimeUnit.HOURS.toMillis(12)
    private val FLEX_MILLIS = TimeUnit.HOURS.toMillis(1)

    /** 이미 예약돼 있으면 그대로 둔다 — 다시 `schedule`하면 주기가 처음부터 다시 세어 잡이 영영 돌지 않을 수 있다. */
    fun sync(context: Context) {
        val scheduler = context.getSystemService(JobScheduler::class.java) ?: return
        if (!StoreUpdatePreferences(context).isEnabled()) {
            scheduler.cancel(JOB_ID)
            StoreUpdateNotifier.cancel(context)
            return
        }
        if (scheduler.getPendingJob(JOB_ID) != null) return
        val job = JobInfo.Builder(JOB_ID, ComponentName(context, StoreUpdateJobService::class.java))
            .setRequiredNetworkType(JobInfo.NETWORK_TYPE_ANY)
            .setPeriodic(PERIOD_MILLIS, FLEX_MILLIS)
            .setPersisted(true)
            .build()
        scheduler.schedule(job)
    }
}
