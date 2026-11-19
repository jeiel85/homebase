package io.github.jeiel85.homebase.share

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.job.JobInfo
import android.app.job.JobParameters
import android.app.job.JobScheduler
import android.app.job.JobService
import android.content.BroadcastReceiver
import android.content.ClipData
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.PersistableBundle
import android.util.Log
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import io.github.jeiel85.homebase.R
import io.github.jeiel85.homebase.clip.ClipActionActivity
import io.github.jeiel85.homebase.data.PcStore
import io.github.jeiel85.homebase.net.ClipCrypto
import io.github.jeiel85.homebase.net.ClipPush
import io.github.jeiel85.homebase.net.FileSendProtocol
import io.github.jeiel85.homebase.net.HomebaseApi
import io.github.jeiel85.homebase.net.NetworkProbe
import io.github.jeiel85.homebase.net.ShareNotice
import io.github.jeiel85.homebase.net.TransferProtocol
import io.github.jeiel85.homebase.push.RecentPushIds
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import java.time.OffsetDateTime
import java.util.concurrent.ConcurrentHashMap

/** What a download job carries: the PC, the transfer, and what the push said (for the notification). */
internal data class DownloadSpec(
    val pcId: String,
    val pcName: String,
    val transferId: String,
    val createdAt: Long,
    val count: Int,
    val total: Long,
    val name: String?,
) {
    fun toExtras(): PersistableBundle = PersistableBundle().apply {
        putString(EXTRA_PC_ID, pcId)
        putString(EXTRA_PC_NAME, pcName)
        putString(EXTRA_TRANSFER_ID, transferId)
        putLong(EXTRA_CREATED_AT, createdAt)
        putInt(EXTRA_COUNT, count)
        putLong(EXTRA_TOTAL, total)
        name?.let { putString(EXTRA_NAME, it) }
    }

    /** The same for the [받기] tap's intent (an activity intent carries a Bundle). */
    fun putInto(intent: Intent): Intent = intent.putExtras(Bundle().apply {
        putString(EXTRA_PC_ID, pcId)
        putString(EXTRA_PC_NAME, pcName)
        putString(EXTRA_TRANSFER_ID, transferId)
        putLong(EXTRA_CREATED_AT, createdAt)
        putInt(EXTRA_COUNT, count)
        putLong(EXTRA_TOTAL, total)
        name?.let { putString(EXTRA_NAME, it) }
    })

    /** "파일 2개 (12.4MB)"-style line parts. */
    val what: String get() = "파일 ${count}개"

    companion object {
        private const val EXTRA_PC_ID = "io.github.jeiel85.homebase.RECEIVE_PC_ID"
        private const val EXTRA_PC_NAME = "io.github.jeiel85.homebase.RECEIVE_PC_NAME"
        private const val EXTRA_TRANSFER_ID = "io.github.jeiel85.homebase.RECEIVE_TRANSFER_ID"
        private const val EXTRA_CREATED_AT = "io.github.jeiel85.homebase.RECEIVE_CREATED_AT"
        private const val EXTRA_COUNT = "io.github.jeiel85.homebase.RECEIVE_COUNT"
        private const val EXTRA_TOTAL = "io.github.jeiel85.homebase.RECEIVE_TOTAL"
        private const val EXTRA_NAME = "io.github.jeiel85.homebase.RECEIVE_NAME"

        fun from(e: PersistableBundle): DownloadSpec? {
            val pcId = e.getString(EXTRA_PC_ID) ?: return null
            val transferId = e.getString(EXTRA_TRANSFER_ID) ?: return null
            return DownloadSpec(pcId, e.getString(EXTRA_PC_NAME) ?: "PC", transferId, e.getLong(EXTRA_CREATED_AT),
                e.getInt(EXTRA_COUNT), e.getLong(EXTRA_TOTAL), e.getString(EXTRA_NAME))
        }

        fun from(intent: Intent?): DownloadSpec? {
            val e = intent?.extras ?: return null
            val pcId = e.getString(EXTRA_PC_ID) ?: return null
            val transferId = e.getString(EXTRA_TRANSFER_ID) ?: return null
            return DownloadSpec(pcId, e.getString(EXTRA_PC_NAME) ?: "PC", transferId, e.getLong(EXTRA_CREATED_AT),
                e.getInt(EXTRA_COUNT), e.getLong(EXTRA_TOTAL), e.getString(EXTRA_NAME))
        }
    }
}

