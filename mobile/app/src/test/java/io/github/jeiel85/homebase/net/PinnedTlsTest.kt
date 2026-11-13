package io.github.jeiel85.homebase.net

import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test
import java.security.cert.CertificateException
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import java.util.Base64
import javax.net.ssl.SSLHandshakeException

/** The LAN path trusts exactly the certificate pinned in the QR (DECISIONS M-15). */
class PinnedTlsTest {

    // A "CN=Homebase Mobile LAN" ECDSA P-256 certificate made the way the Agent makes one.
    private val cert: X509Certificate = CertificateFactory.getInstance("X.509").generateCertificate(
        Base64.getDecoder().decode(
            "MIIBMjCB2aADAgECAgkAsS13RysXlvwwCgYIKoZIzj0EAwIwHjEcMBoGA1UEAxMTSG9tZWJhc2UgTW9iaWxlIExBTjAgFw0yNjExMTIwNDI0MjhaGA8yMDU2" +
                "MTExMzA0MjQyOFowHjEcMBoGA1UEAxMTSG9tZWJhc2UgTW9iaWxlIExBTjBZMBMGByqGSM49AgEGCCqGSM49AwEHA0IABBUdRBxdHYiNo4jN7P3vRehs1Hiv" +
                "ilPj+tpVGDb2mKJUhh6Szayw1fbAxHRWuQ37iwPOdplBVsMiWT+ymifC8VowCgYIKoZIzj0EAwIDSAAwRQIgSMLzG+bCIsbBhpZkzv87quDZoM4pu2YtP3W7" +
                "Yb0p/zACIQC1Rvo0PFnlBljUrAO5+MT4o+gbDGvtxBX2cDkm8dmZTQ==",
        ).inputStream(),
    ) as X509Certificate

    private val pin = "2b2c3d3a744ded07799b91a99771085617cab8f3cd7c226562e53397e9b3bd35"

    @Test
    fun pin_is_sha256_of_the_der_certificate() {
        assertEquals(pin, PinnedTls.sha256Hex(cert.encoded))
    }

    @Test
    fun matching_certificate_is_trusted_regardless_of_case() {
        PinnedTls.trustManager(pin.uppercase()).checkServerTrusted(arrayOf(cert), "ECDHE_ECDSA")
    }

    @Test
    fun any_other_certificate_is_refused() {
        val tm = PinnedTls.trustManager("0".repeat(64))
        assertThrows(PinMismatchException::class.java) { tm.checkServerTrusted(arrayOf(cert), "ECDHE_ECDSA") }
        assertThrows(CertificateException::class.java) { tm.checkServerTrusted(emptyArray(), "ECDHE_ECDSA") }
    }

    @Test
    fun pin_mismatch_is_reported_by_name() {
        val wrapped = SSLHandshakeException("handshake").apply { initCause(PinMismatchException()) }
        assertEquals(PinMismatchException().message, HomebaseApi.describe(wrapped))
    }
}
