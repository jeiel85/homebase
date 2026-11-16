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
import androidx.compose.runtime.saveable.rememberSaveableStateHolder
import androidx.compose.runtime.setValue
import io.github.jeiel85.homebase.data.PcStore
import io.github.jeiel85.homebase.mesh.PresenceScheduler
import io.github.jeiel85.homebase.net.HomebaseApi
import io.github.jeiel85.homebase.net.NetworkProbe
import io.github.jeiel85.homebase.net.PairingLink
import io.github.jeiel85.homebase.push.PushNotifier
import io.github.jeiel85.homebase.ui.HomebaseTheme
import io.github.jeiel85.homebase.ui.NotificationCenterScreen
import io.github.jeiel85.homebase.ui.NotificationPriorityScreen
import io.github.jeiel85.homebase.ui.PairScreen
import io.github.jeiel85.homebase.ui.PcDetailScreen
import io.github.jeiel85.homebase.ui.PcListScreen
import io.github.jeiel85.homebase.ui.PushAutoSetup
import io.github.jeiel85.homebase.ui.SettingsScreen
import io.github.jeiel85.homebase.ui.ThemeSetting
import io.github.jeiel85.homebase.update.UpdateSurface

/** A handful of screens — list, pair, detail, settings, notification center — so plain state instead of a navigation library. */
class MainActivity : ComponentActivity() {
    /** A pairing link the app was opened with (camera → App Link, or the install page's button). */
    private var pairingLink by mutableStateOf<String?>(null)

    /** Bumped when an update notification is tapped while the app is already open: back to the list, where the banner opens the dialog. */
    private var updateTapped by mutableIntStateOf(0)

    /** Where a push notification tapped while the app is already open leads (a PC, or the notification center). */
    private var tappedRoute by mutableStateOf<String?>(null)

