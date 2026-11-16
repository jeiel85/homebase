package io.github.jeiel85.homebase.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class PcAliasTest {
    private fun pc(id: String, name: String, alias: String? = null, token: String = "t") =
        PcEntry(id, name, emptyList(), "dev-$id", token, alias = alias)

    @Test
    fun the_alias_is_shown_instead_of_the_pc_name() {
        assertEquals("집 데스크톱", pc("A", "DESKTOP-V0JCEPJ", alias = "집 데스크톱").displayName)
        assertEquals("DESKTOP-V0JCEPJ", pc("A", "DESKTOP-V0JCEPJ").displayName)
    }

    @Test
    fun the_alias_is_trimmed_and_whitespace_collapsed() {
        assertEquals("집 데스크톱", PcStore.normalizeAlias("  집 \n\t 데스크톱  ", "PC"))
    }

    @Test
    fun blank_or_the_pc_own_name_means_no_alias() {
        assertNull(PcStore.normalizeAlias(null, "PC"))
        assertNull(PcStore.normalizeAlias("", "PC"))
        assertNull(PcStore.normalizeAlias("   ", "PC"))
        assertNull(PcStore.normalizeAlias(" HOME-PC ", "HOME-PC"))
    }

    @Test
    fun a_long_alias_is_cut_without_splitting_an_emoji() {
        val long = "가".repeat(PcStore.ALIAS_MAX - 1) + "🖥️🖥️"
        val cut = PcStore.normalizeAlias(long, "PC")!!

        assertEquals(PcStore.ALIAS_MAX, cut.codePointCount(0, cut.length))
        assertEquals("가".repeat(PcStore.ALIAS_MAX - 1) + "🖥", cut)
    }

    @Test
    fun re_pairing_the_same_pc_keeps_its_alias() {
        val before = listOf(pc("A", "HOME-PC", alias = "거실"), pc("B", "WORK-PC"))

        val after = PcStore.upserted(before, pc("A", "HOME-PC", token = "new"))

        val a = after.single { it.pcId == "A" }
        assertEquals("거실", a.alias)
        assertEquals("new", a.token)
        assertEquals(2, after.size)
    }

    @Test
    fun a_new_pc_has_no_alias() {
        val after = PcStore.upserted(listOf(pc("A", "HOME-PC", alias = "거실")), pc("B", "WORK-PC"))

        assertNull(after.single { it.pcId == "B" }.alias)
    }
}
