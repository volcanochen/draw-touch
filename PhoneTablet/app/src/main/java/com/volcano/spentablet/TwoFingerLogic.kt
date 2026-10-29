package com.volcano.spentablet

import kotlin.math.abs
import kotlin.math.max
import kotlin.math.min

/**
 * 双指手势的纯判定逻辑（无 Android 依赖，便于单元测试）。
 *
 * 用户规格（2026-10-29）：
 *  · 滚动方向**三态**：明显偏竖 → 锁竖轴，之后只出纵向；明显偏横 → 锁横轴，之后只出横向；
 *    斜向（两轴分量相当，允许一点偏角）→ 不锁轴，横纵同时输出。
 *  · 捏合判定（2026-10-29 下午放宽）：两指**沿同一直线反向运动**即判捏合——
 *    幅度不必对称（一指动 20px、另一指动 5px 也算），只要不是一指静止；
 *    判成捏合后**过程只看两指间距**（输出缩放），不再要求维持运动、也不回退到滚动；
 *  · 只有一指动时忽略静止指、退化为单指滑动；
 *  · 进入移动识别（滚动 / 单指跟随）后，本手势不再处理缩放。
 */
object TwoFingerLogic {

    /** 轴锁三态：斜向（未锁，双轴输出） / 锁横轴 / 锁竖轴 */
    const val AXIS_FREE = 0
    const val AXIS_X = 1
    const val AXIS_Y = 2

    /**
     * 由「自双指手势开始以来的累计质心位移」判定轴锁三态。
     * 已锁定的轴由调用方固定、不再重判；本函数只做单次判定。
     *
     * @param totalDx 累计横向位移（px）
     * @param totalDy 累计纵向位移（px）
     * @param minPx 判定门槛：两轴累计都小于它时不判定（返回 [AXIS_FREE]，双轴透传）
     * @param bias 方向偏置：竖向要超过横向的 bias 倍才判竖，横向同理（横滑手带一点
     *             上下漂移不误判）；两轴都在彼此的 [1/bias, bias] 倍以内 → 斜向 FREE
     */
    fun decideAxis(totalDx: Float, totalDy: Float, minPx: Float, bias: Float): Int {
        val adx = abs(totalDx)
        val ady = abs(totalDy)
        if (max(adx, ady) < minPx) return AXIS_FREE
        if (ady > adx * bias) return AXIS_Y
        if (adx > ady * bias) return AXIS_X
        return AXIS_FREE
    }

    /**
     * 同上，但把「位移还不够、无法判定」与「判为斜向 FREE」区分开（返回 null）。
     *
     * 为什么必须区分：[AXIS_FREE] = 0 同时被当成「还没判」和「判成斜向」，
     * 调用方就会每帧重判 —— 斜着滑时手指稍微偏一点竖/横就被锁成单轴，
     * 表现为「起手能斜着走，滑一小段就锁死成横向或纵向」（2026-10-30 用户反馈）。
     * 正确逻辑：位移越过门槛时**只判定一次**，判到什么就锁到什么（含斜向 FREE），
     * 之后本次手势不再改判 —— 激活了斜向就该一直能全方向移动。
     *
     * @return null = 位移未达 [minPx]，尚不能判定（调用方应继续等待）；
     *         否则 = [AXIS_X] / [AXIS_Y] / [AXIS_FREE]，本次判定的最终结果。
     */
    fun decideAxisOrNull(totalDx: Float, totalDy: Float, minPx: Float, bias: Float): Int? {
        val adx = abs(totalDx)
        val ady = abs(totalDy)
        if (max(adx, ady) < minPx) return null
        if (ady > adx * bias) return AXIS_Y
        if (adx > ady * bias) return AXIS_X
        return AXIS_FREE
    }

    /**
     * 两指是否**都**有位移（捏合判定的前提）。
     *
     * @param accumA 手指 A 自手势开始的累计位移（px）
     * @param accumB 手指 B 自手势开始的累计位移（px）
     * @param bothMinPx 「有位移」的最低累计量（px）
     */
    fun bothMoving(accumA: Float, accumB: Float, bothMinPx: Float): Boolean =
        min(accumA, accumB) >= bothMinPx

    /**
     * 捏合判定：两指沿同一直线**反向**运动。幅度不必对称——只要两指都有位移
     * （各累计 ≥ [minPx]）且位移向量点积为负（方向相反、夹角大于 90°）即为捏合。
     * 反向运动时质心几乎不动，所以这个判定不会与「质心位移」驱动的滚动判定打架。
     *
     * @param vecAX/vecAY 手指 A 自手势开始的累计位移向量（px）
     * @param vecBX/vecBY 手指 B 自手势开始的累计位移向量（px）
     * @param minPx 每指「有位移」的最低累计量（px）
     */
    fun opposing(
        vecAX: Float, vecAY: Float,
        vecBX: Float, vecBY: Float,
        minPx: Float,
    ): Boolean {
        val magA = hypot(vecAX, vecAY)
        val magB = hypot(vecBX, vecBY)
        if (magA < minPx || magB < minPx) return false
        return vecAX * vecBX + vecAY * vecBY < 0f
    }

    /**
     * 单指跟随判定：一指基本没动（累计位移低于 bothMinPx）、另一指已明显移动
     * （累计位移达到 moveMinPx）→ 忽略静止指，按单指滑动（移动光标）处理。
     */
    fun singleFingerFollow(accumA: Float, accumB: Float, bothMinPx: Float, moveMinPx: Float): Boolean {
        if (bothMoving(accumA, accumB, bothMinPx)) return false
        return max(accumA, accumB) >= moveMinPx
    }

    private fun hypot(x: Float, y: Float): Float = kotlin.math.hypot(x.toDouble(), y.toDouble()).toFloat()
}
