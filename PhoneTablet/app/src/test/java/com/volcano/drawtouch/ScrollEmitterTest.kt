package com.volcano.drawtouch

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import kotlin.math.abs

/**
 * 双指滚动输出器 [ScrollEmitter] 的**性质**测试。
 *
 * 断言的是**性质**（位移守恒 / 慢速可达 / 噪声不放行），不是某个具体数值。
 *
 * ## 这次要钉死的性质
 *  1. **低速仍可达** —— 慢速滑动时，累计位移必须变成滚动，**一分不丢**。
 *     这是 2026-10-30 用户反馈的回归点：「很缓慢地滑动，累计的移动没有变成滚动」。
 *  2. **总位移守恒** —— 死区只吃**一次**（起手），之后一路 1:1。
 *  3. **快速 1:1** —— 位移大时输出与输入完全相等。
 *  4. **静止不飘** —— 没有位移就不输出。
 *  5. **噪声不放行** —— 正负交替的抖动互相抵消，不该打开闸门。
 *  6. **方向不歪** —— 锁轴后另一轴的分量必须为 0。
 */
class ScrollEmitterTest {

    /** 死区阈值（px）。测试里取 15px 便于推算（真实默认 3dp ≈ 5.6px）。 */
    private val deadZone = 15f

    private fun emitter(dz: Float = deadZone) = ScrollEmitter { dz }

    /**
     * 【核心回归】慢速滑动时累计位移必须变成滚动。
     *
     * 曾经的实现有一道逐帧门限「单帧位移 > 0.5px」，小于它的帧被**整帧丢掉**。
     * 触摸芯片量化步长 0.377 逻辑像素，慢速滑动一帧正好走一个量化步
     * → 每帧都被丢 → 死区累加器永远攒不够 → 累计的移动**永远不会变成滚动**。
     *
     * 0.3px/帧 ≈ 18 逻辑像素/秒，远低于旧门限折算出的 30px/s。
     */
    @Test
    fun `慢速滑动的累计位移必须变成滚动`() {
        val em = emitter()
        val perFrame = 0.3f
        val frames = 200
        var emitted = 0f
        for (i in 0 until frames) {
            val d = em.feed(TwoFingerLogic.AXIS_Y, 0f, perFrame)
            emitted += abs(d.dy)
        }
        val totalIn = perFrame * frames
        assertTrue("慢速滑动必须真的滚起来（闸门应打开）", em.isStarted())
        assertEquals(
            "慢速滚动的总输出应恰好等于「总输入 − 起手死区」：位移一分不能多丢",
            totalIn - deadZone, emitted, 0.01f
        )
    }

    /**
     * 同样场景下，**单帧位移小于 0.5px 也必须算数**。
     * 这一条直接对应旧实现里被丢掉的那个门限。
     */
    @Test
    fun `单帧位移小于零点五像素也必须计入`() {
        val em = emitter()
        // 攒到刚好越过死区：15px / 0.3px ≈ 50 帧
        var startedAt = -1
        for (i in 1..60) {
            em.feed(TwoFingerLogic.AXIS_Y, 0f, 0.3f)
            if (em.isStarted() && startedAt < 0) startedAt = i
        }
        assertTrue("0.3px/帧的慢速滑动应在 60 帧内打开闸门（实际 startedAt=$startedAt）", startedAt > 0)
    }

    /**
     * 总位移守恒：死区只吃**一次**（起手），之后 1:1。
     *
     * 注意守恒量是**位移向量长度**（hypot），不是 |dx|+|dy|：
     * 死区判的是累计位移的**欧氏距离**，所以要拿同一种尺子去比。
     */
    @Test
    fun `总位移守恒_死区只吃一次`() {
        val em = emitter()
        var inX = 0f; var inY = 0f
        var outX = 0f; var outY = 0f
        for (i in 0 until 120) {
            val dx = 2f
            val dy = 1f
            inX += dx; inY += dy
            val d = em.feed(TwoFingerLogic.AXIS_FREE, dx, dy)
            outX += d.dx; outY += d.dy
        }
        val inMag = kotlin.math.hypot(inX.toDouble(), inY.toDouble()).toFloat()
        val outMag = kotlin.math.hypot(outX.toDouble(), outY.toDouble()).toFloat()
        assertEquals("总输出长度 = 总输入长度 − 起手死区（斜向双轴）", inMag - deadZone, outMag, 0.01f)
    }

