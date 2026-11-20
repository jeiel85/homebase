package io.github.jeiel85.homebase.push

import android.content.Context
import android.util.AtomicFile
import android.util.Log
import io.github.jeiel85.homebase.data.KeystoreCipher
import io.github.jeiel85.homebase.data.PcMarks
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.time.Instant
import java.time.LocalDate
import java.time.OffsetDateTime
import java.time.ZoneId
import java.time.format.TextStyle
import java.util.Locale
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors
import java.util.concurrent.RejectedExecutionException
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference

/** One push this phone received and accepted (after the muted / duplicate filters). */
data class HistoryEntry(
    val id: String,
    val pcId: String,
    val pcName: String,
    val category: String,
    val source: String,
    val title: String,
    val body: String?,
    val count: Int,
    /** When the PC sent it (the message `ts`), epoch ms. */
    val sentAt: Long,
    /** When this phone received it, epoch ms — the order and the "unread" mark use this. */
    val receivedAt: Long,
    /** Whether it was also posted as an Android notification (false when notifications were blocked). */
    val shown: Boolean,
    /** Kept out of the shade on purpose: its priority was "센터에만" (DECISIONS M-37). */
    val quiet: Boolean = false,
    /**
     * Index into [io.github.jeiel85.homebase.data.PcMarks.PALETTE] of the badge it wore when it came, so a
     * PC removed since, or a relayed device, keeps its colour here (#57). Null in entries from before.
     */
    val color: Int? = null,
    /**
     * A [FileHistory.RECEIVED] entry's saved files (content URIs, the first opened by a tap); empty for anything else.
     * Where they are, never what is in them.
     */
    val uris: List<String> = emptyList(),
) {
    /** Unique enough for list keys: the same id can arrive twice (e.g. a PC re-sending after a restart). */
    val key: String get() = "$pcId/$id/$receivedAt"

    /** What it is bundled with in the notification center, as in the shade (M-37). */
    val bundleKey: String get() = NotificationPriority.bundleKey(category, pcId, source)

    companion object {
        fun of(message: PushMessage, receivedAt: Long, shown: Boolean, quiet: Boolean = false, color: Int? = null) = HistoryEntry(
            id = message.id,
            pcId = message.pcId,
            pcName = message.pcName,
            category = message.category,
            source = message.source,
            title = message.title,
            body = message.body,
            count = message.count,
            sentAt = runCatching { OffsetDateTime.parse(message.ts).toInstant().toEpochMilli() }.getOrDefault(receivedAt),
            receivedAt = receivedAt,
            shown = shown,
            quiet = quiet,
            color = color,
        )
    }
}

/**
 * Pure rules of the notification center (DECISIONS M-31): what is kept, what counts as unread, the
 * stored form and the day headings. No Android types, so unit tests pin them.
 */
object PushHistoryRules {
    /** Newest entries kept. A day of busy Windows toasts is about 150 (the PC's daily cap, M-28). */
    const val MAX_ENTRIES = 500

    /** Older entries are dropped — a notification center, not an archive. */
    const val MAX_AGE_MS = 30L * 24 * 60 * 60 * 1000

    /** Adds [entry] at the top and applies both limits. The list is newest first. */
    fun add(entries: List<HistoryEntry>, entry: HistoryEntry, now: Long): List<HistoryEntry> =
        prune(listOf(entry) + entries, now)

    fun prune(entries: List<HistoryEntry>, now: Long): List<HistoryEntry> =
        entries.filter { now - it.receivedAt <= MAX_AGE_MS }.take(MAX_ENTRIES)

    /** How the notification center opens for a tapped notification: a kind's filter and the entry opened, either null. */
    data class Focus(val group: String?, val expanded: String?)

    /**
     * Where a tapped notification about [category] — an upload's (docs/SHARE_CONVENIENCE.md §4.5) — leads in the
     * center: that kind's filter when there is any of it, and the entry [id] opened (the newest, if recorded twice).
     * An upload still under way has no entry yet; then the filter alone, or nothing when none was ever recorded.
     */
    fun focus(entries: List<HistoryEntry>, category: String, id: String?): Focus {
        val any = entries.any { it.category == category }
        val entry = id?.let { wanted -> entries.firstOrNull { it.category == category && it.id == wanted } }
        return Focus(group = if (any) categoryLabel(category) else null, expanded = entry?.key)
    }