/**
 * Files a PC sends to this phone (DECISIONS M-65): the push, and the job that fetches them. A small transfer (at most
 * [FileSendProtocol.AUTO_RECEIVE_BYTES]) on a network that isn't metered is fetched at once; anything else waits for
 * the user's [받기], which starts the job from a screen of this app — on Android 14+ as a user-initiated data transfer
 * job, the same as an upload.
 */
internal object FileReceive {
    private const val TAG = "HomebaseShare"

    /** Download jobs use ids from here up (uploads from 0x0c_000000). */
    private const val JOB_ID_BASE = 0x0d_000000

    fun jobId(transferId: String): Int = JOB_ID_BASE + transferId.substring(0, 5).toInt(16)

    val cancelled: MutableSet<String> = ConcurrentHashMap.newKeySet()

    /**
     * A decrypted push of kind `share` (called off the main thread by the push receiver). Dropped when it is not from
     * a paired PC, there is no clipboard key with that PC, it doesn't open, it was seen already, or the PC has dropped
     * the files by now (a day). Never kept in the notification center.
     */
    fun onPush(context: Context, plain: String) {
        val push = ClipPush.parse(plain, FileSendProtocol.PUSH_KIND) ?: run {
            Log.w(TAG, "dropped an unreadable file push")
            return
        }
        val pc = PcStore(context).get(push.pcId) ?: run {
            Log.i(TAG, "dropped files from an unpaired PC")
            return
        }
        val key = ClipCrypto.decodeKey(pc.clipKey) ?: run {
            Log.i(TAG, "dropped files from ${pc.name}: no clipboard key agreed with it")
            return
        }
        val notice = ShareNotice.open(key, push) ?: run {
            Log.w(TAG, "dropped files that don't open with ${pc.name}'s key")
            return
        }
        if (!RecentPushIds(context).firstSighting("share:${push.id}")) return
        val now = System.currentTimeMillis()
        if (FileSendProtocol.isExpired(notice.ts, now)) {
            Log.i(TAG, "dropped files from ${pc.name}: the PC no longer keeps them")
            return
        }
        val createdAt = runCatching { OffsetDateTime.parse(notice.ts).toInstant().toEpochMilli() }.getOrNull() ?: now
        val spec = DownloadSpec(pc.pcId, pc.displayName, push.id, createdAt, notice.count, notice.totalBytes, notice.name)
        val auto = FileSendProtocol.autoReceive(notice.totalBytes, FileShare.isMetered(context))
        // Posted before the job can start, so the job's progress replaces it rather than the other way round.
        ReceiveNotifier.post(context, spec.transferId, ReceiveNotifier.offer(context, spec, receiving = auto))
        if (auto && !schedule(context, spec, userInitiated = false)) {
            ReceiveNotifier.post(context, spec.transferId, ReceiveNotifier.offer(context, spec, receiving = false))
        }
    }

