package io.github.jeiel85.homebase.ui

import android.os.SystemClock
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.selection.toggleable
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.Immutable
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.unit.dp
import io.github.jeiel85.homebase.net.LiveSnapshot
import java.util.Locale

/** A switch row; the whole row toggles (48 dp+ target), not just the thumb. */
@Composable
fun SettingsSwitchRow(
    title: String,
    description: String,
    checked: Boolean,
    onCheckedChange: (Boolean) -> Unit,
    titleModifier: Modifier = Modifier,
) {
    val hb = Hb.colors
    Row(
        Modifier
            .fillMaxWidth()
            .heightIn(min = 56.dp)
            .toggleable(value = checked, role = Role.Switch, onValueChange = onCheckedChange)
            .padding(vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(Modifier.weight(1f)) {
            Text(title, style = MaterialTheme.typography.titleSmall, color = hb.ink, modifier = titleModifier)
            Text(description, style = MaterialTheme.typography.bodySmall, color = hb.inkSoft)
        }
        Spacer(Modifier.width(12.dp))
        Switch(
            checked = checked,
            onCheckedChange = null,
            colors = SwitchDefaults.colors(
                checkedTrackColor = hb.accent,
                checkedThumbColor = Brand.Carbon,
                checkedBorderColor = hb.accent,
            ),
        )
    }
}

/**
 * The last live reading per PC, kept in memory for this app process only. The list's `/info`
 * poll carries no load figures, so the list shows these — labelled with their age — for PCs the
 * user has opened, rather than opening a live stream per PC just to draw the list.
 */
object LastSeen {
    @Immutable
    data class Reading(val cpuPct: Double, val memPct: Double, val atElapsedMs: Long)

    private val readings = mutableStateMapOf<String, Reading>()

    fun record(pcId: String, s: LiveSnapshot) {
        val mem = if (s.system.memTotalBytes > 0) s.system.memUsedBytes * 100.0 / s.system.memTotalBytes else 0.0
        readings[pcId] = Reading(s.system.cpuPct, mem, SystemClock.elapsedRealtime())
    }

    fun get(pcId: String): Reading? = readings[pcId]
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

    /** Always in GB with one decimal — for the RAM gauge where the unit shouldn't jump. */
    fun gb(b: Long): String = String.format(Locale.ROOT, "%.1f GB", b / (1024.0 * 1024 * 1024))

    fun rate(bps: Long?): String = if (bps == null) "—" else bytes(bps) + "/s"

    fun pct(p: Double): String = String.format(Locale.ROOT, "%.1f%%", p)

    /** Gauge readout: whole percent from 10 % up, one decimal below. */
    fun pctShort(p: Double): String = String.format(Locale.ROOT, if (p >= 9.95) "%.0f%%" else "%.1f%%", p)

    fun uptime(sec: Long): String {
        val d = sec / 86400
        val h = (sec % 86400) / 3600
        val m = (sec % 3600) / 60
        return if (d > 0) "${d}일 ${h}시간" else "${h}시간 ${m}분"
    }

    fun age(ms: Long): String {
        val s = ms / 1000
        return when {
            s < 60 -> "방금"
            s < 3600 -> "${s / 60}분 전"
            else -> "${s / 3600}시간 전"
        }
    }
}
