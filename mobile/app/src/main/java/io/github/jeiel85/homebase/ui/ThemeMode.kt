package io.github.jeiel85.homebase.ui

import android.content.Context
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue

/** Light/dark choice in Settings → 화면. [System] follows Android's dark theme. */
enum class ThemeMode(val label: String) {
    System("시스템 설정 따르기"),
    Light("밝게"),
    Dark("어둡게"),
    ;

    /** Whether to draw dark, given what the system currently asks for. */
    fun isDark(systemDark: Boolean): Boolean = when (this) {
        System -> systemDark
        Light -> false
        Dark -> true
    }

    companion object {
        fun fromName(name: String?): ThemeMode = entries.firstOrNull { it.name == name } ?: System
    }
}

/**
 * The saved [ThemeMode], held as Compose state so a change in Settings redraws every screen at
 * once. Process-wide because the theme wraps the whole activity.
 */
object ThemeSetting {
    private const val PREFS = "homebase_app"
    private const val KEY_THEME = "theme_mode"

    var mode by mutableStateOf(ThemeMode.System)
        private set

    fun load(context: Context) {
        mode = ThemeMode.fromName(prefs(context).getString(KEY_THEME, null))
    }

    fun set(context: Context, value: ThemeMode) {
        prefs(context).edit().putString(KEY_THEME, value.name).apply()
        mode = value
    }

    private fun prefs(context: Context) = context.applicationContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
}
