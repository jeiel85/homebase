package io.github.jeiel85.homebase.net

import java.util.Base64

/**
 * The pairing QR as a link (DECISIONS M-20), mirroring `LocalOpsBot.Core.Mobile.PairingLink`:
 *
 * - `https://jeiel85.github.io/homebase/pair/#<base64url(offer JSON)>` — what the PC shows. The
 *   phone's camera opens it in the app (App Link) or, without the app, on the install guide page.
 * - `homebase://pair/<base64url(offer JSON)>` — what that page's "앱에서 열기" button hands over.
 *
 * The offer rides in the fragment / custom-scheme path, so it never reaches a web server.
 */
object PairingLink {
    private const val WEB = "https://jeiel85.github.io/homebase/pair"
    private const val APP = "homebase://pair/"

    /** The base64url payload of a pairing link, or null when [text] isn't one. */
    fun payload(text: String): String? {
        val t = text.trim()
        val raw = when {
            t.startsWith(WEB) -> t.substringAfter('#', "")
            t.startsWith(APP) -> t.removePrefix(APP).substringBefore('?').substringBefore('#')
            else -> return null
        }
        return raw.trim().ifEmpty { null }
    }

    /** The offer JSON inside a pairing link, or null. */
    fun decode(text: String): String? = payload(text)?.let {
        try {
            String(Base64.getUrlDecoder().decode(it.trimEnd('=')), Charsets.UTF_8)
        } catch (_: IllegalArgumentException) {
            null
        }
    }

    fun isLink(text: String?): Boolean = text != null && payload(text) != null
}
