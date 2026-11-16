package io.github.jeiel85.homebase.mesh

import io.github.jeiel85.homebase.net.GroupInfo
import io.github.jeiel85.homebase.net.GroupMember
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/** The phone's side of the device group (DECISIONS M-47). */
class GroupSyncTest {
    private fun fixture(name: String): String = File("../../docs/mobile-protocol/$name").readText(Charsets.UTF_8)

    private fun group(pcId: String, vararg members: Pair<String, Boolean>) =
        GroupInfo(pcId, pcId.uppercase(), members.map { (id, token) -> GroupMember(id, id.uppercase(), token) })

    @Test
    fun the_group_fixture_parses() {
        val g = GroupInfo.parse(fixture("group.v1.json"))
        assertEquals("6f1c0d2e9b8a4c7d", g.pcId)
        assertEquals(listOf("LAPTOP", "WORK-PC"), g.pcs.map { it.pcName })
        assertFalse(g.pcs[0].token)
        assertTrue(g.pcs[1].token)
    }

    @Test
    fun two_paired_pcs_that_dont_know_each_other_get_linked() {
        val plan = GroupSync.plan(listOf("pc1", "pc2"), mapOf("pc1" to group("pc1"), "pc2" to group("pc2")), emptySet())
        assertEquals(listOf("pc1" to "pc2"), plan.links)
        assertTrue(plan.joins.isEmpty())
    }

    @Test
    fun pcs_linked_in_either_direction_are_left_alone() {
        val plan = GroupSync.plan(
            listOf("pc1", "pc2"),
            mapOf("pc1" to group("pc1", "pc2" to false), "pc2" to group("pc2")),
            emptySet(),
        )
        assertTrue(plan.links.isEmpty())
    }

    @Test
    fun the_phone_joins_group_pcs_through_a_pc_holding_their_token_unless_declined() {
        val groups = mapOf("pc1" to group("pc1", "pc3" to true, "pc4" to false, "pc5" to true))
        val plan = GroupSync.plan(listOf("pc1"), groups, declined = setOf("pc5"))
        // pc4 can't be introduced by pc1 (no token): it joins once pc4 links back to pc1.
        assertEquals(listOf("pc1" to "pc3"), plan.joins)
    }

    @Test
    fun a_pc_offered_by_two_members_is_joined_once() {
        val groups = mapOf("pc1" to group("pc1", "pc3" to true), "pc2" to group("pc2", "pc3" to true, "pc1" to true))
        val plan = GroupSync.plan(listOf("pc1", "pc2"), groups, emptySet())
        assertEquals(listOf("pc1" to "pc3"), plan.joins)
    }
}
