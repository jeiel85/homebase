package io.github.jeiel85.homebase.ui

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import java.util.Locale

private val Amber = Color(0xFFECAB37)
private val Carbon = Color(0xFF21242E)
private val Indigo = Color(0xFF3D4F97)

@Composable
fun HomebaseTheme(content: @Composable () -> Unit) {
    val scheme = if (isSystemInDarkTheme()) {
        darkColorScheme(
            primary = Amber, onPrimary = Carbon,
            secondary = Color(0xFF8BA1D4),
            background = Color(0xFF1B1E27), surface = Color(0xFF1B1E27),
            surfaceVariant = Carbon, secondaryContainer = Indigo, onSecondaryContainer = Color.White,
        )
    } else {
        lightColorScheme(
            primary = Indigo, onPrimary = Color.White,
            secondary = Amber,
            secondaryContainer = Color(0xFFFFE3B0), onSecondaryContainer = Carbon,
        )
    }
    MaterialTheme(colorScheme = scheme, content = content)
}

@Composable
fun SettingsSwitchRow(
    title: String,
    description: String,
    checked: Boolean,
    onCheckedChange: (Boolean) -> Unit,
    titleModifier: Modifier = Modifier,
) {
    Row(Modifier.fillMaxWidth().padding(vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
        Column(Modifier.weight(1f)) {
            Text(title, style = MaterialTheme.typography.titleSmall, modifier = titleModifier)
            Text(description, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        Spacer(Modifier.width(12.dp))
        Switch(checked = checked, onCheckedChange = onCheckedChange)
    }
}

object Fmt {
    fun bytes(b: Long): String {
        if (b < 1024) return "$b B"
        val units = arrayOf("KB", "MB", "GB", "TB")
        var v = b / 1024.0
        var i = 0
        while (v >= 1024 && i < units.lastIndex) { v /= 1024; i++ }
        return String.format(Locale.ROOT, if (v >= 100) "%.0f %s" else "%.1f %s", v, units[i])
    }

    fun rate(bps: Long?): String = if (bps == null) "—" else bytes(bps) + "/s"

    fun pct(p: Double): String = String.format(Locale.ROOT, "%.1f%%", p)

    fun uptime(sec: Long): String {
        val d = sec / 86400
        val h = (sec % 86400) / 3600
        val m = (sec % 3600) / 60
        return if (d > 0) "${d}일 ${h}시간" else "${h}시간 ${m}분"
    }
}
