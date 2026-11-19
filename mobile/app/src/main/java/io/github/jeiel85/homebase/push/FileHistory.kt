package io.github.jeiel85.homebase.push

import java.util.Locale

/**
 * Files this phone received from and sent to its PCs, as notification center entries (docs/SHARE_CONVENIENCE.md
 * §4.5, §9): who, how many, how much, the names and how it went — never what is in them, and never clipboard text.
 * They keep the center's rules (30 days, 500 entries) and its filter chips show them on their own ("받은 파일",
 * "보낸 파일"). Pure, so unit tests pin it.
 */
object FileHistory {
    const val RECEIVED = "file-received"
    const val SENT = "file-sent"

    const val RECEIVED_LABEL = "받은 파일"
    const val SENT_LABEL = "보낸 파일"

    /** Names listed in an entry; the rest are counted. */
    const val MAX_NAMES = 10

    fun isFile(category: String): Boolean = category == RECEIVED || category == SENT

    /** One file of an entry: its name, size and type (for "사진 3장"). */
    data class Item(val name: String, val size: Long, val mime: String?)

    /**
     * Files [pcName] ([pcId]) sent — or, with [from], another phone sent through it — and this phone saved at [uris].
     * [transferId] is the entry's id.
     */
    fun received(
        transferId: String,
        pcId: String,
        pcName: String,
        from: String?,
        files: List<Item>,
        uris: List<String>,
        color: Int?,
        now: Long,
    ): HistoryEntry = HistoryEntry(
        id = transferId,
        pcId = pcId,
        pcName = pcName,
        category = RECEIVED,
        source = from?.trim().orEmpty(),
        title = title(files),
        body = names(files),
        count = 1,
        sentAt = now,
        receivedAt = now,
        shown = true,
        color = color,
        uris = uris,
    )

    /**
     * Files sent to [pcName] ([pcId]) — or through it to the phones [toNames] — and how it went: [outcome] is a line
     * such as "받음", "PC에 로그인하면 저장" or "실패: …".
     */
    fun sent(
        transferId: String,
        pcId: String,
        pcName: String,
        toNames: List<String>,
        files: List<Item>,
        outcome: String,
        color: Int?,
        now: Long,
    ): HistoryEntry = HistoryEntry(
        id = transferId,
        pcId = pcId,
        pcName = pcName,
        category = SENT,
        source = toNames.joinToString(", "),
        title = title(files),
        body = "$outcome\n${names(files)}",
        count = 1,
        sentAt = now,
        receivedAt = now,
        shown = true,
        color = color,
    )

    /**
     * The entry's header after the tag: the PC, or the other phone and the PC it went through —
     * "Galaxy S24 · 집 PC 경유" (received from it), "Galaxy Tab S9 · 집 PC 경유" (sent to it).
     */
    fun header(entry: HistoryEntry, pcName: String): String =
        if (entry.source.isBlank()) pcName else "${entry.source} · $pcName 경유"

    /** "사진 3장 · 12.4MB", "IMG_0412.jpg · 820KB". */
    fun title(files: List<Item>): String = "${what(files)} · ${bytes(files.sumOf { it.size.coerceAtLeast(0) })}"

    /** The names, [MAX_NAMES] at most, then how many more. */
    fun names(files: List<Item>): String {
        val shown = files.take(MAX_NAMES).joinToString(", ") { it.name }
        return if (files.size > MAX_NAMES) "$shown 외 ${files.size - MAX_NAMES}개" else shown
    }

    /** "사진 3장", "동영상 2개", "파일 4개", or the name of the one file (the same words as the share notifications). */
    fun what(files: List<Item>): String {
        val n = files.size
        fun all(prefix: String) = files.all { it.mime?.startsWith(prefix) == true }
        return when {
            n == 1 -> files[0].name
            all("image/") -> "사진 ${n}장"
            all("video/") -> "동영상 ${n}개"
            files.all { it.mime?.startsWith("image/") == true || it.mime?.startsWith("video/") == true } -> "사진·동영상 ${n}개"
            else -> "파일 ${n}개"
        }
    }

    /** Decimal units, as Android's own file sizes. */
    fun bytes(n: Long): String = when {
        n < 1_000 -> "${n}B"
        n < 1_000_000 -> String.format(Locale.ROOT, "%.0fKB", n / 1e3)
        n < 1_000_000_000 -> String.format(Locale.ROOT, "%.1fMB", n / 1e6)
        else -> String.format(Locale.ROOT, "%.2fGB", n / 1e9)
    }
}
