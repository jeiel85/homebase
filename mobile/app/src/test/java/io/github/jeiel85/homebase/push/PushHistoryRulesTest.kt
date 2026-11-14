package io.github.jeiel85.homebase.push

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
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
}
