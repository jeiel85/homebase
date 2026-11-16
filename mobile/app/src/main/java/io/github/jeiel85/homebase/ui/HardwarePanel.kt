package io.github.jeiel85.homebase.ui

import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.jeiel85.homebase.net.Hardware
import java.util.Locale

/*
 * The hardware part of the instrument panel (DECISIONS M-34): GPUs, internet ping, temperature and
 * drives. Everything here is drawn on the dark slab, and a value the PC could not read is left out
 * rather than shown as 0.
 */

/** One GPU: load meter with its history, the memory it uses, then temperature / power / fan / clock. */
@OptIn(ExperimentalLayoutApi::class)
@Composable
internal fun GpuCell(g: Hardware.Gpu, trace: List<Float>, capacity: Int, modifier: Modifier = Modifier) {
    val hb = Hb.colors
    val load = g.loadPct
    val mem = g.memory
    val memLabel = if (g.integrated) "공유 메모리" else "VRAM"
    val chips = gpuChips(g)
    val spoken = buildString {
        append("GPU ${g.name}, 사용률 ${load?.let { Fmt.pctShort(it) } ?: "알 수 없음"}")
        if (mem != null) append(", $memLabel ${Fmt.gb(mem.first)} / ${Fmt.gb(mem.second)}")
        chips.forEach { append(", ").append(it.text) }
    }
    Column(
        modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(6.dp))
            .background(hb.slabRaised)
            .padding(horizontal = 10.dp, vertical = 8.dp)
            .semantics(mergeDescendants = true) { contentDescription = spoken },
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text("GPU", style = HbType.label.copy(fontSize = 10.sp), color = hb.onSlabSoft)
            Spacer(Modifier.width(8.dp))
            // Name and tag share the free width, so the load figure always sits at the right edge.
            Row(Modifier.weight(1f), verticalAlignment = Alignment.CenterVertically) {
                Text(
                    shortGpuName(g.name),
                    style = HbType.readout.copy(fontSize = 12.sp, fontWeight = FontWeight.Normal),
                    color = hb.onSlab,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.weight(1f, fill = false),
                )
                if (g.integrated) {
                    Spacer(Modifier.width(6.dp))
                    Tag("내장", hb.meterOff, hb.onSlabSoft)
                }
            }
            Spacer(Modifier.width(8.dp))
            Text(load?.let { Fmt.pctShort(it) } ?: "—", style = HbType.readout.copy(fontSize = 15.sp), color = hb.onSlab, maxLines = 1)
        }
        Spacer(Modifier.height(6.dp))
        SegmentMeter(load ?: 0.0, segments = 24, height = 10.dp)
        Spacer(Modifier.height(4.dp))
        HistoryStrip(trace, capacity = capacity, height = 18.dp)
        if (mem != null) {
            Spacer(Modifier.height(6.dp))
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(memLabel, style = HbType.label.copy(fontSize = 10.sp), color = hb.onSlabSoft)
                Spacer(Modifier.weight(1f))
                Text(
                    "${Fmt.gb(mem.first)} / ${Fmt.gb(mem.second)}",
                    style = HbType.readout.copy(fontSize = 11.sp, fontWeight = FontWeight.Normal),
                    color = hb.onSlab,
                    maxLines = 1,
                )
            }
            Spacer(Modifier.height(3.dp))
            val fraction = (mem.first.toDouble() / mem.second).toFloat()
            ShareBar(fraction, hb.load(fraction * 100.0), Modifier.fillMaxWidth())
        }
        if (chips.isNotEmpty()) {
            Spacer(Modifier.height(8.dp))
            FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                chips.forEach { Chip(it.text, it.color ?: hb.onSlabSoft) }
            }
        }
    }
}

private data class GpuChip(val text: String, val color: Color? = null)

@Composable
private fun gpuChips(g: Hardware.Gpu): List<GpuChip> {
    val hb = Hb.colors
    val list = mutableListOf<GpuChip>()
    g.tempC?.let { t ->
        // Colour by how close it runs to the card's own limit (100 °C when the card does not say).
        val max = g.tempMaxC ?: 100.0
        list += GpuChip(String.format(Locale.ROOT, "%.0f°C", t), hb.load(t / max * 100.0))
    }
    when {
        g.powerW != null && g.powerLimitW != null -> list += GpuChip(String.format(Locale.ROOT, "%.0f / %.0f W", g.powerW, g.powerLimitW))
        g.powerW != null -> list += GpuChip(String.format(Locale.ROOT, "%.0f W", g.powerW))
        g.powerPct != null -> list += GpuChip(String.format(Locale.ROOT, "전력 %.0f%%", g.powerPct))
    }
    when {
        g.fanPct != null -> list += GpuChip(String.format(Locale.ROOT, "팬 %.0f%%", g.fanPct))
        g.fanRpm != null && g.fanRpm > 0 -> list += GpuChip(String.format(Locale.ROOT, "팬 %.0f rpm", g.fanRpm))
    }
    g.clockMhz?.let { list += GpuChip(String.format(Locale.ROOT, "%.0f MHz", it)) }
    return list
}