    /**
     * What came in since [lastSeenAt]; files and commands this phone sent itself are no news ([FileHistory.SENT],
     * [CommandHistory.SENT]).
     */
    fun unreadCount(entries: List<HistoryEntry>, lastSeenAt: Long): Int = entries.count { isUnread(it, lastSeenAt) }

    fun isUnread(entry: HistoryEntry, lastSeenAt: Long): Boolean =
        entry.receivedAt > lastSeenAt && entry.category != FileHistory.SENT && entry.category != CommandHistory.SENT

    /** The notification group a category belongs to (the same groups as the receive settings, M-25). */
    fun groupOf(category: String): NotificationGroup? = NotificationGroups.ALL.firstOrNull { category in it.categories }

    /** Short label of a category for a tag: its group, "받은 파일" / "보낸 파일" / "보낸 명령", or "테스트" / "기타". */
    fun categoryLabel(category: String): String = when {
        category == NotificationGroups.TEST -> "테스트"
        category == FileHistory.RECEIVED -> FileHistory.RECEIVED_LABEL
        category == FileHistory.SENT -> FileHistory.SENT_LABEL
        category == CommandHistory.SENT -> CommandHistory.SENT_LABEL
        else -> groupOf(category)?.label ?: "기타"
    }

    /** Heading for the day [at] falls on: 오늘, 어제, "11월 12일 (목)", or with the year when not this year. */
    fun dayLabel(at: Long, now: Long, zone: ZoneId): String {
        val day = Instant.ofEpochMilli(at).atZone(zone).toLocalDate()
        val today = Instant.ofEpochMilli(now).atZone(zone).toLocalDate()
        return when (day) {
            today -> "오늘"
            today.minusDays(1) -> "어제"
            else -> {
                val weekday = day.dayOfWeek.getDisplayName(TextStyle.SHORT, Locale.KOREAN)
                val year = if (day.year != today.year) "${day.year}년 " else ""
                "$year${day.monthValue}월 ${day.dayOfMonth}일 ($weekday)"
            }
        }
    }

    fun dayOf(at: Long, zone: ZoneId): LocalDate = Instant.ofEpochMilli(at).atZone(zone).toLocalDate()

    /**
     * The rows of one day (DECISIONS M-37): entries whose source is bundled ([bundles]) are stacked
     * with the others of the same source, at the place of the newest; the rest stay one per row.
     * [dayEntries] and the result are newest first.
     */
    fun stack(dayEntries: List<HistoryEntry>, bundles: (HistoryEntry) -> Boolean): List<List<HistoryEntry>> {
        val rows = LinkedHashMap<String, MutableList<HistoryEntry>>()
        dayEntries.forEachIndexed { i, e ->
            val key = if (bundles(e)) "b:${e.bundleKey}" else "s:$i"
            rows.getOrPut(key) { mutableListOf() } += e
        }
        return rows.values.toList()
    }

    fun toJson(entries: List<HistoryEntry>): String {
        val array = JSONArray()
        entries.forEach { e ->
            array.put(
                JSONObject()
                    .put("id", e.id)
                    .put("pcId", e.pcId)
                    .put("pcName", e.pcName)
                    .put("category", e.category)
                    .put("source", e.source)
                    .put("title", e.title)
                    .put("body", e.body ?: JSONObject.NULL)
                    .put("count", e.count)
                    .put("sentAt", e.sentAt)
                    .put("receivedAt", e.receivedAt)
                    .put("shown", e.shown)
                    .put("quiet", e.quiet)
                    .apply { e.color?.let { put("color", it) } }
                    .apply { if (e.uris.isNotEmpty()) put("uris", JSONArray(e.uris)) },
            )
        }
        return JSONObject().put("v", FORMAT).put("entries", array).toString()
    }

