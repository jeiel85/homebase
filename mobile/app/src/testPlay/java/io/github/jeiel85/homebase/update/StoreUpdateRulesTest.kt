package io.github.jeiel85.homebase.update

import com.google.android.play.core.install.model.InstallErrorCode
import com.google.android.play.core.install.model.UpdateAvailability
import io.github.jeiel85.homebase.update.StoreUpdateRules.Result
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.concurrent.TimeUnit

class StoreUpdateRulesTest {

    @Test
    fun versionNameUndoesTheVersionCodeRule() {
        assertEquals("0.42.0", StoreUpdateRules.versionName(4200))
        assertEquals("0.41.1", StoreUpdateRules.versionName(4101))
        assertEquals("1.2.3", StoreUpdateRules.versionName(10203))
    }

    @Test
    fun aHigherVersionOnPlayIsOffered() {
        assertEquals(Result.Available(4200), StoreUpdateRules.fromInfo(UpdateAvailability.UPDATE_AVAILABLE, 4200, 4100))
    }

    @Test
    fun availableButNotHigherThanInstalledIsUpToDate() {
        // Play's cache can lag right after Play itself installed the update.
        assertEquals(Result.UpToDate, StoreUpdateRules.fromInfo(UpdateAvailability.UPDATE_AVAILABLE, 4100, 4100))
    }

    @Test
    fun notAvailableAndInProgressAreUpToDate_unknownIsFailure() {
        assertEquals(Result.UpToDate, StoreUpdateRules.fromInfo(UpdateAvailability.UPDATE_NOT_AVAILABLE, 0, 4100))
        assertEquals(Result.UpToDate, StoreUpdateRules.fromInfo(UpdateAvailability.DEVELOPER_TRIGGERED_UPDATE_IN_PROGRESS, 4200, 4100))
        assertEquals(Result.Failed, StoreUpdateRules.fromInfo(UpdateAvailability.UNKNOWN, 0, 4100))
    }

    @Test
    fun installsPlayDoesNotKnowAreTellApartFromNetworkFailures() {
        assertEquals(Result.NotFromPlay, StoreUpdateRules.fromError(InstallErrorCode.ERROR_APP_NOT_OWNED, installedByPlay = true))
        assertEquals(Result.NotFromPlay, StoreUpdateRules.fromError(InstallErrorCode.ERROR_API_NOT_AVAILABLE, installedByPlay = true))
        assertEquals(Result.NotFromPlay, StoreUpdateRules.fromError(InstallErrorCode.ERROR_PLAY_STORE_NOT_FOUND, installedByPlay = true))
        assertEquals(Result.Failed, StoreUpdateRules.fromError(InstallErrorCode.ERROR_INTERNAL_ERROR, installedByPlay = true))
        assertEquals(Result.Failed, StoreUpdateRules.fromError(null, installedByPlay = true))
        // "Failed to bind to the service" comes as an internal exception without a code (seen on an emulator
        // with a sideloaded build): an install Play didn't make is "not from Play", not a network problem.
        assertEquals(Result.NotFromPlay, StoreUpdateRules.fromError(null, installedByPlay = false))
        assertEquals(Result.NotFromPlay, StoreUpdateRules.fromError(InstallErrorCode.ERROR_INTERNAL_ERROR, installedByPlay = false))
    }

    @Test
    fun offeredOnlyWhenEnabledAndHigherThanInstalled() {
        assertEquals(4200, StoreUpdateRules.offeredVersionCode(enabled = true, cachedAvailableVersionCode = 4200, installedVersionCode = 4100))
        assertNull(StoreUpdateRules.offeredVersionCode(enabled = false, cachedAvailableVersionCode = 4200, installedVersionCode = 4100))
        // Play updated the app: the cached value is no longer news.
        assertNull(StoreUpdateRules.offeredVersionCode(enabled = true, cachedAvailableVersionCode = 4200, installedVersionCode = 4200))
        assertNull(StoreUpdateRules.offeredVersionCode(enabled = true, cachedAvailableVersionCode = 0, installedVersionCode = 4100))
    }

    @Test
    fun notifiesOncePerVersionAndOnlyWhenVisible() {
        assertTrue(StoreUpdateRules.shouldNotify(stopped = false, enabled = true, offeredVersionCode = 4200, notifiedVersionCode = 4100) { true })
        assertFalse(StoreUpdateRules.shouldNotify(stopped = false, enabled = true, offeredVersionCode = 4200, notifiedVersionCode = 4200) { true })
        assertFalse(StoreUpdateRules.shouldNotify(stopped = true, enabled = true, offeredVersionCode = 4200, notifiedVersionCode = 0) { true })
        assertFalse(StoreUpdateRules.shouldNotify(stopped = false, enabled = false, offeredVersionCode = 4200, notifiedVersionCode = 0) { true })
        assertFalse(StoreUpdateRules.shouldNotify(stopped = false, enabled = true, offeredVersionCode = 4200, notifiedVersionCode = 0) { false })
    }

    @Test
    fun recheckOnOpenAfterSixHoursOrWhenTheClockWentBack() {
        val now = TimeUnit.DAYS.toMillis(100)
        val sixHours = TimeUnit.HOURS.toMillis(6)
        assertTrue(StoreUpdateRules.isDueOnOpen(lastCheckedAt = 0, lastAttemptAt = 0, now = now))
        assertFalse(StoreUpdateRules.isDueOnOpen(lastCheckedAt = now - sixHours + 1, lastAttemptAt = 0, now = now))
        assertTrue(StoreUpdateRules.isDueOnOpen(lastCheckedAt = now - sixHours, lastAttemptAt = 0, now = now))
        // A failed attempt in this process also waits, so opening the app again and again doesn't ask again.
        assertFalse(StoreUpdateRules.isDueOnOpen(lastCheckedAt = 0, lastAttemptAt = now - 1000, now = now))
        assertTrue(StoreUpdateRules.isDueOnOpen(lastCheckedAt = now + 1000, lastAttemptAt = 0, now = now))
    }
}