    /**
     * Starts fetching [spec]'s files. [userInitiated] (Android 14+, from a screen of this app): no time limit, its own
     * progress notification. True when the job is scheduled — or already is (a second tap doesn't restart it).
     */
    fun schedule(context: Context, spec: DownloadSpec, userInitiated: Boolean): Boolean {
        val scheduler = context.getSystemService(JobScheduler::class.java) ?: return false
        val id = jobId(spec.transferId)
        if (scheduler.getPendingJob(id) != null) return true
        cancelled -= spec.transferId

        fun build(asUser: Boolean): JobInfo {
            val builder = JobInfo.Builder(id, ComponentName(context, DownloadJobService::class.java))
                .setRequiredNetworkType(JobInfo.NETWORK_TYPE_ANY)
                .setExtras(spec.toExtras())
                .setBackoffCriteria(30_000L, JobInfo.BACKOFF_POLICY_EXPONENTIAL)
            if (Build.VERSION.SDK_INT >= 28) builder.setEstimatedNetworkBytes(spec.total + 64 * 1024L, 64 * 1024L)
            if (asUser && Build.VERSION.SDK_INT >= 34) builder.setUserInitiated(true)
            return builder.build()
        }

        return try {
            scheduler.schedule(build(asUser = userInitiated && Build.VERSION.SDK_INT >= 34)) == JobScheduler.RESULT_SUCCESS
        } catch (e: Exception) {
            Log.w(TAG, "user-initiated download job refused (${e.javaClass.simpleName}); scheduling a normal one")
            try {
                scheduler.schedule(build(asUser = false)) == JobScheduler.RESULT_SUCCESS
            } catch (e2: Exception) {
                Log.w(TAG, "download job not scheduled: ${e2.javaClass.simpleName}")
                false
            }
        }
    }
}

/**
 * Runs a download ([Downloader]). Android 14+ with the user's [받기]: a user-initiated job showing its progress from
 * the start; otherwise a normal job that Android may stop and start again — files already saved stay, the one in
 * progress is fetched again. Gives up a day after the PC sent them: the PC has dropped them by then.
 */
class DownloadJobService : JobService() {
    private val work = ConcurrentHashMap<Int, Job>()

    override fun onStartJob(params: JobParameters): Boolean {
        val spec = DownloadSpec.from(params.extras) ?: return false
        val app = applicationContext
        val first = ReceiveNotifier.progress(app, spec, 0)
        if (Build.VERSION.SDK_INT >= 34 && params.isUserInitiatedJob) {
            setNotification(params, ReceiveNotifier.id(spec.transferId), first, JobService.JOB_END_NOTIFICATION_POLICY_DETACH)
        } else {
            ReceiveNotifier.post(app, spec.transferId, first)
        }
        work[params.jobId] = UploadJobs.scope.launch {
            val expired = System.currentTimeMillis() - spec.createdAt > TransferProtocol.KEEP_FOR_MS
            val outcome = if (expired) {
                DownloadOutcome.Failed("PC에서 더 이상 받을 수 없습니다(24시간 지남)")
            } else {
                var lastShown = 0L
                Downloader(app, HomebaseApi(networks = NetworkProbe(app)::current), spec.pcId, spec.transferId) { got, total ->
                    val now = System.currentTimeMillis()
                    if (now - lastShown >= PROGRESS_EVERY_MS || got >= total) {
                        lastShown = now
                        if (spec.transferId !in FileReceive.cancelled) {
                            ReceiveNotifier.post(app, spec.transferId, ReceiveNotifier.progress(app, spec.copy(total = total), got))
                        }
                    }
                }.run()
            }
            work.remove(params.jobId)
            if (spec.transferId in FileReceive.cancelled) return@launch
            when (outcome) {
                is DownloadOutcome.Done -> {
                    ReceiveNotifier.post(app, spec.transferId, ReceiveNotifier.done(app, spec, outcome.files))
                    jobFinished(params, false)
                }
                is DownloadOutcome.Failed -> {
                    ReceiveProgress.clear(app, spec.transferId)
                    ReceiveNotifier.post(app, spec.transferId, ReceiveNotifier.failed(app, spec, outcome.reason))
                    jobFinished(params, false)
                }
                is DownloadOutcome.Retry -> {
                    Log.i(TAG, "download ${spec.transferId.take(8)} from ${spec.pcName} will be tried again: ${outcome.reason}")
                    ReceiveNotifier.post(app, spec.transferId, ReceiveNotifier.waiting(app, spec, outcome.reason))
                    jobFinished(params, true)
                }
            }
        }
        return true
    }

