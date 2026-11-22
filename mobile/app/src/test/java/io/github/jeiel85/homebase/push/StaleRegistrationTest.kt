package io.github.jeiel85.homebase.push

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** Which distributor broadcasts are for a leftover registration that the app lets go (M-76). */
class StaleRegistrationTest {
    private val current = "current-token"
    private val old = "token-from-before-the-reinstall"

    @Test
    fun a_message_or_endpoint_for_another_token_is_stale() {
        assertTrue(UnifiedPush.isStale(UnifiedPush.ACTION_MESSAGE, old, current))
        assertTrue(UnifiedPush.isStale(UnifiedPush.ACTION_NEW_ENDPOINT, old, current))
    }

    @Test
    fun with_push_off_every_delivered_token_is_stale() {
        assertTrue(UnifiedPush.isStale(UnifiedPush.ACTION_MESSAGE, old, null))
    }

    @Test
    fun the_current_token_is_never_stale() {
        assertFalse(UnifiedPush.isStale(UnifiedPush.ACTION_MESSAGE, current, current))
        assertFalse(UnifiedPush.isStale(UnifiedPush.ACTION_NEW_ENDPOINT, current, current))
    }

    @Test
    fun answers_are_not_answered_so_nothing_loops() {
        // UNREGISTERED is the distributor's answer to the UNREGISTER this sends.
        assertFalse(UnifiedPush.isStale(UnifiedPush.ACTION_UNREGISTERED, old, current))
        assertFalse(UnifiedPush.isStale(UnifiedPush.ACTION_REGISTRATION_FAILED, old, current))
        assertFalse(UnifiedPush.isStale(UnifiedPush.ACTION_TEMP_UNAVAILABLE, old, current))
    }

    @Test
    fun a_broadcast_without_a_token_is_not_stale() {
        assertFalse(UnifiedPush.isStale(UnifiedPush.ACTION_MESSAGE, null, current))
        assertFalse(UnifiedPush.isStale(null, old, current))
    }
}
