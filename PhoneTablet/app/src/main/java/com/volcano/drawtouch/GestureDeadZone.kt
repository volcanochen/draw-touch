package com.volcano.drawtouch

import kotlin.math.abs
import kotlin.math.hypot

/**
 * 一次死区过滤后「真正应该发出去」的净位移。
 */
data class DeadZoneDelta(val dx: Float, val dy: Float) {
    val isZero: Boolean get() = dx == 0f && dy == 0f
}

/**
 * 位移死区闸门（纯逻辑，无 Android 依赖，便于单元测试）。
 *
 * 手势开始后，累计位移在阈值以内时**不产生任何输出**（手抖不会误触）；
 * 累计位移越过阈值的那一刻才输出，之后 1:1 跟手。
 *
 * 两种输出策略：
 *  - [catchUp] = false（相对移动用，如光标、滚动）：越过阈值时只输出「超出阈值的部分」，
 *    死区内那点位移被吃掉，起步不会突然跳一大段。
 *  - [catchUp] = true（绝对映射用，如拖动操作框）：越过阈值时输出**完整累计量**，
 *    保证「框」和「手指」始终严格 1:1 对齐，不会产生固定偏移。
 */
class MoveDeadZone(
    private val catchUp: Boolean = false,
    private val thresholdProvider: () -> Float
) {

    /** 累计位移（px）。由外部提供阈值，随设置实时变化。 */
    private var accumX = 0f
    private var accumY = 0f

    /** 是否已经越过阈值（越过后 1:1 跟随，不再吃死区） */
    private var started = false

    /** 开始新手势时调用：清空累计量，死区重新生效 */
    fun reset() {
        accumX = 0f
        accumY = 0f
        started = false
    }

    /** 当前是否已经越过死区 */
    fun isStarted(): Boolean = started

    /**
     * 喂入本帧位移，返回真正应该输出的位移。
     * 阈值 ≤ 0 时等价于「无死区」，直接透传。
     */
    fun feed(dx: Float, dy: Float): DeadZoneDelta {
        val threshold = thresholdProvider()
        if (started || threshold <= 0f) {
            started = true
            return DeadZoneDelta(dx, dy)
        }
        accumX += dx
        accumY += dy
        val dist = hypot(accumX, accumY)
        if (dist <= threshold) return DeadZoneDelta(0f, 0f)
        if (dist <= 1e-6f) return DeadZoneDelta(0f, 0f)
        started = true
        if (catchUp) {
            // 补回死区内的位移：累计量全量输出，保持与手指 1:1
            val out = DeadZoneDelta(accumX, accumY)
            accumX = 0f
            accumY = 0f
            return out
        }
        // 只输出「超出死区的部分」，方向沿累计运动方向
        val over = dist - threshold
        val ux = accumX / dist
        val uy = accumY / dist
        accumX = 0f
        accumY = 0f
        return DeadZoneDelta(ux * over, uy * over)
    }
}

/**
 * 捏合判定死区（纯逻辑）。
 *
 * 只有**同时**满足下面两个条件才算「捏合」：
 *  1. 跨度相对变化 > 相对阈值（默认 15%）
 *  2. 跨度绝对变化 > 绝对阈值（默认 6dp）
 *
 * 为什么要绝对阈值：只用相对阈值时，两根手指靠得近（比如 span 只有 60px），
 * 15% 才 9px，手指轻微抖动就会被误判成捏合，把本该是「双指滑动」的手势抢走。
 * 加上绝对阈值后，微小的捏合不会干扰双指滑动；真正用力捏（跨度变化几十上百 px）仍能正常触发。
 */
object PinchDeadZone {

    /**
     * @param startSpan 手势开始时的双指跨度（px）
     * @param span 当前双指跨度（px）
     * @param relThreshold 相对阈值（比例，0.15 = 15%）
     * @param absThresholdPx 绝对阈值（px）
     */
    @JvmStatic
    fun engaged(startSpan: Float, span: Float, relThreshold: Float, absThresholdPx: Float): Boolean {
        if (startSpan <= 1f) return false
        val delta = abs(span - startSpan)
        if (delta < absThresholdPx) return false
        return delta / startSpan > relThreshold
    }

    /**
     * 捏合迟滞退出：跨度回落到起始值附近（绝对阈值的一半以内）时，
     * 交还给「双指滑动」，避免一次微小的捏合把整个手势都吃掉。
     */
    @JvmStatic
    fun released(startSpan: Float, span: Float, absThresholdPx: Float): Boolean {
        if (startSpan <= 1f) return false
        return abs(span - startSpan) < absThresholdPx * 0.5f
    }
}