    /** 快速划动：闸门打开后必须**完全 1:1**（位移不丢、不缩放）。 */
    @Test
    fun `快速划动是严格一一比`() {
        val em = emitter()
        // 先用慢速把闸门打开
        for (i in 0 until 60) em.feed(TwoFingerLogic.AXIS_Y, 0f, 0.3f)
        assertTrue(em.isStarted())
        // 之后每一帧都必须原样输出
        val d = em.feed(TwoFingerLogic.AXIS_Y, 0f, 20f)
        assertEquals("闸门打开后必须 1:1 输出", 20f, d.dy, 1e-4f)
    }

    /** 静止不飘：没有位移就不该有输出。 */
    @Test
    fun `静止不产生输出`() {
        val em = emitter()
        for (i in 0 until 30) {
            val d = em.feed(TwoFingerLogic.AXIS_FREE, 0f, 0f)
            assertTrue("静止时不应输出", d.isZero)
        }
        assertFalse("静止时闸门不应打开", em.isStarted())
    }

    /**
     * 噪声不放行：正负交替的抖动**互相抵消**，净位移为 0，不该打开闸门。
     *
     * 这一点很重要 —— 它说明「不丢位移」不等于「噪声也放行」：
     * 我们丢掉的从来不是「慢」，而是「不构成净位移的抖动」。
     */
    @Test
    fun `正负交替的抖动不会打开闸门`() {
        val em = emitter()
        for (i in 0 until 60) {
            val sign = if (i % 2 == 0) 0.377f else -0.377f
            val d = em.feed(TwoFingerLogic.AXIS_Y, 0f, sign)
            assertTrue("交替抖动不应产生输出", d.isZero)
        }
        assertFalse("交替抖动的净位移为 0，不该打开闸门", em.isStarted())
    }

    /** 方向不歪：锁竖轴时横向分量必须为 0，锁横轴时纵向分量必须为 0。 */
    @Test
    fun `轴锁后另一轴分量为零`() {
        val em = emitter()
        for (i in 0 until 60) em.feed(TwoFingerLogic.AXIS_Y, 0f, 0.3f)
        val dy = em.feed(TwoFingerLogic.AXIS_Y, 7f, 5f)
        assertEquals("锁竖轴时横向分量必须为 0", 0f, dy.dx, 1e-6f)
        assertTrue("锁竖轴时纵向分量正常输出", dy.dy > 0f)

        val em2 = emitter()
        for (i in 0 until 60) em2.feed(TwoFingerLogic.AXIS_X, 0.3f, 0f)
        val dx = em2.feed(TwoFingerLogic.AXIS_X, 5f, 7f)
        assertEquals("锁横轴时纵向分量必须为 0", 0f, dx.dy, 1e-6f)
        assertTrue("锁横轴时横向分量正常输出", dx.dx > 0f)
    }

    /** 斜向不锁轴：横纵同时出。 */
    @Test
    fun `斜向双轴同时输出`() {
        val em = emitter()
        for (i in 0 until 60) em.feed(TwoFingerLogic.AXIS_FREE, 0.3f, 0.3f)
        val d = em.feed(TwoFingerLogic.AXIS_FREE, 5f, 5f)
        assertTrue("斜向时横向应输出", d.dx > 0f)
        assertTrue("斜向时纵向应输出", d.dy > 0f)
    }

    /** 死区为 0（用户把设置调到 0）时必须完全 1:1，一帧都不许丢。 */
    @Test
    fun `死区为零时完全一一比`() {
        val em = emitter(dz = 0f)
        val d = em.feed(TwoFingerLogic.AXIS_Y, 0f, 0.3f)
        assertEquals("死区为 0 时单帧也要原样输出", 0.3f, d.dy, 1e-6f)
    }

    /** 手势重置后死区重新生效。 */
    @Test
    fun `重置后死区重新生效`() {
        val em = emitter()
        for (i in 0 until 60) em.feed(TwoFingerLogic.AXIS_Y, 0f, 0.3f)
        assertTrue(em.isStarted())
        em.reset()
        assertFalse("重置后闸门应关闭", em.isStarted())
        val d = em.feed(TwoFingerLogic.AXIS_Y, 0f, 0.3f)
        assertTrue("重置后死区重新生效", d.isZero)
    }
}
