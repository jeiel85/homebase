package io.github.jeiel85.homebase.push

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class NotificationPriorityTest {
    private val device = NotificationGroups.DEVICE
    private val toast = NotificationGroups.TOAST

    @Test
    fun importanceFollowsWhatTheUserWantsToNotice() {
        assertEquals(AlertProfile.Importance.HIGH, AlertProfile(popup = true, sound = false, vibrate = false).importance)
        assertEquals(AlertProfile.Importance.DEFAULT, AlertProfile(popup = false, sound = true, vibrate = false).importance)
        // Vibration alone still needs DEFAULT: Android neither rings nor buzzes for LOW.
        assertEquals(AlertProfile.Importance.DEFAULT, AlertProfile(popup = false, sound = false, vibrate = true).importance)
        assertEquals(AlertProfile.Importance.LOW, AlertProfile(popup = false, sound = false, vibrate = false).importance)
    }

    @Test
    fun channelSuffixDiffersExactlyWhenTheChannelMust() {
        val normal = AlertProfile()
        assertEquals("d11", normal.channelSuffix)
        // Bundling and the shade switch are not channel settings: same channel.
        assertEquals(normal.channelSuffix, normal.copy(bundle = false).channelSuffix)
        assertNotEquals(normal.channelSuffix, normal.copy(popup = true).channelSuffix)
        assertNotEquals(normal.channelSuffix, normal.copy(sound = false).channelSuffix)
        assertNotEquals(normal.channelSuffix, normal.copy(vibrate = false).channelSuffix)
        assertEquals("l00", AlertProfile(sound = false, vibrate = false).channelSuffix)
    }

    @Test
    fun encodeDecodeRoundTripsAndRejectsJunk() {
        val profiles = listOf(
            AlertProfile(),
            AlertProfile(shade = false, popup = true, sound = false, vibrate = true, bundle = false),
            AlertLevel.URGENT.applyTo(AlertProfile(bundle = false)),
        )
        profiles.forEach { assertEquals(it, AlertProfile.decode(it.encode())) }
        assertNull(AlertProfile.decode(null))
        assertNull(AlertProfile.decode(""))
        assertNull(AlertProfile.decode("1111"))
        assertNull(AlertProfile.decode("11121"))
        assertNull(AlertProfile.decode("111111"))
    }

    @Test
    fun levelsSetAlertingButKeepBundling() {
        val unbundled = AlertProfile(bundle = false)
        AlertLevel.entries.forEach { level ->
            val p = level.applyTo(unbundled)
            assertFalse("${level.name} kept bundling off", p.bundle)
            assertEquals(level, AlertLevel.of(p))
        }
        assertTrue(AlertLevel.URGENT.applyTo(unbundled).popup)
        assertFalse(AlertLevel.CENTER.applyTo(unbundled).shade)
        // A combination of one's own is no level; centre-only is, whatever the other switches say.
        assertNull(AlertLevel.of(AlertProfile(popup = true, sound = false, vibrate = false)))
        assertEquals(AlertLevel.CENTER, AlertLevel.of(AlertProfile(shade = false, popup = true)))
    }

    @Test
    fun defaultsKeepAppNotificationsSilent() {
        // DECISIONS M-58: app notifications arrive in bursts, so by default they neither ring nor buzz.
        val deviceDefault = NotificationPriority.resolve(device, "Galaxy · 카카오톡", NotificationRules())
        assertEquals(AlertLevel.QUIET, AlertLevel.of(deviceDefault))
        assertTrue(deviceDefault.shade)
        assertTrue(deviceDefault.bundle)
        val toastDefault = NotificationPriority.resolve(toast, "KakaoTalk", NotificationRules())
        assertEquals(AlertLevel.QUIET, AlertLevel.of(toastDefault))
        assertFalse(toastDefault.vibrate)
        assertFalse(toastDefault.sound)
        // So do the PC's informational alerts.
        for (category in listOf("event_log", "advisor", "boot")) {
            assertEquals(category, AlertLevel.QUIET, AlertLevel.of(NotificationPriority.resolve(category, category, NotificationRules())))
        }
        // Watch alerts keep popping up with sound and vibration; a category no group knows is treated as an alert.
        for (category in listOf("process_watch", "service_watch", "http_monitor", "port_monitor", "something_new")) {
            assertEquals(category, AlertLevel.URGENT, AlertLevel.of(NotificationPriority.resolve(category, category, NotificationRules())))
        }
    }

    @Test
    fun anUnknownCategoryFollowsThePriorityThePcGaveIt() {
        // #88: a newer PC's informational kind must not pop up and buzz on an older app.
        for (priority in listOf("info", "recovery")) {
            assertEquals(priority, AlertLevel.QUIET, AlertLevel.of(NotificationPriority.resolve("something_new", "x", NotificationRules(), priority)))
        }
        for (priority in listOf("warning", "critical", "something_else", null)) {
            assertEquals("$priority", AlertLevel.URGENT, AlertLevel.of(NotificationPriority.resolve("something_new", "x", NotificationRules(), priority)))
        }
        // A known kind keeps its group's choice and default whatever its priority.
        assertEquals(AlertLevel.URGENT, AlertLevel.of(NotificationPriority.resolve("process_watch", "x", NotificationRules(), "recovery")))
        assertEquals(AlertLevel.QUIET, AlertLevel.of(NotificationPriority.resolve("advisor", "x", NotificationRules(), "critical")))
    }

    @Test
    fun everyUrgentGroupExists() {
        val keys = NotificationGroups.ALL.map { it.key }.toSet()
        assertTrue(NotificationPriority.URGENT_GROUPS.toString(), keys.containsAll(NotificationPriority.URGENT_GROUPS))
    }

    @Test
    fun anAiSessionWaitingPopsUpAndOneThatFinishedIsSilent() {
        // DECISIONS M-45/M-58: waiting blocks the work until the user answers; done can wait for a look.
        assertEquals(AlertLevel.URGENT, AlertLevel.of(NotificationPriority.resolve("agent-waiting", "Claude Code", NotificationRules())))
        assertEquals(AlertLevel.QUIET, AlertLevel.of(NotificationPriority.resolve("agent-done", "Codex", NotificationRules())))
        // DECISIONS M-48: a permission prompt holds the session until it is answered.
        assertEquals(AlertLevel.URGENT, AlertLevel.of(NotificationPriority.resolve("agent-approval", "Claude Code", NotificationRules())))
    }

    @Test
    fun appChoiceBeatsGroupChoiceBeatsDefault() {
        val quiet = AlertLevel.QUIET.applyTo(AlertProfile())
        val urgent = AlertLevel.URGENT.applyTo(AlertProfile())
        val rules = NotificationRules(
            groups = mapOf("device" to quiet),
            sources = mapOf(NotificationPriority.sourceKey(device, "Galaxy · 카카오톡") to urgent),
        )
        assertEquals(urgent, NotificationPriority.resolve(device, "galaxy · 카카오톡 ", rules))
        assertEquals(quiet, NotificationPriority.resolve(device, "Galaxy · 문자", rules))
        // An app choice for a relayed app doesn't leak into a Windows app of the same name.
        assertEquals(NotificationPriority.defaultFor("toast"), NotificationPriority.resolve(toast, "Galaxy · 카카오톡", rules))
    }

    @Test
    fun testNotificationsIgnoreTheSettings() {
        val rules = NotificationRules(groups = NotificationGroups.ALL.associate { it.key to AlertProfile(shade = false) })
        val p = NotificationPriority.resolve(NotificationGroups.TEST, "test", rules)
        assertTrue(p.shade)
        assertTrue(p.popup)
    }

    @Test
    fun relayedNotificationsBundleAcrossPcsButOthersPerPc() {
        assertEquals(
            NotificationPriority.bundleKey(device, "pc1", "Galaxy · 카카오톡"),
            NotificationPriority.bundleKey(device, "pc2", "galaxy · 카카오톡"),
        )
        assertNotEquals(
            NotificationPriority.bundleKey(toast, "pc1", "KakaoTalk"),
            NotificationPriority.bundleKey(toast, "pc2", "KakaoTalk"),
        )
        assertNotEquals(
            NotificationPriority.bundleKey(toast, "pc1", "KakaoTalk"),
            NotificationPriority.bundleKey(toast, "pc1", "Outlook"),
        )
    }

    @Test
    fun shortLineDropsTheSourceTheBundleAlreadyNames() {
        assertEquals("홍길동: 안녕하세요", NotificationPriority.shortLine("Galaxy · 카카오톡: 홍길동", "안녕하세요\n두 번째 줄", "Galaxy · 카카오톡"))
        assertEquals("홍길동", NotificationPriority.shortLine("KakaoTalk: 홍길동", null, "KakaoTalk"))
        // A notification with no title of its own is sent as just the source: keep its body.
        assertEquals("본문", NotificationPriority.shortLine("KakaoTalk", "  \n본문", "KakaoTalk"))
        assertEquals("KakaoTalk", NotificationPriority.shortLine("KakaoTalk", null, "KakaoTalk"))
        assertEquals("C 드라이브 여유 공간 부족", NotificationPriority.shortLine("C 드라이브 여유 공간 부족", null, "advisor"))
    }

    @Test
    fun descriptions() {
        assertEquals("긴급 — 팝업 · 소리 · 진동 · 묶음", NotificationPriority.describe(AlertLevel.URGENT.applyTo(AlertProfile())))
        assertEquals("조용히 — 무음 · 하나씩", NotificationPriority.describe(AlertLevel.QUIET.applyTo(AlertProfile(bundle = false))))
        assertEquals("센터에만 — 알림 창에 띄우지 않음", NotificationPriority.describe(AlertProfile(shade = false)))
        assertEquals("사용자 지정 — 팝업 · 묶음", NotificationPriority.describe(AlertProfile(popup = true, sound = false, vibrate = false)))
    }

    @Test
    fun appListPutsRecentAppsFirstThenOtherChoices() {
        fun e(category: String, source: String, at: Long) = HistoryEntry(
            id = "$source$at", pcId = "pc1", pcName = "PC", category = category, source = source,
            title = "t", body = null, count = 1, sentAt = at, receivedAt = at, shown = true,
        )
        val entries = listOf(
            e(device, "Galaxy · 카카오톡", 5),
            e("process_watch", "process_watch", 4),
            e(toast, "Outlook", 3),
            e(device, "galaxy · 카카오톡", 2),
        )
        val labels = mapOf(
            NotificationPriority.sourceKey(toast, "Zoom") to "Zoom",
            NotificationPriority.sourceKey(toast, "Outlook") to "Outlook",
            NotificationPriority.sourceKey(device, "Pixel · 문자") to "Pixel · 문자",
        )
        val apps = NotificationPriority.apps(entries, labels)
        assertEquals(listOf("Galaxy · 카카오톡", "Outlook", "Pixel · 문자", "Zoom"), apps.map { it.label })
        assertEquals(listOf(device, toast, device, toast), apps.map { it.category })
    }
}
