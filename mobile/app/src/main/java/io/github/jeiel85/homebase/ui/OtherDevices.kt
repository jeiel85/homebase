package io.github.jeiel85.homebase.ui

import android.content.Intent
import android.provider.Settings
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.compose.LifecycleResumeEffect
import io.github.jeiel85.homebase.mesh.MeshPrefs
import io.github.jeiel85.homebase.mesh.MeshSync
import io.github.jeiel85.homebase.mesh.NotificationForwarder
import io.github.jeiel85.homebase.net.DeviceKind
import io.github.jeiel85.homebase.net.DeviceStatus
import java.time.Duration
import java.time.OffsetDateTime

/** One device another PC knows about (DECISIONS M-27): kind, name, state, and which PC told us. */
@Composable
fun OtherDeviceCard(device: MeshSync.OtherDevice) {
    val hb = Hb.colors
    val d = device.entry
    Plate(Modifier.padding(bottom = 8.dp), dim = !d.online) {
        Column(Modifier.fillMaxWidth().padding(horizontal = 14.dp, vertical = 12.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Led(if (d.online) hb.ledOn else hb.ledOff, size = 7.dp, pulse = d.online)
                Spacer(Modifier.width(6.dp))
                Text(
                    d.name,
                    style = MaterialTheme.typography.titleSmall.copy(fontWeight = FontWeight.SemiBold),
                    color = hb.ink,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.weight(1f),
                )
                Tag(if (d.kind == DeviceKind.PC) "PC" else "휴대폰", hb.hairline, hb.inkSoft)
            }
            Text(
                DeviceLine.status(d.status, d.kind),
                style = MaterialTheme.typography.bodyMedium,
                color = hb.ink,
                modifier = Modifier.padding(top = 4.dp),
            )
            Text(
                listOfNotNull(DeviceLine.seen(d.online, d.lastSeenAt), "${device.via.joinToString(", ")} 경유", d.model).joinToString(" · "),
                style = HbType.readout.copy(fontSize = 11.sp, fontWeight = FontWeight.Normal),
                color = hb.inkSoft,
                maxLines = 2,
                modifier = Modifier.padding(top = 2.dp),
            )
        }
    }
}

/** Texts for a device's state; pure so the wording is pinned by tests. */
object DeviceLine {
    fun status(s: DeviceStatus?, kind: String): String {
        if (s == null) return "아직 보고 없음"
        val parts = mutableListOf<String>()
        if (kind == DeviceKind.PHONE) {
            s.battery?.let { parts += "배터리 $it%" + if (s.charging == true) " (충전 중)" else "" }
            s.network?.let { parts += when (it) { "wifi" -> "Wi-Fi"; "cellular" -> "모바일 데이터"; "ethernet" -> "유선"; else -> it } }
        } else {
            s.cpuPct?.let { parts += "CPU ${it.toInt()}%" }
            if (s.memUsedBytes != null && s.memTotalBytes != null && s.memTotalBytes > 0) {
                parts += "RAM ${(s.memUsedBytes * 100 / s.memTotalBytes)}%"
            }
            s.uptimeSec?.let { parts += "가동 ${it / 86_400}일 ${(it % 86_400) / 3600}시간" }
        }
        s.appVersion?.let { parts += "v$it" }
        return if (parts.isEmpty()) "아직 보고 없음" else parts.joinToString(" · ")
    }

    fun seen(online: Boolean, lastSeenAt: String?, now: OffsetDateTime = OffsetDateTime.now()): String? {
        if (online) return "온라인"
        val at = lastSeenAt?.let { runCatching { OffsetDateTime.parse(it) }.getOrNull() } ?: return "오프라인"
        val min = Duration.between(at, now).toMinutes().coerceAtLeast(0)
        return when {
            min < 1 -> "방금 확인"
            min < 60 -> "${min}분 전 확인"
            min < 1440 -> "${min / 60}시간 전 확인"
            else -> "${min / 1440}일 전 확인"
        }
    }
}

/**
 * Settings → 다른 기기: forward this phone's notifications to the PCs (off by default, needs
 * Android's notification access) and leave chosen apps out (DECISIONS M-28).
 */
@Composable
fun DeviceSharingSettings() {
    val hb = Hb.colors
    val context = LocalContext.current
    val prefs = remember { MeshPrefs(context) }
    var enabled by remember { mutableStateOf(prefs.forwardEnabled()) }
    var access by remember { mutableStateOf(NotificationForwarder.hasAccess(context)) }
    var excluded by remember { mutableStateOf(prefs.excluded()) }
    var seen by remember { mutableStateOf(prefs.seenApps()) }
    // Back from Android's notification-access screen: re-read what changed there.
    LifecycleResumeEffect(Unit) {
        access = NotificationForwarder.hasAccess(context)
        seen = prefs.seenApps()
        onPauseOrDispose { }
    }

    Column(Modifier.padding(vertical = 6.dp)) {
        SettingsSwitchRow(
            title = "이 휴대폰 알림을 PC로 보내기",
            description = "연결한 PC에 이 휴대폰의 알림이 뜨고, 그 PC에 연결된 다른 휴대폰에도 전달됩니다. " +
                "인증 앱·비밀번호 관리자는 기본으로 빠집니다.",
            checked = enabled,
            onCheckedChange = {
                enabled = it
                prefs.setForwardEnabled(it)
            },
        )
        if (enabled && !access) {
            Text(
                "Android의 '알림 접근' 권한이 필요합니다. 아래 버튼에서 Homebase를 허용하세요.",
                style = MaterialTheme.typography.bodySmall,
                color = hb.danger,
            )
            TextButton(onClick = {
                context.startActivity(Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
            }) { Text("알림 접근 허용하기") }
        }
        if (enabled && access && seen.isNotEmpty()) {
            Text(
                "보낸 적 있는 앱 — 끄면 그 앱의 알림은 보내지 않습니다",
                style = MaterialTheme.typography.titleSmall,
                color = hb.ink,
                modifier = Modifier.padding(top = 8.dp),
            )
            seen.forEach { (pkg, label) ->
                SettingsSwitchRow(
                    title = label,
                    description = pkg,
                    checked = pkg !in excluded,
                    onCheckedChange = { on ->
                        prefs.setExcluded(pkg, !on)
                        excluded = prefs.excluded()
                    },
                )
            }
        }
        Text(
            "PC 목록 아래 '다른 기기'에서 PC에 연결된 다른 휴대폰·PC의 배터리·CPU·온라인 여부를 봅니다. " +
                "이 휴대폰의 배터리·네트워크 종류도 30분마다 PC에 알립니다.",
            style = MaterialTheme.typography.bodySmall,
            color = hb.inkSoft,
            modifier = Modifier.padding(top = 6.dp, bottom = 10.dp),
        )
    }
}
