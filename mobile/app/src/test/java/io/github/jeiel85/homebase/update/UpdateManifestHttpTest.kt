package io.github.jeiel85.homebase.update

import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Homebase-specific: plain http is only accepted when a debug verification build opts in. */
class UpdateManifestHttpTest {

    private val httpManifest = """
        {"versionCode":1401,"versionName":"0.14.1","apkUrl":"http://10.0.2.2:8000/app.apk",
         "apkSizeBytes":10,"sha256":"ab","minSdk":26,"releaseNotesUrl":"http://10.0.2.2:8000/notes"}
    """.trimIndent()

    @Test
    fun http_is_rejected_by_default_and_accepted_only_when_allowed() {
        assertNull(UpdateManifestParser.parse(httpManifest, allowHttp = false))
        assertNotNull(UpdateManifestParser.parse(httpManifest, allowHttp = true))
    }

    @Test
    fun allowed_url_rule() {
        assertTrue(UpdateManifestParser.isAllowedUrl("https://github.com/x", allowHttp = false))
        assertFalse(UpdateManifestParser.isAllowedUrl("http://github.com/x", allowHttp = false))
        assertFalse(UpdateManifestParser.isAllowedUrl("ftp://x", allowHttp = true))
    }
}
