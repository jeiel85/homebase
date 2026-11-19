package io.github.jeiel85.homebase.net

import io.github.jeiel85.homebase.clip.ClipShare
import io.github.jeiel85.homebase.share.Uploader
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * docs/SHARE_CONVENIENCE.md §4.8: phone → phone through a PC. Reads `share-relay.v1.json`, which the .NET side writes
 * from its own models (plaintext samples of what is sealed or answered), with this phone's models, so the field names
 * of `share/phones`, `to`, `from`, `forwarded` and `saved: "forwarded"` can't drift apart between the two.
 */
class ShareRelayTest {

    private fun fixture(name: String) = File("../../docs/mobile-protocol/$name").readText(Charsets.UTF_8)

    private val relay: JSONObject by lazy { JSONObject(fixture("share-relay.v1.json")) }

    private fun strings(a: JSONArray): List<String> = (0 until a.length()).map { a.getString(it) }

    @Test
    fun the_phones_a_pc_relays_to_parse() {
        val raw = relay.getJSONObject("phones")
        val phones = RelayPhone.parseList(raw.toString())!!
        val listed = raw.getJSONArray("phones")
        assertEquals(listed.length(), phones.size)
        assertTrue(phones.isNotEmpty())
        phones.forEachIndexed { i, p ->
            val o = listed.getJSONObject(i)
            assertEquals(o.getString("deviceId"), p.deviceId)
            assertEquals(o.getString("name"), p.name)
            assertEquals(o.getBoolean("clip"), p.clip)
            assertEquals(o.getBoolean("files"), p.files)
        }
    }

    @Test
    fun clip_content_with_to_parses_and_the_phone_writes_to() {
        val raw = relay.getJSONObject("clipContent")
        val content = ClipContent.parse(raw.toString())!!
        assertEquals(strings(raw.getJSONArray("to")), content.to)
        assertTrue(content.to.isNotEmpty())
        assertEquals(raw.getString("text"), content.text)
        assertEquals(raw.getInt("chars"), content.chars)
        assertNull(content.from)

        // What this phone seals for a relayed send carries the same "to".
        val written = ClipContent.of(raw.getString("text"), sensitive = false, to = content.to).toJson()
        assertEquals(content.to, strings(written.getJSONArray("to")))
        // And a direct send leaves it out, as before.
        assertFalse(ClipContent.of("x", sensitive = false).toJson().has("to"))
    }

    @Test
    fun clip_content_passed_on_names_the_sending_phone() {
        val raw = relay.getJSONObject("clipForwarded")
        val content = ClipContent.parse(raw.toString())!!
        assertEquals(raw.getString("from"), content.from)
        assertNotNull(content.from)
    }

    @Test
    fun the_pcs_answer_to_relayed_text_lists_each_phone() {
        val raw = relay.getJSONObject("clipAccepted")
        val result = ClipSendResult.of(200, raw.toString())
        assertTrue(result is ClipSendResult.Relayed)
        val forwarded = (result as ClipSendResult.Relayed).forwarded
        val listed = raw.getJSONArray("forwarded")
        assertEquals(listed.length(), forwarded.size)
        forwarded.forEachIndexed { i, f ->
            val o = listed.getJSONObject(i)
            assertEquals(o.getString("deviceId"), f.deviceId)
            assertEquals(o.getBoolean("ok"), f.ok)
            assertEquals(if (o.isNull("error")) null else o.getString("error"), f.error)
        }
    }

    @Test
    fun transfer_meta_with_to_parses_and_the_phone_writes_to() {
        val raw = relay.getJSONObject("transferMeta")
        val meta = TransferMeta.parse(raw)!!
        assertEquals(strings(raw.getJSONArray("to")), meta.to)
        assertTrue(meta.to.isNotEmpty())
        assertEquals(raw.getJSONArray("files").length(), meta.files.size)
        assertEquals(raw.getJSONArray("files").getJSONObject(0).getString("name"), meta.files[0].name)

        // Sealed by this phone with "to", it opens to the same metadata.
        val key = ByteArray(ClipCrypto.KEY_BYTES) { it.toByte() }
        val id = ClipProtocol.newId()
        val sealed = TransferMessages.sealMeta(key, id, meta.files, ts = meta.ts, to = meta.to)
        assertEquals(meta, TransferMessages.openMetadata(key, sealed))
        assertEquals(meta.files, TransferMessages.openMeta(key, sealed))
        assertEquals(meta.to, strings(TransferMeta(meta.files, meta.ts, meta.to).toJson().getJSONArray("to")))
        // An upload to the PC itself leaves it out.
        assertFalse(TransferMeta(meta.files, meta.ts).toJson().has("to"))
    }

    @Test
    fun done_says_the_files_were_passed_on() {
        assertEquals(ShareRelay.SAVED_FORWARDED, Uploader.savedOf(relay.getJSONObject("transferDone").toString()))
        assertEquals("tray", Uploader.savedOf("{\"schema\":1}"))
    }

