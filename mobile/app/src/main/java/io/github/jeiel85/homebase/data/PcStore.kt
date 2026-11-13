package io.github.jeiel85.homebase.data

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import org.json.JSONArray
import org.json.JSONObject
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/** A paired PC. [hosts] come from the QR (MagicDNS name first, then the fixed Tailscale IP). */
data class PcEntry(
    val pcId: String,
    val name: String,
    val hosts: List<String>,
    val port: Int,
    val deviceId: String,
    val token: String,
    val lastGoodHost: String? = null,
) {
    /** Hosts to try, the one that last worked first. */
    val orderedHosts: List<String>
        get() = listOfNotNull(lastGoodHost) + hosts.filter { it != lastGoodHost }
}

/**
 * Paired PCs in SharedPreferences. The bearer token is encrypted with an AES-GCM key that never
 * leaves the Android Keystore, so a backup or file copy doesn't carry usable credentials
 * (backups are also disabled in the manifest).
 */
class PcStore(context: Context) {

    private val prefs = context.applicationContext.getSharedPreferences(NAME, Context.MODE_PRIVATE)

    @Synchronized
    fun all(): List<PcEntry> {
        val raw = prefs.getString(KEY_PCS, null) ?: return emptyList()
        return try {
            val arr = JSONArray(raw)
            (0 until arr.length()).mapNotNull { i -> arr.optJSONObject(i)?.let(::fromJson) }
        } catch (_: Exception) {
            emptyList()
        }
    }

    fun get(pcId: String): PcEntry? = all().firstOrNull { it.pcId == pcId }

    /** Adds or replaces (re-pairing the same PC keeps one entry). */
    @Synchronized
    fun upsert(pc: PcEntry) = save(all().filterNot { it.pcId == pc.pcId } + pc)

    @Synchronized
    fun remove(pcId: String) = save(all().filterNot { it.pcId == pcId })

    @Synchronized
    fun rememberGoodHost(pcId: String, host: String) {
        val list = all()
        if (list.none { it.pcId == pcId && it.lastGoodHost != host }) return
        save(list.map { if (it.pcId == pcId) it.copy(lastGoodHost = host) else it })
    }

    private fun save(list: List<PcEntry>) {
        val arr = JSONArray()
        list.forEach { arr.put(toJson(it)) }
        prefs.edit().putString(KEY_PCS, arr.toString()).apply()
    }

    private fun toJson(pc: PcEntry) = JSONObject()
        .put("pcId", pc.pcId)
        .put("name", pc.name)
        .put("hosts", JSONArray(pc.hosts))
        .put("port", pc.port)
        .put("deviceId", pc.deviceId)
        .put("token", encrypt(pc.token))
        .put("lastGoodHost", pc.lastGoodHost)

    private fun fromJson(o: JSONObject): PcEntry? {
        val token = decrypt(o.optString("token")) ?: return null
        val hosts = o.optJSONArray("hosts")?.let { a -> (0 until a.length()).map { a.optString(it) } } ?: emptyList()
        return PcEntry(
            pcId = o.optString("pcId"),
            name = o.optString("name"),
            hosts = hosts,
            port = o.optInt("port"),
            deviceId = o.optString("deviceId"),
            token = token,
            lastGoodHost = o.optString("lastGoodHost").ifBlank { null },
        )
    }

    private fun key(): SecretKey {
        val ks = KeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }
        (ks.getEntry(KEY_ALIAS, null) as? KeyStore.SecretKeyEntry)?.let { return it.secretKey }
        val gen = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, ANDROID_KEYSTORE)
        gen.init(
            KeyGenParameterSpec.Builder(KEY_ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build(),
        )
        return gen.generateKey()
    }

    private fun encrypt(plain: String): String {
        val cipher = Cipher.getInstance(TRANSFORMATION).apply { init(Cipher.ENCRYPT_MODE, key()) }
        val out = cipher.iv + cipher.doFinal(plain.toByteArray(Charsets.UTF_8))
        return Base64.encodeToString(out, Base64.NO_WRAP)
    }

    /** null when the Keystore key is gone (e.g. app data restored elsewhere) — the PC must be re-paired. */
    private fun decrypt(encoded: String): String? = try {
        val bytes = Base64.decode(encoded, Base64.NO_WRAP)
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, bytes, 0, IV_SIZE))
        String(cipher.doFinal(bytes, IV_SIZE, bytes.size - IV_SIZE), Charsets.UTF_8)
    } catch (_: Exception) {
        null
    }

    private companion object {
        const val NAME = "homebase_pcs"
        const val KEY_PCS = "pcs"
        const val ANDROID_KEYSTORE = "AndroidKeyStore"
        const val KEY_ALIAS = "homebase_pc_tokens"
        const val TRANSFORMATION = "AES/GCM/NoPadding"
        const val IV_SIZE = 12
    }
}