@Composable
private fun Chip(text: String, color: Color) {
    val hb = Hb.colors
    Text(
        text,
        style = HbType.readout.copy(fontSize = 11.sp, fontWeight = FontWeight.Normal),
        color = color,
        maxLines = 1,
        modifier = Modifier.clip(RoundedCornerShape(3.dp)).background(hb.meterOff).padding(horizontal = 6.dp, vertical = 2.dp),
    )
}

/** Internet latency: the last ping, then the window's average and loss, over a line of recent pings. */
@Composable
internal fun PingCell(p: Hardware.Ping, trace: List<Float>, capacity: Int, modifier: Modifier = Modifier) {
    val hb = Hb.colors
    val value = p.lastMs?.let { String.format(Locale.ROOT, "%.0f ms", it) } ?: "응답 없음"
    val detail = buildString {
        p.avgMs?.let { append(String.format(Locale.ROOT, "평균 %.0f", it)) }
        if (isNotEmpty()) append(" · ")
        append(String.format(Locale.ROOT, "손실 %.0f%%", p.lossPct))
    }
    Column(
        modifier
            .clip(RoundedCornerShape(6.dp))
            .background(hb.slabRaised)
            .padding(horizontal = 10.dp, vertical = 8.dp)
            .semantics(mergeDescendants = true) { contentDescription = "인터넷 핑 ${p.host} $value, $detail" },
    ) {
        Text("핑 · ${p.host}", style = HbType.label.copy(fontSize = 10.sp), color = hb.onSlabSoft, maxLines = 1, overflow = TextOverflow.Ellipsis)
        Row(verticalAlignment = Alignment.Bottom) {
            Text(value, style = HbType.readout.copy(fontSize = 13.sp), color = if (p.lastMs == null) hb.ledOff else hb.onSlab, maxLines = 1)
            Spacer(Modifier.width(6.dp))
            Text(
                detail,
                style = HbType.readout.copy(fontSize = 10.sp, fontWeight = FontWeight.Normal),
                color = if (p.lossPct > 0) hb.accent else hb.onSlabSoft,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }
        Spacer(Modifier.height(4.dp))
        RateTrace(trace, hb.meterOk, capacity = capacity, height = 26.dp, floor = 50f)
    }
}

/** A temperature (CPU package, else the board) with its recent history on the 0–100 °C scale. */
@Composable
internal fun TempCell(label: String, celsius: Double, trace: List<Float>, capacity: Int, modifier: Modifier = Modifier) {
    val hb = Hb.colors
    val value = String.format(Locale.ROOT, "%.0f°C", celsius)
    Column(
        modifier
            .clip(RoundedCornerShape(6.dp))
            .background(hb.slabRaised)
            .padding(horizontal = 10.dp, vertical = 8.dp)
            .semantics(mergeDescendants = true) { contentDescription = "$label $value" },
    ) {
        Text(label, style = HbType.label.copy(fontSize = 10.sp), color = hb.onSlabSoft, maxLines = 1)
        Text(value, style = HbType.readout.copy(fontSize = 13.sp), color = hb.load(celsius), maxLines = 1)
        Spacer(Modifier.height(4.dp))
        HistoryStrip(trace, capacity = capacity, height = 26.dp)
    }
}

/** Fixed drives: a meter of how full each is, and the space left. */
@Composable
internal fun DrivesBlock(disks: List<Hardware.Disk>, modifier: Modifier = Modifier) {
    val hb = Hb.colors
    Column(modifier.fillMaxWidth()) {
        Text("드라이브", style = HbType.label.copy(fontSize = 10.sp), color = hb.onSlabSoft)
        disks.forEach { d ->
            val free = "${Fmt.bytes((d.totalBytes - d.usedBytes).coerceAtLeast(0))} 남음"
            Row(
                Modifier.fillMaxWidth().padding(top = 6.dp).semantics(mergeDescendants = true) {
                    contentDescription = "드라이브 ${d.name} ${Fmt.pctShort(d.usedPct)} 사용, $free"
                },
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(d.name, style = HbType.readout.copy(fontSize = 12.sp), color = hb.onSlab, maxLines = 1, modifier = Modifier.widthIn(min = 26.dp))
                Spacer(Modifier.width(8.dp))
                SegmentMeter(d.usedPct, Modifier.weight(1f), segments = 20, height = 8.dp)
                Spacer(Modifier.width(8.dp))
                Text(
                    free,
                    style = HbType.readout.copy(fontSize = 11.sp, fontWeight = FontWeight.Normal),
                    color = hb.onSlabSoft,
                    maxLines = 1,
                    textAlign = TextAlign.End,
                    modifier = Modifier.widthIn(min = 84.dp),
                )
            }
        }
    }
}

/** "NVIDIA GeForce RTX 5060 Laptop GPU" → "RTX 5060 Laptop", "AMD Radeon(TM) 780M Graphics" → "Radeon 780M Graphics". */
internal fun shortGpuName(name: String): String {
    val short = name
        .replace("(TM)", "").replace("(R)", "")
        .split(' ')
        .filter { it.isNotBlank() && it !in setOf("NVIDIA", "GeForce", "AMD", "Intel") }
        .joinToString(" ")
        .removeSuffix(" GPU")
        .trim()
    return short.ifEmpty { name }
}
