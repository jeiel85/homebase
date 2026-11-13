package io.github.jeiel85.homebase.update

import android.app.job.JobInfo
import android.app.job.JobParameters
import android.app.job.JobScheduler
import android.app.job.JobService
import android.content.ComponentName
import android.content.Context
import android.util.Log
import java.util.concurrent.TimeUnit

/**
 * 앱을 열지 않아도 새 버전을 알게 하는 백그라운드 확인(DECISIONS M-22).
 *
 * 플랫폼 `JobScheduler`를 쓴다 — WorkManager는 이 일 하나에 의존성을 들일 만큼 하는 일이 없다
 * (M-3). 네트워크가 있을 때만, 12시간마다 깨어나되 실제 GET은 [UpdateRefresh]가 하루 1회로 거른다.
 * 주기를 하루보다 짧게 잡는 이유: 잡이 마지막 확인 23시간 뒤에 깨면 이번엔 건너뛰고 다음 깨어남을
 * 기다리므로, 주기가 하루면 최악 48시간이 된다. 12시간이면 최악 36시간이다.
 */
class UpdateCheckJobService : JobService() {

    override fun onStartJob(params: JobParameters): Boolean {
        // onStartJob은 메인 스레드다. 확인은 블로킹 GET이라 스레드로 넘긴다.
        Thread({
            try {
                runCheck(applicationContext)
            } catch (e: Exception) {
                // 배경 확인의 실패는 알릴 일이 아니다(UpdateChecker와 같은 계약). 다음 주기에 다시 돈다.
                Log.w(TAG, "background update check failed", e)
            } finally {
                jobFinished(params, false)
            }
        }, "hb-update-check").start()
        return true
    }

    // 잡이 중단되면(네트워크 끊김 등) 다시 예약할 필요는 없다 — 주기 잡이라 다음 주기에 돈다.
    override fun onStopJob(params: JobParameters): Boolean = false

    internal companion object {
        private const val TAG = "HbUpdateJob"

        /**
         * 확인 한 번. 알릴 버전이 있고 아직 알리지 않았다면 알림을 띄우고 그 버전을 기억한다.
         * 알림 권한이 없어 못 띄웠으면 기억하지 않는다 — 나중에 권한을 주면 그때 알린다.
         */
        fun runCheck(context: Context) {
            val preferences = UpdatePreferences(context)
            val manifest = UpdateRefresh.run(preferences) ?: return
            if (!UpdateDecision.shouldNotify(manifest.versionCode, preferences.notifiedVersionCode())) return
            if (UpdateNotifier.show(context, manifest)) {
                preferences.setNotifiedVersionCode(manifest.versionCode)
            }
        }
    }
}

/** [UpdateCheckJobService]의 예약을 "업데이트 확인" 설정에 맞춘다. */
internal object UpdateCheckScheduler {
    private const val JOB_ID = 0x0b_0001
    private val PERIOD_MILLIS = TimeUnit.HOURS.toMillis(12)
    private val FLEX_MILLIS = TimeUnit.HOURS.toMillis(1)

    /**
     * 켜져 있으면 예약하고(이미 있으면 그대로 둔다), 꺼져 있으면 취소한다. 앱이 열릴 때마다 부른다.
     *
     * 이미 예약된 잡을 다시 `schedule`하지 않는 이유: 같은 ID로 다시 예약하면 주기가 처음부터
     * 다시 센다. 앱을 자주 여는 사용자는 잡이 영영 돌지 않게 된다.
     */
    fun sync(context: Context) {
        val scheduler = context.getSystemService(JobScheduler::class.java) ?: return
        if (!UpdatePreferences(context).isEnabled()) {
            scheduler.cancel(JOB_ID)
            UpdateNotifier.cancel(context)
            return
        }
        if (scheduler.getPendingJob(JOB_ID) != null) return
        val job = JobInfo.Builder(JOB_ID, ComponentName(context, UpdateCheckJobService::class.java))
            .setRequiredNetworkType(JobInfo.NETWORK_TYPE_ANY)
            // flex를 주지 않으면 flex = 주기라 12시간 창 아무 데서나 돌 수 있어, 두 실행 사이가
            // 거의 24시간까지 벌어진다. 창 끝 1시간으로 좁혀 간격을 12시간 안팎으로 둔다.
            .setPeriodic(PERIOD_MILLIS, FLEX_MILLIS)
            // 재부팅 뒤에도 남게 한다(RECEIVE_BOOT_COMPLETED 권한 필요).
            .setPersisted(true)
            .build()
        scheduler.schedule(job)
    }
}
