package io.github.jeiel85.homebase.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class PcNameTest {
    private fun pc(id: String, name: String, alias: String? = null) =
        PcEntry(id, name, emptyList(), "dev-$id", "t", alias = alias)

    @Test
    fun a_pc_renamed_in_windows_gets_its_new_name_and_keeps_its_alias() {
        val before = listOf(pc("A", "OLD-NAME", alias = "거실"), pc("B", "WORK-PC"))

        val after = PcStore.renamed(before, "A", " NEW-NAME ")!!

        val a = after.single { it.pcId == "A" }
        assertEquals("NEW-NAME", a.name)
        assertEquals("거실", a.alias)
        assertEquals("거실", a.displayName)
        assertEquals(before[1], after.single { it.pcId == "B" })
    }

    @Test
    fun without_an_alias_the_new_name_is_what_the_app_shows() {
        val after = PcStore.renamed(listOf(pc("A", "OLD-NAME")), "A", "NEW-NAME")!!

        assertEquals("NEW-NAME", after.single().displayName)
    }

    @Test
    fun a_blank_or_unchanged_name_or_an_unknown_pc_changes_nothing() {
        val list = listOf(pc("A", "HOME-PC"))

        assertNull(PcStore.renamed(list, "A", "HOME-PC"))
        assertNull(PcStore.renamed(list, "A", "  "))
        assertNull(PcStore.renamed(list, "Z", "OTHER-PC"))
    }
}
