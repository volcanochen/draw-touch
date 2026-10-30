package com.volcano.drawtouch

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import kotlin.math.abs

/**
 * 双指手势状态机的集成级测试。
 *
 * 这里复现 [PenCanvasView.handleTrackpad] 双指分支的**三态状态机**
 * （2026-10-29 下午修订规格）：
 *  · 滚动方向三态：近似竖锁竖 / 近似横锁横 / 斜向双轴同时输出；
 *  · 捏合判定放宽：两指**沿同一直线反向运动**即判捏合——幅度不必对称（只要不是
 *    一指静止）；判成捏合后本手势固定，过程只看两指间距；
 *  · **缩放手停死区**：单帧跨度变化 <0.5% 视为手指已停，不发缩放帧
 *    （停手后的微抖会让 PC 端缩放余量随机游走，画面还在动 —— 2026-10-29 反馈）；
 *  · 判定优先级：捏合随时可判最优先；滚动 / 单指跟随要等**启动窗口**
 *    （150ms ≈ SM 的 10 帧）过后才允许锁定；
 *  · 只有一指动 → 退化为单指跟随；
 *  · 进入移动识别（滚动 / 单指跟随）后，本手势不再处理缩放。
 *
 * SM 每帧按 16ms 计（60Hz 触摸采样），窗口 150ms ≈ 10 帧。
 */
class TwoFingerGestureTest {

    /** 复刻 PenCanvasView 双指分支的小型状态机（判定顺序严格一致） */
    private class SM {
        var state = 0          // 0 未定 / 1 缩放 / 2 滚动 / 3 单指跟随
        var axis = 0           // 0 斜向(双轴) / 1 横锁 / 2 竖锁
        private var accumA = 0f
        private var accumB = 0f
        private var vecAX = 0f; private var vecAY = 0f
        private var vecBX = 0f; private var vecBY = 0f
        private var frameIdx = 0L          // 每帧按 16ms 计
        private var lastSpan = 200f        // 上一帧跨度（缩放 factor 与手停死区用）
        var lastZoomed = false
        var lastScrolled = false
        var lastMoved = false
        private val scrollEmitter = ScrollEmitter { 14f }   // 8dp ≈ 14px
        private val moveGate = MoveDeadZone { 14f }

        fun frame(
            dAx: Float, dAy: Float, dBx: Float, dBy: Float,  // 本帧两指各自位移向量
            centroidDx: Float, centroidDy: Float,            // 本帧质心位移
            totalCx: Float, totalCy: Float,                  // 自双指手势开始的累计质心位移
            spanNow: Float = lastSpan,                       // 本帧两指跨度
        ) {
            frameIdx++
            accumA += kotlin.math.hypot(dAx.toDouble(), dAy.toDouble()).toFloat()
            accumB += kotlin.math.hypot(dBx.toDouble(), dBy.toDouble()).toFloat()
            vecAX += dAx; vecAY += dAy
            vecBX += dBx; vecBY += dBy
            if (state == 0) {
                val both = TwoFingerLogic.bothMoving(accumA, accumB, 8f)
                val windowOver = frameIdx * 16L > 150L   // 启动窗口 150ms
                when {
                    // 捏合（反向运动）随时可判、最优先；不看跨度死区
                    both && TwoFingerLogic.opposing(vecAX, vecAY, vecBX, vecBY, 8f) -> state = 1
                    windowOver && TwoFingerLogic.singleFingerFollow(accumA, accumB, 8f, 27f) -> state = 3
                    windowOver -> {
                        val dec = TwoFingerLogic.decideAxis(totalCx, totalCy, 27f, 2.0f)
                        val big = maxOf(abs(totalCx), abs(totalCy)) > 27f
                        if (dec != TwoFingerLogic.AXIS_FREE || big) {
                            state = 2
                            axis = dec
                        }
                    }
                }
            }
            lastZoomed = false; lastScrolled = false; lastMoved = false
            when (state) {
                1 -> {
                    // 缩放手停死区：跨度变化 <0.5% 视为手指已停，不发缩放帧
                    val factor = if (lastSpan > 1f) spanNow / lastSpan else 1f
                    lastZoomed = abs(factor - 1f) > 0.005f
                }
                3 -> {
                    val g = moveGate.feed(centroidDx, centroidDy)
                    lastMoved = !g.isZero
                }
                2 -> {
                    if (axis == 0) {
                        axis = TwoFingerLogic.decideAxis(totalCx, totalCy, 27f, 2.0f)
                    }
                    // 用生产代码同一个 [ScrollEmitter]（不再复刻一份）——
                    // 复刻会漂移：曾经的「单帧位移 > 0.5px」逐帧门限就只存在于生产代码里，
                    // 复刻版没有它，于是单测全绿、真机却是「慢速滑动完全不滚」。
                    val g = scrollEmitter.feed(axis, centroidDx, centroidDy)
                    lastScrolled = !g.isZero
                }
                else -> { /* 未定态：不输出（缩放启动窗口期不漏滚动） */ }
            }
            lastSpan = spanNow
        }
    }

