package io.github.jeiel85.homebase.mesh

import io.github.jeiel85.homebase.data.PcEntry
import io.github.jeiel85.homebase.net.DeviceEntry
import io.github.jeiel85.homebase.net.DeviceKind
import io.github.jeiel85.homebase.net.DeviceStatus
import io.github.jeiel85.homebase.net.DevicesResponse
import io.github.jeiel85.homebase.net.HubEntry
import io.github.jeiel85.homebase.ui.DeviceLine
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.OffsetDateTime

class MeshSyncTest {
    private fun pc(id: String, name: String, deviceId: String) = PcEntry(id, name, emptyList(), deviceId, "token")

    private fun dev(id: String, name: String, kind: String = DeviceKind.PHONE, online: Boolean = true, seen: String? = null) =
        DeviceEntry(id, name, kind, null, seen, online, null)

    private val now = 10_000_000L

    private fun hub(name: String, vararg devices: DeviceEntry, receivedAt: Long = now) =
        MeshSync.HubView(DevicesResponse(HubEntry("x", name, "0.22.0", null), devices.toList()), receivedAt)

    @Test
    fun this_phone_and_pcs_already_in_the_list_are_left_out() {
        val mine = listOf(pc("A", "HOME-PC", "meOnA"), pc("B", "WORK-PC", "meOnB"))
        val hubs = mapOf(
            "A" to hub("HOME-PC", dev("meOnA", "My phone"), dev("b-on-a", "WORK-PC", DeviceKind.PC), dev("tab", "Galaxy Tab")),
            "B" to hub("WORK-PC", dev("meOnB", "My phone"), dev("laptop", "LAPTOP", DeviceKind.PC, online = false)),
        )

        val others = MeshSync.otherDevices(hubs, mine, now)

        // PCs first, then phones; the linked laptop is new to this phone, WORK-PC is already a card.
        assertEquals(listOf("LAPTOP", "Galaxy Tab"), others.map { it.entry.name })
        assertEquals(listOf("WORK-PC"), others[0].via)
    }

    @Test
    fun a_phone_paired_with_two_pcs_shows_once_with_its_freshest_state() {
        val mine = listOf(pc("A", "HOME-PC", "me1"), pc("B", "WORK-PC", "me2"))
        val hubs = mapOf(
            "A" to hub("HOME-PC", dev("t1", "Galaxy Tab", online = false, seen = "2026-11-14T08:00:00+00:00")),
            "B" to hub("WORK-PC", dev("t2", "Galaxy Tab", online = true, seen = "2026-11-14T09:00:00+00:00")),
        )

        val only = MeshSync.otherDevices(hubs, mine, now).single()

        assertTrue(only.entry.online)
        assertEquals("t2", only.entry.deviceId)
        assertEquals(listOf("HOME-PC", "WORK-PC"), only.via)
    }

    @Test
    fun online_from_a_pc_that_went_quiet_expires() {
        val mine = listOf(pc("A", "HOME-PC", "me1"))
        val hubs = mapOf("A" to hub("HOME-PC", dev("t1", "Galaxy Tab", online = true), receivedAt = now - MeshSync.FRESH_MS - 1))

        assertFalse(MeshSync.otherDevices(hubs, mine, now).single().entry.online)
        assertTrue(MeshSync.otherDevices(hubs, mine, now - 2).single().entry.online)
    }

    @Test
    fun a_relayed_push_seen_from_a_second_pc_is_dropped() {
        val (first, kept) = io.github.jeiel85.homebase.push.RecentPushIds.firstSighting(emptyList(), "n1", now)
        assertTrue(first)
        assertFalse(io.github.jeiel85.homebase.push.RecentPushIds.firstSighting(kept, "n1", now + 1_000).first)
        assertTrue(io.github.jeiel85.homebase.push.RecentPushIds.firstSighting(kept, "n2", now + 1_000).first)
        val later = now + io.github.jeiel85.homebase.push.RecentPushIds.WINDOW_MS + 1
        assertTrue(io.github.jeiel85.homebase.push.RecentPushIds.firstSighting(kept, "n1", later).first)
    }

    @Test
    fun device_lines_read_naturally() {
        assertEquals("배터리 76% (충전 중) · Wi-Fi · v0.22.0",
            DeviceLine.status(DeviceStatus("0.22.0", battery = 76, charging = true, network = "wifi"), DeviceKind.PHONE))
        assertEquals("CPU 8% · RAM 50% · 가동 1일 2시간",
            DeviceLine.status(DeviceStatus(cpuPct = 8.4, memUsedBytes = 8, memTotalBytes = 16, uptimeSec = 93_600), DeviceKind.PC))
        assertEquals("아직 보고 없음", DeviceLine.status(null, DeviceKind.PC))
        val now = OffsetDateTime.parse("2026-11-14T10:00:00+00:00")
        assertEquals("온라인", DeviceLine.seen(true, null, now))
        assertEquals("15분 전 확인", DeviceLine.seen(false, "2026-11-14T09:45:00+00:00", now))
        assertEquals("2일 전 확인", DeviceLine.seen(false, "2026-11-12T09:00:00+00:00", now))
        assertEquals("오프라인", DeviceLine.seen(false, null, now))
    }

    @Test
    fun an_unchanged_repost_is_sent_once_a_minute() {
        val t = 1_000_000L
        assertTrue(NotificationForwarder.Recent.firstTime("k", "엄마", "밥", t))
        assertFalse(NotificationForwarder.Recent.firstTime("k", "엄마", "밥", t + 5_000))
        assertTrue(NotificationForwarder.Recent.firstTime("k", "엄마", "밥 먹었니?", t + 6_000)) // text changed
        assertTrue(NotificationForwarder.Recent.firstTime("k", "엄마", "밥", t + 61_000 + 5_000))
    }
}
