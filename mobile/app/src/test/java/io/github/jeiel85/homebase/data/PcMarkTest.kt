package io.github.jeiel85.homebase.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Test

class PcMarkTest {
    private fun pc(id: String, name: String = "PC-$id", alias: String? = null, color: Int? = null) =
        PcEntry(id, name, emptyList(), "dev-$id", "t", alias = alias, color = color)

    /** Ids that all start their colour search on the same slot, so only collision handling tells them apart. */
    private fun collidingIds(count: Int): List<String> {
        val target = PcMarks.slot("seed")
        return generateSequence(0) { it + 1 }.map { "pc-$it" }.filter { PcMarks.slot(it) == target }.take(count).toList()
    }

    @Test
    fun the_letter_is_the_first_character_of_the_shown_name() {
        assertEquals("작", PcMarks.letter("작업실"))
        assertEquals("D", PcMarks.letter("desktop-v0jcepj"))
        assertEquals("거", pc("A", "DESKTOP-V0JCEPJ", alias = "거실 PC").mark.letter)
        assertEquals("?", PcMarks.letter("   "))
    }

    @Test
    fun an_emoji_name_keeps_its_whole_first_emoji() {
        assertEquals("🖥️", PcMarks.letter("🖥️ 작업실"))
        assertEquals("👨‍💻", PcMarks.letter("👨‍💻 코딩"))
    }

    @Test
    fun pcs_whose_ids_collide_still_get_different_colours() {
        val ids = collidingIds(3)

        val colours = PcMarks.withColors(ids.map { pc(it) }).map { it.color }

        assertEquals(3, colours.toSet().size)
    }

    @Test
    fun a_pc_that_has_a_colour_keeps_it() {
        val list = PcMarks.withColors(listOf(pc("A", color = 5), pc("B")))

        assertEquals(5, list[0].color)
        assertNotEquals(5, list[1].color)
    }

    @Test
    fun a_new_pc_never_recolours_the_ones_already_paired() {
        val (a, b, c) = collidingIds(3)
        val before = PcMarks.withColors(listOf(pc(a), pc(b)))

        val after = PcStore.upserted(before, pc(c))

        assertEquals(before.map { it.color }, after.take(2).map { it.color })
        assertEquals(3, after.map { it.color }.toSet().size)
    }

    @Test
    fun re_pairing_keeps_the_colour_even_though_the_pc_moves_to_the_end() {
        val (a, b) = collidingIds(2)
        val before = PcMarks.withColors(listOf(pc(a), pc(b)))

        val after = PcStore.upserted(before, pc(a))

        assertEquals(before.single { it.pcId == a }.color, after.single { it.pcId == a }.color)
        assertEquals(before.single { it.pcId == b }.color, after.single { it.pcId == b }.color)
    }

    @Test
    fun removing_a_pc_leaves_the_others_their_colours() {
        val (a, b, c) = collidingIds(3)
        val all = PcMarks.withColors(listOf(pc(a), pc(b), pc(c)))

        val left = PcMarks.withColors(all.filterNot { it.pcId == a })

        assertEquals(all.drop(1).map { it.color }, left.map { it.color })
    }

    @Test
    fun once_every_colour_is_used_pcs_share_rather_than_fail() {
        val list = PcMarks.withColors((0..PcMarks.PALETTE.size).map { pc("pc-$it") })

        assertEquals(PcMarks.PALETTE.size, list.mapNotNull { it.color }.toSet().size)
        assertEquals(PcMarks.PALETTE.size + 1, list.count { it.color != null })
    }

    @Test
    fun a_stored_colour_outside_the_palette_is_dropped() {
        assertNull(PcMarks.validColor(-1))
        assertNull(PcMarks.validColor(PcMarks.PALETTE.size))
        assertNull(PcMarks.validColor(null))
        assertEquals(0, PcMarks.validColor(0))
    }

    @Test
    fun an_unpaired_name_always_gets_the_same_mark() {
        assertEquals(PcMarks.unpaired("Galaxy S24"), PcMarks.unpaired(" galaxy s24 ").copy(letter = "G"))
        assertEquals("G", PcMarks.unpaired("Galaxy S24").letter)
    }
}
