package io.github.jeiel85.homebase.update

import android.content.Context

/**
 * What [UpdateRefresh] reads and writes: [UpdatePreferences] on the phone, an in-memory copy in the
 * JVM unit tests (`UpdateRefreshTest`), so its result branches can be pinned without a device.
 */
internal interface UpdateStore {
    fun isEnabled(): Boolean
    fun lastCheckedAt(): Long
    fun setLastCheckedAt(at: Long)
    fun cachedManifest(): String?
    fun setCachedManifest(raw: String)
    fun skippedVersionCode(): Int
    fun setSkippedVersionCode(versionCode: Int)
}

/**
 * 업데이트 확인이 기억해야 하는 세 가지. 전부 이 빌드에서만 의미가 있으므로 앱의
 * `AppSettings`(DataStore)에 넣지 않는다 — 스토어 빌드의 설정 모델에 쓰이지 않는 필드를
 * 남기지 않기 위해서다.
 *
 * `SharedPreferences`인 이유는 `SingleNoteWidgetStore`·`WidgetPaletteStore`와 같다: 읽는 쪽이
 * 코루틴 밖(Compose 컴포지션, 앱 시작 경로)이고, 값이 셋뿐이라 DataStore의 비동기 계약을
 * 끌어올 이유가 없다.
 */
internal class UpdatePreferences(context: Context) : UpdateStore {

    private val prefs = context.applicationContext.getSharedPreferences(NAME, Context.MODE_PRIVATE)

    /** Homebase는 사이드로드 전용이라 기본 켜짐이다(Markleaf는 F-Droid 정책 때문에 기본 꺼짐). 설정에서 끌 수 있다. */
    override fun isEnabled(): Boolean = prefs.getBoolean(KEY_ENABLED, true)

    fun setEnabled(enabled: Boolean) {
        prefs.edit().putBoolean(KEY_ENABLED, enabled).apply()
    }

    /** 마지막으로 확인에 **성공한** 시각. 실패는 기록하지 않는다 — 실패로 하루를 소모하면
     *  잠깐 오프라인이었던 사용자가 다음 날까지 확인하지 못한다. 설정 화면이 "마지막 확인"으로 보여 준다. */
    override fun lastCheckedAt(): Long = prefs.getLong(KEY_LAST_CHECKED_AT, 0L)

    override fun setLastCheckedAt(at: Long) {
        prefs.edit().putLong(KEY_LAST_CHECKED_AT, at).apply()
    }

    /**
     * 마지막으로 받아온 `update.json` 원문. 확인은 하루 1회지만 배너는 그 사이에도 떠 있어야
     * 하므로, 네트워크를 다시 타지 않고 같은 판단을 재현할 수 있게 원문을 그대로 둔다.
     *
     * **온전하게 파싱된 원문만 저장한다.** 깨진 응답을 캐시하면 다음 하루 동안 매 실행에서
     * 같은 쓰레기를 파싱하고 버리게 된다.
     */
    override fun cachedManifest(): String? = prefs.getString(KEY_CACHED_MANIFEST, null)

    override fun setCachedManifest(raw: String) {
        prefs.edit().putString(KEY_CACHED_MANIFEST, raw).apply()
    }

    /** 사용자가 "이 버전 건너뛰기"를 누른 versionCode. 없으면 0. */
    override fun skippedVersionCode(): Int = prefs.getInt(KEY_SKIPPED_VERSION_CODE, 0)

    override fun setSkippedVersionCode(versionCode: Int) {
        prefs.edit().putInt(KEY_SKIPPED_VERSION_CODE, versionCode).apply()
    }

    /** 백그라운드 확인이 Android 알림을 띄운 versionCode. 없으면 0 ([UpdateDecision.shouldNotify]). */
    fun notifiedVersionCode(): Int = prefs.getInt(KEY_NOTIFIED_VERSION_CODE, 0)

    fun setNotifiedVersionCode(versionCode: Int) {
        prefs.edit().putInt(KEY_NOTIFIED_VERSION_CODE, versionCode).apply()
    }

    /**
     * "업데이트"를 눌렀는데 '알 수 없는 앱 설치' 권한이 없어 설정 화면으로 보낸 버전과 그 시각.
     * 권한을 바꾸면 Android가 앱 프로세스를 다시 시작하는 기기가 있어, 화면 상태만으로는 돌아온 뒤
     * 이어서 받을 수 없다 — 배너가 이 값을 보고 이어 간다([UpdateDecision.shouldResumeInstall]).
     */
    fun pendingInstallVersionCode(): Int = prefs.getInt(KEY_PENDING_INSTALL_VERSION_CODE, 0)

