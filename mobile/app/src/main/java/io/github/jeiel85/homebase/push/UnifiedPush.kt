package io.github.jeiel85.homebase.push

import android.app.BroadcastOptions
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import java.util.UUID

/**
 * The UnifiedPush connector side (spec AND_3.1.0, https://unifiedpush.org/developers/spec/android/),
 * implemented with plain broadcast intents instead of the connector library (DECISIONS M-3, M-19).
 * A distributor app (ntfy, …) holds the one persistent connection; Homebase only registers, receives
 * [PushReceiver] broadcasts and acknowledges them.
 */
object UnifiedPush {
    // App → distributor
    const val ACTION_REGISTER = "org.unifiedpush.android.distributor.REGISTER"
    const val ACTION_UNREGISTER = "org.unifiedpush.android.distributor.UNREGISTER"
    const val ACTION_MESSAGE_ACK = "org.unifiedpush.android.distributor.MESSAGE_ACK"

    // Distributor → app
    const val ACTION_NEW_ENDPOINT = "org.unifiedpush.android.connector.NEW_ENDPOINT"
    const val ACTION_MESSAGE = "org.unifiedpush.android.connector.MESSAGE"
    const val ACTION_REGISTRATION_FAILED = "org.unifiedpush.android.connector.REGISTRATION_FAILED"
    const val ACTION_UNREGISTERED = "org.unifiedpush.android.connector.UNREGISTERED"
    const val ACTION_TEMP_UNAVAILABLE = "org.unifiedpush.android.connector.TEMP_UNAVAILABLE"

    const val EXTRA_TOKEN = "token"
    const val EXTRA_ENDPOINT = "endpoint"
    const val EXTRA_BYTES_MESSAGE = "bytesMessage"
    const val EXTRA_ID = "id"
    const val EXTRA_REASON = "reason"
    const val EXTRA_MESSAGE = "message"
    const val EXTRA_PI = "pi"

    /** Legacy (AND_2) extra some distributors still read to know which app registered. */
    private const val EXTRA_APPLICATION = "application"

    /** The spec's identity carrier: an immutable PendingIntent whose creator package is ours. */
    private const val DUMMY_APP = "org.unifiedpush.dummy_app"

    /** Distributors the user can install; ntfy is the common one and works with ntfy.sh, no account. */
    const val NTFY_PACKAGE = "io.heckel.ntfy"

    /** Installed distributor packages (needs the `<queries>` entry for ACTION_REGISTER, API 30+). */
    fun distributors(context: Context): List<String> {
        val pm = context.packageManager
        val intent = Intent(ACTION_REGISTER)
        val receivers = if (Build.VERSION.SDK_INT >= 33) {
            pm.queryBroadcastReceivers(intent, PackageManager.ResolveInfoFlags.of(0))
        } else {
            @Suppress("DEPRECATION")
            pm.queryBroadcastReceivers(intent, 0)
        }
        return receivers.mapNotNull { it.activityInfo?.packageName }.distinct().filter { it != context.packageName }
    }

    fun label(context: Context, pkg: String): String = runCatching {
        val pm = context.packageManager
        val info = if (Build.VERSION.SDK_INT >= 33) {
            pm.getApplicationInfo(pkg, PackageManager.ApplicationInfoFlags.of(0))
        } else {
            @Suppress("DEPRECATION")
            pm.getApplicationInfo(pkg, 0)
        }
        pm.getApplicationLabel(info).toString()
    }.getOrDefault(pkg)

    /**
     * Asks [distributor] for an endpoint. A fresh high-entropy token every time (required after
     * REGISTRATION_FAILED, harmless otherwise); the answer arrives as NEW_ENDPOINT.
     */
    fun register(context: Context, distributor: String) {
        val state = PushState(context)
        // A registration this replaces is let go first; left alone, the distributor would keep it (and its
        // subscription) for good, since nothing would ever answer for its token again (M-76).
        val previousToken = state.token
        val previousDistributor = state.distributor
        val token = UUID.randomUUID().toString()
        state.startRegistration(token, distributor)
        if (previousToken != null && previousDistributor != null) sendUnregister(context, previousDistributor, previousToken)
        val intent = Intent(ACTION_REGISTER)
            .setPackage(distributor)
            .putExtra(EXTRA_TOKEN, token)
            .putExtra(EXTRA_MESSAGE, "Homebase PC 알림")
            .putExtra(EXTRA_APPLICATION, context.packageName)
            .putExtra(EXTRA_PI, identity(context))
        send(context, intent)
    }

    /** Tells the distributor to drop our endpoint; the token is forgotten at once (spec MUST). */
    fun unregister(context: Context) {
        val state = PushState(context)
        val token = state.token
        val distributor = state.distributor
        state.clear()
        if (token == null || distributor == null) return
        sendUnregister(context, distributor, token)
    }

    /**
     * Whether a distributor broadcast is for a registration this install doesn't hold, and worth ending: a
     * NEW_ENDPOINT or MESSAGE (the distributor still delivers to it). The spec has the connector ignore these;
     * Homebase also lets them go, because the distributor never drops one by itself — not when the app is
     * uninstalled — so a reinstalled app would leave one subscription behind each time (M-76). The other
     * actions are answers (UNREGISTERED to our own UNREGISTER, …) and are only ignored, so this never loops.
     */
    fun isStale(action: String?, token: String?, current: String?): Boolean =
        token != null && token != current && (action == ACTION_NEW_ENDPOINT || action == ACTION_MESSAGE)

    /**
     * UNREGISTER for [token] to every installed distributor. The broadcast doesn't say which one sent it, and
     * one that doesn't know the token ignores the request (spec).
     */
    fun unregisterStale(context: Context, token: String) {
        for (distributor in distributors(context)) sendUnregister(context, distributor, token)
    }

    private fun sendUnregister(context: Context, distributor: String, token: String) {
        val intent = Intent(ACTION_UNREGISTER)
            .setPackage(distributor)
            .putExtra(EXTRA_TOKEN, token)
            .putExtra(EXTRA_APPLICATION, context.packageName)
            .putExtra(EXTRA_PI, identity(context))
        send(context, intent)
    }

    fun ack(context: Context, distributor: String, token: String, id: String) {
        send(
            context,
            Intent(ACTION_MESSAGE_ACK).setPackage(distributor).putExtra(EXTRA_TOKEN, token).putExtra(EXTRA_ID, id),
        )
    }

    private fun identity(context: Context): PendingIntent =
        PendingIntent.getBroadcast(context, 0, Intent().setPackage(DUMMY_APP), PendingIntent.FLAG_IMMUTABLE)

    /** On API 34+ the spec identifies the sender by a broadcast that shares its identity. */
    private fun send(context: Context, intent: Intent) {
        if (Build.VERSION.SDK_INT >= 34) {
            val options = BroadcastOptions.makeBasic().setShareIdentityEnabled(true)
            context.sendBroadcast(intent, null, options.toBundle())
        } else {
            context.sendBroadcast(intent)
        }
    }
}
