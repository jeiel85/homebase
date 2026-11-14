package io.github.jeiel85.homebase.mesh

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject

/**
 * The phone side of sharing with other devices (DECISIONS M-27, M-28): whether this phone forwards
 * its notifications to the PCs, which apps it leaves out, and which apps it has seen (so the user
 * can leave one out after the fact).
 */
class MeshPrefs(context: Context) {
    private val prefs = context.applicationContext.getSharedPreferences(NAME, Context.MODE_PRIVATE)

    /** Off until the user turns it on: reading every notification is a big permission. */
    fun forwardEnabled(): Boolean = prefs.getBoolean(KEY_FORWARD, false)

    fun setForwardEnabled(on: Boolean) = prefs.edit().putBoolean(KEY_FORWARD, on).apply()

    fun excluded(): Set<String> = prefs.getStringSet(KEY_EXCLUDED, null)?.toSet() ?: DEFAULT_EXCLUDED

    fun setExcluded(packageName: String, excluded: Boolean) {
        val next = if (excluded) excluded() + packageName else excluded() - packageName
        prefs.edit().putStringSet(KEY_EXCLUDED, next).apply()
    }

    /** Apps that posted a notification since forwarding was turned on, newest first (package → label). */
    fun seenApps(): List<Pair<String, String>> {
        val raw = prefs.getString(KEY_SEEN, null) ?: return emptyList()
        return try {
            val arr = JSONArray(raw)
            (0 until arr.length()).mapNotNull { i ->
                arr.optJSONObject(i)?.let { it.optString("p") to it.optString("l") }
            }
        } catch (_: Exception) {
            emptyList()
        }
    }

    @Synchronized
    fun rememberApp(packageName: String, label: String) {
        val list = seenApps()
        if (list.firstOrNull()?.first == packageName) return
        val next = (listOf(packageName to label) + list.filterNot { it.first == packageName }).take(MAX_SEEN)
        val arr = JSONArray()
        next.forEach { (p, l) -> arr.put(JSONObject().put("p", p).put("l", l)) }
        prefs.edit().putString(KEY_SEEN, arr.toString()).apply()
    }

    companion object {
        private const val NAME = "homebase.mesh"
        private const val KEY_FORWARD = "forward"
        private const val KEY_EXCLUDED = "excluded"
        private const val KEY_SEEN = "seen"
        private const val MAX_SEEN = 30

        /**
         * Left out until the user says otherwise: authenticators and password managers (codes must
         * not travel), and phone-mirroring apps that would echo PC notifications back to the PC.
         */
        val DEFAULT_EXCLUDED: Set<String> = setOf(
            "com.google.android.apps.authenticator2",
            "com.azure.authenticator",
            "com.authy.authy",
            "com.x8bit.bitwarden",
            "com.agilebits.onepassword",
            "com.lastpass.lpandroid",
            "keepass2android.keepass2android",
            "com.microsoft.appmanager", // Link to Windows
            "com.samsung.android.mdx",  // Samsung's Link to Windows service
            "com.android.vending",      // download progress and "app updated" chatter
        )
    }
}
