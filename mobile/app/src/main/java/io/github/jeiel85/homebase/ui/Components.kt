package io.github.jeiel85.homebase.ui

import android.provider.Settings
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

private val PlateShape = RoundedCornerShape(8.dp)

/**
 * Carbon command slab across the top, drawn under the status bar, closed by an amber stripe —
 * the tray's masthead on a phone. [title] is shown in the heavy uppercase display face.
 */
@Composable
fun ConsoleBar(
    title: String,
    onBack: (() -> Unit)? = null,
    eyebrow: String? = null,
    status: (@Composable () -> Unit)? = null,
    actions: @Composable RowScope.() -> Unit = {},
) {
    val hb = Hb.colors
    Column(Modifier.fillMaxWidth().background(hb.slab).statusBarsPadding()) {
        Row(
            Modifier.fillMaxWidth().heightIn(min = 64.dp).padding(start = if (onBack == null) 20.dp else 4.dp, end = 8.dp, top = 6.dp, bottom = 8.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            if (onBack != null) {
                IconButton(onClick = onBack) {
                    Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "뒤로", tint = hb.onSlab)
                }
            }
            Column(Modifier.weight(1f)) {
                if (eyebrow != null) Text(eyebrow, style = HbType.label, color = hb.accent)
                Text(
                    title.uppercase(),
                    style = HbType.display.copy(fontSize = if (onBack == null) 26.sp else 22.sp),
                    color = hb.onSlab,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.semantics { heading() },
                )
                if (status != null) {
                    Spacer(Modifier.height(2.dp))
                    status()
                }
            }
            actions()
        }
        Box(Modifier.fillMaxWidth().height(3.dp).background(hb.accent))
    }
}

/** Section label on the page background: small uppercase text with a short amber tick. */
@Composable
fun SectionLabel(text: String, modifier: Modifier = Modifier, trailing: String? = null) {
    val hb = Hb.colors
    Row(modifier.fillMaxWidth().padding(top = 18.dp, bottom = 8.dp), verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.size(width = 10.dp, height = 3.dp).background(hb.accent))
        Spacer(Modifier.width(8.dp))
        Text(text, style = HbType.label, color = hb.inkSoft, modifier = Modifier.weight(1f).semantics { heading() })
        if (trailing != null) Text(trailing, style = HbType.label, color = hb.inkSoft)
    }
}

/**
 * A faceplate: rounded plate with a coloured bevel line along the bottom (depth is a physical
 * edge, not a soft shadow — same rule as the tray). [rail] paints an amber stripe on the left
 * edge to mark the one item that is "live" (the foreground window).
 */
@Composable
fun Plate(
    modifier: Modifier = Modifier,
    onClick: (() -> Unit)? = null,
    onClickLabel: String? = null,
    rail: Boolean = false,
    dim: Boolean = false,
    content: @Composable ColumnScope.() -> Unit,
) {
    val hb = Hb.colors
    val click = if (onClick != null) Modifier.clickable(onClickLabel = onClickLabel, role = Role.Button, onClick = onClick) else Modifier
    Box(
        modifier
            .fillMaxWidth()
            .clip(PlateShape)
            .background(if (rail) hb.accent else hb.plateEdge)
            .then(click),
    ) {
        Column(
            Modifier
                .fillMaxWidth()
                .padding(start = if (rail) 4.dp else 0.dp, bottom = 2.dp)
                .background(if (dim) hb.plate.copy(alpha = if (hb.dark) 0.6f else 0.75f) else hb.plate)
                .background(if (dim) hb.background.copy(alpha = 0.35f) else Color.Transparent),
            content = content,
        )
    }
}

/** Small filled tag (e.g. "활성", "v0.14.0"). */
@Composable
fun Tag(text: String, background: Color, foreground: Color, modifier: Modifier = Modifier) {
    Text(
        text,
        style = HbType.label.copy(fontSize = 10.sp, letterSpacing = 0.8.sp),
        color = foreground,
        maxLines = 1,
        modifier = modifier.clip(RoundedCornerShape(3.dp)).background(background).padding(horizontal = 6.dp, vertical = 2.dp),
    )
}

/** True when the user turned animations off (Developer options / accessibility "Remove animations"). */
@Composable
fun rememberReduceMotion(): Boolean {
    val context = LocalContext.current
    return remember(context) {
        Settings.Global.getFloat(context.contentResolver, Settings.Global.ANIMATOR_DURATION_SCALE, 1f) == 0f
    }
}

