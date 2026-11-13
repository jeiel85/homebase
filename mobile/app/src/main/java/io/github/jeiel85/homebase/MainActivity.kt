package io.github.jeiel85.homebase

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.compose.material3.Surface
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import io.github.jeiel85.homebase.data.PcStore
import io.github.jeiel85.homebase.net.HomebaseApi
import io.github.jeiel85.homebase.net.NetworkProbe
import io.github.jeiel85.homebase.ui.HomebaseTheme
import io.github.jeiel85.homebase.ui.PairScreen
import io.github.jeiel85.homebase.ui.PcDetailScreen
import io.github.jeiel85.homebase.ui.PcListScreen

/** Three screens — list, pair, detail — so plain state instead of a navigation library. */
class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val store = PcStore(this)
        val probe = NetworkProbe(this)
        val api = HomebaseApi(networks = probe::current)
        setContent {
            HomebaseTheme {
                Surface {
                    // "" = list, "+" = pair, otherwise a pcId.
                    var route by rememberSaveable { mutableStateOf("") }
                    val pc = remember(route) { route.takeIf { it.length > 1 }?.let(store::get) }
                    BackHandler(enabled = route.isNotEmpty()) { route = "" }
                    when {
                        route == "+" -> PairScreen(store, api, onDone = { route = it.pcId }, onBack = { route = "" })
                        pc != null -> PcDetailScreen(pc, store, api, onBack = { route = "" })
                        else -> PcListScreen(store, api, onOpen = { route = it.pcId }, onAdd = { route = "+" })
                    }
                }
            }
        }
    }
}
