package io.github.jeiel85.homebase.share

import io.github.jeiel85.homebase.data.PcEntry
import io.github.jeiel85.homebase.net.Endpoint
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Test

/** docs/SHARE_CONVENIENCE.md §4.6, §4.7, §9.2: what the share center checks first, the PC shortcuts, the path label. */
class ShareCenterRulesTest {
    private val pcs = listOf("a", "b", "c")

    @Test
    fun a_shortcut_checks_only_its_pc() {
        assertEquals(setOf("pc:b"), ShareChoice.preselect(pcs, setOf("pc:a", "phone:a/x"), "c", preset = "b"))
        // A shortcut of a PC removed since checks nothing of its own.
        assertEquals(setOf("pc:a"), ShareChoice.preselect(pcs, setOf("pc:a"), null, preset = "gone"))
    }

    @Test
    fun the_last_targets_come_back_checked() {
        assertEquals(setOf("pc:a", "pc:c", "phone:a/x"), ShareChoice.preselect(pcs, setOf("pc:a", "pc:c", "phone:a/x", "pc:gone"), "b", null))
        // Only phones last time: the PC text went to last joins them; else nothing (more than one PC to choose from).
        assertEquals(setOf("phone:a/x", "pc:b"), ShareChoice.preselect(pcs, setOf("phone:a/x"), "b", null))
        assertEquals(emptySet<String>(), ShareChoice.preselect(pcs, emptySet(), null, null))
        // The only PC is checked.
        assertEquals(setOf("pc:a"), ShareChoice.preselect(listOf("a"), emptySet(), null, null))
    }

    private fun pc(id: String, name: String, color: Int) = PcEntry(id, name, emptyList(), "dev-$id", "token", color = color)

    @Test
    fun shortcuts_are_the_most_used_pcs_first() {
        val list = listOf(pc("a", "거실", 0), pc("b", "작업실", 1), pc("c", "회사", 2), pc("d", "노트북", 3), pc("e", "NAS", 4))
        val ranked = ShareShortcuts.rank(list, mapOf("c" to 5, "e" to 2, "a" to 2))
        assertEquals(listOf("c", "a", "e", "b"), ranked.map { it.pcId })
        assertEquals(ShareShortcuts.MAX, ranked.size)
        // A rename shows in the shortcut: the signature changes.
        val renamed = ShareShortcuts.rank(list.map { if (it.pcId == "c") it.copy(alias = "본사") else it }, mapOf("c" to 5, "e" to 2, "a" to 2))
        assertNotEquals(ShareShortcuts.signature(ranked), ShareShortcuts.signature(renamed))
    }

    @Test
    fun shortcut_ids_name_their_pc() {
        assertEquals("pc:abc", ShareShortcuts.idOf("abc"))
        assertEquals("abc", ShareShortcuts.pcIdOf("pc:abc"))
        assertNull(ShareShortcuts.pcIdOf("pc:"))
        assertNull(ShareShortcuts.pcIdOf("other"))
        assertNull(ShareShortcuts.pcIdOf(null))
    }

    @Test
    fun the_path_is_named_and_cloudflare_says_it_may_be_slow() {
        assertNull(FileShare.pathLabel(null))
        assertEquals("LAN 경로", FileShare.pathLabel(Endpoint(Endpoint.LAN, "192.168.0.2", 8443, true, "ab".repeat(32))))
        assertEquals("Tailscale 경로", FileShare.pathLabel(Endpoint(Endpoint.TAILSCALE, "100.64.0.1", 8765, false)))
        assertEquals("Cloudflare 경로 — 느릴 수 있음", FileShare.pathLabel(Endpoint(Endpoint.CLOUDFLARE, "pc.example.com", 443, true)))
        assertEquals("Cloudflare 경로 — 느릴 수 있음", FileShare.pathLabel(Endpoint(Endpoint.CLOUDFLARE, "x.trycloudflare.com", 443, true)))
    }
}