    /** Reads [toJson]'s output; an entry missing a field is skipped rather than failing the whole list. */
    fun fromJson(json: String): List<HistoryEntry> {
        val root = JSONObject(json)
        if (root.optInt("v") != FORMAT) return emptyList()
        val array = root.optJSONArray("entries") ?: return emptyList()
        return (0 until array.length()).mapNotNull { i ->
            val o = array.optJSONObject(i) ?: return@mapNotNull null
            try {
                HistoryEntry(
                    id = o.getString("id"),
                    pcId = o.getString("pcId"),
                    pcName = o.getString("pcName"),
                    category = o.getString("category"),
                    source = o.getString("source"),
                    title = o.getString("title"),
                    body = if (o.isNull("body")) null else o.optString("body").ifEmpty { null },
                    count = o.optInt("count", 1).coerceAtLeast(1),
                    sentAt = o.getLong("sentAt"),
                    receivedAt = o.getLong("receivedAt"),
                    shown = o.optBoolean("shown", true),
                    // Absent in entries from before M-37.
                    quiet = o.optBoolean("quiet", false),
                    // Absent before #57; a colour a newer build added is dropped.
                    color = PcMarks.validColor(if (o.has("color")) o.optInt("color", -1) else null),
                    // Only received files have them; absent in everything else and before.
                    uris = o.optJSONArray("uris")?.let { a -> (0 until a.length()).mapNotNull { a.optString(it).ifEmpty { null } } } ?: emptyList(),
                )
            } catch (_: Exception) {
                null
            }
        }.sortedByDescending { it.receivedAt }
    }

    private const val FORMAT = 1
}

/**
 * The notification center's store (DECISIONS M-31): every push this phone accepted, kept on this
 * phone only. The file is encrypted with a [KeystoreCipher] key like the PC tokens, since forwarded
 * Windows toasts carry message text; backups are off (manifest), so it never leaves the phone.
 *
 * One process-wide copy: the push receiver and the screens share it, so a push arriving while the
 * notification center is open shows up at once through [state].
 *
 * Changes apply to [state] at once; the file is written behind them on one background thread
 * ([saveLater]), so neither the receiver nor a delete button does the encryption and disk write on the
 * main thread. One writer keeps the writes in the order of the changes.
 */
object PushHistory {
    data class State(val entries: List<HistoryEntry>, val lastSeenAt: Long) {
        val unread: Int get() = PushHistoryRules.unreadCount(entries, lastSeenAt)
    }

    private val _state = MutableStateFlow(State(emptyList(), 0L))
    val state: StateFlow<State> = _state.asStateFlow()

    @Volatile
    private var loaded = false

    /** The single thread that writes the file. */
    private val writer: ExecutorService = Executors.newSingleThreadExecutor { r -> Thread(r, "hb-push-history") }

    /** The newest list not yet written; a write already queued picks it up, so a burst of changes writes once. */
    private val unsaved = AtomicReference<List<HistoryEntry>?>(null)

    /**
     * Reads the file once per process. Cheap after the first call; the first does disk and Keystore work,
     * so callers on the main thread switch to IO first.
     */
    @Synchronized
    fun ensureLoaded(context: Context) {
        if (loaded) return
        val app = context.applicationContext
        var unreadable = false
        val entries = try {
            val file = file(app)
            if (!file.baseFile.exists()) {
                emptyList()
            } else {
                val plain = cipher.decrypt(file.readFully().toString(Charsets.UTF_8))
                if (plain == null) {
                    // Keystore key gone (restored data) or a damaged file: start over rather than fail every push.
                    Log.w(TAG, "notification history unreadable; starting empty")
                    unreadable = true
                    emptyList()
                } else {
                    PushHistoryRules.fromJson(plain.toString(Charsets.UTF_8))
                }
            }
        } catch (e: Exception) {
            Log.w(TAG, "notification history unreadable (${e.javaClass.simpleName}); starting empty")
            unreadable = true
            emptyList()
        }
        val lastSeen = prefs(app).getLong(KEY_LAST_SEEN, 0L)
        _state.value = State(PushHistoryRules.prune(entries, System.currentTimeMillis()), lastSeen)
        loaded = true
        if (unreadable) {
            // Say so once in the notification center ([takeRestartNotice]), and replace the file now so the
            // next start doesn't find it unreadable again (the next push would overwrite it anyway).
            prefs(app).edit().putBoolean(KEY_RESTARTED, true).apply()
            saveLater(app, emptyList())
        }
    }

    /**
     * True once after the history could not be read and started over empty (Keystore key gone after a
     * restore, or a damaged file) — the notification center tells the user instead of only showing
     * "no notifications". Reading it clears it.
     */
    @Synchronized
    fun takeRestartNotice(context: Context): Boolean {
        ensureLoaded(context)
        val prefs = prefs(context)
        if (!prefs.getBoolean(KEY_RESTARTED, false)) return false
        prefs.edit().remove(KEY_RESTARTED).apply()
        return true
    }

