package com.volcano.spentablet

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 双指手势状态机的集成级测试。
 *
 * 这里复现 [PenCanvasView.handleTrackpad] 里双指分支的判定顺序，
 * 验证「微小捏合不抢走双指滑动」这个核心需求在完整状态机下成立。
 *
 * 判定顺序（与 PenCanvasView 一致）：
 *  1. 累计跨度变化超过死区（相对 + 绝对双重条件）→ 进入捏合模式
 *  2. 捏合模式下若跨度回到起点附近 → 迟滞退出，交还给双指滑动
 *  3. 捏合模式 → 发缩放；否则 → 发双指滚动
 */
class TwoFingerGestureTest {

    /** 一次双指事件的处理结果 */
    private class Result(val pinched: Boolean, val scrolled: Boolean, val zoomFactor: Float)

    /**
     * 模拟一次双指 MOVE：两指从 (cx±span/2, cy) 出发，质心平移 (dx,dy)，跨度变为 newSpan。
     */
    private fun step(
        spanStart: Float,
        spanNow: Float,
        relThreshold: Float,
        absThresholdPx: Float,
        pinchMode: Boolean,
        scrollGate: MoveDeadZone,
        dx: Float,
        dy: Float
    ): Result {
        var pinched = pinchMode
        if (!pinched && PinchDeadZone.engaged(spanStart, spanNow, relThreshold, absThresholdPx)) {
            pinched = true
        }
        if (pinched && PinchDeadZone.released(spanStart, spanNow, absThresholdPx)) {
            pinched = false
            scrollGate.reset()
        }
        if (pinched) {
            // 捏合分支：发缩放（连续量）
            return Result(true, false, spanNow / (spanStart + 1f))
        }
        // 双指滑动分支：发滚动（受移动死区约束）
        val g = scrollGate.feed(dx, dy)
        return Result(false, !g.isZero, 1f)
    }

    @Test
    fun `双指滑动全程不被微小捏合抢走`() {
        val scrollGate = MoveDeadZone { 14f }   // 8dp ≈ 14px
        val spanStart = 200f
        var pinchMode = false

        // 用户在做「双指平移滚动」，两指间距只轻微抖动（±6px，远小于 15% 与 10dp）
        val spans = listOf(202f, 205f, 198f, 203f, 201f, 206f)
        var scrolled = 0
        for (s in spans) {
            val r = step(spanStart, s, 0.15f, 28f, pinchMode, scrollGate, 12f, 0f)
            pinchMode = r.pinched
            if (r.scrolled) scrolled++
            assertFalse("微小抖动不应触发捏合（span=$s）", r.pinched)
        }
        assertTrue("双指滑动应正常产生滚动", scrolled > 0)
    }

    @Test
    fun `真正的捏合能正常触发`() {
        val scrollGate = MoveDeadZone { 14f }
        val spanStart = 200f
        // 手指明显张开：span 200 → 320（+60%），远超 15% 与 10dp
        val r = step(spanStart, 320f, 0.15f, 28f, false, scrollGate, 0f, 0f)
        assertTrue("明显的捏合应判定为捏合", r.pinched)
        assertFalse("捏合模式下不应再发双指滚动", r.scrolled)
    }

    @Test
    fun `捏合中途回到起点会交还给双指滑动`() {
        val scrollGate = MoveDeadZone { 14f }
        val spanStart = 200f
        // 先捏到 320（进入捏合）
        val r1 = step(spanStart, 320f, 0.15f, 28f, false, scrollGate, 0f, 0f)
        assertTrue(r1.pinched)
        // 再回到 202（接近起点）→ 迟滞退出
        val r2 = step(spanStart, 202f, 0.15f, 28f, r1.pinched, scrollGate, 0f, 0f)
        assertFalse("回到起点附近应退出捏合", r2.pinched)
        // 退出后双指滑动恢复
        val r3 = step(spanStart, 205f, 0.15f, 28f, r2.pinched, scrollGate, 20f, 0f)
        assertFalse(r3.pinched)
        assertTrue("退出捏合后双指滑动应恢复", r3.scrolled)
    }

    @Test
    fun `两指靠得近时轻微变化不触发捏合`() {
        val scrollGate = MoveDeadZone { 14f }
        val spanStart = 50f   // 两指几乎贴在一起
        // 相对变化 20%（> 15%），但绝对变化只有 10px（< 28px 绝对阈值）→ 不判定
        val r = step(spanStart, 60f, 0.15f, 28f, false, scrollGate, 0f, 0f)
        assertFalse("绝对阈值应拦住小跨度的轻微变化", r.pinched)
        // 真正用力捏：变化 40px → 判定
        val r2 = step(spanStart, 90f, 0.15f, 28f, false, scrollGate, 0f, 0f)
        assertTrue("大幅捏合应判定为捏合", r2.pinched)
    }

    @Test
    fun `双指滚动的移动死区拦得住手抖`() {
        val scrollGate = MoveDeadZone { 14f }
        // 手抖：每次 2px，累计不到死区 → 不滚动
        var any = false
        for (i in 0 until 5) {
            val r = step(200f, 200f, 0.15f, 28f, false, scrollGate, 2f, 0f)
            if (r.scrolled) any = true
        }
        assertFalse("累计 10px（< 14px 死区）不应产生滚动", any)

        // 继续移动越过死区 → 开始滚动
        var scrolled = false
        for (i in 0 until 10) {
            val r = step(200f, 200f, 0.15f, 28f, false, scrollGate, 2f, 0f)
            if (r.scrolled) { scrolled = true; break }
        }
        assertTrue("越过死区后应产生滚动", scrolled)
    }
}
