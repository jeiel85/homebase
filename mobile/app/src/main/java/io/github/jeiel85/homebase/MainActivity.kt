package io.github.jeiel85.homebase

import android.content.Intent
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.material3.Surface
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import io.github.jeiel85.homebase.data.PcStore
import io.github.jeiel85.homebase.net.HomebaseApi
import io.github.jeiel85.homebase.net.NetworkProbe
import io.github.jeiel85.homebase.net.PairingLink
import io.github.jeiel85.homebase.ui.HomebaseTheme
import io.github.jeiel85.homebase.ui.PairScreen
import io.github.jeiel85.homebase.ui.PcDetailScreen
import io.github.jeiel85.homebase.ui.PcListScreen

/** Three screens — list, pair, detail — so plain state instead of a navigation library. */
class MainActivity : ComponentActivity() {
    /** A pairing link the app was opened with (camera → App Link, or the install page's button). */
    private var pairingLink by mutableStateOf<String?>(null)

    override fun onCreate(savedInstanceState: Bundle?) {
        // Every screen starts with the carbon console bar, so the status bar always gets light
        // icons; the navigation bar follows the system theme.
        enableEdgeToEdge(statusBarStyle = SystemBarStyle.dark(android.graphics.Color.TRANSPARENT))
        super.onCreate(savedInstanceState)
        // A pending (unconfirmed) link survives rotation and other recreation; the first launch reads the intent.
        pairingLink = if (savedInstanceState == null) intent.pairingLink() else savedInstanceState.getString(KEY_PAIRING_LINK)
        val store = PcStore(this)
        val probe = NetworkProbe(this)
        val api = HomebaseApi(networks = probe::current)
        setContent {
            HomebaseTheme {
                Surface {
                    // "" = list, "+" = pair, otherwise a pcId.
                    var route by rememberSaveable { mutableStateOf("") }
                    val pc = remember(route) { route.takeIf { it.length > 1 }?.let(store::get) }
                    BackHandler(enabled = route.isNotEmpty()) { route = ""; pairingLink = null }
                    LaunchedEffect(pairingLink) { if (pairingLink != null) route = "+" }
                    when {
                        route == "+" -> PairScreen(
                            store, api,
                            onDone = { pairingLink = null; route = it.pcId },
                            onBack = { pairingLink = null; route = "" },
                            link = pairingLink,
                        )
                        pc != null -> PcDetailScreen(pc, store, api, onBack = { route = "" })
                        else -> PcListScreen(store, api, onOpen = { route = it.pcId }, onAdd = { route = "+" })
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
    }

    private companion object {
        const val KEY_PAIRING_LINK = "pairingLink"
    }

    private fun Intent?.pairingLink(): String? =
        this?.takeIf { it.action == Intent.ACTION_VIEW }?.dataString?.takeIf(PairingLink::isLink)
}