    @Test
    fun `双指滑动全程不被误判成捏合`() {
        val sm = SM()
        // 用户在做「双指平移滚动」：两指同向各动 12px/帧（点积为正 → 不是反向运动）
        var scrolled = 0
        for (i in 1..15) {
            sm.frame(12f, 0f, 12f, 0f, 12f, 0f, 12f * i, 0f)
            assertFalse("同向运动不应触发捏合（i=$i）", sm.lastZoomed)
            if (sm.lastScrolled) scrolled++
        }
        assertEquals("轴锁应锁在横轴", 1, sm.axis)
        assertTrue("双指滑动应正常产生滚动", scrolled > 0)
    }

    @Test
    fun `真正的捏合能正常触发`() {
        val sm = SM()
        // 两指反向运动（各 10px/帧，相向而行）：span 200→180，factor=0.9
        sm.frame(-10f, 0f, 10f, 0f, 0f, 0f, 0f, 0f, 180f)
        assertEquals(1, sm.state)
        assertTrue("反向运动应判定为捏合并输出缩放", sm.lastZoomed)
        assertFalse("捏合模式下不应再发双指滚动", sm.lastScrolled)
    }

    @Test
    fun `两指反向运动幅度不等也算捏合`() {
        val sm = SM()
        // 用户规格：幅度不必对称——一指动 20px、另一指只动 6px，反向即捏合。
        // 第一帧：B 累计 6px < 8px 还不算「都有位移」；第二帧累计 12px 越过门槛。
        sm.frame(-20f, 0f, 6f, 0f, -7f, 0f, -7f, 0f, 190f)
        sm.frame(-20f, 0f, 6f, 0f, -7f, 0f, -14f, 0f, 180f)
        assertEquals("幅度不对称的反向运动也应判捏合", 1, sm.state)
        assertTrue(sm.lastZoomed)
    }

    @Test
    fun `捏合起手一指先动不抢跑成滚动或单指跟随`() {
        val sm = SM()
        // 捏合起手常见节奏：一指先动 5 帧（质心被带着动），另一指随后开始反向。
        // 启动窗口内不判滚动/单指跟随；两指一反向立刻判捏合。
        for (i in 1..5) {
            sm.frame(15f, 0f, 0f, 0f, 7.5f, 0f, 7.5f * i, 0f, 200f)
            assertFalse("窗口内不得锁单指跟随（i=$i）", sm.state == 3)
            assertFalse("窗口内不得锁滚动（i=$i）", sm.state == 2)
            assertFalse("窗口内不得输出移动", sm.lastMoved || sm.lastScrolled)
        }
        // 第二指开始反向运动 → 立即判捏合；span 200→170，factor=0.85 → 输出缩放
        sm.frame(15f, 0f, -15f, 0f, 0f, 0f, 37.5f, 0f, 170f)
        assertEquals("一指先动的捏合应正确判成捏合", 1, sm.state)
        assertTrue(sm.lastZoomed)
    }

    @Test
    fun `捏合后手指停住不再发缩放`() {
        val sm = SM()
        // 用户反馈（2026-10-29）：缩小画面后手指停住（没离开），画面不该再动。
        // 帧1：捏合判入且 span 变化 → 发缩放
        sm.frame(-10f, 0f, 10f, 0f, 0f, 0f, 0f, 0f, 180f)
        assertEquals(1, sm.state)
        assertTrue(sm.lastZoomed)
        // 帧2：两指完全静止（span 不变）→ 仍是缩放态，但**不发**缩放帧
        sm.frame(0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 180f)
        assertEquals("捏合态保持（不退出）", 1, sm.state)
        assertFalse("手停死区：不应发缩放帧", sm.lastZoomed)
        assertFalse("更不能发滚动/移动", sm.lastScrolled || sm.lastMoved)
        // 帧3：间距继续变化（180→178）→ 继续缩放
        sm.frame(-1f, 0f, 1f, 0f, 0f, 0f, 0f, 0f, 178f)
        assertTrue("间距继续变化应继续缩放", sm.lastZoomed)
    }

