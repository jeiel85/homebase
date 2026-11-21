package io.github.jeiel85.homebase.update

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Test
import java.io.File

/** #74: a stopped background job doesn't go on through the remaining download attempts. */
class UpdateDownloaderTest {
    // Port 1 on loopback refuses at once: every attempt fails fast without a network.
    private val unreachable = "http://127.0.0.1:1/app.apk"

    @Test
    fun a_stopped_job_makes_no_further_attempt() {
        val file = File.createTempFile("hb-update", ".apk")
        var asked = 0
        // Not stopped before the first attempt, stopped before the second.
        val result = UpdateDownloader().download(unreachable, file, "00", isStopped = { asked++ > 0 })
        assertEquals(UpdateDownloader.Result.Stopped, result)
        assertEquals(2, asked)
        assertFalse(file.exists())
    }

    @Test
    fun without_a_stop_every_attempt_is_made_and_it_fails() {
        val file = File.createTempFile("hb-update", ".apk")
        var asked = 0
        val result = UpdateDownloader().download(unreachable, file, "00", isStopped = { asked++; false })
        assertEquals(UpdateDownloader.Result.Failed, result)
        assertEquals(3, asked) // UpdateDownloader.MAX_ATTEMPTS
        assertFalse(file.exists())
    }
}
