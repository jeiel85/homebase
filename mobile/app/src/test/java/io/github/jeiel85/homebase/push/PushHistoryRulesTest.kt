package io.github.jeiel85.homebase.push

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.json.JSONObject
import org.junit.Test
import java.time.LocalDateTime
import java.time.ZoneId

class PushHistoryRulesTest {
    private val zone = ZoneId.of("Asia/Seoul")
    private fun at(y: Int, mo: Int, d: Int, h: Int = 12, mi: Int = 0) =
        LocalDateTime.of(y, mo, d, h, mi).atZone(zone).toInstant().toEpochMilli()

    private fun entry(id: String, receivedAt: Long, category: String = "windows-toast", body: String? = "본문") = HistoryEntry(
        id = id, pcId = "pc1", pcName = "HOME-PC", category = category, source = "KakaoTalk",
        title = "제목 $id", body = body, count = 1, sentAt = receivedAt - 500, receivedAt = receivedAt, shown = true,
    )

    @Test
    fun addPutsNewestFirstAndCapsCount() {
        val now = at(2026, 11, 14)
        var list = emptyList<HistoryEntry>()
        repeat(PushHistoryRules.MAX_ENTRIES + 5) { i -> list = PushHistoryRules.add(list, entry("e$i", now - 1000L * (600 - i)), now) }
        assertEquals(PushHistoryRules.MAX_ENTRIES, list.size)
        assertEquals("e${PushHistoryRules.MAX_ENTRIES + 4}", list.first().id)
        // The five oldest fell off.
        assertTrue(list.none { it.id == "e4" })
        assertTrue(list.any { it.id == "e5" })
    }

    @Test
    fun pruneDropsEntriesOlderThanThirtyDays() {
        val now = at(2026, 11, 14)
        val kept = entry("new", now - PushHistoryRules.MAX_AGE_MS)
        val old = entry("old", now - PushHistoryRules.MAX_AGE_MS - 1)
        assertEquals(listOf(kept), PushHistoryRules.prune(listOf(kept, old), now))
    }

    @Test
    fun unreadCountsOnlyAfterLastSeen() {
        val list = listOf(entry("c", 300), entry("b", 200), entry("a", 100))
        assertEquals(3, PushHistoryRules.unreadCount(list, 0))
        assertEquals(1, PushHistoryRules.unreadCount(list, 200))
        assertEquals(0, PushHistoryRules.unreadCount(list, 300))
    }

    @Test
    fun jsonRoundTripKeepsEveryFieldAndOrder() {
        val list = listOf(
            entry("b", 2_000).copy(count = 3, shown = false),
            entry("a", 1_000, category = "process_watch", body = null),
        )
        val back = PushHistoryRules.fromJson(PushHistoryRules.toJson(list))
        assertEquals(list, back)
        assertNull(back[1].body)
        assertFalse(back[0].shown)
    }

    @Test
    fun quietRoundTripsAndOldEntriesReadAsNotQuiet() {
        val list = listOf(entry("q", 2_000).copy(shown = false, quiet = true), entry("n", 1_000))
        assertEquals(list, PushHistoryRules.fromJson(PushHistoryRules.toJson(list)))
        // Entries written before M-37 have no "quiet".
        val root = JSONObject(PushHistoryRules.toJson(listOf(entry("o", 1_000))))
        root.getJSONArray("entries").getJSONObject(0).remove("quiet")
        val old = root.toString()
        assertFalse(old.contains("quiet"))
        assertFalse(PushHistoryRules.fromJson(old).single().quiet)
    }

    @Test
    fun stackFoldsSameSourceEntriesAtTheNewest() {
        fun e(id: String, at: Long, source: String, pc: String = "pc1", category: String = "windows-toast") =
            entry(id, at, category).copy(source = source, pcId = pc)
        val day = listOf(
            e("k3", 900, "KakaoTalk"),
            e("o1", 800, "Outlook"),
            e("k2", 700, "KakaoTalk"),
            e("k-other-pc", 650, "KakaoTalk", pc = "pc2"),
            e("k1", 600, "KakaoTalk"),
        )
        val rows = PushHistoryRules.stack(day) { true }
        assertEquals(listOf(listOf("k3", "k2", "k1"), listOf("o1"), listOf("k-other-pc")), rows.map { r -> r.map { it.id } })
        // Not bundled: one row per entry, in order.
        assertEquals(day.map { listOf(it.id) }, PushHistoryRules.stack(day) { false }.map { r -> r.map { it.id } })
        // Relayed notifications stack across the PCs that relayed them.
        val relayed = listOf(
            e("a", 3, "Galaxy · 카카오톡", "pc1", NotificationGroups.DEVICE),
            e("b", 2, "Galaxy · 카카오톡", "pc2", NotificationGroups.DEVICE),
        )
        assertEquals(1, PushHistoryRules.stack(relayed) { true }.size)
    }

