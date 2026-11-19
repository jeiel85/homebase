package io.github.jeiel85.homebase.share

import android.content.ContentResolver
import android.content.ContentValues
import android.content.Context
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.provider.MediaStore
import androidx.annotation.RequiresApi
import androidx.core.content.FileProvider
import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.io.OutputStream

/**
 * A file being received (DECISIONS M-65): written while hidden, then [publish]ed — or [discard]ed when it didn't
 * arrive whole. [uri] is what other apps are given to open or share it.
 */
internal interface ReceivingFile {
    val uri: Uri
    fun open(): OutputStream
    fun publish()
    fun discard()
}

/**
 * Where received files go. Android 10+ (API 29): MediaStore, no permission — pictures and videos in
 * `Pictures/Homebase` (they show in the gallery), everything else in `Download/Homebase`, each pending
 * (`IS_PENDING`) until it is whole. Android 8–9: this app's own external folder `…/files/Download/Homebase`, shared
 * through [ReceivedFileProvider] — no storage permission either.
 */
internal object ReceivedStore {
    fun create(context: Context, name: String, mime: String?): ReceivingFile =
        if (Build.VERSION.SDK_INT >= 29) MediaStoreFile.create(context, name, mime) else AppDirFile.create(context, name)

    /** "name (2).ext", "name (3).ext", … for the first that [exists] says is free. */
    fun unique(name: String, exists: (String) -> Boolean): String {
        if (!exists(name)) return name
        val dot = name.lastIndexOf('.')
        val stem = if (dot > 0) name.substring(0, dot) else name
        val ext = if (dot > 0) name.substring(dot) else ""
        var i = 2
        while (true) {
            val candidate = "$stem ($i)$ext"
            if (!exists(candidate)) return candidate
            i++
        }
    }
}

@RequiresApi(29)
private class MediaStoreFile(private val resolver: ContentResolver, override val uri: Uri) : ReceivingFile {
    override fun open(): OutputStream = resolver.openOutputStream(uri, "w") ?: throw IOException("no output stream")

    override fun publish() {
        val values = ContentValues().apply { put(MediaStore.MediaColumns.IS_PENDING, 0) }
        if (resolver.update(uri, values, null, null) < 1) throw IOException("could not publish")
    }

    override fun discard() {
        try {
            resolver.delete(uri, null, null)
        } catch (_: Exception) {
            // A pending item left behind is removed by MediaStore after a week.
        }
    }

    companion object {
        fun create(context: Context, name: String, mime: String?): MediaStoreFile {
            val resolver = context.contentResolver
            val volume = MediaStore.VOLUME_EXTERNAL_PRIMARY
            val downloads = MediaStore.Downloads.getContentUri(volume)
            fun insert(collection: Uri, folder: String, type: String?): Uri? = try {
                val values = ContentValues().apply {
                    put(MediaStore.MediaColumns.DISPLAY_NAME, name)
                    type?.let { put(MediaStore.MediaColumns.MIME_TYPE, it) }
                    put(MediaStore.MediaColumns.RELATIVE_PATH, "$folder/Homebase")
                    put(MediaStore.MediaColumns.IS_PENDING, 1)
                }
                resolver.insert(collection, values)
            } catch (_: Exception) {
                // MediaStore refuses a type it doesn't take in that collection (an odd image type, a name whose
                // extension doesn't fit the type): the next place is tried.
                null
            }
            val uri = when (mime?.substringBefore('/')) {
                "image" -> insert(MediaStore.Images.Media.getContentUri(volume), Environment.DIRECTORY_PICTURES, mime)
                "video" -> insert(MediaStore.Video.Media.getContentUri(volume), Environment.DIRECTORY_PICTURES, mime)
                else -> null
            } ?: insert(downloads, Environment.DIRECTORY_DOWNLOADS, mime)
                ?: insert(downloads, Environment.DIRECTORY_DOWNLOADS, null)
                ?: throw IOException("could not create $name")
            return MediaStoreFile(resolver, uri)
        }
    }
}

private class AppDirFile(private val context: Context, private val target: File) : ReceivingFile {
    private val part = File(target.parentFile, target.name + ".part")

    override val uri: Uri get() = FileProvider.getUriForFile(context, ReceivedFileProvider.authority(context), target)

    override fun open(): OutputStream = FileOutputStream(part)

    override fun publish() {
        if (!part.renameTo(target)) throw IOException("could not publish")
    }

    override fun discard() {
        part.delete()
        target.delete()
    }

    companion object {
        fun create(context: Context, name: String): AppDirFile {
            val root = context.getExternalFilesDir(Environment.DIRECTORY_DOWNLOADS) ?: File(context.filesDir, "Download")
            val dir = File(root, "Homebase")
            if (!dir.isDirectory && !dir.mkdirs()) throw IOException("could not create the folder")
            val free = ReceivedStore.unique(name) { File(dir, it).exists() || File(dir, "$it.part").exists() }
            return AppDirFile(context, File(dir, free))
        }
    }
}

/**
 * Shares the files received on Android 8–9 (kept in this app's own folder) with the app that opens or shares them.
 * Its own class and authority: the GitHub build's updater has the plain FileProvider at `<app id>.fileprovider`.
 */
class ReceivedFileProvider : FileProvider() {
    companion object {
        fun authority(context: Context): String = "${context.packageName}.received"
    }
}
