package io.github.jeiel85.homebase.share

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.job.JobParameters
import android.app.job.JobScheduler
import android.app.job.JobService
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.os.Build
import android.util.Log
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import io.github.jeiel85.homebase.R
import io.github.jeiel85.homebase.data.PcStore
import io.github.jeiel85.homebase.net.HomebaseApi
import io.github.jeiel85.homebase.net.NetworkProbe
import io.github.jeiel85.homebase.net.TransferFile
import io.github.jeiel85.homebase.net.TransferProtocol
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull
import okhttp3.Request
import java.util.concurrent.ConcurrentHashMap

/** What an upload job carries: the PC, the transfer, and the files with the URIs the job was granted. */
internal data class UploadSpec(
    val pcId: String,
    val pcName: String,
    val transferId: String,
    val createdAt: Long,
    val files: List<OutgoingFile>,
) {
    val transferFiles: List<TransferFile> get() = files.map { it.toTransferFile() }
    val total: Long get() = files.sumOf { it.size }

    companion object {
        /** Null when the job's extras or ClipData don't describe a transfer (nothing to do). */
        fun from(params: JobParameters): UploadSpec? {
            val e = params.extras
            val pcId = e.getString(FileShare.EXTRA_PC_ID) ?: return null
            val transferId = e.getString(FileShare.EXTRA_TRANSFER_ID) ?: return null
            val names = e.getStringArray(FileShare.EXTRA_NAMES) ?: return null
            val sizes = e.getLongArray(FileShare.EXTRA_SIZES) ?: return null
            val mimes = e.getStringArray(FileShare.EXTRA_MIMES) ?: return null
            val clip = params.clipData ?: return null
            if (names.size != sizes.size || names.size != mimes.size || names.size != clip.itemCount || names.isEmpty()) return null
            val files = names.indices.map { i ->
                val uri = clip.getItemAt(i).uri ?: return null
                OutgoingFile(uri, names[i], sizes[i], mimes[i].ifEmpty { null })
            }
            return UploadSpec(pcId, e.getString(FileShare.EXTRA_PC_NAME) ?: "PC", transferId, e.getLong(FileShare.EXTRA_CREATED_AT), files)
        }
    }
}

/** Upload jobs running in this process, and transfers the user cancelled (their job must not report anything). */
internal object UploadJobs {
    val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    val cancelled: MutableSet<String> = ConcurrentHashMap.newKeySet()
}

/**
 * Runs an upload ([Uploader]) for the job [FileShare.schedule] made. Android 14+: a user-initiated data transfer
 * job, which shows its progress notification from the start and has no time limit. Older Android: a normal job
 * Android stops after about 10 minutes; it is rescheduled and goes on from the PC's status. A failure that waiting
 * can fix (no path to the PC) is retried with backoff until [TransferProtocol.KEEP_FOR_MS] after it was sent —
 * by then the PC has dropped it.
 */
class UploadJobService : JobService() {
    private val work = ConcurrentHashMap<Int, Job>()