    fun pendingInstallAt(): Long = prefs.getLong(KEY_PENDING_INSTALL_AT, 0L)

    fun setPendingInstall(versionCode: Int, at: Long) {
        prefs.edit().putInt(KEY_PENDING_INSTALL_VERSION_CODE, versionCode).putLong(KEY_PENDING_INSTALL_AT, at).apply()
    }

    fun clearPendingInstall() {
        if (pendingInstallVersionCode() == 0) return
        prefs.edit().remove(KEY_PENDING_INSTALL_VERSION_CODE).remove(KEY_PENDING_INSTALL_AT).apply()
    }

    /** "새 버전 자동 설치"(DECISIONS M-56). 기본 켜짐 — "업데이트 확인"이 켜져 있을 때만 의미가 있다. */
    fun isAutoInstallEnabled(): Boolean = prefs.getBoolean(KEY_AUTO_INSTALL, true)

    fun setAutoInstallEnabled(enabled: Boolean) {
        prefs.edit().putBoolean(KEY_AUTO_INSTALL, enabled).apply()
    }

    /** 확인 없는 설치에 시스템이 확인을 요구한 versionCode. 없으면 0 — 그 버전은 다시 받지 않는다. */
    fun needsConfirmVersionCode(): Int = prefs.getInt(KEY_NEEDS_CONFIRM_VERSION_CODE, 0)

    fun setNeedsConfirmVersionCode(versionCode: Int) {
        prefs.edit().putInt(KEY_NEEDS_CONFIRM_VERSION_CODE, versionCode).apply()
    }

    /** 확인 없는 설치가 실패한 versionCode와 그 버전의 실패 횟수. */
    fun silentFailedVersionCode(): Int = prefs.getInt(KEY_SILENT_FAILED_VERSION_CODE, 0)

    fun silentFailures(): Int = prefs.getInt(KEY_SILENT_FAILURES, 0)

    /** 그 버전의 실패를 하나 센다. 다른 버전이면 1부터. */
    fun addSilentFailure(versionCode: Int) {
        val count = if (silentFailedVersionCode() == versionCode) silentFailures() + 1 else 1
        prefs.edit().putInt(KEY_SILENT_FAILED_VERSION_CODE, versionCode).putInt(KEY_SILENT_FAILURES, count).apply()
    }

    /**
     * 확인 없이 설치를 맡긴(세션을 커밋한) versionCode. 없으면 0. 교체된 뒤 새 프로세스가
     * (`MY_PACKAGE_REPLACED`) 이 값이 자기 버전이면 "업데이트했습니다"를 한 번 알린다 — 사용자가 직접
     * 설치한 업데이트에는 알리지 않는다.
     */
    fun silentInstallingVersionCode(): Int = prefs.getInt(KEY_SILENT_INSTALLING_VERSION_CODE, 0)

    fun setSilentInstallingVersionCode(versionCode: Int) {
        // commit: 바로 뒤에 이 프로세스가 교체될 수 있다 — apply의 비동기 쓰기가 사라지면 안 된다.
        prefs.edit().putInt(KEY_SILENT_INSTALLING_VERSION_CODE, versionCode).commit()
    }

    private companion object {
        const val NAME = "homebase_update"
        const val KEY_AUTO_INSTALL = "auto_install"
        const val KEY_NEEDS_CONFIRM_VERSION_CODE = "needs_confirm_version_code"
        const val KEY_SILENT_FAILED_VERSION_CODE = "silent_failed_version_code"
        const val KEY_SILENT_FAILURES = "silent_failures"
        const val KEY_SILENT_INSTALLING_VERSION_CODE = "silent_installing_version_code"
        const val KEY_NOTIFIED_VERSION_CODE = "notified_version_code"
        const val KEY_ENABLED = "enabled"
        const val KEY_LAST_CHECKED_AT = "last_checked_at"
        const val KEY_SKIPPED_VERSION_CODE = "skipped_version_code"
        const val KEY_CACHED_MANIFEST = "cached_manifest"
        const val KEY_PENDING_INSTALL_VERSION_CODE = "pending_install_version_code"
        const val KEY_PENDING_INSTALL_AT = "pending_install_at"
    }
}
