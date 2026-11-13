package io.github.jeiel85.homebase.ui

import android.content.Context
import android.graphics.Bitmap
import android.graphics.ImageDecoder
import android.net.Uri
import android.os.Build
import android.provider.MediaStore
import android.provider.Settings
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.google.zxing.BarcodeFormat
import com.google.zxing.BinaryBitmap
import com.google.zxing.DecodeHintType
import com.google.zxing.MultiFormatReader
import com.google.zxing.RGBLuminanceSource
import com.google.zxing.common.HybridBinarizer
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import io.github.jeiel85.homebase.data.PcEntry
import io.github.jeiel85.homebase.data.PcStore
import io.github.jeiel85.homebase.net.HomebaseApi
import io.github.jeiel85.homebase.push.PushRegistrar
import io.github.jeiel85.homebase.net.PairException
import io.github.jeiel85.homebase.net.PairingOffer
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * QR pairing. Camera scan is the main path; reading the QR from an image (a screenshot sent to
 * the phone) and pasting the QR text are fallbacks for when the camera can't be used.
 */
@Composable
fun PairScreen(store: PcStore, api: HomebaseApi, onDone: (PcEntry) -> Unit, onBack: () -> Unit, link: String? = null) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var busy by remember { mutableStateOf(false) }
    var message by remember { mutableStateOf<String?>(null) }
    var pasted by remember { mutableStateOf("") }

    fun handle(text: String?) {
        if (text.isNullOrBlank()) return
        val offer = PairingOffer.parse(text)
        if (offer == null) {
            message = "Homebase 페어링 QR 코드가 아닙니다."
            return
        }
        busy = true
        message = "${offer.pcName}에 연결하는 중…"
        scope.launch {
            try {
                val (res, endpoint) = api.pair(offer, deviceName(context), "${Build.MANUFACTURER} ${Build.MODEL}")
                val pc = PcEntry(res.pcId, res.pcName, offer.endpoints, res.deviceId, res.token, endpoint.key)
                store.upsert(pc)
                PushRegistrar.ensureAsync(context, api, pc)
                onDone(pc)
            } catch (e: PairException) {
                message = e.message
            } catch (e: Exception) {
                message = e.message ?: "연결 실패"
            } finally {
                busy = false
            }
        }
    }

    // Opened from a pairing link: fill it in but let the user confirm — any web page can send a link.
    LaunchedEffect(link) {
        if (link == null) return@LaunchedEffect
        pasted = link
        message = PairingOffer.parse(link)?.let { "${it.pcName} 연결 링크를 열었습니다. 아래 '붙여넣은 코드로 연결'을 누르면 연결합니다." }
            ?: "Homebase 페어링 링크가 아니거나 손상되었습니다."
    }

    val scanner = rememberLauncherForActivityResult(ScanContract()) { result -> handle(result.contents) }
    val picker = rememberLauncherForActivityResult(ActivityResultContracts.PickVisualMedia()) { uri ->
        if (uri != null) {
            scope.launch {
                val text = withContext(Dispatchers.Default) { decodeQr(context, uri) }
                if (text == null) message = "이미지에서 QR 코드를 찾지 못했습니다." else handle(text)
            }
        }
    }

    val hb = Hb.colors
    Column(Modifier.fillMaxSize().background(hb.background)) {
        ConsoleBar(title = "PC 추가", onBack = onBack, eyebrow = "QR 페어링")
        Column(
            Modifier
                .fillMaxSize()
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 16.dp)
                .navigationBarsPadding()
                .imePadding(),
        ) {
            SectionLabel("연결 순서")
            Plate {
                Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                    Step(1, "PC 작업 표시줄의 Homebase 아이콘을 우클릭해 \"설정…\" → \"휴대폰\" 탭을 엽니다.")
                    Step(2, "아래 버튼을 눌러 PC 화면의 QR 코드를 비춥니다.")
                    Step(3, "연결되면 그 PC의 실시간 화면이 바로 열립니다.")
                    Text(
                        "QR 코드는 5분 동안 한 번만 쓸 수 있습니다. 만료되면 PC 창에서 새 코드를 받으세요.",
                        style = MaterialTheme.typography.bodySmall,
                        color = hb.inkSoft,
                    )
                }
            }
            Spacer(Modifier.height(16.dp))
            PairStatus(busy, message)
            SignalButton(
                "카메라로 QR 스캔",
                onClick = {
                    scanner.launch(
                        ScanOptions()
                            .setDesiredBarcodeFormats(ScanOptions.QR_CODE)
                            .setPrompt("PC 화면의 QR 코드를 비추세요")
                            .setBeepEnabled(false)
                            .setOrientationLocked(true),
                    )
                },
                enabled = !busy,
            )

            SectionLabel("카메라를 쓸 수 없을 때")
            QuietButton(
                "이미지에서 QR 읽기",
                onClick = { picker.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly)) },
                enabled = !busy,
            )
            Text(
                "PC에서 QR 화면을 캡처해 휴대폰으로 보낸 경우에 씁니다.",
                style = MaterialTheme.typography.bodySmall,
                color = hb.inkSoft,
                modifier = Modifier.padding(top = 6.dp, bottom = 14.dp),
            )
            OutlinedTextField(
                value = pasted,
                onValueChange = { pasted = it },
                label = { Text("QR 내용 붙여넣기") },
                textStyle = HbType.readout.copy(fontSize = 13.sp, fontWeight = FontWeight.Normal),
                modifier = Modifier.fillMaxWidth(),
                maxLines = 4,
            )
            Spacer(Modifier.height(8.dp))
            QuietButton("붙여넣은 코드로 연결", onClick = { handle(pasted) }, enabled = !busy && pasted.isNotBlank())
            Spacer(Modifier.height(24.dp))
        }
    }
}

