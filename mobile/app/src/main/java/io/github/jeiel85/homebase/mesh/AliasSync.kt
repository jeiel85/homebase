package io.github.jeiel85.homebase.mesh

import io.github.jeiel85.homebase.data.PcEntry
import io.github.jeiel85.homebase.net.GroupAlias
import io.github.jeiel85.homebase.net.GroupInfo

/**
 * The phone's side of the group's aliases (DECISIONS M-51). Each PC keeps the group's table and serves it
 * with its group answer; the phone compares it, PC by PC, with the aliases it holds for its own PCs. Per
 * PC the latest decision wins — a tie goes to the larger alias text, the Agent's rule — so the phone and
 * every PC end with the same name whatever order changes arrive in.
 */
object AliasSync {
    /** What to do after reading the group answers. */
    data class Plan(
        /** pcId → the group's newer entry to take (its alias, or null to go back to the PC's name). */
        val apply: Map<String, GroupAlias>,
        /** Answering pcId → entries this phone decided later than that PC knows. */
        val upload: Map<String, List<GroupAlias>>,
    )

    /**
     * What this phone holds for [pc] as a group entry, or null when it knows nothing. An alias set before
     * aliases were shared has no time: it counts as the oldest, so any alias set since wins over it.
     */
    fun local(pc: PcEntry): GroupAlias? {
        val at = pc.aliasAt ?: if (pc.alias != null) 0L else return null
        return GroupAlias(GroupAlias.pcKey(pc.pcId), pc.name, pc.alias, at)
    }

    /** True when [a] replaces [b]: newer, or as new with the larger alias text (the Agent's `DeviceAliasTable.Wins`). */
    fun wins(a: GroupAlias, b: GroupAlias): Boolean =
        a.atMillis > b.atMillis || (a.atMillis == b.atMillis && (a.alias ?: "") > (b.alias ?: ""))

    /**
     * Pure planning from [groups] (answering pcId → its group). Only what this phone decided goes out, and
     * only to PCs that know aliases ([GroupInfo.aliasesKnown]); the PCs pass the rest between themselves.
     */
    fun plan(pcs: List<PcEntry>, groups: Map<String, GroupInfo>): Plan {
        val apply = mutableMapOf<String, GroupAlias>()
        val upload = mutableMapOf<String, MutableList<GroupAlias>>()
        for (pc in pcs) {
            val key = GroupAlias.pcKey(pc.pcId)
            val mine = local(pc)
            val theirs = groups.mapValues { (_, g) -> g.aliases.firstOrNull { it.key == key } }
            val best = theirs.values.filterNotNull().fold(null as GroupAlias?) { acc, e -> if (acc == null || wins(e, acc)) e else acc }
            if (best != null && (mine == null || wins(best, mine))) {
                apply[pc.pcId] = best
                continue
            }
            if (mine == null) continue
            for ((answering, known) in theirs) {
                if (groups[answering]?.aliasesKnown != true) continue
                if (known == null || wins(mine, known)) upload.getOrPut(answering) { mutableListOf() } += mine
            }
        }
        return Plan(apply, upload)
    }
}
