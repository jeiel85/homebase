package io.github.jeiel85.homebase.clip

import android.content.ClipData
import android.content.ClipDescription
import android.content.ClipboardManager
import android.content.Context
import android.os.Build
import android.os.PersistableBundle
import android.util.Log

/** Text on the phone's clipboard, and whether the app that put it there marked it sensitive. */
data class ClipText(val text: String, val sensitive: Boolean)

/**
 * The phone's clipboard (DECISIONS M-62). Android 10+ lets an app **read** it only while one of its
 * windows has focus — the callers are a screen the user is on, or the tile's activity once it has
 * focus — but **write** it at any time, so text a PC sends goes on the clipboard as it arrives.
 */
object PhoneClipboard {
    private const val TAG = "HomebaseClip"
    private const val LABEL = "Homebase"

    /** The clipboard's text, or null when it holds none (empty, an image, or reading was refused). */
    fun read(context: Context): ClipText? = try {
        val cm = context.getSystemService(ClipboardManager::class.java)
        val clip = cm?.primaryClip
        val text = if (clip != null && clip.itemCount > 0) clip.getItemAt(0).text?.toString() else null
        if (text.isNullOrEmpty()) {
            null
        } else {
            val sensitive = Build.VERSION.SDK_INT >= 33 &&
                clip?.description?.extras?.getBoolean(ClipDescription.EXTRA_IS_SENSITIVE, false) == true
            ClipText(text, sensitive)
        }
    } catch (e: Exception) {
        // A SecurityException when Android decides this app may not read it right now.
        Log.w(TAG, "could not read the clipboard: ${e.javaClass.simpleName}")
        null
    }

    /**
     * Puts [text] on the clipboard. [sensitive] (a password manager put it on the PC's clipboard) keeps it
     * out of Android 13+'s clipboard preview. False when Android refused.
     */
    fun write(context: Context, text: String, sensitive: Boolean): Boolean = try {
        val clip = ClipData.newPlainText(LABEL, text)
        if (sensitive && Build.VERSION.SDK_INT >= 33) {
            clip.description.extras = PersistableBundle().apply { putBoolean(ClipDescription.EXTRA_IS_SENSITIVE, true) }
        }
        val cm = context.getSystemService(ClipboardManager::class.java)
        if (cm == null) {
            false
        } else {
            cm.setPrimaryClip(clip)
            true
        }
    } catch (e: Exception) {
        Log.w(TAG, "could not write the clipboard: ${e.javaClass.simpleName}")
        false
    }
}
