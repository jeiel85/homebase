package io.github.jeiel85.homebase.ui

import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.graphics.vector.addPathNodes
import androidx.compose.ui.unit.dp

/**
 * The remote command keys' glyphs, drawn in one weight like material-icons-core's Lock and Refresh beside them. The app
 * has only material-icons-core, and the text symbols they replace (⏮ ⏯ ⏭ 🔇 − +) came out in the phone's fonts — the
 * mute one as a colour emoji — so the keys did not read as one set. Paths on a 24-unit grid; most follow the Material
 * icons of the same name (Apache 2.0).
 */
internal object CommandIcons {
    val Previous = icon("Previous", "M6,6h2v12H6zM9.5,12l8.5,6V6z")
    val Next = icon("Next", "M6,18l8.5,-6L6,6v12zM16,6v12h2V6h-2z")
    val PlayPause = icon("PlayPause", "M3,5v14l9,-7zM14,5h2.5v14H14zM18.5,5H21v14h-2.5z")
    val Mute = icon(
        "Mute",
        "M16.5,12c0,-1.77 -1.02,-3.29 -2.5,-4.03v2.21l2.45,2.45c0.03,-0.2 0.05,-0.41 0.05,-0.63zM19,12c0,0.94 -0.2,1.82 -0.54,2.64" +
            "l1.51,1.51C20.63,14.91 21,13.5 21,12c0,-4.28 -2.99,-7.86 -7,-8.77v2.06c2.89,0.86 5,3.54 5,6.71zM4.27,3L3,4.27 7.73,9H3v6h4" +
            "l5,5v-6.73l4.25,4.25c-0.67,0.52 -1.42,0.93 -2.25,1.18v2.06c1.38,-0.31 2.63,-0.95 3.69,-1.81L19.73,21 21,19.73l-9,-9L4.27,3z" +
            "M12,4L9.91,6.09 12,8.18V4z",
    )
    val VolumeDown = icon("VolumeDown", "M18.5,12c0,-1.77 -1.02,-3.29 -2.5,-4.03v8.05c1.48,-0.73 2.5,-2.25 2.5,-4.02zM5,9v6h4l5,5V4L9,9H5z")
    val VolumeUp = icon(
        "VolumeUp",
        "M3,9v6h4l5,5V4L7,9H3zM16.5,12c0,-1.77 -1.02,-3.29 -2.5,-4.03v8.05c1.48,-0.73 2.5,-2.25 2.5,-4.02zM14,3.23v2.06" +
            "c2.89,0.86 5,3.54 5,6.71s-2.11,5.85 -5,6.71v2.06c4.01,-0.91 7,-4.49 7,-8.77s-2.99,-7.86 -7,-8.77z",
    )

    /** A monitor with its screen dark. */
    val ScreenOff = icon(
        "ScreenOff",
        "M3,4h18c1.1,0 2,0.9 2,2v10c0,1.1 -0.9,2 -2,2h-6v2h3v1.5H6V20h3v-2H3c-1.1,0 -2,-0.9 -2,-2V6c0,-1.1 0.9,-2 2,-2z",
    )

    /** A crescent moon. */
    val Sleep = icon(
        "Sleep",
        "M12,3c-4.97,0 -9,4.03 -9,9s4.03,9 9,9s9,-4.03 9,-9c0,-0.46 -0.04,-0.92 -0.1,-1.36c-0.98,1.37 -2.58,2.26 -4.4,2.26" +
            "c-2.98,0 -5.4,-2.42 -5.4,-5.4c0,-1.81 0.89,-3.42 2.26,-4.4C12.92,3.04 12.46,3 12,3z",
    )

    private fun icon(name: String, path: String): ImageVector =
        ImageVector.Builder(name = "Command.$name", defaultWidth = 24.dp, defaultHeight = 24.dp, viewportWidth = 24f, viewportHeight = 24f)
            .addPath(addPathNodes(path), fill = SolidColor(Color.Black))
            .build()
}
