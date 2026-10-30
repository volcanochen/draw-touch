package com.volcano.drawtouch

import android.content.Context
import android.media.AudioAttributes
import android.os.Build
import android.os.VibrationEffect
import android.os.Vibrator
import android.os.VibratorManager

/**
 * 触摸反馈（马达震动）统一管理。
 *
 * 设计原则：**轻、短、有区分**——
 *  · 点击类 = 一次短促的「哒」，模拟鼠标微动开关的手感；
 *  · 手势识别成功 = 稍长一点，告诉你「系统已经认出你在做什么」；
 *  · 滚动 = 极轻的连续细震（带节流），像高端触摸板的段落感，但不会变成嗡嗡响；
 *  · 重按按下 = 明显更重的一下，松开 = 更轻更短，形成「按下/抬起」的非对称手感。
 *
 * 两项可调（设置页）：
 *  · [strengthScale]：整体强度倍率（0~200%）。
 *  · [ignoreSystemLimit]：**覆盖系统对震动的限制**——见下面「为什么要换通道」。
 *
 * ### 为什么要「覆盖系统限制」
 * Android 12+ 系统设置里有「触摸反馈强度 / 震动强度」滑条，系统会按那个比例**缩放**我们
 * 请求的震动幅度（`Settings.System.HAPTIC_FEEDBACK_INTENSITY`），用户调低时我们再怎么加码
 * 也上不去。绕开办法是换一个 usage 通道：
 *  · 默认通道 `USAGE_ASSISTANCE_SONIFICATION` —— 受系统触觉强度缩放；
 *  · 强制通道 `USAGE_ALARM` —— 走闹钟/提醒那条通道，**不受触觉强度缩放**（有自己的音量档）。
 * 打开开关即切到强制通道，震动幅度就是真正的 255，绕过系统限制。
 */
object Haptics {

    /** 总开关（设置页「震动反馈」）。关掉后所有震动都是空操作。 */
    @Volatile
    var enabled: Boolean = true

    /** 整体强度倍率（0~2.0，1.0 = 原始强度）。设置页可调 0%~200%。 */
    @Volatile
    var strengthScale: Float = 1.0f

    /** 是否绕过系统的触觉强度限制（改走 USAGE_ALARM 通道）。 */
    @Volatile
    var ignoreSystemLimit: Boolean = false

    private var vibrator: Vibrator? = null
    private var hasAmplitudeControl: Boolean = false

    /** 在 Activity 里调一次拿 Vibrator 实例。 */
    fun init(context: Context) {
        try {
            vibrator = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
                val vm = context.getSystemService(Context.VIBRATOR_MANAGER_SERVICE) as? VibratorManager
                vm?.defaultVibrator
            } else {
                @Suppress("DEPRECATION")
                context.getSystemService(Context.VIBRATOR_SERVICE) as? Vibrator
            }
            hasAmplitudeControl = vibrator?.hasAmplitudeControl() ?: false
        } catch (_: Exception) {
            vibrator = null
        }
    }

    /** 按倍率换算最终幅度（1~255）。 */
    private fun scaled(base: Int): Int {
        val v = (base * strengthScale).toInt()
        return v.coerceIn(1, 255)
    }

    private fun fire(ms: Long, base: Int) {
        if (!enabled) return
        val v = vibrator ?: return
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                val amp = if (hasAmplitudeControl) scaled(base) else VibrationEffect.DEFAULT_AMPLITUDE
                val effect = VibrationEffect.createOneShot(ms, amp)
                if (ignoreSystemLimit) {
                    // 强制通道：USAGE_ALARM 不受系统「触觉反馈强度」缩放
                    v.vibrate(effect, ALARM_ATTRS)
                } else {
                    v.vibrate(effect, SONIFICATION_ATTRS)
                }
            } else {
                @Suppress("DEPRECATION")
                v.vibrate(ms)
            }
        } catch (_: Exception) {
            // 震动失败绝不能影响手势主流程
        }
    }

    private val SONIFICATION_ATTRS: AudioAttributes = AudioAttributes.Builder()
        .setUsage(AudioAttributes.USAGE_ASSISTANCE_SONIFICATION)
        .setContentType(AudioAttributes.CONTENT_TYPE_SONIFICATION)
        .build()

    private val ALARM_ATTRS: AudioAttributes = AudioAttributes.Builder()
        .setUsage(AudioAttributes.USAGE_ALARM)
        .setContentType(AudioAttributes.CONTENT_TYPE_SONIFICATION)
        .build()

    // ---------- 各种反馈（base = 1~255 的强度基准，可被 strengthScale 整体缩放）----------

    /** 鼠标点击（单击 / 双击 / 左中右键）：短促的「哒」 */
    fun click() = fire(18, 150)

    /** 手势被识别出来（捏合 / 滚动 / 单指跟随 / 多指滑动）：确认感 */
    fun gestureStart() = fire(16, 175)

    /** 一次性的手势动作（音量、任务切换、后退前进…）比 gestureStart 稍重一点 */
    fun action() = fire(22, 195)

    /** 双击按住拖动开始（左键已按下） */
    fun dragStart() = fire(24, 200)

    /** 拖动结束（左键松开） */
    fun dragEnd() = fire(10, 110)

    /** 重按 → 按下左键：明显更重的一下 */
    fun pressDown() = fire(28, 255)

    /** 重按 → 松开左键：更轻更短 */
    fun pressUp() = fire(10, 120)

    /**
     * 滚动中的细震（带节流）：按时间节流，避免变成持续嗡嗡声。
     * 双指滚动与右侧虚拟滚轮条共用同一套反馈，手感一致。
     */
    private var lastScrollTick = 0L
    fun scrollTick(minIntervalMs: Long = SCROLL_TICK_INTERVAL_MS) {
        val now = android.os.SystemClock.uptimeMillis()
        if (now - lastScrollTick < minIntervalMs) return
        lastScrollTick = now
        fire(9, 105)
    }

    /** 重置节流（新手势开始时调，第一次滚动立刻有反馈） */
    fun resetScrollTick() { lastScrollTick = 0L }

    private const val SCROLL_TICK_INTERVAL_MS = 45L
}
