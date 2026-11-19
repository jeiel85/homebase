package io.github.jeiel85.homebase.push

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** docs/SHARE_CONVENIENCE.md §4.5: received and sent files in the notification center — metadata only. */
class FileHistoryTest {
    private val photos = listOf(
        FileHistory.Item("IMG_1.jpg", 4_000_000, "image/jpeg"),
        FileHistory.Item("IMG_2.jpg", 4_200_000, "image/jpeg"),
        FileHistory.Item("IMG_3.jpg", 4_200_000, "image/jpeg"),
    )

    @Test
    fun a_received_entry_says_who_what_and_where_it_was_saved() {
        val e = FileHistory.received(
            "t1", "pc1", "집 PC", from = "Galaxy S24", files = photos,
            uris = listOf("content://media/external/images/media/1"), color = 2, now = 1_000L,
        )
        assertEquals(FileHistory.RECEIVED, e.category)
        assertEquals("사진 3장 · 12.4MB", e.title)
        assertEquals("IMG_1.jpg, IMG_2.jpg, IMG_3.jpg", e.body)
        assertEquals("Galaxy S24", e.source)
        assertEquals("Galaxy S24 · 집 PC 경유", FileHistory.header(e, "집 PC"))
        assertEquals(listOf("content://media/external/images/media/1"), e.uris)
        assertEquals(1, e.count)

        val direct = FileHistory.received("t2", "pc1", "집 PC", from = null, files = photos.take(1), uris = emptyList(), color = null, now = 1L)
        assertEquals("집 PC", FileHistory.header(direct, "집 PC"))
        assertEquals("IMG_1.jpg · 4.0MB", direct.title)
    }

    @Test
    fun a_sent_entry_says_to_whom_and_how_it_went() {
        val e = FileHistory.sent("t3", "pc1", "집 PC", listOf("Galaxy Tab S9"), photos, "보냄 — 집 PC이(가) 휴대폰에 알림", color = 1, now = 5L)
        assertEquals(FileHistory.SENT, e.category)
        assertEquals("Galaxy Tab S9 · 집 PC 경유", FileHistory.header(e, "집 PC"))
        assertTrue(e.body!!.startsWith("보냄 — "))
        assertTrue(e.uris.isEmpty())
    }

    @Test
    fun only_ten_names_are_listed() {
        val many = (1..13).map { FileHistory.Item("f$it.bin", 1, null) }
        assertEquals((1..10).joinToString(", ") { "f$it.bin" } + " 외 3개", FileHistory.names(many))
        assertEquals("파일 13개", FileHistory.what(many))
    }

    @Test
    fun file_entries_have_their_own_chips_and_sent_ones_are_no_news() {
        assertEquals("받은 파일", PushHistoryRules.categoryLabel(FileHistory.RECEIVED))
        assertEquals("보낸 파일", PushHistoryRules.categoryLabel(FileHistory.SENT))
        val received = FileHistory.received("r", "pc1", "PC", null, photos, emptyList(), null, now = 200)
        val sent = FileHistory.sent("s", "pc1", "PC", emptyList(), photos, "보냄", null, now = 300)
        assertEquals(1, PushHistoryRules.unreadCount(listOf(sent, received), lastSeenAt = 100))
        assertFalse(PushHistoryRules.isUnread(sent, 100))
        assertTrue(FileHistory.isFile(sent.category))
        assertFalse(FileHistory.isFile("windows-toast"))
    }

    @Test
    fun uris_round_trip_and_older_entries_have_none() {
        val e = FileHistory.received("r", "pc1", "PC", null, photos, listOf("content://a/1", "content://a/2"), 3, now = 200)
        assertEquals(listOf(e), PushHistoryRules.fromJson(PushHistoryRules.toJson(listOf(e))))
        val root = JSONObject(PushHistoryRules.toJson(listOf(e)))
        root.getJSONArray("entries").getJSONObject(0).remove("uris")
        assertTrue(PushHistoryRules.fromJson(root.toString()).single().uris.isEmpty())
    }
}
