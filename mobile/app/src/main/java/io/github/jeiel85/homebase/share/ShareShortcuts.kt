package io.github.jeiel85.homebase.share

import android.content.Context
import android.content.Intent
import android.content.pm.ShortcutInfo
import android.content.pm.ShortcutManager
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.Typeface
import android.graphics.drawable.Icon
import android.os.Build
import android.util.Log
import androidx.annotation.RequiresApi
import io.github.jeiel85.homebase.MainActivity
import io.github.jeiel85.homebase.data.PcEntry
import io.github.jeiel85.homebase.push.PushNotifier

/**
 * The share sheet's direct targets (docs/SHARE_CONVENIENCE.md §4.6, Android 10+): one long-lived dynamic shortcut per
 * PC, the most shared-to first, up to [MAX]. `res/xml/shortcuts.xml` makes them share targets of
 * [io.github.jeiel85.homebase.clip.ClipShareActivity] (category [CATEGORY]); picking one opens the share center with
 * that PC checked — it still waits for [공유 시작], since any app can put a shortcut id into a share intent. From the
 * launcher (long press on the app icon) the same shortcut opens that PC's screen.
 *
 * Rebuilt whenever the paired PCs change ([io.github.jeiel85.homebase.data.PcStore] calls [sync] on every save), but
 * only when what they show changed. Platform [ShortcutManager] only, no new library; Android 8–9 have none.
 */
object ShareShortcuts {
    private const val TAG = "HomebaseShare"

    /** The share target category in `res/xml/shortcuts.xml`. */
    const val CATEGORY = "io.github.jeiel85.homebase.SHARE_TO_PC"

    /** The shortcut's launcher action: opens the PC's screen ([PushNotifier.EXTRA_PC_ID]). */
    const val ACTION_OPEN_PC = "io.github.jeiel85.homebase.action.OPEN_PC"

    const val MAX = 4

    private const val PREFIX = "pc:"

    fun idOf(pcId: String): String = PREFIX + pcId

    /** The PC a shortcut id stands for, or null for anything else. */
    fun pcIdOf(shortcutId: String?): String? = shortcutId?.takeIf { it.startsWith(PREFIX) }?.removePrefix(PREFIX)?.ifEmpty { null }

    /** The PCs to offer, most shared-to first (ties in list order), at most [max]. */
    fun rank(pcs: List<PcEntry>, uses: Map<String, Int>, max: Int = MAX): List<PcEntry> =
        pcs.withIndex()
            .sortedWith(compareByDescending<IndexedValue<PcEntry>> { uses[it.value.pcId] ?: 0 }.thenBy { it.index })
            .map { it.value }
            .take(max)

    /** What the shortcuts show; they are rebuilt only when it changes. */
    fun signature(ranked: List<PcEntry>): String =
        ranked.joinToString("\n") { "${it.pcId}|${it.displayName}|${it.mark.colorIndex}|${it.mark.letter}" }

    /** Brings the shortcuts in line with [pcs] (all paired PCs). Cheap when nothing they show changed; never throws. */
    fun sync(context: Context, pcs: List<PcEntry>) {
        if (Build.VERSION.SDK_INT < 29) return
        try {
            val prefs = SharePrefs(context)
            val ranked = rank(pcs, prefs.useCounts())
            val shown = signature(ranked)
            if (shown == prefs.shortcutsSignature) return
            if (publish(context.applicationContext, ranked)) prefs.shortcutsSignature = shown
        } catch (e: Exception) {
            Log.w(TAG, "share shortcuts not updated: ${e.javaClass.simpleName}")
        }
    }

    /** After a share to [pcIds]: counts it, re-ranks the shortcuts, and tells Android (its own ranking of targets). */
    fun used(context: Context, pcs: List<PcEntry>, pcIds: Collection<String>) {
        if (pcIds.isEmpty()) return
        SharePrefs(context).countUse(pcIds)
        if (Build.VERSION.SDK_INT < 29) return
        sync(context, pcs)
        try {
            val manager = context.getSystemService(ShortcutManager::class.java) ?: return
            pcIds.forEach { manager.reportShortcutUsed(idOf(it)) }
        } catch (e: Exception) {
            Log.w(TAG, "shortcut use not reported: ${e.javaClass.simpleName}")
        }
    }

    @RequiresApi(29)
    private fun publish(context: Context, ranked: List<PcEntry>): Boolean {
        val manager = context.getSystemService(ShortcutManager::class.java) ?: return false
        val keep = ranked.map { idOf(it.pcId) }.toSet()
        val gone = manager.dynamicShortcuts.map { it.id }.filter { it.startsWith(PREFIX) && it !in keep }
        val infos = ranked.take(manager.maxShortcutCountPerActivity.coerceAtLeast(1)).mapIndexed { position, pc -> info(context, pc, position) }
        if (!manager.setDynamicShortcuts(infos)) return false // rate-limited in the background: next change tries again
        // A removed PC's long-lived shortcut can stay cached for the share sheet (Android 11+): take it out too.
        if (gone.isNotEmpty() && Build.VERSION.SDK_INT >= 30) manager.removeLongLivedShortcuts(gone)
        return true
    }

    @RequiresApi(29)
    private fun info(context: Context, pc: PcEntry, rank: Int): ShortcutInfo {
        val name = pc.displayName.ifBlank { "PC" }
        val open = Intent(context, MainActivity::class.java)
            .setAction(ACTION_OPEN_PC)
            .putExtra(PushNotifier.EXTRA_PC_ID, pc.pcId)
        return ShortcutInfo.Builder(context, idOf(pc.pcId))
            .setShortLabel(name)
            .setLongLabel("$name(으)로 보내기")
            .setIcon(Icon.createWithAdaptiveBitmap(badge(pc.mark.argb, pc.mark.letter)))
            .setIntent(open)
            .setCategories(setOf(CATEGORY))
            .setLongLived(true)
            .setRank(rank)
            .build()
    }

    /**
     * The PC's badge (its colour, the first letter of its name) as an adaptive icon: the colour fills the whole
     * square, and the letter sits in the middle, inside the part every launcher mask keeps.
     */
    private fun badge(argb: Int, letter: String): Bitmap {
        val size = ICON_PX
        val bitmap = Bitmap.createBitmap(size, size, Bitmap.Config.ARGB_8888)
        val canvas = Canvas(bitmap)
        canvas.drawColor(argb)
        val paint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
            color = android.graphics.Color.WHITE
            textAlign = Paint.Align.CENTER
            typeface = Typeface.DEFAULT_BOLD
            textSize = size * 0.34f
        }
        val baseline = size / 2f - (paint.descent() + paint.ascent()) / 2f
        canvas.drawText(letter, size / 2f, baseline, paint)
        return bitmap
    }

    /** 108 dp at about xhdpi: an adaptive icon's full square; the launcher scales it. */
    private const val ICON_PX = 216
}