    @Synchronized
    fun record(context: Context, entry: HistoryEntry) {
        ensureLoaded(context)
        val next = PushHistoryRules.add(_state.value.entries, entry, System.currentTimeMillis())
        saveLater(context, next)
        _state.value = _state.value.copy(entries = next)
    }

    /**
     * Waits (at most [timeoutMs]) until the writes queued so far are on disk. The push receiver calls it
     * on its worker thread before it lets the broadcast finish, so the process isn't let go mid-write.
     * Never call it on the main thread.
     */
    fun awaitWrites(timeoutMs: Long) {
        try {
            // One writer thread: this empty task runs only after every write queued before it.
            writer.submit(Runnable {}).get(timeoutMs, TimeUnit.MILLISECONDS)
        } catch (e: Exception) {
            Log.w(TAG, "notification history write still pending: ${e.javaClass.simpleName}")
        }
    }

    /** Everything received so far counts as read. */
    @Synchronized
    fun markAllRead(context: Context) {
        ensureLoaded(context)
        val newest = _state.value.entries.maxOfOrNull { it.receivedAt } ?: return
        if (newest <= _state.value.lastSeenAt) return
        prefs(context).edit().putLong(KEY_LAST_SEEN, newest).apply()
        _state.value = _state.value.copy(lastSeenAt = newest)
    }

    @Synchronized
    fun remove(context: Context, entry: HistoryEntry) {
        ensureLoaded(context)
        val next = _state.value.entries.filterNot { it.key == entry.key }
        saveLater(context, next)
        _state.value = _state.value.copy(entries = next)
    }

    @Synchronized
    fun clear(context: Context) {
        ensureLoaded(context)
        saveLater(context, emptyList())
        _state.value = _state.value.copy(entries = emptyList())
    }

    /** A PC deleted from the app takes its history with it, like its notification channels. */
    @Synchronized
    fun forgetPc(context: Context, pcId: String) {
        ensureLoaded(context)
        val next = _state.value.entries.filterNot { it.pcId == pcId }
        if (next.size == _state.value.entries.size) return
        saveLater(context, next)
        _state.value = _state.value.copy(entries = next)
    }

    /**
     * Queues writing [entries] on the writer thread. Callers hold this object's lock, so the lists are
     * handed over in the order of the changes; only the newest one is written.
     */
    private fun saveLater(context: Context, entries: List<HistoryEntry>) {
        val app = context.applicationContext
        if (unsaved.getAndSet(entries) != null) return // the write already queued takes this list
        try {
            writer.execute {
                // An exception escaping a pool thread would crash the app: save() catches its own.
                unsaved.getAndSet(null)?.let { save(app, it) }
            }
        } catch (e: RejectedExecutionException) {
            unsaved.set(null)
            Log.w(TAG, "could not queue a notification history write")
        }
    }

    /** Runs on the writer thread only. Catches everything it can: nothing may escape the pool thread. */
    private fun save(context: Context, entries: List<HistoryEntry>) {
        val file = file(context)
        val out = try {
            file.startWrite()
        } catch (e: Exception) {
            Log.w(TAG, "could not save notification history: ${e.javaClass.simpleName}")
            return
        }
        try {
            out.write(cipher.encrypt(PushHistoryRules.toJson(entries).toByteArray(Charsets.UTF_8)).toByteArray(Charsets.UTF_8))
            file.finishWrite(out)
        } catch (e: Exception) {
            file.failWrite(out)
            // The in-memory list still updates; the next successful write brings the file up to date.
            Log.w(TAG, "could not save notification history: ${e.javaClass.simpleName}")
        }
    }

    private fun file(context: Context) = AtomicFile(File(context.filesDir, FILE_NAME))

    private fun prefs(context: Context) = context.applicationContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)

    private val cipher = KeystoreCipher("homebase.push.history")
    private const val FILE_NAME = "push_history.enc"
    private const val PREFS = "homebase.push.history"
    private const val KEY_LAST_SEEN = "last_seen_at"
    private const val KEY_RESTARTED = "restarted_empty"
    private const val TAG = "HomebasePush"
}
