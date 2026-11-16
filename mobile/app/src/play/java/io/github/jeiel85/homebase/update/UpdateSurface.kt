package io.github.jeiel85.homebase.update

import android.content.Context
import android.content.Intent
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier

/**
 * Google Play 빌드의 업데이트 이음매 — 아무것도 하지 않는다. `src/github/java`의 같은 이름 파일과
 * **배타적으로** 컴파일된다(DECISIONS M-33).
 *
 * Play 빌드는 스토어가 업데이트한다. Google Play 정책은 Play로 받은 앱이 Play 밖에서 자기 APK를
 * 받아 설치하는 것을 금지하므로(기기·네트워크 악용 정책), 업데이트 확인·다운로드·설치 코드와
 * `REQUEST_INSTALL_PACKAGES` 권한은 이 빌드에 들어가지 않는다. 런타임에 설치 출처를 보고 끄는
 * 방식은 쓰지 않는다 — 코드가 남아 있으면 그 자체로 정책 위반이 될 수 있다.
 */
internal object UpdateSurface {

    /** 설정 화면이 "업데이트" 섹션을 그리지 않게 한다. */
    const val IN_APP = false

    @Suppress("UNUSED_PARAMETER")
    fun sync(context: Context) = Unit

    @Suppress("UNUSED_PARAMETER")
    fun isUpdateTap(intent: Intent?): Boolean = false

    fun requestOpen() = Unit

    /** 목록을 당겨 새로고침할 때 알릴 업데이트 결과가 없다. */
    @Suppress("UNUSED_PARAMETER", "RedundantSuspendModifier")
    suspend fun checkNow(context: Context): String? = null

    @Composable
    @Suppress("UNUSED_PARAMETER")
    fun Banner(modifier: Modifier = Modifier) = Unit

    @Composable
    @Suppress("UNUSED_PARAMETER")
    fun SettingsRows(titleModifier: Modifier = Modifier) = Unit
}