    override fun onCreate(savedInstanceState: Bundle?) {
        // The saved light/dark choice sets the window background before anything draws — first, since
        // enableEdgeToEdge creates the window's views and with them the background.
        ThemeSetting.applyToWindow(this)
        // Every screen starts with the carbon console bar, so the status bar always gets light
        // icons; the navigation bar follows the system theme.
        enableEdgeToEdge(statusBarStyle = SystemBarStyle.dark(android.graphics.Color.TRANSPARENT))
        super.onCreate(savedInstanceState)
        // The launch splash follows the choice from the next start on (Android 13+).
        ThemeSetting.syncSplash(this)
        // A pending (unconfirmed) link survives rotation and other recreation; the first launch reads the intent.
        pairingLink = if (savedInstanceState == null) intent.pairingLink() else savedInstanceState.getString(KEY_PAIRING_LINK)
        if (savedInstanceState == null && UpdateSurface.isUpdateTap(intent)) UpdateSurface.requestOpen()
        // Keeps the background update check (DECISIONS M-22) scheduled — or cancelled — to match the setting.
        // A no-op in the Play build, which Play updates (DECISIONS M-33).
        UpdateSurface.sync(this)
        // Keeps this phone's state reaching the paired PCs while the app is closed (DECISIONS M-27).
        PresenceScheduler.sync(this)
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
                    // "" = list, "+" = pair, [ROUTE_SETTINGS] = settings, [ROUTE_PRIORITY] = notification priorities,
                    // [ROUTE_NOTIFICATIONS] = notification center, otherwise a pcId.
                    // A tapped push notification opens its PC; a tapped bundle summary, the notification center.
                    var route by rememberSaveable { mutableStateOf(intent.pushRoute() ?: "") }
                    // Where back leads from a PC's screen: the notification center when its "PC 열기" opened it, else the list.
                    var pcBack by rememberSaveable { mutableStateOf("") }
                    val pc = remember(route) { route.takeIf { it.length > 1 }?.let(store::get) }
                    // The notification center keeps its filters, opened entry and scroll while a PC opened from it is shown.
                    val kept = rememberSaveableStateHolder()
                    fun openPc(pcId: String, backTo: String = "") {
                        pcBack = backTo
                        route = pcId
                    }
                    fun back() {
                        route = when {
                            // The priorities screen is opened from settings, so back returns there.
                            route == ROUTE_PRIORITY -> ROUTE_SETTINGS
                            pc != null -> pcBack
                            else -> ""
                        }
                        pcBack = ""
                        pairingLink = null
                    }
                    BackHandler(enabled = route.isNotEmpty()) { back() }
                    LaunchedEffect(pairingLink) { if (pairingLink != null) route = "+" }
                    LaunchedEffect(updateTapped) { if (updateTapped > 0) route = "" }
                    LaunchedEffect(tappedRoute) { tappedRoute?.let { pcBack = ""; route = it; tappedRoute = null } }
                    // Back on the list: the next visit to the notification center starts fresh.
                    LaunchedEffect(route) { if (route.isEmpty()) kept.removeState(ROUTE_NOTIFICATIONS) }
                    // Turns push on after the first pairing, and asks for the notification permission — on any screen but pairing.
                    if (route != "+") PushAutoSetup(store)
                    when {
                        route == ROUTE_SETTINGS -> SettingsScreen(
                            store, api,
                            onBack = { route = "" },
                            onOpenPriority = { route = ROUTE_PRIORITY },
                        )
                        route == ROUTE_PRIORITY -> NotificationPriorityScreen(onBack = { route = ROUTE_SETTINGS })
                        route == ROUTE_NOTIFICATIONS -> kept.SaveableStateProvider(ROUTE_NOTIFICATIONS) {
                            NotificationCenterScreen(
                                store,
                                onBack = { route = "" },
                                onOpenPc = { openPc(it, backTo = ROUTE_NOTIFICATIONS) },
                                onSettings = { route = ROUTE_SETTINGS },
                            )
                        }
                        route == "+" -> PairScreen(
                            store, api,
                            onDone = {
                                pairingLink = null
                                openPc(it.pcId)
                                // The first pairing is when the background state report becomes needed (M-27).
                                PresenceScheduler.sync(this@MainActivity)
                            },
                            onBack = { pairingLink = null; route = "" },
                            link = pairingLink,
                        )
                        pc != null -> PcDetailScreen(pc, store, api, onBack = { back() })
                        else -> PcListScreen(store, api, onOpen = { openPc(it.pcId) }, onAdd = { route = "+" }, onSettings = { route = ROUTE_SETTINGS }, onNotifications = { route = ROUTE_NOTIFICATIONS })
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
        if (UpdateSurface.isUpdateTap(intent)) {
            UpdateSurface.requestOpen()
            updateTapped++
        }
        intent.pushRoute()?.let { tappedRoute = it }
    }

    override fun onResume() {
        super.onResume()
        // A bundle whose notifications were opened or cleared leaves its summary behind (DECISIONS M-37).
        // Again a moment later: the notification tapped to get here is cancelled after the app opens.
        PushNotifier.tidy(this)
        window.decorView.postDelayed({ PushNotifier.tidy(this) }, TIDY_DELAY_MS)
    }

    private companion object {
        const val KEY_PAIRING_LINK = "pairingLink"
        /** Checked before the pcId branch, so it never reaches the PC store. */
        const val ROUTE_SETTINGS = "app:settings"
        const val ROUTE_PRIORITY = "app:priority"
        const val ROUTE_NOTIFICATIONS = "app:notifications"
        const val TIDY_DELAY_MS = 1_500L
        // androidx.activity's default navigation-bar scrims for gesture-less (3-button) navigation.
        val NAV_SCRIM_LIGHT = android.graphics.Color.argb(0xe6, 0xFF, 0xFF, 0xFF)
        val NAV_SCRIM_DARK = android.graphics.Color.argb(0x80, 0x1b, 0x1b, 0x1b)
    }

    private fun Intent?.pairingLink(): String? =
        this?.takeIf { it.action == Intent.ACTION_VIEW }?.dataString?.takeIf(PairingLink::isLink)

    /** The screen a tapped push notification opens: the notification center for a bundle summary, else its PC. */
    private fun Intent?.pushRoute(): String? {
        val tapped = this ?: return null
        return if (tapped.action == PushNotifier.ACTION_OPEN_CENTER) ROUTE_NOTIFICATIONS else tapped.getStringExtra(PushNotifier.EXTRA_PC_ID)
    }
}
