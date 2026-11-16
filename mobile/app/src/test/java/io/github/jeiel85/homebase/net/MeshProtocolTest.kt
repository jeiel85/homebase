package io.github.jeiel85.homebase.net

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/** Device sharing (DECISIONS M-26 ~ M-28) against the fixtures the .NET tests serialize. */
class MeshProtocolTest {

    private fun fixture(name: String): String = File("../../docs/mobile-protocol/$name").readText(Charsets.UTF_8)

    /** Same JSON value, ignoring key order and whitespace. */
    private fun assertJsonEquals(expected: String, actual: JSONObject) {
        val e = JSONObject(expected)
        assertEquals(e.keys().asSequence().toSortedSet(), actual.keys().asSequence().toSortedSet())
        for (k in e.keys()) assertEquals("field $k", e.get(k).toString(), actual.get(k).toString())
    }

    @Test
    fun work_items_parse_in_the_pc_order() {
        val s = LiveSnapshot.parse(fixture("snapshot.v1.json"))
        val work = s.workItems
        assertEquals(listOf("Claude Code", "Codex", "Word", "VS Code", "Chrome"), work.map { it.app })
        assertEquals(WorkItem.AGENT, work[1].kind)
        assertEquals("OpenCode에 모델 추가", work[1].title)
        assertEquals("2026-11-13T09:25:00+00:00", work[1].lastActivity)
        assertTrue(work[2].foreground)
        assertEquals(WorkItem.DOCUMENT, work[2].kind)
        assertEquals("homebase", work[3].detail)
        assertNull(work[4].detail)
        assertNull(work[2].agent)
    }

    @Test
    fun ai_sessions_carry_their_detail() {
        val work = LiveSnapshot.parse(fixture("snapshot.v1.json")).workItems
        val claude = work[0].agent!!
        assertEquals(AgentInfo.WAITING, claude.status)
        assertEquals("Bash 실행 승인", claude.waitingFor)
        assertEquals("claude-opus-5-5", claude.model)
        assertEquals(184503L, claude.contextTokens)
        assertNull(claude.contextShare)
        assertEquals("Bash", claude.lastTool)
        assertEquals("plan", claude.mode)
        assertEquals(56, claude.prNumber)
        assertEquals("https://claude.ai/code/session_01KuE5FPMg4WPj9yciyPDgKg", claude.remoteUrl)
        val codex = work[1].agent!!
        assertEquals(AgentInfo.BUSY, codex.status)
        assertEquals("high", codex.effort)
        assertEquals(42000f / 258400f, codex.contextShare!!, 0.0001f)
        assertEquals(51000L, codex.totalTokens)
        assertNull(codex.remoteUrl)
    }

    @Test
    fun only_claude_ai_links_are_opened() {
        val o = JSONObject("""{"status":"busy","remoteUrl":"https://evil.example/claude.ai/"}""")
        assertNull(AgentInfo.parse(o)!!.remoteUrl)
    }

    @Test
    fun an_older_pc_without_work_shows_its_claude_sessions() {
        val json = JSONObject(fixture("snapshot.v1.json")).apply { remove("work") }.toString()
        val work = LiveSnapshot.parse(json).workItems
        assertEquals(1, work.size)
        assertEquals("Claude Code", work[0].app)
        assertEquals("Homebase 모바일 앱", work[0].title)
        assertEquals("""D:\Project\homebase""", work[0].detail)
    }

    @Test
    fun devices_fixture_parses_hub_and_every_device() {
        val r = DevicesResponse.parse(fixture("devices.v1.json"))
        assertEquals("HOME-PC", r.hub.pcName)
        assertEquals(23.4, r.hub.status!!.cpuPct!!, 0.001)
        assertEquals(listOf(DeviceKind.PC, DeviceKind.PHONE, DeviceKind.PHONE), r.devices.map { it.kind })
        val phone = r.devices[1]
        assertEquals("Galaxy S24", phone.name)
        assertTrue(phone.online)
        assertEquals(76, phone.status!!.battery)
        assertEquals(true, phone.status!!.charging)
        assertEquals("wifi", phone.status!!.network)
        val tab = r.devices[2]
        assertFalse(tab.online)
        assertNull(tab.status)
    }

    @Test
    fun presence_report_matches_the_fixture() {
        val report = PresenceReport(
            appVersion = "0.33.0", battery = 76, charging = true, network = "wifi",
            memUsedBytes = 5_368_709_120, memTotalBytes = 11_811_160_064, uptimeSec = 259_200,
            os = "Android 15", storageUsedBytes = 143_881_404_416, storageTotalBytes = 255_550_554_112,
            batteryTempC = 31.5, batteryHealth = "good", plug = "usb", chargeFullSec = 2_100, powerSave = false,
            thermal = "none", signal = 4, linkMbps = 866, forwarding = true, dnd = false,
        )
        assertJsonEquals(fixture("presence.v1.json"), report.toJson())
    }

    @Test
    fun an_older_style_report_leaves_the_new_fields_out() {
        val json = PresenceReport(appVersion = "0.33.0", battery = 50, charging = null, network = null).toJson()
        assertEquals(setOf("schema", "kind", "appVersion", "battery"), json.keys().asSequence().toSet())
    }

    @Test
    fun device_notification_matches_the_fixture() {
        val n = DeviceNotification("9c1d2e3f4a5b46c7d8e9f0a1b2c3d4e5", "카카오톡", "엄마", "저녁 먹었니?", "2026-11-14T09:31:00+00:00")
        assertJsonEquals(fixture("device-notification.v1.json"), n.toJson())
    }

    @Test
    fun a_long_notification_is_cut_to_the_pc_limits() {
        val n = DeviceNotification("id", "a".repeat(100), "t".repeat(500), "b".repeat(5000), "2026-11-14T09:31:00Z").toJson()
        assertEquals(DeviceNotification.MAX_APP, n.getString("app").length)
        assertEquals(DeviceNotification.MAX_TITLE, n.getString("title").length)
        assertEquals(DeviceNotification.MAX_BODY, n.getString("body").length)
    }
}