/** Status lamp with a soft glow; [pulse] breathes slowly while a live stream is flowing. */
@Composable
fun Led(color: Color, modifier: Modifier = Modifier, size: Dp = 10.dp, pulse: Boolean = false) {
    val still = rememberReduceMotion()
    val glow = if (pulse && !still) {
        val t = rememberInfiniteTransition(label = "led")
        val a by t.animateFloat(0.25f, 0.7f, infiniteRepeatable(tween(900), RepeatMode.Reverse), label = "glow")
        a
    } else {
        0.45f
    }
    Canvas(modifier.size(size * 2)) {
        val r = size.toPx() / 2
        drawCircle(Brush.radialGradient(listOf(color.copy(alpha = glow), Color.Transparent), center, radius = r * 2), radius = r * 2)
        drawCircle(color, radius = r)
        drawCircle(Color.White.copy(alpha = 0.55f), radius = r * 0.35f, center = Offset(center.x - r * 0.3f, center.y - r * 0.3f))
    }
}

/**
 * The signature instrument: a row of discrete LED segments like a VU meter. Segments past 75 %
 * are amber and past 90 % red when lit, so the scale itself shows where trouble starts.
 */
@Composable
fun SegmentMeter(
    pct: Double,
    modifier: Modifier = Modifier,
    segments: Int = 24,
    height: Dp = 14.dp,
    description: String? = null,
) {
    val hb = Hb.colors
    val p = pct.coerceIn(0.0, 100.0)
    val sem = if (description != null) Modifier.semantics { contentDescription = description } else Modifier
    Canvas(modifier.fillMaxWidth().height(height).then(sem)) {
        val gap = 2.dp.toPx()
        val w = (size.width - gap * (segments - 1)) / segments
        val radius = CornerRadius(1.5.dp.toPx())
        for (i in 0 until segments) {
            val start = i * 100.0 / segments
            val lit = p > start + 0.01 || (i == 0 && p > 0.5)
            val zone = hb.load(start + 100.0 / segments / 2)
            drawRoundRect(
                color = if (lit) zone else hb.meterOff,
                topLeft = Offset(i * (w + gap), 0f),
                size = Size(w, size.height),
                cornerRadius = radius,
            )
        }
    }
}

/**
 * Arc version of [SegmentMeter] for the headline CPU/RAM gauges: 240° of LED segments with the
 * readout in the middle. Drawn on a slab, so text uses the on-slab roles.
 */
@Composable
fun ArcGauge(
    pct: Double,
    label: String,
    value: String,
    detail: String?,
    modifier: Modifier = Modifier,
    segments: Int = 30,
) {
    val hb = Hb.colors
    val p = pct.coerceIn(0.0, 100.0)
    BoxWithConstraints(
        modifier.semantics(mergeDescendants = true) { contentDescription = "$label $value" + (detail?.let { ", $it" } ?: "") },
    ) {
        // A 240° arc spans 0.75 of its circle's height, so size the circle to fill the box
        // without an empty bottom quarter, and centre the readout on the circle's centre.
        val strokeDp = minOf(maxWidth, maxHeight) * 0.085f
        val d = minOf(maxWidth - strokeDp, (maxHeight - strokeDp) / 0.75f)
        Canvas(Modifier.matchParentSize()) {
            val stroke = strokeDp.toPx()
            val dia = d.toPx()
            val topLeft = Offset((size.width - dia) / 2, stroke / 2)
            val each = 240f / segments
            val gap = 1.6f
            for (i in 0 until segments) {
                val start = i * 100.0 / segments
                val lit = p > start + 0.01 || (i == 0 && p > 0.5)
                drawArc(
                    color = if (lit) hb.load(start + 100.0 / segments / 2) else hb.meterOff,
                    startAngle = 150f + i * each + gap / 2,
                    sweepAngle = each - gap,
                    useCenter = false,
                    topLeft = topLeft,
                    size = Size(dia, dia),
                    style = Stroke(width = stroke),
                )
            }
        }
        Column(
            horizontalAlignment = Alignment.CenterHorizontally,
            modifier = Modifier.align(Alignment.TopCenter).padding(top = (strokeDp / 2 + d / 2 - 30.dp).coerceAtLeast(0.dp)),
        ) {
            Text(label, style = HbType.label, color = hb.onSlabSoft)
            Text(value, style = HbType.readout.copy(fontSize = 30.sp, letterSpacing = (-0.5).sp), color = hb.onSlab, maxLines = 1)
            if (detail != null) Text(detail, style = HbType.readout.copy(fontSize = 11.sp, fontWeight = FontWeight.Normal), color = hb.onSlabSoft, maxLines = 1)
        }
    }
}

/**
 * Rolling line for unbounded values (bytes/s): auto-scales to the window's peak so the shape of
 * the traffic is readable even when the absolute number isn't. Filled area under a line.
 */