    /** Android stops the job (constraints, the 10-minute limit before Android 14): stop, and run again later. */
    override fun onStopJob(params: JobParameters): Boolean {
        work.remove(params.jobId)?.cancel()
        val transferId = DownloadSpec.from(params.extras)?.transferId
        return transferId != null && transferId !in FileReceive.cancelled
    }

    private companion object {
        const val TAG = "HomebaseShare"
        const val PROGRESS_EVERY_MS = 1_000L
    }
}

/**
 * Notifications of files a PC sends (DECISIONS M-65), in a quiet channel of their own ("파일 받기"): the offer with
 * [받기], progress with 취소, then [열기] and [공유] — one notification per transfer, replaced as it goes.
 */
internal object ReceiveNotifier {
    private const val CHANNEL_ID = "hb.share.in"

    fun id(transferId: String): Int = "download:$transferId".hashCode()

    /** "집 PC에서 파일 2개 (12.4MB)" — the first name and how many more; [받기] unless it is [receiving] already. */
    fun offer(context: Context, spec: DownloadSpec, receiving: Boolean): Notification {
        val first = spec.name ?: "파일"
        val more = if (spec.count > 1) " 외 ${spec.count - 1}개" else ""
        val builder = builder(context)
            .setContentTitle("${spec.pcName}에서 ${spec.what} (${FileShare.bytes(spec.total)})")
            .setContentText(first + more + if (receiving) " — 받는 중" else "")
            .setOnlyAlertOnce(true)
        if (!receiving) {
            val receive = PendingIntent.getActivity(
                context,
                id(spec.transferId),
                spec.putInto(Intent(context, ClipActionActivity::class.java).setAction(ClipActionActivity.ACTION_RECEIVE_FILES))
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
                PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
            )
            builder.setContentIntent(receive).addAction(0, "받기", receive).setAutoCancel(true)
                // The PC keeps them a day; after that a tap would only say so.
                .setTimeoutAfter((spec.createdAt + TransferProtocol.KEEP_FOR_MS - System.currentTimeMillis()).coerceAtLeast(60_000L))
        }
        return builder.build()
    }

    fun progress(context: Context, spec: DownloadSpec, got: Long): Notification {
        val total = spec.total
        val permille = if (total <= 0) 0 else (got * 1000 / total).toInt().coerceIn(0, 1000)
        return builder(context)
            .setContentTitle("${spec.pcName}에서 받는 중")
            .setContentText("${spec.what} · ${FileShare.progress(got, total)}")
            .setProgress(1000, permille, false)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .addAction(0, "취소", cancelIntent(context, spec))
            .build()
    }

    fun waiting(context: Context, spec: DownloadSpec, reason: String): Notification = builder(context)
        .setContentTitle("${spec.pcName}에서 받기를 기다리는 중")
        .setContentText("$reason — 연결되면 이어서 받습니다")
        .setOnlyAlertOnce(true)
        .addAction(0, "취소", cancelIntent(context, spec))
        .build()