    override fun onStartJob(params: JobParameters): Boolean {
        val spec = UploadSpec.from(params) ?: return false
        val app = applicationContext
        val first = UploadNotifier.progress(app, spec, 0)
        if (Build.VERSION.SDK_INT >= 34 && params.isUserInitiatedJob) {
            // Required within moments of starting; it stays after the job ends so the outcome can replace it.
            setNotification(params, UploadNotifier.id(spec.transferId), first, JobService.JOB_END_NOTIFICATION_POLICY_DETACH)
        } else {
            UploadNotifier.post(app, spec.transferId, first)
        }
        work[params.jobId] = UploadJobs.scope.launch {
            val expired = System.currentTimeMillis() - spec.createdAt > TransferProtocol.KEEP_FOR_MS
            val outcome = if (expired) {
                UploadOutcome.Failed("하루가 지나 보내지 못했습니다. 다시 보내세요.")
            } else {
                var lastShown = 0L
                Uploader(app, HomebaseApi(networks = NetworkProbe(app)::current), spec.pcId, spec.transferId, spec.files) { sent, total ->
                    val now = System.currentTimeMillis()
                    if (now - lastShown >= PROGRESS_EVERY_MS || sent >= total) {
                        lastShown = now
                        if (spec.transferId !in UploadJobs.cancelled) UploadNotifier.post(app, spec.transferId, UploadNotifier.progress(app, spec, sent))
                    }
                }.run()
            }
            work.remove(params.jobId)
            if (spec.transferId in UploadJobs.cancelled) return@launch
            when (outcome) {
                is UploadOutcome.Done -> {
                    UploadNotifier.post(app, spec.transferId, UploadNotifier.done(app, spec, outcome.saved))
                    jobFinished(params, false)
                }
                is UploadOutcome.Failed -> {
                    UploadNotifier.post(app, spec.transferId, UploadNotifier.failed(app, spec, outcome.reason))
                    jobFinished(params, false)
                }
                is UploadOutcome.Retry -> {
                    Log.i(TAG, "upload ${spec.transferId.take(8)} to ${spec.pcName} will be tried again: ${outcome.reason}")
                    UploadNotifier.post(app, spec.transferId, UploadNotifier.waiting(app, spec, outcome.reason))
                    jobFinished(params, true)
                }
            }
        }
        return true
    }

    /** Android stops the job (constraints, the 10-minute limit before Android 14): stop, and run again later. */
    override fun onStopJob(params: JobParameters): Boolean {
        work.remove(params.jobId)?.cancel()
        val transferId = params.extras.getString(FileShare.EXTRA_TRANSFER_ID)
        return transferId == null || transferId !in UploadJobs.cancelled
    }

    private companion object {
        const val TAG = "HomebaseShare"
        const val PROGRESS_EVERY_MS = 1_000L
    }
}

/**
 * The upload's notification (DECISIONS M-64) in a quiet channel of its own: progress with a cancel action, then
 * what came of it. One notification per transfer, replaced as it goes.
 */
internal object UploadNotifier {
    private const val CHANNEL_ID = "hb.share.out"

    fun id(transferId: String): Int = "upload:$transferId".hashCode()

    fun progress(context: Context, spec: UploadSpec, sent: Long): Notification {
        val total = spec.total
        val permille = if (total <= 0) 0 else (sent * 1000 / total).toInt().coerceIn(0, 1000)
        return builder(context)
            .setContentTitle("${spec.pcName}에 보내는 중")
            .setContentText("${FileShare.what(spec.transferFiles)} · ${FileShare.progress(sent, total)}")
            .setProgress(1000, permille, false)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .addAction(0, "취소", cancelIntent(context, spec))
            .build()
    }

    fun waiting(context: Context, spec: UploadSpec, reason: String): Notification = builder(context)
        .setContentTitle("${spec.pcName}에 보내기를 기다리는 중")
        .setContentText("$reason — 연결되면 이어서 보냅니다")
        .setOngoing(false)
        .setOnlyAlertOnce(true)
        .addAction(0, "취소", cancelIntent(context, spec))
        .build()

    fun done(context: Context, spec: UploadSpec, saved: String): Notification {
        val what = FileShare.what(spec.transferFiles)
        val text = "${spec.pcName}에 $what${FileShare.objectParticle(what)} 보냈습니다" +
            if (saved == "waiting") " — PC에 로그인하면 저장됩니다" else ""
        return builder(context)
            .setContentTitle("보냈습니다")
            .setContentText(text)
            .setStyle(NotificationCompat.BigTextStyle().bigText(text))
            .setAutoCancel(true)
            .build()
    }

