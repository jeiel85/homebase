package io.github.jeiel85.homebase.update

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Test

/** GitHub Release `update.json` 파싱 규칙 — Markleaf(D075)에서 가져온 테스트. org.json은 testImplementation의 실제 구현을 쓴다. */
class UpdateManifestParserTest {

    @Test
    fun parse_readsACompleteManifest() {
        val manifest = UpdateManifestParser.parse(VALID)

        assertNotNull(manifest)
        assertEquals(145, manifest!!.versionCode)
        assertEquals("2.42.0", manifest.versionName)
        assertEquals(2_831_155L, manifest.apkSizeBytes)
        assertEquals(26, manifest.minSdk)
    }

    @Test
    fun parse_returnsNullForBrokenJson() {
        assertNull(UpdateManifestParser.parse("not json at all"))
        assertNull(UpdateManifestParser.parse(""))
        assertNull(UpdateManifestParser.parse("{"))
    }

    @Test
    fun parse_returnsNullWhenAnyFieldIsMissing() {
        // 하나만 빠져도 통째로 포기한다. 부분적으로 채워진 manifest로 배너를 띄우면 링크나
        // 크기가 비어 있는 화면이 나오고, 그 화면은 버그처럼 보인다.
        listOf(
            "versionCode",
            "versionName",
            "apkUrl",
            "apkSizeBytes",
            "sha256",
            "minSdk",
            "releaseNotesUrl",
        ).forEach { missing ->
            assertNull("$missing is missing but parsing succeeded", UpdateManifestParser.parse(VALID.without(missing)))
        }
    }

    @Test
    fun parse_returnsNullWhenATypeIsWrong() {
        assertNull(UpdateManifestParser.parse(VALID.replace("\"versionCode\": 145", "\"versionCode\": \"latest\"")))
    }

    @Test
    fun parse_returnsNullForNonsenseValues() {
        assertNull(UpdateManifestParser.parse(VALID.replace("\"versionCode\": 145", "\"versionCode\": 0")))
        assertNull(UpdateManifestParser.parse(VALID.replace("\"versionCode\": 145", "\"versionCode\": -3")))
        assertNull(UpdateManifestParser.parse(VALID.replace("\"apkSizeBytes\": 2831155", "\"apkSizeBytes\": 0")))
        assertNull(UpdateManifestParser.parse(VALID.replace("\"versionName\": \"2.42.0\"", "\"versionName\": \"  \"")))
    }

    @Test
    fun parse_rejectsCleartextUrls() {
        // 이 URL은 브라우저로 넘어가고(B단계), 그 다음 단계에서는 앱이 직접 내려받는다.
        // 평문을 받아들이면 중간자가 APK 주소를 갈아끼울 수 있다.
        assertNull(UpdateManifestParser.parse(VALID.replace("https://example.invalid/markleaf.apk", "http://example.invalid/markleaf.apk")))
        assertNull(UpdateManifestParser.parse(VALID.replace("https://example.invalid/notes", "http://example.invalid/notes")))
    }

    private fun String.without(key: String): String =
        JSONObject(this).apply { remove(key) }.toString()

    private companion object {
        val VALID = """
            {
              "versionCode": 145,
              "versionName": "2.42.0",
              "apkUrl": "https://example.invalid/markleaf.apk",
              "apkSizeBytes": 2831155,
              "sha256": "0be97352a650c3d1a3d2332fd18afc44e0c95a4abca347e9250a2b8a7eecf91a",
              "minSdk": 26,
              "releaseNotesUrl": "https://example.invalid/notes"
            }
        """.trimIndent()
    }
}