    fun done(context: Context, spec: DownloadSpec, files: List<SavedFile>): Notification {
        val text = "${spec.pcName}에서 파일 ${files.size}개를 받았습니다"
        val names = files.joinToString(", ") { it.name }
        val builder = builder(context)
            .setContentTitle(text)
            .setContentText(names)
            .setStyle(NotificationCompat.BigTextStyle().bigText(names))
            .setAutoCancel(true)
        if (files.isNotEmpty()) {
            val base = id(spec.transferId)
            val open = PendingIntent.getActivity(context, base + 1, openIntent(context, files.first()),
                PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
            val share = PendingIntent.getActivity(context, base + 2, shareIntent(files),
                PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
            builder.setContentIntent(open).addAction(0, "열기", open).addAction(0, "공유", share)
        }
        return builder.build()
    }

    fun failed(context: Context, spec: DownloadSpec, reason: String): Notification = builder(context)
        .setContentTitle("${spec.pcName}에서 받지 못했습니다")
        .setContentText(reason)
        .setStyle(NotificationCompat.BigTextStyle().bigText(reason))
        .setAutoCancel(true)
        .build()

    fun post(context: Context, transferId: String, notification: Notification) {
        if (!ensureChannel(context)) return
        try {
            NotificationManagerCompat.from(context).notify(id(transferId), notification)
        } catch (_: SecurityException) {
            // Notification permission withdrawn: the files are still saved.
        }
    }

    fun cancel(context: Context, transferId: String) {
        NotificationManagerCompat.from(context).cancel(id(transferId))
    }

    /** Opens the (first) file in whatever app handles its type; the grant lets that app read it. */
    private fun openIntent(context: Context, file: SavedFile): Intent {
        val type = try {
            context.contentResolver.getType(file.uri)
        } catch (_: Exception) {
            null
        } ?: file.mime ?: "*/*"
        return Intent(Intent.ACTION_VIEW)
            .setDataAndType(file.uri, type)
            .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
    }

    /** The share sheet with every received file; the ClipData carries the read grants. */
    private fun shareIntent(files: List<SavedFile>): Intent {
        val uris = ArrayList<Uri>(files.map { it.uri })
        val types = files.map { it.mime ?: "*/*" }.distinct()
        val send = if (uris.size == 1) {
            Intent(Intent.ACTION_SEND).setType(types.single()).putExtra(Intent.EXTRA_STREAM, uris[0])
        } else {
            Intent(Intent.ACTION_SEND_MULTIPLE).setType(types.singleOrNull() ?: "*/*").putParcelableArrayListExtra(Intent.EXTRA_STREAM, uris)
        }
        send.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        send.clipData = ClipData.newRawUri("Homebase", uris[0]).also { clip -> uris.drop(1).forEach { clip.addItem(ClipData.Item(it)) } }
        return Intent.createChooser(send, "공유").addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
    }

    private fun cancelIntent(context: Context, spec: DownloadSpec): PendingIntent = PendingIntent.getBroadcast(
        context,
        id(spec.transferId),
        Intent(context, DownloadCancelReceiver::class.java)
            .setAction(DownloadCancelReceiver.ACTION)
            .putExtra(DownloadCancelReceiver.EXTRA_TRANSFER_ID, spec.transferId),
        PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
    )

    private fun builder(context: Context): NotificationCompat.Builder {
        ensureChannel(context)
        return NotificationCompat.Builder(context, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_notification)
            .setCategory(NotificationCompat.CATEGORY_PROGRESS)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            // File names can be private: the lock screen says only that files came.
            .setVisibility(NotificationCompat.VISIBILITY_PRIVATE)
    }

    /** False when notifications are off for the app or this channel. */
    private fun ensureChannel(context: Context): Boolean {
        val nm = context.getSystemService(NotificationManager::class.java) ?: return false
        val existing = nm.getNotificationChannel(CHANNEL_ID)
        if (existing != null) return existing.importance != NotificationManager.IMPORTANCE_NONE
        nm.createNotificationChannel(
            NotificationChannel(CHANNEL_ID, "파일 받기", NotificationManager.IMPORTANCE_LOW).apply {
                description = "PC가 보낸 파일이 왔을 때와 받는 동안"
                setShowBadge(false)
                enableVibration(false)
                setSound(null, null)
            },
        )
        return true
    }
}

/**
 * "취소" on a download's notification: the job stops — the file being written is thrown away by the job itself — and
 * the notification goes. Files already saved stay. Only this app's own notification action reaches it.
 */
class DownloadCancelReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != ACTION) return
        val transferId = intent.getStringExtra(EXTRA_TRANSFER_ID) ?: return
        val app = context.applicationContext
        FileReceive.cancelled += transferId
        app.getSystemService(JobScheduler::class.java)?.cancel(FileReceive.jobId(transferId))
        ReceiveProgress.clear(app, transferId)
        ReceiveNotifier.cancel(app, transferId)
    }

    companion object {
        const val ACTION = "io.github.jeiel85.homebase.action.CANCEL_DOWNLOAD"
        const val EXTRA_TRANSFER_ID = "io.github.jeiel85.homebase.DOWNLOAD_TRANSFER_ID"
    }
}
