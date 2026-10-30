package com.volcano.drawtouch

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 双指手势三态轴锁的纯逻辑测试（[TwoFingerLogic.decideAxis]）。
 *
 * 用户规格：近似竖 → 锁竖；近似横 → 锁横；斜向（允许一点偏角）→ 不锁，双轴同时输出。
 * bias = 1.6：竖向要超过横向 1.6 倍才判竖，反之亦然；两轴相当 → FREE。
 */
class TwoFingerLogicTest {

    private fun decide(dx: Float, dy: Float) =
        TwoFingerLogic.decideAxis(dx, dy, 27f, 2.0f)   // 10dp ≈ 27px，斜向窗口 45°±18°

    @Test
    fun `近似竖滑锁竖轴`() {
        // 竖向为主，带一点横向偏角（允许）
        assertEquals(TwoFingerLogic.AXIS_Y, decide(5f, 100f))
        assertEquals(TwoFingerLogic.AXIS_Y, decide(-10f, 120f))
        assertEquals(TwoFingerLogic.AXIS_Y, decide(0f, 60f))
    }

    @Test
    fun `近似横滑锁横轴`() {
        assertEquals(TwoFingerLogic.AXIS_X, decide(100f, 5f))
        assertEquals(TwoFingerLogic.AXIS_X, decide(-120f, 10f))
        assertEquals(TwoFingerLogic.AXIS_X, decide(60f, 0f))
    }

    @Test
    fun `斜向不锁轴双轴同时输出`() {
        // 45° 对角线
        assertEquals(TwoFingerLogic.AXIS_FREE, decide(80f, 80f))
        assertEquals(TwoFingerLogic.AXIS_FREE, decide(-80f, 80f))
        // 在 bias 带内（如 1.4 倍）仍算斜向
        assertEquals(TwoFingerLogic.AXIS_FREE, decide(100f, 140f))
        assertEquals(TwoFingerLogic.AXIS_FREE, decide(140f, 100f))
    }

    @Test
    fun `累计位移低于门槛时不判定`() {
        assertEquals(TwoFingerLogic.AXIS_FREE, decide(20f, 10f))
        assertEquals(TwoFingerLogic.AXIS_FREE, decide(0f, 0f))
    }

    @Test
    fun `超过门槛的微小偏角也按主方向锁定`() {
        // 位移 100/40：40 < 200 且 100 > 80 → 横
        assertEquals(TwoFingerLogic.AXIS_X, decide(100f, 40f))
        // 100/60：60/100 = 0.6 在 (0.5, 2.0) 带内 → 斜向双轴
        assertEquals(TwoFingerLogic.AXIS_FREE, decide(100f, 60f))
        // 100/70：斜向
        assertEquals(TwoFingerLogic.AXIS_FREE, decide(100f, 70f))
        // 100/220：220 > 200 → 竖
        assertEquals(TwoFingerLogic.AXIS_Y, decide(100f, 220f))
    }

    // ---- 轴锁「未决态」与「斜向」的区分（2026-10-30 修正）----

    private fun decideOrNull(dx: Float, dy: Float) =
        TwoFingerLogic.decideAxisOrNull(dx, dy, 27f, 2.0f)

    @Test
    fun `位移未达门槛返回 null 表示还不能判定`() {
        assertEquals(null, decideOrNull(20f, 10f))
        assertEquals(null, decideOrNull(0f, 0f))
        assertEquals("刚越过门槛就要给出结论", TwoFingerLogic.AXIS_Y, decideOrNull(5f, 30f))
    }

    @Test
    fun `斜向判定为 FREE 而不是未判定`() {
        // 关键：斜向必须返回 AXIS_FREE（非 null），调用方据此「判定完成、永久保持双轴」，
        // 才不会每帧重判、滑一小段就被锁成单轴（用户反馈的锁死问题）。
        assertEquals(TwoFingerLogic.AXIS_FREE, decideOrNull(80f, 80f))
        assertEquals(TwoFingerLogic.AXIS_FREE, decideOrNull(-80f, 80f))
        assertEquals(TwoFingerLogic.AXIS_FREE, decideOrNull(100f, 140f))
    }

    /**
     * 模拟真实斜滑过程：累计位移在斜向带内逐渐增大、期间轻微偏向某一轴。
     * 期望：首次越过门槛就定为 FREE，之后**不再改判**为单轴（由调用方负责锁存，
     * 这里验证「任何时刻的判定结果都不会误变成 X/Y」这一前提）。
     */
    @Test
    fun `斜滑过程中累计位移仍在斜向带内则始终判 FREE`() {
        var dx = 0f
        var dy = 0f
        for (i in 1..12) {
            // 每帧推进：横向 10px、纵向 9px（比值 0.9，稳稳落在斜向带 0.5~2.0 内）
            dx += 10f
            dy += 9f
            val r = decideOrNull(dx, dy)
            if (r != null) {
                assertEquals("第 $i 帧累计 ($dx,$dy) 应为斜向 FREE", TwoFingerLogic.AXIS_FREE, r)
            }
        }
    }

    // ---- 两指位移分类 ----

    @Test
    fun `两指都有位移才算捏合前提`() {
        assertTrue(TwoFingerLogic.bothMoving(30f, 30f, 8f))
        assertTrue(TwoFingerLogic.bothMoving(30f, 9f, 8f))
        assertFalse("一指累计低于阈值 → 不算都在动", TwoFingerLogic.bothMoving(30f, 5f, 8f))
        assertFalse(TwoFingerLogic.bothMoving(2f, 30f, 8f))
    }

    @Test
    fun `一指静止另一指明显移动才算单指跟随`() {
        assertTrue(TwoFingerLogic.singleFingerFollow(40f, 3f, 8f, 27f))
        assertTrue(TwoFingerLogic.singleFingerFollow(3f, 40f, 8f, 27f))
        assertFalse("两指都在动 → 不是单指跟随", TwoFingerLogic.singleFingerFollow(40f, 40f, 8f, 27f))
        assertFalse("活动指还没超过移动门槛 → 不判定", TwoFingerLogic.singleFingerFollow(20f, 2f, 8f, 27f))
        assertFalse("两指都静止 → 不判定", TwoFingerLogic.singleFingerFollow(2f, 2f, 8f, 27f))
    }

    // ---- 捏合判定：反向运动（2026-10-29 放宽规格）----

    private fun opposing(ax: Float, ay: Float, bx: Float, by: Float) =
        TwoFingerLogic.opposing(ax, ay, bx, by, 8f)

    @Test
    fun `两指反向运动判捏合`() {
        assertTrue("相向（点积为负）", opposing(-20f, 0f, 20f, 0f))
        assertTrue("一横一竖反向", opposing(-5f, -30f, 3f, 20f))
        assertTrue("幅度不对称也算", opposing(-40f, 0f, 9f, 0f))
    }

    @Test
    fun `两指同向或斜交运动不算反向`() {
        assertFalse("同向", opposing(20f, 0f, 20f, 0f))
        assertFalse("夹角 90°（点积为 0）不算反向", opposing(20f, 0f, 0f, 20f))
        assertFalse("夹角锐角", opposing(20f, 0f, 5f, 5f))
    }

    @Test
    fun `反向但有指位移不足不判捏合`() {
        assertFalse("A 累计低于门槛", opposing(-5f, 0f, 20f, 0f))
        assertFalse("B 累计低于门槛", opposing(-20f, 0f, 5f, 0f))
        assertFalse("两指都没动", opposing(-1f, 0f, 1f, 0f))
    }
}
