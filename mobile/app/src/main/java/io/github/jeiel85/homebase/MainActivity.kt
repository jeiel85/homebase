package io.github.jeiel85.homebase

import android.content.Intent
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.Surface
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import io.github.jeiel85.homebase.data.PcStore
import io.github.jeiel85.homebase.net.HomebaseApi
import io.github.jeiel85.homebase.net.NetworkProbe
import io.github.jeiel85.homebase.net.PairingLink
import io.github.jeiel85.homebase.push.PushNotifier
import io.github.jeiel85.homebase.ui.HomebaseTheme
import io.github.jeiel85.homebase.ui.PairScreen
import io.github.jeiel85.homebase.ui.PcDetailScreen
import io.github.jeiel85.homebase.ui.PcListScreen
import io.github.jeiel85.homebase.ui.PushAutoSetup
import io.github.jeiel85.homebase.ui.SettingsScreen
import io.github.jeiel85.homebase.ui.ThemeSetting
import io.github.jeiel85.homebase.update.UpdateCheckScheduler
import io.github.jeiel85.homebase.update.UpdateNotifier
import io.github.jeiel85.homebase.update.UpdateSurface

/** Three screens — list, pair, detail — so plain state instead of a navigation library. */
class MainActivity : ComponentActivity() {
    /** A pairing link the app was opened with (camera → App Link, or the install page's button). */
    private var pairingLink by mutableStateOf<String?>(null)

    /** Bumped when an update notification is tapped while the app is already open: back to the list, where the banner opens the dialog. */
    private var updateTapped by mutableIntStateOf(0)

    override fun onCreate(savedInstanceState: Bundle?) {
        // Every screen starts with the carbon console bar, so the status bar always gets light
        // icons; the navigation bar follows the system theme.
        enableEdgeToEdge(statusBarStyle = SystemBarStyle.dark(android.graphics.Color.TRANSPARENT))
        super.onCreate(savedInstanceState)
        ThemeSetting.load(this)
        // A pending (unconfirmed) link survives rotation and other recreation; the first launch reads the intent.
        pairingLink = if (savedInstanceState == null) intent.pairingLink() else savedInstanceState.getString(KEY_PAIRING_LINK)
        if (savedInstanceState == null && intent.isUpdateTap()) UpdateSurface.requestOpen()
        // Keeps the background update check (DECISIONS M-22) scheduled — or cancelled — to match the setting.
        UpdateCheckScheduler.sync(this)
        val store = PcStore(this)
        val probe = NetworkProbe(this)
        val api = HomebaseApi(networks = probe::current)
        setContent {
            val dark = ThemeSetting.mode.isDark(isSystemInDarkTheme())
            // The navigation bar follows the app's theme choice, not only the system's.
            LaunchedEffect(dark) {
                enableEdgeToEdge(
                    statusBarStyle = SystemBarStyle.dark(android.graphics.Color.TRANSPARENT),
                    navigationBarStyle = SystemBarStyle.auto(NAV_SCRIM_LIGHT, NAV_SCRIM_DARK) { dark },
                )
            }
            HomebaseTheme(dark = dark) {
                Surface {
                    // "" = list, "+" = pair, [ROUTE_SETTINGS] = settings, otherwise a pcId.
                    // A tapped push notification opens its PC.
                    var route by rememberSaveable { mutableStateOf(intent?.getStringExtra(PushNotifier.EXTRA_PC_ID) ?: "") }
                    val pc = remember(route) { route.takeIf { it.length > 1 }?.let(store::get) }
                    BackHandler(enabled = route.isNotEmpty()) { route = ""; pairingLink = null }
                    LaunchedEffect(pairingLink) { if (pairingLink != null) route = "+" }
                    LaunchedEffect(updateTapped) { if (updateTapped > 0) route = "" }
                    // Turns push on after the first pairing, and asks for the notification permission — on any screen but pairing.
                    if (route != "+") PushAutoSetup(store)
                    when {
                        route == ROUTE_SETTINGS -> SettingsScreen(store, api, onBack = { route = "" })
                        route == "+" -> PairScreen(
                            store, api,
                            onDone = { pairingLink = null; route = it.pcId },
                            onBack = { pairingLink = null; route = "" },
                            link = pairingLink,
                        )
                        pc != null -> PcDetailScreen(pc, store, api, onBack = { route = "" })
                        else -> PcListScreen(store, api, onOpen = { route = it.pcId }, onAdd = { route = "+" }, onSettings = { route = ROUTE_SETTINGS })
                    }
                }
            }
        }
    }

    override fun onSaveInstanceState(outState: Bundle) {
        super.onSaveInstanceState(outState)
        pairingLink?.let { outState.putString(KEY_PAIRING_LINK, it) }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        intent.pairingLink()?.let { pairingLink = it }
        if (intent.isUpdateTap()) {
            UpdateSurface.requestOpen()
            updateTapped++
        }
    }

    private companion object {
        const val KEY_PAIRING_LINK = "pairingLink"
        /** Checked before the pcId branch, so it never reaches the PC store. */
        const val ROUTE_SETTINGS = "app:settings"
        // androidx.activity's default navigation-bar scrims for gesture-less (3-button) navigation.
        val NAV_SCRIM_LIGHT = android.graphics.Color.argb(0xe6, 0xFF, 0xFF, 0xFF)
        val NAV_SCRIM_DARK = android.graphics.Color.argb(0x80, 0x1b, 0x1b, 0x1b)
    }

    private fun Intent?.isUpdateTap(): Boolean = this?.getBooleanExtra(UpdateNotifier.EXTRA_OPEN_UPDATE, false) == true

    private fun Intent?.pairingLink(): String? =
        this?.takeIf { it.action == Intent.ACTION_VIEW }?.dataString?.takeIf(PairingLink::isLink)
}
