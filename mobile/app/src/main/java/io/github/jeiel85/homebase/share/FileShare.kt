package io.github.jeiel85.homebase.share

import android.app.job.JobInfo
import android.app.job.JobScheduler
import android.content.ClipData
import android.content.ComponentName
import android.content.ContentResolver
import android.content.Context
import android.content.Intent
import android.net.ConnectivityManager
import android.net.Uri
import android.os.Build
import android.os.PersistableBundle
import android.provider.OpenableColumns
import android.util.Log
import android.webkit.MimeTypeMap
import io.github.jeiel85.homebase.data.PcEntry
import io.github.jeiel85.homebase.net.ClipProtocol
import io.github.jeiel85.homebase.net.TransferFile
import io.github.jeiel85.homebase.net.TransferProtocol
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.util.Locale

/** A file to send: where to read it and how it is described to the PC. */
data class OutgoingFile(val uri: Uri, val name: String, val size: Long, val mime: String?) {
    fun toTransferFile() = TransferFile(name, size, mime)
}

/** A file this phone can't read (gone, or the app that shared it took the permission back). */
class UnreadableFileException(val name: String, cause: Throwable? = null) : Exception(name, cause)

/**
 * Files from this phone to a PC (DECISIONS M-64, docs/FILE_SHARE.md 2a): what the share sheet, a PC card's
 * "파일 보내기" and the quick settings tile have in common — reading what was picked, checking the limits, and
 * handing the upload to a job ([UploadJobService]) that goes on after the screen is gone.
 */
object FileShare {
    private const val TAG = "HomebaseShare"

    /** Over this much on a metered network the user is asked first. */
    const val METERED_ASK_BYTES = 20_000_000L

    /** Upload jobs use ids from here up (the update check and presence jobs are 0x0b_0001 and 0x0b_0002). */
    private const val JOB_ID_BASE = 0x0c_000000

    // Job extras: the PC, the transfer, and the files in the order of the job's ClipData.
    internal const val EXTRA_PC_ID = "pcId"
    internal const val EXTRA_PC_NAME = "pcName"
    internal const val EXTRA_TRANSFER_ID = "transferId"
    internal const val EXTRA_CREATED_AT = "createdAt"
    internal const val EXTRA_NAMES = "names"
    internal const val EXTRA_SIZES = "sizes"
    internal const val EXTRA_MIMES = "mimes"

    /** The job of transfer [transferId] (32 hex characters). */
    fun jobId(transferId: String): Int = JOB_ID_BASE + transferId.substring(0, 5).toInt(16)

    /**
     * Only `content://` items another app shared or the user picked. A `file://` path, or one of this app's own
     * providers, could make the app send its own private files.
     */
    fun acceptable(context: Context, uri: Uri): Boolean =
        uri.scheme == ContentResolver.SCHEME_CONTENT && uri.authority?.startsWith(context.packageName) != true

