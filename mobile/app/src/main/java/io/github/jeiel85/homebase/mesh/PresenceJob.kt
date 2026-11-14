package io.github.jeiel85.homebase.mesh

import android.app.job.JobInfo
import android.app.job.JobParameters
import android.app.job.JobScheduler
import android.app.job.JobService
import android.content.ComponentName
import android.content.Context
import io.github.jeiel85.homebase.data.PcStore
import io.github.jeiel85.homebase.net.HomebaseApi
import io.github.jeiel85.homebase.net.NetworkProbe
import io.github.jeiel85.homebase.push.PushRegistrar
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import java.util.concurrent.TimeUnit

/**
 * Tells the paired PCs this phone's state about every 30 minutes, so other devices see its battery
 * and that it is around even when the app is closed (DECISIONS M-27). Platform JobScheduler, like
 * the update check (M-22); Android may stretch the period in Doze — the PCs then show the phone as
 * "seen N minutes ago", which is the truth.
 */
class PresenceJobService : JobService() {
    private var work: Job? = null

    override fun onStartJob(params: JobParameters): Boolean {
        val app = applicationContext
        work = PushRegistrar.scope.launch {
            try {
                MeshSync.reportAll(app, HomebaseApi(networks = NetworkProbe(app)::current), PcStore(app))
            } finally {
                jobFinished(params, false)
            }
        }
        return true
    }

    override fun onStopJob(params: JobParameters): Boolean {
        work?.cancel()
        return false
    }
}

internal object PresenceScheduler {
    private const val JOB_ID = 0x0b_0002
    private val PERIOD_MILLIS = TimeUnit.MINUTES.toMillis(30)
    private val FLEX_MILLIS = TimeUnit.MINUTES.toMillis(10)

    /** Scheduled while at least one PC is paired; cancelled when none is. Called when the app opens. */
    fun sync(context: Context) {
        val scheduler = context.getSystemService(JobScheduler::class.java) ?: return
        if (PcStore(context).all().isEmpty()) {
            scheduler.cancel(JOB_ID)
            return
        }
        // Rescheduling an existing periodic job restarts its period; leave it alone (see UpdateCheckScheduler).
        if (scheduler.getPendingJob(JOB_ID) != null) return
        scheduler.schedule(
            JobInfo.Builder(JOB_ID, ComponentName(context, PresenceJobService::class.java))
                .setRequiredNetworkType(JobInfo.NETWORK_TYPE_ANY)
                .setPeriodic(PERIOD_MILLIS, FLEX_MILLIS)
                .setPersisted(true)
                .build(),
        )
    }
}
