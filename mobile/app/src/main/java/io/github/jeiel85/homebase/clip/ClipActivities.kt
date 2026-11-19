package io.github.jeiel85.homebase.clip

import android.annotation.SuppressLint
import android.app.PendingIntent
import android.content.ClipData
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.core.content.IntentCompat
import io.github.jeiel85.homebase.data.PcEntry
import io.github.jeiel85.homebase.share.FileSendFlow
import io.github.jeiel85.homebase.data.PcStore
import io.github.jeiel85.homebase.net.HomebaseApi
import io.github.jeiel85.homebase.net.NetworkProbe
import io.github.jeiel85.homebase.ui.HomebaseTheme
import io.github.jeiel85.homebase.ui.ThemeSetting
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * "Homebase" in the share sheet: text goes onto a PC's clipboard (DECISIONS M-62), files and pictures are sent to
 * it (M-64). Always one tap on a PC, even with only one paired: any app can open this with any text or file, and
 * only the user should decide that it lands on a PC.
 */
class ClipShareActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        ThemeSetting.load(this)
        super.onCreate(savedInstanceState)
        val streams = sharedStreams(intent)
        if (streams.isNotEmpty()) {
            setContent {
                HomebaseTheme(dark = ThemeSetting.mode.isDark(isSystemInDarkTheme())) {
                    FileSendFlow(streams, presetPcId = null, pickAlways = true, onDone = { finish() })
                }
            }
            return
        }
        val text = if (intent?.action == Intent.ACTION_SEND) intent.getCharSequenceExtra(Intent.EXTRA_TEXT)?.toString() else null
        if (text.isNullOrEmpty()) {
            toast(this, "보낼 텍스트가 없습니다")
            finish()
            return
        }
        setContent {
            HomebaseTheme(dark = ThemeSetting.mode.isDark(isSystemInDarkTheme())) {
                ClipSendFlow(text, sensitive = false, pickAlways = true, onDone = { finish() })
            }
        }
    }
}

/**
 * Not exported.
 * - [ACTION_SEND_CLIPBOARD] (the quick settings tile): reads the clipboard once this window has focus — Android
 *   10+ allows nothing earlier — and sends it to the PC used last (asking when there is none): a picture as a file
 *   (M-64), text onto the PC's clipboard (M-62).
 * - [ACTION_FETCH] (a "long text" notification): fetches what the PC kept and copies it.
 * - [ACTION_SEND_FILES] (a PC card's "파일 보내기"): the picked files, in the intent's ClipData, to [EXTRA_PC_ID].
 */
class ClipActionActivity : ComponentActivity() {
    private var clip by mutableStateOf<ClipText?>(null)
    private var picture by mutableStateOf<Uri?>(null)
    private var asked = false

    override fun onCreate(savedInstanceState: Bundle?) {
        ThemeSetting.load(this)
        super.onCreate(savedInstanceState)
        val action = intent?.action
        val pcId = intent?.getStringExtra(EXTRA_PC_ID)
        val clipId = intent?.getStringExtra(EXTRA_CLIP_ID)
        val picked = if (action == ACTION_SEND_FILES) clipUris(intent?.clipData) else emptyList()
        val valid = when (action) {
            ACTION_SEND_CLIPBOARD -> true
            ACTION_FETCH -> pcId != null && clipId != null
            ACTION_SEND_FILES -> pcId != null && picked.isNotEmpty()
            else -> false
        }
        if (!valid) {
            finish()
            return
        }
        setContent {
            HomebaseTheme(dark = ThemeSetting.mode.isDark(isSystemInDarkTheme())) {
                when {
                    action == ACTION_FETCH && pcId != null && clipId != null -> ClipFetchFlow(pcId, clipId, onDone = { finish() })
                    action == ACTION_SEND_FILES -> FileSendFlow(picked, presetPcId = pcId, pickAlways = false, onDone = { finish() })
                    else -> {
                        val image = picture
                        val text = clip
                        if (image != null) {
                            FileSendFlow(listOf(image), presetPcId = null, pickAlways = false, onDone = { finish() })
                        } else if (text != null) {
                            ClipSendFlow(text.text, text.sensitive, pickAlways = false, onDone = { finish() })
                        }
                    }
                }
            }
        }
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (!hasFocus || asked || intent?.action != ACTION_SEND_CLIPBOARD) return
        asked = true
        // A copied picture goes as a file (M-64); anything else is the text path of M-62.
        val image = PhoneClipboard.readImage(this)
        if (image != null) {
            picture = image
            return
        }
        val read = PhoneClipboard.read(this)
        if (read == null) {
            toast(this, "클립보드에 보낼 텍스트나 사진이 없습니다")
            finish()
        } else {
            clip = read
        }
    }

    companion object {
        const val ACTION_SEND_CLIPBOARD = "io.github.jeiel85.homebase.action.SEND_CLIPBOARD"
        const val ACTION_FETCH = "io.github.jeiel85.homebase.action.FETCH_CLIP"
        const val ACTION_SEND_FILES = "io.github.jeiel85.homebase.action.SEND_FILES"
        const val EXTRA_PC_ID = "io.github.jeiel85.homebase.CLIP_PC_ID"
        const val EXTRA_CLIP_ID = "io.github.jeiel85.homebase.CLIP_ID"
    }
}

