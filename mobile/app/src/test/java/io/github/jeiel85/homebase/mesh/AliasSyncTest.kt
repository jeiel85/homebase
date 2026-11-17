package io.github.jeiel85.homebase.mesh

import io.github.jeiel85.homebase.data.PcEntry
import io.github.jeiel85.homebase.data.PcStore
import io.github.jeiel85.homebase.net.GroupAlias
import io.github.jeiel85.homebase.net.GroupInfo
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/** The group's aliases on the phone (DECISIONS M-50). */
class AliasSyncTest {
    private fun fixture(name: String): String = File("../../docs/mobile-protocol/$name").readText(Charsets.UTF_8)

    private fun pc(id: String, alias: String? = null, at: Long? = null) =
        PcEntry(id, id.uppercase(), emptyList(), "dev-$id", "t", alias = alias, aliasAt = at)

    private fun entry(pcId: String, alias: String?, at: Long) = GroupAlias(GroupAlias.pcKey(pcId), pcId.uppercase(), alias, at)

    private fun group(pcId: String, vararg aliases: GroupAlias, known: Boolean = true) =
        GroupInfo(pcId, pcId.uppercase(), emptyList(), aliases.toList(), aliasesKnown = known)

    @Test
    fun the_group_fixture_carries_aliases_and_a_cleared_one() {
        val g = GroupInfo.parse(fixture("group.v1.json"))
        assertTrue(g.aliasesKnown)
        assertEquals(listOf("회사", "내 폰", null), g.aliases.map { it.alias })
        assertEquals("pc:a1b2c3d4e5f60718", g.aliases[0].key)
        // Sub-second times survive, so a round trip compares equal with the PC's.
        assertEquals(java.time.Instant.parse("2026-11-17T08:30:00.125Z").toEpochMilli(), g.aliases[0].atMillis)
    }

    @Test
    fun an_entry_round_trips_through_the_wire_shape() {
        val e = entry("pc1", "작업실", 1_763_368_200_125L)
        assertEquals(e, GroupAlias.parse(e.toJson()))
        val cleared = entry("pc1", null, 1_763_368_200_000L)
        assertEquals(cleared, GroupAlias.parse(JSONObject(cleared.toJson().toString())))
        assertNull(GroupAlias.parse(JSONObject().put("key", "pc:x").put("at", "yesterday")))
    }

    @Test
    fun a_newer_alias_from_the_group_is_taken() {
        val plan = AliasSync.plan(listOf(pc("pc1", "집", at = 100)), mapOf("pc1" to group("pc1", entry("pc1", "작업실", 200))))
        assertEquals("작업실", plan.apply["pc1"]?.alias)
        assertTrue(plan.upload.isEmpty())
    }

    @Test
    fun a_later_choice_on_this_phone_goes_to_the_pcs_that_lack_it() {
        val mine = pc("pc1", "작업실", at = 300)
        val plan = AliasSync.plan(
            listOf(mine),
            mapOf(
                "pc1" to group("pc1", entry("pc1", "집", 200)), // older
                "pc2" to group("pc2"), // has none
                "pc3" to group("pc3", entry("pc1", "작업실", 300)), // already has it
                "pc4" to group("pc4", known = false), // older than v0.37.0
            ),
        )
        assertTrue(plan.apply.isEmpty())
        assertEquals(setOf("pc1", "pc2"), plan.upload.keys)
        assertEquals(listOf(AliasSync.local(mine)), plan.upload["pc1"])
    }

    @Test
    fun clearing_travels_like_any_other_choice() {
        val plan = AliasSync.plan(listOf(pc("pc1", "집", at = 100)), mapOf("pc2" to group("pc2", entry("pc1", null, 200))))
        assertNull(plan.apply.getValue("pc1").alias)

        val cleared = AliasSync.plan(listOf(pc("pc1", null, at = 300)), mapOf("pc2" to group("pc2", entry("pc1", "집", 200))))
        assertEquals(listOf(null), cleared.upload["pc2"]?.map { it.alias })
    }

    @Test
    fun an_alias_from_before_sharing_counts_as_the_oldest() {
        val legacy = pc("pc1", "집 데스크톱")
        // Nobody has one yet: it is shared as it is.
        assertEquals(0L, AliasSync.plan(listOf(legacy), mapOf("pc2" to group("pc2"))).upload["pc2"]?.single()?.atMillis)
        // Anything set since wins over it.
        assertEquals("회사", AliasSync.plan(listOf(legacy), mapOf("pc2" to group("pc2", entry("pc1", "회사", 1)))).apply["pc1"]?.alias)
        // A phone that never named it has nothing to share.
        assertTrue(AliasSync.plan(listOf(pc("pc1")), mapOf("pc2" to group("pc2"))).upload.isEmpty())
    }

    @Test
    fun a_tie_goes_to_the_larger_alias_like_on_the_pcs() {
        assertTrue(AliasSync.wins(entry("pc1", "B폰", 5), entry("pc1", "A폰", 5)))
        val plan = AliasSync.plan(listOf(pc("pc1", "A폰", at = 5)), mapOf("pc2" to group("pc2", entry("pc1", "B폰", 5))))
        assertEquals("B폰", plan.apply["pc1"]?.alias)
    }

    @Test
    fun the_newest_of_several_pcs_answers_wins() {
        val plan = AliasSync.plan(
            listOf(pc("pc1")),
            mapOf("pc1" to group("pc1", entry("pc1", "옛 이름", 10)), "pc2" to group("pc2", entry("pc1", "새 이름", 20))),
        )
        assertEquals("새 이름", plan.apply["pc1"]?.alias)
    }

    @Test
    fun re_pairing_keeps_the_alias_and_when_it_was_set() {
        val kept = PcStore.upserted(listOf(pc("pc1", "작업실", at = 42)), pc("pc1")).single()
        assertEquals("작업실", kept.alias)
        assertEquals(42L, kept.aliasAt)
    }
}
