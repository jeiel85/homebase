package io.github.jeiel85.homebase.ui

import android.app.Activity
import android.content.Context
import android.content.ContextWrapper
import android.content.res.Resources
import android.graphics.drawable.ColorDrawable
import android.os.Build
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import io.github.jeiel85.homebase.R

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

    /** The window theme: its background is what shows before Compose draws. [System] keeps values-night's. */
    val windowTheme: Int
        get() = when (this) {
            System -> R.style.Theme_Homebase
            Light -> R.style.Theme_Homebase_Light
            Dark -> R.style.Theme_Homebase_Dark
        }

    /** The background colour of [windowTheme]. */
    val windowBackground: Int
        get() = when (this) {
            System -> R.color.canvas
            Light -> R.color.canvas_day
            Dark -> R.color.canvas_night
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

    /** The choice the launch splash was last set to in this process (see [syncSplash]). */
    @Volatile
    private var splashMode: ThemeMode? = null

    fun load(context: Context) {
        mode = ThemeMode.fromName(prefs(context).getString(KEY_THEME, null))
    }

    /**
     * Loads the saved choice and gives [activity] the matching window theme. Call before
     * `super.onCreate` (and before anything touches the window), so "어둡게" on a light system — or
     * "밝게" on a dark one — doesn't flash the other background before Compose draws.
     */
    fun applyToWindow(activity: Activity) {
        load(activity)
        activity.setTheme(mode.windowTheme)
    }

    fun set(context: Context, value: ThemeMode) {
        prefs(context).edit().putString(KEY_THEME, value.name).apply()
        mode = value
        context.findActivity()?.let { activity ->
            // Behind Compose, but shown while the keyboard resizes the window.
            activity.window.setBackgroundDrawable(ColorDrawable(activity.getColor(value.windowBackground)))
            syncSplash(activity)
        }
    }

    /**
     * Android 13+ draws the launch splash before the app runs, from the manifest theme and the
     * system's dark setting; this makes it follow the saved choice from the next launch on (Android
     * keeps it). Once per process, and again when the choice changes. Older Android has no such hook.
     */
    fun syncSplash(activity: Activity) {
        val chosen = mode
        if (splashMode == chosen) return
        if (Build.VERSION.SDK_INT >= 33) {
            try {
                // ID_NULL goes back to the manifest theme, which follows the system (values-night).
                activity.splashScreen.setSplashScreenTheme(if (chosen == ThemeMode.System) Resources.ID_NULL else chosen.windowTheme)
                splashMode = chosen
            } catch (_: RuntimeException) {
                // Only the splash keeps the system's colours; nothing else depends on it.
            }
        }
    }

    private fun prefs(context: Context) = context.applicationContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)

    private tailrec fun Context.findActivity(): Activity? = when (this) {
        is Activity -> this
        is ContextWrapper -> baseContext.findActivity()
        else -> null
    }
}