@Composable
private fun Step(n: Int, text: String) {
    val hb = Hb.colors
    Row(verticalAlignment = Alignment.Top) {
        Box(
            Modifier.size(26.dp).clip(RoundedCornerShape(4.dp)).background(hb.slab),
            contentAlignment = Alignment.Center,
        ) { Text("$n", style = HbType.readout.copy(fontSize = 14.sp), color = hb.accent) }
        Spacer(Modifier.width(12.dp))
        Text(text, style = MaterialTheme.typography.bodyMedium, color = hb.ink, modifier = Modifier.padding(top = 2.dp))
    }
}

/** Progress while pairing; otherwise the last error, said plainly with what to do next. */
@Composable
private fun PairStatus(busy: Boolean, message: String?) {
    val hb = Hb.colors
    if (!busy && message == null) return
    Row(
        Modifier
            .fillMaxWidth()
            .padding(bottom = 12.dp)
            .clip(RoundedCornerShape(6.dp))
            .background(if (busy) hb.slab else hb.danger.copy(alpha = 0.14f))
            .padding(14.dp)
            .semantics { liveRegion = LiveRegionMode.Polite },
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (busy) {
            CircularProgressIndicator(Modifier.size(18.dp), color = hb.accent, strokeWidth = 2.dp)
        } else {
            Led(hb.ledOff, size = 7.dp)
        }
        Spacer(Modifier.width(10.dp))
        Text(message.orEmpty(), style = MaterialTheme.typography.bodyMedium, color = if (busy) hb.onSlab else hb.ink)
    }
}

private fun deviceName(context: Context): String =
    Settings.Global.getString(context.contentResolver, "device_name")?.takeIf { it.isNotBlank() }
        ?: "${Build.MANUFACTURER} ${Build.MODEL}"

/** Decodes the first QR code in an image, or null. */
internal fun decodeQr(context: Context, uri: Uri): String? = try {
    val bitmap: Bitmap = if (Build.VERSION.SDK_INT >= 28) {
        ImageDecoder.decodeBitmap(ImageDecoder.createSource(context.contentResolver, uri)) { decoder, _, _ ->
            decoder.allocator = ImageDecoder.ALLOCATOR_SOFTWARE
            decoder.isMutableRequired = false
        }
    } else {
        @Suppress("DEPRECATION")
        MediaStore.Images.Media.getBitmap(context.contentResolver, uri)
    }
    val pixels = IntArray(bitmap.width * bitmap.height)
    bitmap.getPixels(pixels, 0, bitmap.width, 0, 0, bitmap.width, bitmap.height)
    val source = RGBLuminanceSource(bitmap.width, bitmap.height, pixels)
    MultiFormatReader().decode(
        BinaryBitmap(HybridBinarizer(source)),
        mapOf(DecodeHintType.POSSIBLE_FORMATS to listOf(BarcodeFormat.QR_CODE), DecodeHintType.TRY_HARDER to true),
    ).text
} catch (_: Exception) {
    null
}
