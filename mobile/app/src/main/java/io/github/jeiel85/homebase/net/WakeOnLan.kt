package io.github.jeiel85.homebase.net

/**
 * Wake-on-LAN's magic packet (docs/REMOTE_COMMANDS.md §4.6): six 0xFF bytes, then the adapter's 6-byte MAC address
 * sixteen times, sent by UDP broadcast on the PC's LAN. The PC lists its adapters' MACs in `GET /api/v1/commands`
 * (`wake.macs`). Pure, so unit tests pin the bytes; sending is [io.github.jeiel85.homebase.command.WakeSender].
 */
object WakeOnLan {
    const val MAC_BYTES = 6
    const val PACKET_BYTES = 6 + 16 * MAC_BYTES

    /** The discard port, then the echo port: either is what WoL listeners and routers' forwards expect. */
    val PORTS = intArrayOf(9, 7)

    private val SEPARATED = Regex("^[0-9a-fA-F]{2}([:-])[0-9a-fA-F]{2}(\\1[0-9a-fA-F]{2}){4}$")
    private val BARE = Regex("^[0-9a-fA-F]{12}$")

    /**
     * The 6 bytes of [text] — `aa:bb:cc:dd:ee:ff`, `AA-BB-CC-DD-EE-FF` or `aabbccddeeff` — or null when it is none of
     * those, or all zeros or all ones (no adapter has those).
     */
    fun parseMac(text: String?): ByteArray? {
        val t = text?.trim() ?: return null
        val hex = when {
            SEPARATED.matches(t) -> t.replace(":", "").replace("-", "")
            BARE.matches(t) -> t
            else -> return null
        }
        val bytes = ByteArray(MAC_BYTES) { i -> hex.substring(i * 2, i * 2 + 2).toInt(16).toByte() }
        return if (bytes.all { it == 0.toByte() } || bytes.all { it == 0xFF.toByte() }) null else bytes
    }

    /** [text] as `aa:bb:cc:dd:ee:ff`, or null when it is not a MAC address ([parseMac]). */
    fun normalize(text: String?): String? = parseMac(text)?.joinToString(":") { (it.toInt() and 0xFF).toString(16).padStart(2, '0') }

    /** 6 × 0xFF, then [mac] 16 times: 102 bytes. */
    fun magicPacket(mac: ByteArray): ByteArray {
        require(mac.size == MAC_BYTES) { "a MAC address is $MAC_BYTES bytes" }
        val packet = ByteArray(PACKET_BYTES)
        for (i in 0 until 6) packet[i] = 0xFF.toByte()
        for (r in 0 until 16) mac.copyInto(packet, 6 + r * MAC_BYTES)
        return packet
    }

    /**
     * The directed broadcast address of the IPv4 network [ip] / [prefixLength] (host bits all set), as an int in
     * network order; null for a /31, /32 or /0, which have none worth sending to.
     */
    fun directedBroadcast(ip: Int, prefixLength: Int): Int? =
        if (prefixLength !in 1..30) null else ip or (-1 ushr prefixLength)

    /** [ip] (network order, as [directedBroadcast] returns it) as the 4 address bytes. */
    fun bytesOf(ip: Int): ByteArray = byteArrayOf((ip ushr 24).toByte(), (ip ushr 16).toByte(), (ip ushr 8).toByte(), ip.toByte())

    /** The 4 bytes of an IPv4 address as an int in network order. */
    fun intOf(address: ByteArray): Int {
        require(address.size == 4) { "an IPv4 address is 4 bytes" }
        return ((address[0].toInt() and 0xFF) shl 24) or ((address[1].toInt() and 0xFF) shl 16) or
            ((address[2].toInt() and 0xFF) shl 8) or (address[3].toInt() and 0xFF)
    }
}
