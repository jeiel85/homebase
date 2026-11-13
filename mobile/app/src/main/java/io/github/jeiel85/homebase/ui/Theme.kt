package io.github.jeiel85.homebase.ui

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.Immutable
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.LineBreak
import androidx.compose.ui.unit.sp

/*
 * Homebase "console" identity, shared with the PC tray (src/LocalOpsBot.Tray/Themes/HomebaseTheme.xaml):
 * periwinkle metal plates, carbon command slabs, amber/signal kept for wayfinding only, and a
 * heavy uppercase wordmark. Readouts use a monospace face like an instrument display. No font
 * files are bundled — Roboto Black stands in for Arial Black and the platform mono for readouts.
 */

/** Brand constants — the same hex values as the tray palette. */
object Brand {
    val Canvas = Color(0xFF7A8ABA)
    val Periwinkle = Color(0xFF8BA1D4)
    val Carbon = Color(0xFF21242E)
    val ChromeIndigo = Color(0xFF3D4F97)
    val Amber = Color(0xFFECAB37)
    val Signal = Color(0xFFF68D1F)
    val Red = Color(0xFFE60012)
}

/**
 * Role colours. [slab] is the carbon instrument panel; it stays dark in both modes (as in the
 * tray), so anything drawn on it uses the `onSlab*` roles regardless of the system theme.
 */
@Immutable
data class HbColors(
    val dark: Boolean,
    val background: Color,
    val plate: Color,
    val plateEdge: Color,
    val ink: Color,
    val inkSoft: Color,
    val hairline: Color,
    val slab: Color,
    val slabRaised: Color,
    val onSlab: Color,
    val onSlabSoft: Color,
    val meterOk: Color,
    val meterOff: Color,
    val ledOn: Color,
    val ledOff: Color,
    val accent: Color,
    val action: Color,
    val onAction: Color,
    val danger: Color,
)

private val Light = HbColors(
    dark = false,
    background = Color(0xFFE3E7F3),
    plate = Color(0xFFFFFFFF),
    plateEdge = Brand.Canvas,
    ink = Brand.Carbon,
    inkSoft = Color(0xFF4A5180), // 7.6:1 on white
    hairline = Color(0xFFC9CFE3),
    slab = Brand.Carbon,
    slabRaised = Color(0xFF2E3240),
    onSlab = Color(0xFFFFFFFF),
    onSlabSoft = Color(0xFFB4BEE2), // 9:1 on carbon
    meterOk = Brand.Periwinkle,
    meterOff = Color(0xFF353A4A),
    ledOn = Color(0xFF43C463),
    ledOff = Color(0xFFFF4D4D),
    accent = Brand.Amber,
    action = Brand.Signal,
    onAction = Brand.Carbon, // 6.9:1 — white on signal would fail contrast
    danger = Color(0xFFC40010),
)

private val Dark = HbColors(
    dark = true,
    background = Color(0xFF1A1D26),
    plate = Color(0xFF252935),
    plateEdge = Brand.ChromeIndigo,
    ink = Color(0xFFE9ECF7),
    inkSoft = Color(0xFFA9B3DA), // 8:1 on plate
    hairline = Color(0xFF363B52),
    slab = Color(0xFF0E0F14),
    slabRaised = Color(0xFF1C1F29),
    onSlab = Color(0xFFFFFFFF),
    onSlabSoft = Color(0xFFA9B3DA),
    meterOk = Brand.Periwinkle,
    meterOff = Color(0xFF262A37),
    ledOn = Color(0xFF4AD66D),
    ledOff = Color(0xFFFF5A52),
    accent = Brand.Amber,
    action = Brand.Signal,
    onAction = Brand.Carbon,
    danger = Color(0xFFFF6B61),
)

val LocalHb = staticCompositionLocalOf { Light }

/** Shorthand: `Hb.colors.plate`. */
object Hb {
    val colors: HbColors
        @Composable get() = LocalHb.current
}

/** Display and readout faces. */
object HbType {
    /** Heavy uppercase chrome voice (wordmark, PC names). Callers pass uppercased text. */
    val display = TextStyle(fontFamily = FontFamily.SansSerif, fontWeight = FontWeight.Black, letterSpacing = (-0.3).sp)

    /** Small labels on slabs ("CPU", "내 PC"). Light tracking — wide tracking breaks up Hangul. */
    val label = TextStyle(fontFamily = FontFamily.SansSerif, fontWeight = FontWeight.Bold, fontSize = 11.sp, letterSpacing = 0.6.sp)

    /**
     * Instrument readouts — tabular (fixed-width) digits so values don't jitter every second.
     * Roboto's `tnum` instead of a monospace family: mono fallbacks space Hangul and units apart.
     */
    val readout = TextStyle(fontFamily = FontFamily.SansSerif, fontWeight = FontWeight.Bold, fontFeatureSettings = "tnum")
}

/** Meter colour by load: cool by default, amber from 75 %, red from 90 % (same thresholds as the tray). */
fun HbColors.load(pct: Double): Color = when {
    pct >= 90 -> ledOff
    pct >= 75 -> accent
    else -> meterOk
}

/**
 * Body text breaks between words (phrase-based breaking, where the platform supports it for
 * Korean) instead of mid-word, which reads badly on narrow screens.
 */
private val koreanTypography: Typography = Typography().let { t ->
    val lb = LineBreak.Paragraph.copy(wordBreak = LineBreak.WordBreak.Phrase)
    t.copy(
        bodyLarge = t.bodyLarge.copy(lineBreak = lb),
        bodyMedium = t.bodyMedium.copy(lineBreak = lb),
        bodySmall = t.bodySmall.copy(lineBreak = lb),
        titleSmall = t.titleSmall.copy(lineBreak = lb),
        titleMedium = t.titleMedium.copy(lineBreak = lb),
    )
}

@Composable
fun HomebaseTheme(content: @Composable () -> Unit) {
    val hb = if (isSystemInDarkTheme()) Dark else Light
    // Material components (dialogs, text fields, switches) follow the same roles.
    val scheme = if (hb.dark) {
        darkColorScheme(
            primary = Brand.Amber, onPrimary = Brand.Carbon,
            secondary = Brand.Periwinkle, onSecondary = Brand.Carbon,
            secondaryContainer = Brand.ChromeIndigo, onSecondaryContainer = Color.White,
            background = hb.background, onBackground = hb.ink,
            surface = hb.plate, onSurface = hb.ink,
            surfaceVariant = hb.slabRaised, onSurfaceVariant = hb.inkSoft,
            surfaceContainerHigh = hb.plate, surfaceContainerHighest = hb.slabRaised,
            outline = hb.inkSoft, outlineVariant = hb.hairline,
            error = hb.danger,
        )
    } else {
        lightColorScheme(
            primary = Brand.ChromeIndigo, onPrimary = Color.White,
            secondary = Brand.Amber, onSecondary = Brand.Carbon,
            secondaryContainer = Color(0xFFFFE3B0), onSecondaryContainer = Brand.Carbon,
            background = hb.background, onBackground = hb.ink,
            surface = hb.plate, onSurface = hb.ink,
            surfaceVariant = Color(0xFFEDEFF7), onSurfaceVariant = hb.inkSoft,
            surfaceContainerHigh = hb.plate, surfaceContainerHighest = Color(0xFFEDEFF7),
            outline = hb.inkSoft, outlineVariant = hb.hairline,
            error = hb.danger,
        )
    }
    CompositionLocalProvider(LocalHb provides hb) {
        MaterialTheme(colorScheme = scheme, typography = koreanTypography, content = content)
    }
}
