package io.github.jeiel85.homebase.share

import android.content.Context
import org.json.JSONObject

/**
 * What the share center and the receiving side remember (docs/SHARE_CONVENIENCE.md §9): the targets of the last
 * share (checked again next time), how often each PC was shared to (the order of the share sheet's PC shortcuts),
 * and whether files a PC sends are fetched by themselves over mobile data too.
 */
class SharePrefs(context: Context) {
    private val prefs = context.applicationContext.getSharedPreferences(NAME, Context.MODE_PRIVATE)

    /** "모바일 데이터에서도 자동으로 받기": off by default, so more than 25 MB over mobile data waits for [받기]. */
    fun receiveOnMetered(): Boolean = prefs.getBoolean(KEY_RECEIVE_ON_METERED, false)

    fun setReceiveOnMetered(on: Boolean) = prefs.edit().putBoolean(KEY_RECEIVE_ON_METERED, on).apply()

    /** [ShareChoice] keys of what the last share went to. */
    fun lastTargets(): Set<String> = prefs.getStringSet(KEY_LAST_TARGETS, null)?.toSet() ?: emptySet()

    fun setLastTargets(keys: Set<String>) = prefs.edit().putStringSet(KEY_LAST_TARGETS, keys.toSet()).apply()

    /** pcId → how many shares went to it. */
    fun useCounts(): Map<String, Int> {
        val raw = prefs.getString(KEY_USES, null) ?: return emptyMap()
        return try {
            val o = JSONObject(raw)
            o.keys().asSequence().associateWith { o.optInt(it) }
        } catch (_: Exception) {
            emptyMap()
        }
    }

    @Synchronized
    fun countUse(pcIds: Collection<String>) {
        if (pcIds.isEmpty()) return
        val next = useCounts().toMutableMap()
        pcIds.forEach { next[it] = (next[it] ?: 0) + 1 }
        val o = JSONObject()
        next.forEach { (pcId, n) -> o.put(pcId, n) }
        prefs.edit().putString(KEY_USES, o.toString()).apply()
    }

    /** What the share sheet's shortcuts were last built from ([ShareShortcuts]); null before the first time. */
    var shortcutsSignature: String?
        get() = prefs.getString(KEY_SHORTCUTS, null)
        set(value) {
            prefs.edit().putString(KEY_SHORTCUTS, value).apply()
        }

    private companion object {
        const val NAME = "homebase.share"
        const val KEY_RECEIVE_ON_METERED = "receive_on_metered"
        const val KEY_LAST_TARGETS = "last_targets"
        const val KEY_USES = "uses"
        const val KEY_SHORTCUTS = "shortcuts"
    }
}

/**
 * Which targets the share center checks before the user does anything — pure, so tests pin it. Nothing is ever sent
 * before the user's [공유 시작]; this only saves taps.
 */
object ShareChoice {
    fun pcKey(pcId: String) = "pc:$pcId"

    /**
     * The PC a share sheet shortcut stands for ([preset]) alone, when it is still paired. Else the last share's
     * targets that are still offered ([last], phones only once they are listed), else the PC text was sent to last
     * ([legacyLastPc], from before the share center), else the only PC.
     */
    fun preselect(pcIds: List<String>, last: Set<String>, legacyLastPc: String?, preset: String?): Set<String> {
        if (preset != null && preset in pcIds) return setOf(pcKey(preset))
        val pcKeys = pcIds.map(::pcKey).toSet()
        val fromLast = last.filter { it in pcKeys || it.startsWith("phone:") }.toSet()
        if (fromLast.any { it in pcKeys }) return fromLast
        val legacy = legacyLastPc?.takeIf { it in pcIds }?.let(::pcKey)
        return when {
            legacy != null -> fromLast + legacy
            pcIds.size == 1 -> fromLast + pcKey(pcIds.single())
            else -> fromLast
        }
    }
}

/** Sizes in the share center that follow the screen (#111) — pure, so tests pin them. */
object ShareLayout {
    /** The narrowest a target tile may get at font scale 1 — a 46 dp badge, the check and a two-line name. */
    const val MIN_TILE_DP = 150

    /** Space between tiles and the page's gutters, as [ShareCenterFlow] lays them out. */
    private const val GAP_DP = 10
    private const val GUTTERS_DP = 28

    /**
     * Target tiles in a row for a screen [widthDp] wide at [fontScale]: as many as fit at [MIN_TILE_DP] (wider with
     * larger text), two at least — a phone in portrait — and four at most, so a tablet or landscape screen doesn't
     * stretch two tiles across it.
     */
    fun columns(widthDp: Int, fontScale: Float): Int {
        val tile = MIN_TILE_DP * fontScale.coerceAtLeast(1f) + GAP_DP
        return ((widthDp - GUTTERS_DP + GAP_DP) / tile).toInt().coerceIn(2, 4)
    }

    /** Badges the bottom dock draws before it says "+n" for the rest. */
    const val DOCK_BADGES = 5

    /** The "+n" after the dock's badges for [picked] targets, or null when all of them have a badge. */
    fun dockOverflow(picked: Int): String? = (picked - DOCK_BADGES).takeIf { it > 0 }?.let { "+$it" }

    /**
     * Pixels to ask a thumbnail for, to fill [dp] on a screen of [density] without blur: at least 128 (a tiny
     * thumbnail is not worth a second request) and at most 1,024 (a full-width preview on a tablet — more only
     * costs memory).
     */
    fun thumbnailPx(dp: Int, density: Float): Int = (dp * density).toInt().coerceIn(128, 1024)
}
