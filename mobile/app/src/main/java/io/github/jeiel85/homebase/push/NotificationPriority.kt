package io.github.jeiel85.homebase.push

import android.content.Context

/**
 * How one kind (or one app) of notification appears on this phone (DECISIONS M-37). Chosen in
 * Settings → 알림 우선순위; the PC knows nothing about it.
 */
data class AlertProfile(
    /** Posted to Android's notification shade at all; off = kept in the notification center only. */
    val shade: Boolean = true,
    /** Pops up over the screen (heads-up). */
    val popup: Boolean = false,
    val sound: Boolean = true,
    val vibrate: Boolean = true,
    /** Same-source notifications are bundled: one group in the shade, one stack in the notification center. */
    val bundle: Boolean = true,
) {
    enum class Importance { HIGH, DEFAULT, LOW }

    /**
     * The Android channel importance this needs: heads-up needs HIGH; sound or vibration needs at
     * least DEFAULT (Android neither rings nor buzzes for LOW); neither is LOW.
     */
    val importance: Importance
        get() = when {
            popup -> Importance.HIGH
            sound || vibrate -> Importance.DEFAULT
            else -> Importance.LOW
        }

    /**
     * Part of the channel id. Android fixes a channel's importance, sound and vibration when the app
     * creates it, so each combination gets its own channel.
     */
    val channelSuffix: String
        get() = when (importance) {
            Importance.HIGH -> "h"
            Importance.DEFAULT -> "d"
            Importance.LOW -> "l"
        } + bit(sound) + bit(vibrate)

    /** Stored form: five 0/1 flags in a fixed order. */
    fun encode(): String = listOf(shade, popup, sound, vibrate, bundle).joinToString("") { bit(it) }

    companion object {
        private fun bit(b: Boolean) = if (b) "1" else "0"

        /** Reads [encode]'s output; null for anything else, so a damaged value falls back to the default. */
        fun decode(s: String?): AlertProfile? {
            if (s == null || s.length != 5 || s.any { it != '0' && it != '1' }) return null
            val b = s.map { it == '1' }
            return AlertProfile(shade = b[0], popup = b[1], sound = b[2], vibrate = b[3], bundle = b[4])
        }
    }
}

/**
 * The four priorities offered as one tap. A level sets what reaches the user (shade, popup, sound,
 * vibration); bundling is a separate switch and a level leaves it as it was.
 */
enum class AlertLevel(val label: String, val description: String) {
    URGENT("긴급", "화면 위에 띄우고 소리·진동"),
    NORMAL("보통", "알림 창에 소리·진동과 함께"),
    QUIET("조용히", "소리·진동 없이 알림 창에만"),
    CENTER("센터에만", "알림 창에도 띄우지 않고 알림 센터에만 남김");

    /** [profile] at this level, keeping its bundling choice. */
    fun applyTo(profile: AlertProfile): AlertProfile = when (this) {
        URGENT -> profile.copy(shade = true, popup = true, sound = true, vibrate = true)
        NORMAL -> profile.copy(shade = true, popup = false, sound = true, vibrate = true)
        QUIET -> profile.copy(shade = true, popup = false, sound = false, vibrate = false)
        CENTER -> profile.copy(shade = false)
    }

    companion object {
        /** The level [profile] is at, or null for a combination of its own (사용자 지정). */
        fun of(profile: AlertProfile): AlertLevel? =
            if (!profile.shade) CENTER else entries.firstOrNull { it != CENTER && it.applyTo(profile) == profile }
    }
}

/** A user's choices: one profile per notification group, and per-app overrides. Absent = default. */
data class NotificationRules(
    val groups: Map<String, AlertProfile> = emptyMap(),
    val sources: Map<String, AlertProfile> = emptyMap(),
) {
    fun resolve(category: String, source: String): AlertProfile = NotificationPriority.resolve(category, source, this)
}

/** Pure rules of the notification priorities (DECISIONS M-37). No Android types, so unit tests pin them. */
object NotificationPriority {
    /** The Tray's "send test" pops up and stays on its own whatever the settings say, so the path can be checked. */
    val TEST_PROFILE = AlertProfile(popup = true, bundle = false)

    /**
     * The default of a group ([NotificationGroup.key]; null for a category no group knows). App
     * notifications — this PC's Windows ones and other devices' — arrive in bursts, so they ring
     * without popping up; PC alerts pop up as they always did. Everything is bundled.
     */
    fun defaultFor(groupKey: String?): AlertProfile = when (groupKey) {
        "toast", "device" -> AlertLevel.NORMAL.applyTo(AlertProfile())
        else -> AlertLevel.URGENT.applyTo(AlertProfile())
    }

    /** Categories whose notifications get per-app choices: their `source` names an app. */
    fun hasApps(category: String): Boolean = category == NotificationGroups.TOAST || category == NotificationGroups.DEVICE

    /** Key of a per-app choice: the category and the app as the PC names it, case-insensitive. */
    fun sourceKey(category: String, source: String): String = "$category|${source.trim().lowercase()}"

    /** The category part of a [sourceKey]. */
    fun categoryOfKey(key: String): String = key.substringBefore('|')

    /** Per-app choice first, then the group's, then the group's default. */
    fun resolve(category: String, source: String, rules: NotificationRules): AlertProfile {
        if (category == NotificationGroups.TEST) return TEST_PROFILE
        if (hasApps(category)) rules.sources[sourceKey(category, source)]?.let { return it }
        val group = NotificationGroups.ALL.firstOrNull { category in it.categories }?.key
        return group?.let { rules.groups[it] } ?: defaultFor(group)
    }