    /**
     * Name, size and type of each of [uris], read off the main thread. A provider that doesn't tell the size gets
     * its stream counted once. Throws [UnreadableFileException] for one that can't be read.
     */
    suspend fun describe(context: Context, uris: List<Uri>): List<OutgoingFile> = withContext(Dispatchers.IO) {
        val resolver = context.contentResolver
        uris.mapIndexed { i, uri ->
            var name: String? = null
            var size: Long? = null
            try {
                resolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE), null, null, null)?.use { c ->
                    if (c.moveToFirst()) {
                        val ni = c.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                        if (ni >= 0 && !c.isNull(ni)) name = c.getString(ni)
                        val si = c.getColumnIndex(OpenableColumns.SIZE)
                        if (si >= 0 && !c.isNull(si)) size = c.getLong(si)
                    }
                }
                val mime = resolver.getType(uri)
                val shown = fileName(name ?: uri.lastPathSegment, mime, i)
                val bytes = size?.takeIf { it > 0 } ?: count(resolver, uri, shown)
                OutgoingFile(uri, shown, bytes, mime)
            } catch (e: UnreadableFileException) {
                throw e
            } catch (e: Exception) {
                Log.w(TAG, "could not read a shared file: ${e.javaClass.simpleName}")
                throw UnreadableFileException(name ?: "파일 ${i + 1}", e)
            }
        }
    }

    private fun count(resolver: ContentResolver, uri: Uri, name: String): Long {
        val input = resolver.openInputStream(uri) ?: throw UnreadableFileException(name)
        input.use {
            val buffer = ByteArray(64 * 1024)
            var total = 0L
            while (true) {
                val r = it.read(buffer)
                if (r < 0) return total
                total += r
            }
        }
    }

    /** A name for the PC: what the provider said, else "file-N" with an extension from the type. */
    internal fun fileName(provided: String?, mime: String?, index: Int): String {
        val trimmed = provided?.substringAfterLast('/')?.trim().orEmpty()
        if (trimmed.isNotEmpty()) return trimmed
        val ext = mime?.let { MimeTypeMap.getSingleton().getExtensionFromMimeType(it) }
        return "file-${index + 1}" + (ext?.let { ".$it" } ?: "")
    }

    fun isMetered(context: Context): Boolean =
        context.getSystemService(ConnectivityManager::class.java)?.isActiveNetworkMetered == true

    /**
     * Hands [files] to an upload job for [pc]. The job carries the read permission for the files ([ClipData]
     * grants) rather than copies of them. Android 14+: a user-initiated data transfer job — no time limit, its own
     * progress notification; it can only be scheduled while a screen of this app is in front, which is when this
     * runs. Older Android: a normal job that Android may stop and start again; it goes on from where the PC is.
     * Returns the transfer id, or null when the job could not be scheduled.
     */
    fun schedule(context: Context, pc: PcEntry, files: List<OutgoingFile>): String? {
        val scheduler = context.getSystemService(JobScheduler::class.java) ?: return null
        val transferId = ClipProtocol.newId()
        // Raw URIs: the ClipData is only how the job gets the read grants, so the providers aren't asked for types.
        val clip = ClipData.newRawUri("Homebase", files.first().uri)
        files.drop(1).forEach { clip.addItem(ClipData.Item(it.uri)) }
        val extras = PersistableBundle().apply {
            putString(EXTRA_PC_ID, pc.pcId)
            putString(EXTRA_PC_NAME, pc.displayName)
            putString(EXTRA_TRANSFER_ID, transferId)
            putLong(EXTRA_CREATED_AT, System.currentTimeMillis())
            putStringArray(EXTRA_NAMES, files.map { it.name }.toTypedArray())
            putLongArray(EXTRA_SIZES, files.map { it.size }.toLongArray())
            putStringArray(EXTRA_MIMES, files.map { it.mime.orEmpty() }.toTypedArray())
        }
        val total = files.sumOf { it.size }

        fun build(userInitiated: Boolean): JobInfo {
            val builder = JobInfo.Builder(jobId(transferId), ComponentName(context, UploadJobService::class.java))
                .setRequiredNetworkType(JobInfo.NETWORK_TYPE_ANY)
                .setClipData(clip, Intent.FLAG_GRANT_READ_URI_PERMISSION)
                .setExtras(extras)
                .setBackoffCriteria(30_000L, JobInfo.BACKOFF_POLICY_EXPONENTIAL)
            if (Build.VERSION.SDK_INT >= 28) {
                // Chunks go up with a 16-byte tag each; nothing much comes down.
                builder.setEstimatedNetworkBytes(64 * 1024L, total + 64 * 1024L)
            }
            if (userInitiated && Build.VERSION.SDK_INT >= 34) builder.setUserInitiated(true)
            return builder.build()
        }

        val scheduled = try {
            scheduler.schedule(build(userInitiated = Build.VERSION.SDK_INT >= 34)) == JobScheduler.RESULT_SUCCESS
        } catch (e: Exception) {
            // Not allowed as user-initiated right now (no screen in front, permission withheld): a normal job still goes.
            Log.w(TAG, "user-initiated upload job refused (${e.javaClass.simpleName}); scheduling a normal one")
            try {
                scheduler.schedule(build(userInitiated = false)) == JobScheduler.RESULT_SUCCESS
            } catch (e2: Exception) {
                Log.w(TAG, "upload job not scheduled: ${e2.javaClass.simpleName}")
                false
            }
        }
        return if (scheduled) transferId else null
    }

    /** "사진 3장 · 12.4MB": what is being sent and how much. */
    fun summary(files: List<TransferFile>): String = "${what(files)} · ${bytes(files.sumOf { it.size })}"

    /** "사진 3장", "동영상 2개", "파일 4개", or the name of the one file. */
    fun what(files: List<TransferFile>): String {
        val n = files.size
        fun all(prefix: String) = files.all { it.mime?.startsWith(prefix) == true }
        return when {
            n == 1 -> files[0].name
            all("image/") -> "사진 ${n}장"
            all("video/") -> "동영상 ${n}개"
            files.all { it.mime?.startsWith("image/") == true || it.mime?.startsWith("video/") == true } -> "사진·동영상 ${n}개"
            else -> "파일 ${n}개"
        }
    }

    /** Decimal units, as Android's own file sizes: "820KB", "12.4MB", "1.25GB". */
    fun bytes(n: Long): String = when {
        n < 1_000 -> "${n}B"
        n < 1_000_000 -> String.format(Locale.ROOT, "%.0fKB", n / 1e3)
        n < 1_000_000_000 -> String.format(Locale.ROOT, "%.1fMB", n / 1e6)
        else -> String.format(Locale.ROOT, "%.2fGB", n / 1e9)
    }

    /** "4.1/12.4 MB": both in the unit of [total]. */
    fun progress(sent: Long, total: Long): String = when {
        total < 1_000_000 -> String.format(Locale.ROOT, "%.0f/%.0f KB", sent / 1e3, total / 1e3)
        total < 1_000_000_000 -> String.format(Locale.ROOT, "%.1f/%.1f MB", sent / 1e6, total / 1e6)
        else -> String.format(Locale.ROOT, "%.2f/%.2f GB", sent / 1e9, total / 1e9)
    }

    /**
     * The object particle for [word]: 을 after a final consonant, 를 after a vowel. Korean words and digits are
     * read by their sound; anything else (a file name ending in Latin letters) gets "을(를)".
     */
    fun objectParticle(word: String): String {
        val c = word.trimEnd().lastOrNull() ?: return "을(를)"
        return when {
            c in '가'..'힣' -> if ((c - '가') % 28 != 0) "을" else "를"
            // 영 일 이 삼 사 오 육 칠 팔 구
            c in '0'..'9' -> if (c in "013678") "을" else "를"
            else -> "을(를)"
        }
    }
}
