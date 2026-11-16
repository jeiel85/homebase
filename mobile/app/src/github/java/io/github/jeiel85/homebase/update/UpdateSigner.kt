package io.github.jeiel85.homebase.update

import android.annotation.SuppressLint
import android.content.Context
import android.content.pm.PackageManager
import android.content.pm.Signature
import android.os.Build
import io.github.jeiel85.homebase.BuildConfig
import java.security.MessageDigest

/**
 * 이 설치본의 서명이 업데이트 APK의 서명(릴리스 키, DECISIONS M-12)과 같은지 본다.
 *
 * 디버그 빌드처럼 다른 키로 서명된 설치본은 릴리스 APK로 덮어 설치할 수 없다 — 시스템 설치 앱이
 * 확인 화면까지 띄운 뒤 `INSTALL_FAILED_UPDATE_INCOMPATIBLE`로 거부해 사용자는 이유를 모른다.
 * 그래서 받기 전에 알아보고, 다르면 "지우고 릴리스 APK를 새로 설치"를 안내한다([UpdateSurface]).
 *
 * 내려받은 APK의 서명을 미리 읽는 것([UpdateInstaller]가 하지 않는 일)과 달리, 이 앱 **자신의**
 * 서명은 `PackageManager`가 API 26~36 모두 확실히 돌려준다. 읽지 못하면 막지 않는다.
 */
internal object UpdateSigner {

    /** 서명은 앱이 도는 동안 바뀌지 않으므로 프로세스마다 한 번만 읽는다. */
    @Volatile
    private var cached: Boolean? = null

    /** 업데이트 APK를 이 설치본 위에 설치할 수 있는가([UpdateDecision.canUpdateInPlace]). */
    fun canUpdateInPlace(context: Context): Boolean =
        cached ?: UpdateDecision.canUpdateInPlace(installedSignerSha256(context), BuildConfig.UPDATE_SIGNER_SHA256)
            .also { cached = it }

    /** 설치된 앱 서명 인증서마다 SHA-256(소문자 16진수) — `keytool -printcert`의 SHA256 지문과 같은 값. */
    private fun installedSignerSha256(context: Context): List<String> = try {
        signatures(context).map { sha256Hex(it.toByteArray()) }
    } catch (_: Exception) {
        emptyList()
    }

    // API 28 전에는 GET_SIGNATURES뿐이다. 여기 결과는 안내 문구를 고를 뿐 신뢰 판단에 쓰이지 않는다.
    @Suppress("DEPRECATION")
    @SuppressLint("PackageManagerGetSignatures")
    private fun signatures(context: Context): List<Signature> {
        val pm = context.packageManager
        val name = context.packageName
        if (Build.VERSION.SDK_INT >= 28) {
            val info = if (Build.VERSION.SDK_INT >= 33) {
                pm.getPackageInfo(name, PackageManager.PackageInfoFlags.of(PackageManager.GET_SIGNING_CERTIFICATES.toLong()))
            } else {
                pm.getPackageInfo(name, PackageManager.GET_SIGNING_CERTIFICATES)
            }
            val signing = info.signingInfo ?: return emptyList()
            // 키를 교체한(rotation) 앱이면 이력 전체 — 그 안에 릴리스 키가 있으면 업데이트가 설치된다.
            val certs = if (signing.hasMultipleSigners()) signing.apkContentsSigners else signing.signingCertificateHistory
            return certs?.toList() ?: emptyList()
        }
        return pm.getPackageInfo(name, PackageManager.GET_SIGNATURES).signatures?.toList() ?: emptyList()
    }

    private fun sha256Hex(bytes: ByteArray): String =
        MessageDigest.getInstance("SHA-256").digest(bytes).joinToString(separator = "") { "%02x".format(it) }
}