@Composable
fun RateTrace(values: List<Float>, color: Color, modifier: Modifier = Modifier, capacity: Int = 60, height: Dp = 30.dp) {
    val hb = Hb.colors
    Canvas(modifier.fillMaxWidth().height(height)) {
        drawLine(hb.meterOff, Offset(0f, size.height - 0.5f), Offset(size.width, size.height - 0.5f), strokeWidth = 1f)
        if (values.size < 2) return@Canvas
        val peak = values.max().coerceAtLeast(1024f) // below 1 KB/s everything is "flat"
        val step = size.width / (capacity - 1)
        val x0 = (capacity - values.size) * step
        val line = Path()
        values.forEachIndexed { i, v ->
            val x = x0 + i * step
            val y = size.height - (v / peak) * (size.height - 2f) - 1f
            if (i == 0) line.moveTo(x, y) else line.lineTo(x, y)
        }
        val area = Path().apply {
            addPath(line)
            lineTo(x0 + (values.size - 1) * step, size.height)
            lineTo(x0, size.height)
            close()
        }
        drawPath(area, Brush.verticalGradient(listOf(color.copy(alpha = 0.45f), color.copy(alpha = 0.05f))))
        drawPath(line, color, style = Stroke(width = 1.6.dp.toPx()))
    }
}

/** Thin proportion bar (share of something), for per-app CPU/RAM. */
@Composable
fun ShareBar(fraction: Float, color: Color, modifier: Modifier = Modifier, height: Dp = 4.dp) {
    val hb = Hb.colors
    Canvas(modifier.height(height)) {
        val r = CornerRadius(size.height / 2)
        drawRoundRect(hb.hairline, size = size, cornerRadius = r)
        val w = size.width * fraction.coerceIn(0f, 1f)
        if (w > 0f) drawRoundRect(color, size = Size(w.coerceAtLeast(size.height), size.height), cornerRadius = r)
    }
}

/** Last N samples as thin bars (newest on the right), drawn on a slab. */
@Composable
fun HistoryStrip(values: List<Float>, modifier: Modifier = Modifier, capacity: Int = 60, height: Dp = 28.dp) {
    val hb = Hb.colors
    Canvas(modifier.fillMaxWidth().height(height)) {
        val step = size.width / capacity
        val barW = (step * 0.62f).coerceAtLeast(1f)
        // Baseline + 50 % guide.
        drawLine(hb.meterOff, Offset(0f, size.height - 0.5f), Offset(size.width, size.height - 0.5f), strokeWidth = 1f)
        drawLine(hb.meterOff.copy(alpha = 0.6f), Offset(0f, size.height / 2), Offset(size.width, size.height / 2), strokeWidth = 1f)
        val offset = capacity - values.size
        values.forEachIndexed { i, v ->
            val h = (v.coerceIn(0f, 100f) / 100f) * size.height
            if (h <= 0f) return@forEachIndexed
            val x = (offset + i) * step + (step - barW) / 2
            val fade = 0.35f + 0.65f * ((i + 1f) / values.size)
            drawRect(hb.load(v.toDouble()).copy(alpha = fade), Offset(x, size.height - h), Size(barW, h))
        }
    }
}

/** The one forward action on a screen — signal orange, full width, 56 dp tall. */
@Composable
fun SignalButton(text: String, onClick: () -> Unit, modifier: Modifier = Modifier, enabled: Boolean = true) {
    val hb = Hb.colors
    Button(
        onClick = onClick,
        enabled = enabled,
        shape = RoundedCornerShape(6.dp),
        colors = ButtonDefaults.buttonColors(containerColor = hb.action, contentColor = hb.onAction),
        modifier = modifier.fillMaxWidth().heightIn(min = 56.dp),
    ) { Text(text, style = HbType.label.copy(fontSize = 15.sp, letterSpacing = 0.5.sp)) }
}

/** Secondary command — outlined in ink, 48 dp minimum. */
@Composable
fun QuietButton(text: String, onClick: () -> Unit, modifier: Modifier = Modifier, enabled: Boolean = true) {
    val hb = Hb.colors
    OutlinedButton(
        onClick = onClick,
        enabled = enabled,
        shape = RoundedCornerShape(6.dp),
        colors = ButtonDefaults.outlinedButtonColors(contentColor = hb.ink),
        border = androidx.compose.foundation.BorderStroke(1.5.dp, if (enabled) hb.inkSoft else hb.hairline),
        modifier = modifier.fillMaxWidth().heightIn(min = 48.dp),
    ) { Text(text, style = HbType.label.copy(fontSize = 13.sp, letterSpacing = 0.4.sp)) }
}