    @Test
    fun the_share_notice_names_the_sending_phone() {
        val raw = relay.getJSONObject("shareNotice")
        val notice = ShareNotice.parse(raw.toString())!!
        assertEquals(raw.getString("from"), notice.from)
        assertEquals(raw.getInt("count"), notice.count)
        assertEquals(raw.getLong("totalBytes"), notice.totalBytes)
        assertEquals(raw.getString("name"), notice.name)
    }

    @Test
    fun older_pcs_without_the_new_fields_still_parse() {
        val clip = ClipContent.parse("{\"schema\":1,\"text\":\"hi\",\"chars\":2,\"ts\":\"2026-11-19T06:30:00+00:00\"}")!!
        assertEquals(emptyList<String>(), clip.to)
        assertNull(clip.from)
        val notice = ShareNotice.parse("{\"schema\":1,\"count\":1,\"totalBytes\":5,\"ts\":\"2026-11-19T06:30:00+00:00\"}")!!
        assertNull(notice.from)
        assertNull(notice.name)
        val meta = TransferMeta.parse(JSONObject("{\"schema\":1,\"files\":[{\"name\":\"a.txt\",\"size\":1}],\"ts\":\"x\"}"))!!
        assertEquals(emptyList<String>(), meta.to)
        assertNull(Forwarded.parseList("{\"schema\":1,\"accepted\":true}"))
        assertEquals(ClipSendResult.Sent, ClipSendResult.of(200, "{\"schema\":1,\"accepted\":true}"))
    }

    @Test
    fun unknown_fields_and_bad_entries_are_ignored() {
        val phones = RelayPhone.parseList(
            "{\"schema\":1,\"extra\":true,\"phones\":[" +
                "{\"deviceId\":\"d1\",\"name\":\"Tab\",\"clip\":true,\"files\":false,\"battery\":80}," +
                "{\"name\":\"no id\",\"clip\":true,\"files\":true}," +
                "{\"deviceId\":\"d3\",\"name\":\" \"}]}",
        )!!
        assertEquals(listOf(RelayPhone("d1", "Tab", clip = true, files = false), RelayPhone("d3", "휴대폰", clip = false, files = false)), phones)
        assertNull(RelayPhone.parseList("{\"schema\":2,\"phones\":[]}"))
        assertNull(RelayPhone.parseList("not json"))
        assertEquals(emptyList<RelayPhone>(), RelayPhone.parseList("{\"schema\":1}"))
        // "from" that is blank is no sender.
        assertNull(ShareNotice.parse("{\"schema\":1,\"count\":1,\"totalBytes\":5,\"ts\":\"t\",\"from\":\"  \"}")!!.from)
    }

    @Test
    fun phones_are_offered_once_through_the_first_pc_that_lists_them() {
        val home = RelayAnswer("pc1", "집 PC", listOf(RelayPhone("a1", "Galaxy Tab S9", clip = true, files = true), RelayPhone("a2", "iPad", clip = true, files = false)))
        val work = RelayAnswer("pc2", "회사 PC", listOf(RelayPhone("b1", "galaxy tab s9", clip = true, files = true), RelayPhone("b2", "Pixel", clip = false, files = true)))
        val forFiles = ShareRelay.targets(listOf(home, work), forFiles = true)
        assertEquals(listOf("a1" to "pc1", "b2" to "pc2"), forFiles.map { it.phone.deviceId to it.pcId })
        val forText = ShareRelay.targets(listOf(home, work), forFiles = false)
        assertEquals(listOf("a1", "a2"), forText.map { it.phone.deviceId })
        assertEquals("phone:pc1/a1", forText[0].key)
    }

    @Test
    fun relayed_lines_name_the_phone_and_the_pc() {
        assertEquals("집 PC에서", ShareRelay.fromLine("집 PC", null))
        assertEquals("집 PC에서", ShareRelay.fromLine("집 PC", " "))
        assertEquals("Galaxy S24에서(집 PC 경유)", ShareRelay.fromLine("집 PC", "Galaxy S24"))
        assertEquals("Tab", ShareRelay.names(listOf("Tab")))
        assertEquals("Tab, iPad", ShareRelay.names(listOf("Tab", "iPad")))
        assertEquals("Tab 외 2대", ShareRelay.names(listOf("Tab", "iPad", "Pixel")))

        val tab = RelayPhone("a1", "Galaxy Tab S9", clip = true, files = true)
        val pad = RelayPhone("a2", "iPad", clip = true, files = true)
        assertEquals(
            "Galaxy Tab S9에 보냈습니다(집 PC 경유)\niPad에 보내지 못했습니다(집 PC 경유)",
            ClipShare.relayedLine(listOf(Forwarded("a1", true), Forwarded("a2", false, "push_failed")), listOf(tab, pad), "집 PC"),
        )
        // A phone the PC didn't mention was not reached.
        assertEquals("Galaxy Tab S9, iPad에 보내지 못했습니다(집 PC 경유)", ClipShare.relayedLine(emptyList(), listOf(tab, pad), "집 PC"))
    }
}