/** Quick settings tile "클립보드 → PC": the clipboard goes to the PC used last, in two taps from anywhere. */
class ClipTileService : TileService() {
    override fun onStartListening() {
        super.onStartListening()
        qsTile?.let {
            it.state = Tile.STATE_INACTIVE
            it.updateTile()
        }
    }

    // The Intent overload is the only one before Android 14 and throws from 14 on; each is used on its own side.
    @SuppressLint("StartActivityAndCollapseDeprecated")
    override fun onClick() {
        super.onClick()
        val intent = Intent(this, ClipActionActivity::class.java)
            .setAction(ClipActionActivity.ACTION_SEND_CLIPBOARD)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        if (Build.VERSION.SDK_INT >= 34) {
            startActivityAndCollapse(
                PendingIntent.getActivity(this, 0, intent, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT),
            )
        } else {
            @Suppress("DEPRECATION")
            startActivityAndCollapse(intent)
        }
    }
}

/**
 * Picks a PC (unless [pickAlways] is off and [ClipShare.autoTarget] names one), sends, toasts the outcome
 * and calls [onDone].
 */
@Composable
private fun ClipSendFlow(text: String, sensitive: Boolean, pickAlways: Boolean, onDone: () -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val api = remember { HomebaseApi(networks = NetworkProbe(context)::current) }
    var choices by remember { mutableStateOf<List<PcEntry>?>(null) }
    var sending by remember { mutableStateOf<PcEntry?>(null) }

    fun send(pc: PcEntry) {
        if (sending != null) return
        sending = pc
        scope.launch {
            toast(context, ClipShare.send(context, api, pc, text, sensitive))
            onDone()
        }
    }

    LaunchedEffect(Unit) {
        ClipShare.problem(text)?.let {
            toast(context, it)
            onDone()
            return@LaunchedEffect
        }
        // The PC list is decrypted with a Keystore key: off the main thread.
        val pcs = withContext(Dispatchers.IO) { PcStore(context).all() }
        if (pcs.isEmpty()) {
            toast(context, "연결된 PC가 없습니다. 먼저 앱에서 PC를 추가하세요.")
            onDone()
            return@LaunchedEffect
        }
        val last = ClipShare.lastPcId(context)
        val direct = if (pickAlways) null else ClipShare.autoTarget(pcs, last)
        if (direct != null) send(direct) else choices = ClipShare.ordered(pcs, last)
    }

    val list = choices
    if (list != null) {
        AlertDialog(
            onDismissRequest = { if (sending == null) onDone() },
            title = { Text("PC 클립보드로 보내기") },
            text = {
                Column {
                    Text(
                        if (sensitive) "민감한 내용 (${text.length}자)" else ClipNotifier.preview(text),
                        style = MaterialTheme.typography.bodySmall,
                        maxLines = 3,
                        overflow = TextOverflow.Ellipsis,
                    )
                    Spacer(Modifier.height(12.dp))
                    Column(Modifier.heightIn(max = 320.dp).verticalScroll(rememberScrollState())) {
                        list.forEach { pc ->
                            TextButton(onClick = { send(pc) }, enabled = sending == null, modifier = Modifier.fillMaxWidth()) {
                                Text(if (sending == pc) "${pc.displayName} — 보내는 중…" else pc.displayName)
                            }
                        }
                    }
                }
            },
            confirmButton = {},
            dismissButton = {
                TextButton(onClick = onDone, enabled = sending == null) { Text("취소") }
            },
        )
    } else if (sending != null) {
        AlertDialog(
            onDismissRequest = {},
            text = { Text("${sending?.displayName}에 보내는 중…") },
            confirmButton = {},
        )
    }
}

/** Fetches the long text a PC offered, copies it, toasts and calls [onDone]. */
@Composable
private fun ClipFetchFlow(pcId: String, clipId: String, onDone: () -> Unit) {
    val context = LocalContext.current
    val api = remember { HomebaseApi(networks = NetworkProbe(context)::current) }
    LaunchedEffect(Unit) {
        toast(context, ClipShare.fetch(context, api, pcId, clipId))
        onDone()
    }
    AlertDialog(onDismissRequest = {}, text = { Text("PC에서 텍스트를 받는 중…") }, confirmButton = {})
}

private fun toast(context: Context, text: String) = Toast.makeText(context.applicationContext, text, Toast.LENGTH_SHORT).show()

/** The URIs in [clip], in order. */
internal fun clipUris(clip: ClipData?): List<Uri> =
    if (clip == null) emptyList() else (0 until clip.itemCount).mapNotNull { clip.getItemAt(it).uri }

/**
 * The files a share intent carries (M-64): `EXTRA_STREAM` of `ACTION_SEND` (one) or `ACTION_SEND_MULTIPLE` (a
 * list), else the URIs of its ClipData, which some apps fill instead. Empty for a text share.
 */
private fun sharedStreams(intent: Intent?): List<Uri> {
    if (intent == null) return emptyList()
    val streams = when (intent.action) {
        Intent.ACTION_SEND -> listOfNotNull(IntentCompat.getParcelableExtra(intent, Intent.EXTRA_STREAM, Uri::class.java))
        Intent.ACTION_SEND_MULTIPLE -> IntentCompat.getParcelableArrayListExtra(intent, Intent.EXTRA_STREAM, Uri::class.java).orEmpty()
        else -> emptyList()
    }
    if (streams.isNotEmpty()) return streams
    return if (intent.action == Intent.ACTION_SEND_MULTIPLE) clipUris(intent.clipData) else emptyList()
}
