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
        return manifest.takeIf {
            UpdateDecision.shouldOffer(
                manifest = it,
                installedVersionCode = BuildConfig.VERSION_CODE,
                skippedVersionCode = preferences.skippedVersionCode(),
                deviceSdk = Build.VERSION.SDK_INT,
            )
        }
    }
}
