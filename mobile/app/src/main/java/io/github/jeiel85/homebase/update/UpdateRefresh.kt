package io.github.jeiel85.homebase.update

import android.os.Build
import io.github.jeiel85.homebase.BuildConfig

/**
 * 목록 화면의 배너([UpdateSurface.Banner])와 백그라운드 확인([UpdateCheckJobService])이 함께 쓰는
 * 판단 한 벌. 두 곳이 같은 캐시·같은 하루 1회 규칙을 쓰므로, 화면을 열었든 백그라운드에서 돌았든
 * 네트워크 확인은 하루에 한 번이다.
 */
internal object UpdateRefresh {

    /**
     * Input : [UpdatePreferences]
     * Output: 지금 알릴 만한 [UpdateManifest], 아니면 null
     *
     * 핵심 로직: 네트워크는 하루 1회만 타고, 판단은 캐시된 원문으로 매번 다시 한다.
     *
     * **온전하게 파싱되는 응답만 저장하고, 그때만 확인 시각을 찍는다.** 실패로 하루를 소모하면
     * 잠깐 오프라인이었던 사용자가 다음 날까지 확인하지 못하고, 깨진 응답을 캐시하면 그 하루
     * 동안 매 실행에서 같은 쓰레기를 파싱하고 버린다.
     *
     * 블로킹 네트워크 호출이 있으므로 메인 스레드가 아닌 곳에서 부른다.
     */
    fun run(preferences: UpdatePreferences): UpdateManifest? {
        if (!preferences.isEnabled()) return null

        val now = System.currentTimeMillis()
        if (UpdateDecision.shouldCheckNow(preferences.lastCheckedAt(), now)) {
            UpdateChecker().fetchRaw()?.let { raw ->
                if (UpdateManifestParser.parse(raw) != null) {
                    preferences.setCachedManifest(raw)
                    preferences.setLastCheckedAt(now)
                }
            }
        }

        val manifest = preferences.cachedManifest()?.let(UpdateManifestParser::parse) ?: return null
        return manifest.takeIf { shouldOffer(it, preferences) }
    }

    /** 사용자가 직접 요청한 확인([runNow])의 결과. 화면이 무엇을 말할지 고르는 데 쓴다. */
    sealed interface ManualResult {
        data class Offer(val manifest: UpdateManifest) : ManualResult
        data object UpToDate : ManualResult
        data object Failed : ManualResult
        data object Disabled : ManualResult
    }

    /**
     * Input : [UpdatePreferences]
     * Output: [ManualResult]
     *
     * 핵심 로직: 목록을 아래로 당겼을 때 부른다. 사용자가 **지금** 확인해 달라고 한 것이므로
     * 하루 1회 규칙을 건너뛰고 바로 GET 한다. 결과 저장 규칙은 [run]과 같다(온전히 파싱되는
     * 응답만 캐시하고 그때만 확인 시각을 찍는다) — 그래서 그 뒤의 배너·백그라운드 확인은 이
     * 결과를 그대로 이어 쓴다.
     *
     * 건너뛴 버전은 여기서 풀어 준다. "이 버전 건너뛰기"를 되돌릴 다른 길이 없고, 직접 확인을
     * 요청한 사용자에게 "최신입니다"라고 답하면서 실제로는 새 버전을 숨기는 것은 틀린 답이다.
     * 알림은 버전당 한 번 규칙([UpdateDecision.shouldNotify])이 그대로라 다시 울리지 않는다.
     *
     * "업데이트 확인"이 꺼져 있으면 네트워크를 타지 않는다 — 그 설정은 GitHub로 나가는 요청
     * 자체에 대한 동의다.
     *
     * 블로킹 네트워크 호출이 있으므로 메인 스레드가 아닌 곳에서 부른다.
     */
    fun runNow(preferences: UpdatePreferences): ManualResult {
        if (!preferences.isEnabled()) return ManualResult.Disabled
        val raw = UpdateChecker().fetchRaw() ?: return ManualResult.Failed
        val manifest = UpdateManifestParser.parse(raw) ?: return ManualResult.Failed
        preferences.setCachedManifest(raw)
        preferences.setLastCheckedAt(System.currentTimeMillis())
        if (preferences.skippedVersionCode() != 0) preferences.setSkippedVersionCode(0)
        return if (shouldOffer(manifest, preferences)) ManualResult.Offer(manifest) else ManualResult.UpToDate
    }

    private fun shouldOffer(manifest: UpdateManifest, preferences: UpdatePreferences): Boolean =
        UpdateDecision.shouldOffer(
            manifest = manifest,
            installedVersionCode = BuildConfig.VERSION_CODE,
            skippedVersionCode = preferences.skippedVersionCode(),
            deviceSdk = Build.VERSION.SDK_INT,
        )
}