    fun failed(context: Context, spec: UploadSpec, reason: String): Notification = builder(context)
        .setContentTitle("${spec.pcName}에 보내지 못했습니다")
        .setContentText(reason)
        .setStyle(NotificationCompat.BigTextStyle().bigText(reason))
        .setAutoCancel(true)
        .build()

    fun post(context: Context, transferId: String, notification: Notification) {
        if (!ensureChannel(context)) return
        try {
            NotificationManagerCompat.from(context).notify(id(transferId), notification)
        } catch (_: SecurityException) {
            // Notification permission withdrawn: the upload goes on regardless.
        }
    }

    fun cancel(context: Context, transferId: String) {
        NotificationManagerCompat.from(context).cancel(id(transferId))
    }

    private fun builder(context: Context): NotificationCompat.Builder {
        ensureChannel(context)
        return NotificationCompat.Builder(context, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_notification)
            .setCategory(NotificationCompat.CATEGORY_PROGRESS)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            // File names can be private: the lock screen says only that something is being sent.
            .setVisibility(NotificationCompat.VISIBILITY_PRIVATE)
    }

    private fun cancelIntent(context: Context, spec: UploadSpec): PendingIntent = PendingIntent.getBroadcast(
        context,
        id(spec.transferId),
        Intent(context, UploadCancelReceiver::class.java)
            .setAction(UploadCancelReceiver.ACTION)
            .putExtra(UploadCancelReceiver.EXTRA_PC_ID, spec.pcId)
            .putExtra(UploadCancelReceiver.EXTRA_TRANSFER_ID, spec.transferId),
        PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
    )

    /** False when notifications are off for the app or this channel. */
    private fun ensureChannel(context: Context): Boolean {
        val nm = context.getSystemService(NotificationManager::class.java) ?: return false
        val existing = nm.getNotificationChannel(CHANNEL_ID)
        if (existing != null) return existing.importance != NotificationManager.IMPORTANCE_NONE
        nm.createNotificationChannel(
            NotificationChannel(CHANNEL_ID, "파일 보내기", NotificationManager.IMPORTANCE_LOW).apply {
                description = "휴대폰에서 PC로 파일을 보내는 동안과 끝났을 때"
                setShowBadge(false)
                enableVibration(false)
                setSound(null, null)
            },
        )
        return true
    }
}

/**
 * "취소" on an upload's notification: the job stops, the PC drops what arrived (`DELETE /api/v1/transfer`, best
 * effort), and the notification goes. Only this app's own notification action reaches it.
 */
class UploadCancelReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != ACTION) return
        val transferId = intent.getStringExtra(EXTRA_TRANSFER_ID) ?: return
        val pcId = intent.getStringExtra(EXTRA_PC_ID) ?: return
        val app = context.applicationContext
        UploadJobs.cancelled += transferId
        app.getSystemService(JobScheduler::class.java)?.cancel(FileShare.jobId(transferId))
        UploadNotifier.cancel(app, transferId)
        val pending = goAsync()
        UploadJobs.scope.launch {
            try {
                withTimeoutOrNull(8_000) {
                    val pc = PcStore(app).get(pcId)
                    if (pc != null) {
                        try {
                            HomebaseApi(networks = NetworkProbe(app)::current)
                                .transfer(pc, "${TransferProtocol.PATH}?id=$transferId", Request.Builder().delete())
                        } catch (e: Exception) {
                            // The PC drops it by itself after a day.
                            Log.i("HomebaseShare", "cancel of ${transferId.take(8)} not delivered: ${e.javaClass.simpleName}")
                        }
                    }
                }
            } finally {
                pending.finish()
            }
        }
    }

    companion object {
        const val ACTION = "io.github.jeiel85.homebase.action.CANCEL_UPLOAD"
        const val EXTRA_PC_ID = "io.github.jeiel85.homebase.UPLOAD_PC_ID"
        const val EXTRA_TRANSFER_ID = "io.github.jeiel85.homebase.UPLOAD_TRANSFER_ID"
    }
}