    @Test
    fun `两指靠得近时轻微变化不触发捏合`() {
        val sm = SM()
        // 两指各动 5px（<8dp bothMin）→ 连捏合前提都不满足；质心不动也不触发滚动
        sm.frame(-5f, 0f, 5f, 0f, 0f, 0f, 0f, 0f, 195f)
        assertFalse("低于位移门槛不应判捏合", sm.lastZoomed)
        assertEquals("不应提前进入任何状态", 0, sm.state)
        // 真正用力捏：两指各动 20px（反向），span 195→90
        sm.frame(-20f, 0f, 20f, 0f, 0f, 0f, 0f, 0f, 90f)
        assertTrue("大幅捏合应判定为捏合", sm.lastZoomed)
    }

    @Test
    fun `双指滚动的移动死区拦得住手抖`() {
        val sm = SM()
        // 手抖：每次 2px 同向，累计不到死区 → 不滚动
        for (i in 1..5) {
            sm.frame(2f, 0f, 2f, 0f, 2f, 0f, 2f * i, 0f)
            assertFalse("累计 10px（< 14px 死区）不应产生滚动", sm.lastScrolled)
        }
        // 继续移动越过轴锁门槛（27px）+ 死区（14px）→ 开始滚动
        var scrolled = false
        for (i in 6..40) {
            sm.frame(2f, 0f, 2f, 0f, 2f, 0f, 2f * i, 0f)
            if (sm.lastScrolled) { scrolled = true; break }
        }
        assertTrue("越过死区后应产生滚动", scrolled)
    }

    @Test
    fun `只有一根手指动时退化为单指跟随`() {
        val sm = SM()
        // 手指 A 移动、手指 B 基本静止（0.5px/帧 抖动，累计 7px < 8px 门槛）：
        // 窗口过后判单指跟随，忽略 B
        for (i in 1..14) {
            sm.frame(15f, 0f, 0.5f, 0f, 7.75f, 0f, 7.75f * i, 0f)
        }
        assertEquals("应进入单指跟随态", 3, sm.state)
        assertFalse("单指跟随不应发缩放", sm.lastZoomed)
        assertTrue("单指跟随应移动光标", sm.lastMoved)
    }

    @Test
    fun `进入移动识别后不再处理缩放`() {
        val sm = SM()
        // 先水平滚动（窗口过后锁横轴）
        for (i in 1..14) {
            sm.frame(12f, 0f, 12f, 0f, 12f, 0f, 12f * i, 0f)
        }
        assertEquals(2, sm.state)
        // 然后手指反向运动（若还在未定态本会判成捏合）→ 仍必须是滚动态
        sm.frame(-12f, 0f, 12f, 0f, 12f, 0f, 180f, 0f)
        assertEquals("移动识别后不再切捏合", 2, sm.state)
        assertFalse(sm.lastZoomed)
        assertTrue("滚动态应继续输出滚动", sm.lastScrolled)
    }

    @Test
    fun `斜向滚动双轴同时输出`() {
        val sm = SM()
        // 45° 对角线滚动：两轴分量相当 → 不锁轴，横纵同时透传
        for (i in 1..14) {
            sm.frame(10f, 10f, 10f, 10f, 10f, 10f, 10f * i, 10f * i)
        }
        assertEquals("斜向应保持双轴", 0, sm.axis)
        assertTrue(sm.state == 2 || sm.state == 0)
        assertTrue("斜向滚动应正常输出", sm.lastScrolled)
    }

    /**
     * 【回归】慢速双指滑动的累计位移必须变成滚动。
     *
     * 用户反馈（2026-10-30）：「双指滚动内容的时候，好像过程中是有死区的，
     * 也就是**我很缓慢的滑动，累计的移动没有变成滚动**。」
     *
     * 旧实现有一道逐帧门限「单帧位移 > 0.5px」，小于它的帧被**整帧丢掉**、
     * 压根没进死区累加器。触摸芯片量化步长 0.377 逻辑像素 —— 慢速滑动一帧
     * 正好走一个量化步 → **每一帧都被丢掉** → 累计位移永远攒不起来。
     *
     * 本例 0.3px/帧 ≈ 18 逻辑像素/秒（远低于旧门限折算出的 30px/s），
     * 两指同向移动 → 既不是捏合、也不是单指跟随 → 应当最终滚起来。
     */
    @Test
    fun `慢速双指滑动的累计位移必须变成滚动`() {
        val sm = SM()
        var totalY = 0f
        var scrolled = false
        for (i in 1..300) {
            totalY += 0.3f
            sm.frame(0f, -0.3f, 0f, -0.3f, 0f, -0.3f, 0f, totalY)
            if (sm.lastScrolled) { scrolled = true; break }
        }
        assertTrue("慢速双指滑动必须累计成滚动（累计位移不能被丢掉）", scrolled)
        assertEquals("慢速滑动应判成滚动态", 2, sm.state)
    }
}
