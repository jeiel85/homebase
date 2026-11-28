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
import androidx.core.app.NotificationManagerCompat
import io.github.jeiel85.homebase.data.PcStore
import io.github.jeiel85.homebase.mesh.PresenceScheduler
import io.github.jeiel85.homebase.net.HomebaseApi
import io.github.jeiel85.homebase.net.NetworkProbe
import io.github.jeiel85.homebase.net.PairingLink
import io.github.jeiel85.homebase.clip.ClipNotifier
import io.github.jeiel85.homebase.push.ClipHistory
import io.github.jeiel85.homebase.push.FileHistory
import io.github.jeiel85.homebase.push.PushNotifier
import io.github.jeiel85.homebase.push.PushRegistrar
import io.github.jeiel85.homebase.share.ReceiveNotifier
import io.github.jeiel85.homebase.share.ShareShortcuts
import io.github.jeiel85.homebase.share.UploadNotifier
import io.github.jeiel85.homebase.ui.AdviceRequests
import io.github.jeiel85.homebase.ui.CenterFocus
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
import kotlinx.coroutines.launch

/** A handful of screens — list, pair, detail, settings, notification center — so plain state instead of a navigation library. */
class MainActivity : ComponentActivity() {
    /** A pairing link the app was opened with (camera → App Link, or the install page's button). */
    private var pairingLink by mutableStateOf<String?>(null)

    /** Bumped when an update notification is tapped while the app is already open: back to the list, where the banner opens the dialog. */
    private var updateTapped by mutableIntStateOf(0)

    /** Where a push notification tapped while the app is already open leads (a PC, or the notification center). */
    private var tappedRoute by mutableStateOf<String?>(null)

    /**
     * What a tapped upload, download or received-text notification shows in the notification center ("보낸 파일",
     * "받은 파일" or "받은 클립보드", its entry open); taken once shown. Kept across recreation until then (#109).
     */
    private var centerFocus by mutableStateOf<CenterFocus?>(null)

