package io.github.jeiel85.homebase.push

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.util.Log
import io.github.jeiel85.homebase.data.PcStore
import io.github.jeiel85.homebase.net.HomebaseApi
import io.github.jeiel85.homebase.net.NetworkProbe
import io.github.jeiel85.homebase.net.UnauthorizedException
import io.github.jeiel85.homebase.net.UnconfirmedException
import kotlinx.coroutines.launch
import java.io.IOException

/**
 * Allow / Deny tapped on a Claude Code permission prompt (DECISIONS M-48): sends the answer to the
 * PC that asked and replaces the notification with the outcome. Only this app's own notification
 * actions reach it (not exported).
 */
class ApprovalActionReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != ACTION) return
        val pcId = intent.getStringExtra(EXTRA_PC_ID) ?: return
        val approvalId = intent.getStringExtra(EXTRA_APPROVAL_ID) ?: return
        val decision = intent.getStringExtra(EXTRA_DECISION)?.takeIf { it == ALLOW || it == DENY } ?: return
        val notificationId = intent.getIntExtra(EXTRA_NOTIFICATION_ID, 0)
        val app = context.applicationContext
        val pending = goAsync()
        PushRegistrar.scope.launch {
            val text = try {
                val pc = PcStore(app).get(pcId)
                if (pc == null) {
                    "이 PC가 목록에 없어 답하지 못했습니다."
                } else {
                    // A broadcast may run ~10 s. The budget bounds the blocking call itself (a coroutine
                    // timeout could not stop it, and a late success would then read as a failure).
                    val accepted = HomebaseApi(networks = NetworkProbe(app)::current)
                        .answerApproval(pc, approvalId, decision, budgetMillis = 8_000)
                    when {
                        accepted -> if (decision == ALLOW) "허용했습니다." else "거부했습니다."
                        else -> "이미 답했거나 PC가 더는 기다리지 않습니다."
                    }
                }
            } catch (e: UnauthorizedException) {
                "이 PC와의 연결이 끊겼습니다. 다시 페어링해 주세요."
            } catch (e: UnconfirmedException) {
                // The answer may have reached the PC just as the time ran out.
                "PC가 답을 받았는지 확인하지 못했습니다. PC에서 확인해 주세요."
            } catch (e: IOException) {
                Log.w(TAG, "approval answer failed: ${e.message}")
                "PC에 닿지 않았습니다. PC에서 답해 주세요."
            } catch (e: Exception) {
                Log.w(TAG, "approval answer failed: ${e.javaClass.simpleName}")
                "답을 보내지 못했습니다. PC에서 답해 주세요."
            }
            try {
                PushNotifier.showApprovalResult(app, notificationId, text)
            } finally {
                pending.finish()
            }
        }
    }

    companion object {
        const val ACTION = "io.github.jeiel85.homebase.APPROVAL_ANSWER"
        const val EXTRA_PC_ID = "pcId"
        const val EXTRA_APPROVAL_ID = "approvalId"
        const val EXTRA_DECISION = "decision"
        const val EXTRA_NOTIFICATION_ID = "notificationId"
        const val ALLOW = "allow"
        const val DENY = "deny"
        private const val TAG = "HomebaseApproval"
    }
}