    @Test
    fun fromJsonSkipsBrokenEntriesAndUnknownFormat() {
        val good = PushHistoryRules.toJson(listOf(entry("a", 1_000)))
        val withBroken = good.replace("\"entries\":[", "\"entries\":[{\"id\":\"x\"},")
        assertEquals(listOf("a"), PushHistoryRules.fromJson(withBroken).map { it.id })
        assertTrue(PushHistoryRules.fromJson("{\"v\":99,\"entries\":[]}").isEmpty())
    }

    @Test
    fun categoryLabelsFollowReceiveGroups() {
        assertEquals("Windows 앱 알림", PushHistoryRules.categoryLabel("windows-toast"))
        assertEquals("감시 경보", PushHistoryRules.categoryLabel("http_monitor"))
        assertEquals("다른 기기 알림", PushHistoryRules.categoryLabel(NotificationGroups.DEVICE))
        assertEquals("테스트", PushHistoryRules.categoryLabel(NotificationGroups.TEST))
        assertEquals("기타", PushHistoryRules.categoryLabel("something_new"))
    }

    @Test
    fun dayLabels() {
        val now = at(2026, 11, 14, 9)
        assertEquals("오늘", PushHistoryRules.dayLabel(at(2026, 11, 14, 0, 1), now, zone))
        assertEquals("어제", PushHistoryRules.dayLabel(at(2026, 11, 13, 23, 59), now, zone))
        assertEquals("11월 12일 (목)", PushHistoryRules.dayLabel(at(2026, 11, 12), now, zone))
        assertEquals("2025년 12월 31일 (수)", PushHistoryRules.dayLabel(at(2025, 12, 31), now, zone))
    }

    @Test
    fun entryFromMessageUsesMessageTimeOrFallsBack() {
        val msg = PushMessage("id1", "pc1", "HOME-PC", "boot", "high", "부팅", null, "boot", 1, "2026-11-14T09:00:00+09:00")
        val e = HistoryEntry.of(msg, receivedAt = 42L, shown = true)
        assertEquals(at(2026, 11, 14, 9), e.sentAt)
        assertEquals(42L, e.receivedAt)
        assertEquals(42L, HistoryEntry.of(msg.copy(ts = "not a time"), 42L, false).sentAt)
    }

    @Test
    fun theBadgeColourIsKeptAndAnUnknownOneDropped() {
        val coloured = entry("c", 1_000).copy(color = 3)
        assertEquals(3, PushHistoryRules.fromJson(PushHistoryRules.toJson(listOf(coloured))).single().color)
        // Entries from before #57 have none, and a newer build's colour outside the palette is dropped.
        assertNull(PushHistoryRules.fromJson(PushHistoryRules.toJson(listOf(entry("n", 1_000)))).single().color)
        val root = JSONObject(PushHistoryRules.toJson(listOf(coloured)))
        root.getJSONArray("entries").getJSONObject(0).put("color", 99)
        assertNull(PushHistoryRules.fromJson(root.toString()).single().color)
    }

    @Test
    fun clipboardEntriesAreCappedApartFromTheRest() {
        val now = at(2026, 11, 21)
        val text = "가".repeat(ClipHistory.KEPT_CHARS)
        // Newest first: 150 clipboard entries (received and sent) mixed with 150 others.
        val list = (0 until 300).map { i ->
            val category = when {
                i % 2 == 1 -> "windows-toast"
                i % 4 == 0 -> ClipHistory.RECEIVED
                else -> ClipHistory.SENT
            }
            entry("e$i", now - i * 1000L, category, body = if (ClipHistory.isClip(category)) text else "본문")
        }
        val kept = PushHistoryRules.prune(list, now)
        assertEquals(PushHistoryRules.MAX_CLIP_ENTRIES, kept.count { ClipHistory.isClip(it.category) })
        // Every other entry stays, and the clipboard entries kept are the newest.
        assertEquals(150, kept.count { it.category == "windows-toast" })
        assertEquals("e0", kept.first().id)
        assertTrue(kept.none { it.id == "e200" })
        // What that costs on disk before encryption: about 6 KB a full entry, so the cap holds the file well under 1 MB.
        val bytes = PushHistoryRules.toJson(kept.filter { ClipHistory.isClip(it.category) }).toByteArray(Charsets.UTF_8).size
        assertTrue("clipboard entries take $bytes bytes", bytes in 600_000..700_000)
    }
}