    private lateinit var store: PcStore
    private lateinit var api: HomebaseApi

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
        store = PcStore(this)
        val probe = NetworkProbe(this)
        api = HomebaseApi(networks = probe::current)
        // The share sheet's PC shortcuts (Android 10+), also for PCs paired before they existed; a no-op when current.
        val app = applicationContext
        PushRegistrar.scope.launch { ShareShortcuts.sync(app, PcStore(app).all()) }
        // An alert's "AI 조언" button (M-61); not again when the activity is recreated with the same intent.
        if (savedInstanceState == null) askAdviceIfTapped(intent)
        centerFocus = if (savedInstanceState == null) intent.centerFocus() else savedInstanceState.centerFocus()
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
                    // A tapped push notification opens its entry in the notification center (M-81), a bundle summary the
                    // center itself; an alert's "AI 조언", its PC.
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
                                focus = centerFocus,
                                onFocused = { centerFocus = null },
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
        // A focus not applied yet — the history was still loading when the screen turned (#109).
        centerFocus?.let {
            outState.putString(KEY_FOCUS_CATEGORY, it.category)
            it.id?.let { id -> outState.putString(KEY_FOCUS_ID, id) }
            it.pcId?.let { pcId -> outState.putString(KEY_FOCUS_PC, pcId) }
        }
    }

    private fun Bundle.centerFocus(): CenterFocus? =
        getString(KEY_FOCUS_CATEGORY)?.let { CenterFocus(it, getString(KEY_FOCUS_ID), getString(KEY_FOCUS_PC)) }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        intent.pairingLink()?.let { pairingLink = it }
        if (UpdateSurface.isUpdateTap(intent)) {
            UpdateSurface.requestOpen()
            updateTapped++
        }
        intent.pushRoute()?.let { tappedRoute = it }
        intent.centerFocus()?.let { centerFocus = it }
        askAdviceIfTapped(intent)
    }

    override fun onResume() {
        super.onResume()
        // A bundle whose notifications were opened or cleared leaves its summary behind (DECISIONS M-37).
        // Again a moment later: the notification tapped to get here is cancelled after the app opens.
        PushNotifier.tidy(this)
        window.decorView.postDelayed({ PushNotifier.tidy(this) }, TIDY_DELAY_MS)
        // Back to an app that lived in the background a long time: the Play build asks again when it is due (#77).
        UpdateSurface.onResume(this)
    }

    private companion object {
        const val KEY_PAIRING_LINK = "pairingLink"
        const val KEY_FOCUS_CATEGORY = "centerFocusCategory"
        const val KEY_FOCUS_ID = "centerFocusId"
        const val KEY_FOCUS_PC = "centerFocusPc"
        /** Checked before the pcId branch, so it never reaches the PC store. */
        const val ROUTE_SETTINGS = "app:settings"
        const val ROUTE_PRIORITY = "app:priority"
        const val ROUTE_NOTIFICATIONS = "app:notifications"
        const val TIDY_DELAY_MS = 1_500L
        // androidx.activity's default navigation-bar scrims for gesture-less (3-button) navigation.
        val NAV_SCRIM_LIGHT = android.graphics.Color.argb(0xe6, 0xFF, 0xFF, 0xFF)
        val NAV_SCRIM_DARK = android.graphics.Color.argb(0x80, 0x1b, 0x1b, 0x1b)
    }

    /** An alert's "AI 조언" (DECISIONS M-61): ask the PC now; its screen opens through [pushRoute]. */
    private fun askAdviceIfTapped(tapped: Intent?) {
        if (tapped?.action != PushNotifier.ACTION_ASK_ADVICE) return
        val pcId = tapped.getStringExtra(PushNotifier.EXTRA_PC_ID) ?: return
        tapped.getIntExtra(PushNotifier.EXTRA_NOTIFICATION_ID, 0).takeIf { it != 0 }
            ?.let { NotificationManagerCompat.from(this).cancel(it) }
        AdviceRequests.ask(this, api, store, pcId)
    }

    private fun Intent?.pairingLink(): String? =
        this?.takeIf { it.action == Intent.ACTION_VIEW }?.dataString?.takeIf(PairingLink::isLink)

    /**
     * The screen a tapped notification opens: the notification center for a push (on its entry, M-81), a bundle summary,
     * an upload's or a received text's notification; the PC for an alert's "AI 조언".
     */
    private fun Intent?.pushRoute(): String? {
        val tapped = this ?: return null
        return when (tapped.action) {
            PushNotifier.ACTION_OPEN_CENTER, PushNotifier.ACTION_OPEN_ENTRY, UploadNotifier.ACTION_OPEN_SENT,
            ReceiveNotifier.ACTION_OPEN_RECEIVED, ClipNotifier.ACTION_OPEN_RECEIVED -> ROUTE_NOTIFICATIONS
            else -> tapped.getStringExtra(PushNotifier.EXTRA_PC_ID)
        }
    }

    /**
     * A tapped upload notification: the sent files, with its transfer open; a download's: the received files; a received
     * text's: the received texts, it open.
     */
    private fun Intent?.centerFocus(): CenterFocus? = when (this?.action) {
        UploadNotifier.ACTION_OPEN_SENT -> CenterFocus(FileHistory.SENT, getStringExtra(UploadNotifier.EXTRA_TRANSFER_ID))
        ReceiveNotifier.ACTION_OPEN_RECEIVED -> CenterFocus(FileHistory.RECEIVED, getStringExtra(ReceiveNotifier.EXTRA_TRANSFER_ID))
        ClipNotifier.ACTION_OPEN_RECEIVED -> CenterFocus(ClipHistory.RECEIVED, getStringExtra(ClipNotifier.EXTRA_CLIP_ID))
        // A push: its own entry, open, with no filter picked (M-81).
        PushNotifier.ACTION_OPEN_ENTRY -> getStringExtra(PushNotifier.EXTRA_CATEGORY)?.let { category ->
            CenterFocus(category, getStringExtra(PushNotifier.EXTRA_PUSH_ID), pcId = getStringExtra(PushNotifier.EXTRA_PC_ID))
        }
        else -> null
    }
}
