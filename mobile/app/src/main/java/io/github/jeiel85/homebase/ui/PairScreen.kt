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
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
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
import io.github.jeiel85.homebase.net.PairException
import io.github.jeiel85.homebase.net.PairingOffer
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * QR pairing. Camera scan is the main path; reading the QR from an image (a screenshot sent to
 * the phone) and pasting the QR text are fallbacks for when the camera can't be used.
 */
@OptIn(ExperimentalMaterial3Api::class)
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

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("PC 추가") },
                navigationIcon = {
                    IconButton(onClick = onBack) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "뒤로") }
                },
            )
        },
    ) { padding ->
        Column(
            Modifier.fillMaxSize().padding(padding).padding(16.dp).verticalScroll(rememberScrollState()),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            Text(
                "PC의 트레이 아이콘 메뉴에서 \"Connect Phone…\"을 열고 표시된 QR 코드를 스캔하세요. 코드는 5분간 한 번만 쓸 수 있습니다.",
                style = MaterialTheme.typography.bodyMedium,
            )
            Button(
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
                modifier = Modifier.fillMaxWidth(),
            ) { Text("카메라로 QR 스캔") }
            OutlinedButton(
                onClick = { picker.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly)) },
                enabled = !busy,
                modifier = Modifier.fillMaxWidth(),
            ) { Text("이미지에서 QR 읽기") }

            OutlinedTextField(
                value = pasted,
                onValueChange = { pasted = it },
                label = { Text("또는 QR 내용 붙여넣기") },
                modifier = Modifier.fillMaxWidth(),
                maxLines = 4,
            )
            OutlinedButton(onClick = { handle(pasted) }, enabled = !busy && pasted.isNotBlank(), modifier = Modifier.fillMaxWidth()) {
                Text("붙여넣은 코드로 연결")
            }

            Row(verticalAlignment = Alignment.CenterVertically) {
                if (busy) CircularProgressIndicator(Modifier.size(20.dp).padding(end = 8.dp))
                message?.let { Text(it, style = MaterialTheme.typography.bodyMedium) }
            }
        }
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