    /**
     * What notifications are bundled by: their source on their PC. A relayed notification can arrive
     * through either of two PCs (and is shown once), so it is bundled by device and app alone — its
     * source already reads "Galaxy S24 · 카카오톡".
     */
    fun bundleKey(category: String, pcId: String, source: String): String {
        val s = source.trim().lowercase()
        return if (category == NotificationGroups.DEVICE) "device|$s" else "$pcId|$s"
    }

    /**
     * One line for a bundled notification: the title without the source the bundle already names
     * (the PC sends "카카오톡: 홍길동"), then the first line of the body.
     */
    fun shortLine(title: String, body: String?, source: String): String {
        val t = title.removePrefix("$source: ").trim().takeIf { it.isNotEmpty() && it != source }
        val b = body?.lineSequence()?.map { it.trim() }?.firstOrNull { it.isNotEmpty() }
        return listOfNotNull(t, b).joinToString(": ").ifEmpty { title }
    }

    /** One line describing [profile], e.g. "팝업 · 소리 · 진동 · 묶음". */
    fun summary(profile: AlertProfile): String {
        if (!profile.shade) return "알림 센터에만"
        val parts = listOfNotNull(
            "팝업".takeIf { profile.popup },
            "소리".takeIf { profile.sound },
            "진동".takeIf { profile.vibrate },
        ).ifEmpty { listOf("무음") }
        return (parts + if (profile.bundle) "묶음" else "하나씩").joinToString(" · ")
    }

    /** [summary] with the level in front: "긴급 — 팝업 · 소리 · 진동 · 묶음", or "사용자 지정 — …". */
    fun describe(profile: AlertProfile): String {
        val level = AlertLevel.of(profile)?.label ?: "사용자 지정"
        return if (!profile.shade) "$level — 알림 창에 띄우지 않음" else "$level — ${summary(profile)}"
    }

    /** An app that has a choice or sent something recently, for the per-app list. */
    data class AppSource(val key: String, val category: String, val label: String)

    /**
     * The per-app list: apps in [entries] (newest first, so the most recent come first), then apps
     * with a choice that sent nothing lately ([labels]: key → name as last seen), by name.
     */
    fun apps(entries: List<HistoryEntry>, labels: Map<String, String>): List<AppSource> {
        val seen = LinkedHashMap<String, AppSource>()
        entries.forEach { e ->
            if (!hasApps(e.category)) return@forEach
            val key = sourceKey(e.category, e.source)
            if (key !in seen) seen[key] = AppSource(key, e.category, e.source)
        }
        val rest = labels.filterKeys { it !in seen }
            .map { (key, label) -> AppSource(key, categoryOfKey(key), label) }
            .sortedBy { it.label.lowercase() }
        return seen.values.toList() + rest
    }
}

/**
 * Where the choices live: one SharedPreferences file, read for every push (small and cached by
 * Android). Values are [AlertProfile.encode]d; a choice equal to the default is not stored.
 */
class NotificationRuleStore(context: Context) {
    private val prefs = context.applicationContext.getSharedPreferences(NAME, Context.MODE_PRIVATE)

    fun rules(): NotificationRules {
        val groups = mutableMapOf<String, AlertProfile>()
        val sources = mutableMapOf<String, AlertProfile>()
        prefs.all.forEach { (k, v) ->
            val profile = AlertProfile.decode(v as? String) ?: return@forEach
            when {
                k.startsWith(GROUP) -> groups[k.removePrefix(GROUP)] = profile
                k.startsWith(SOURCE) -> sources[k.removePrefix(SOURCE)] = profile
            }
        }
        return NotificationRules(groups, sources)
    }

    /** Names of the apps that have a choice, as last seen: key → label. */
    fun appLabels(): Map<String, String> {
        val rules = rules().sources.keys
        return prefs.all.mapNotNull { (k, v) ->
            if (!k.startsWith(LABEL)) return@mapNotNull null
            val key = k.removePrefix(LABEL)
            if (key in rules) key to (v as? String ?: key) else null
        }.toMap()
    }

    fun resolve(category: String, source: String): AlertProfile = rules().resolve(category, source)

    /** [profile] for a group; null or the default clears the choice. */
    fun setGroup(groupKey: String, profile: AlertProfile?) {
        val edit = prefs.edit()
        if (profile == null || profile == NotificationPriority.defaultFor(groupKey)) edit.remove(GROUP + groupKey)
        else edit.putString(GROUP + groupKey, profile.encode())
        edit.apply()
    }

    /** [profile] for one app ([NotificationPriority.sourceKey]); null goes back to the group's choice. */
    fun setApp(key: String, label: String, profile: AlertProfile?) {
        val edit = prefs.edit()
        if (profile == null) {
            edit.remove(SOURCE + key).remove(LABEL + key)
        } else {
            edit.putString(SOURCE + key, profile.encode()).putString(LABEL + key, label)
        }
        edit.apply()
    }

    /** Every choice back to its default. */
    fun clear() {
        prefs.edit().clear().apply()
    }

    private companion object {
        const val NAME = "homebase_notification_rules"
        const val GROUP = "group:"
        const val SOURCE = "app:"
        const val LABEL = "label:"
    }
}
