package io.github.jeiel85.homebase.clip

import android.content.Context
import android.widget.Toast
import io.github.jeiel85.homebase.data.PcEntry
import io.github.jeiel85.homebase.net.HomebaseApi
import io.github.jeiel85.homebase.net.NetworkProbe
import io.github.jeiel85.homebase.share.UploadJobs
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * Sends text from a share sheet PC shortcut without the share center (docs/QUICK_SEND.md §4.2), after the screen is
 * gone — like the share center's own text send. A toast says how it went; a failure becomes a notification with
 * [다시 보내기] and [다른 기기로], so the text is not lost.
 */
internal object QuickSender {
    fun start(context: Context, pc: PcEntry, text: String) {
        val app = context.applicationContext
        UploadJobs.scope.launch {
            val api = HomebaseApi(networks = NetworkProbe(app)::current)
            val sent = ClipShare.sendQuick(app, api, pc, text)
            withContext(Dispatchers.Main) {
                if (sent.failed) {
                    ClipNotifier.quickFailed(app, pc.pcId, pc.displayName, text, sent.line)
                } else {
                    ClipNotifier.cancelQuickFailed(app, pc.pcId, text)
                }
                Toast.makeText(app, sent.line, Toast.LENGTH_LONG).show()
            }
        }
    }
}
