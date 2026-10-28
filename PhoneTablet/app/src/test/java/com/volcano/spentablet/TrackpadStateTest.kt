package com.volcano.spentablet

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 触摸板状态机「防卡死」回归测试。
 *
 * 覆盖的线上故障：笔按在屏幕上时抬起最后一根手指 → 触摸板状态不复位
 * → 手机端吞掉所有笔事件、PC 端"笔让位"标志永真 → 光标一动不动，
 * 但心跳正常、界面仍有响应，只能切模式或重启 App。
 *
 * 详见 [TrackpadState] 的类注释。
 */
class TrackpadStateTest {

    // ---- 复位判定：手指全没了 + 结束类事件 → 必须复位 ----

    @Test
    fun `笔按着时最后一根手指抬起必须复位`() {
        // 关键场景：pointerCount 仍是 1（笔），但手指数为 0
        assertTrue(
            TrackpadState.mustReset(
                mouseMode = true, contact = true,
                fingerCount = 0, action = TrackpadState.ACTION_POINTER_UP
            )
        )
    }

    @Test
    fun `ACTION_UP 且没有手指时要复位`() {
        assertTrue(
            TrackpadState.mustReset(
                mouseMode = true, contact = true,
                fingerCount = 0, action = TrackpadState.ACTION_UP
            )
        )
    }

    @Test
    fun `ACTION_CANCEL 且没有手指时要复位`() {
        assertTrue(
            TrackpadState.mustReset(
                mouseMode = true, contact = true,
                fingerCount = 0, action = TrackpadState.ACTION_CANCEL
            )
        )
    }

    @Test
    fun `还有手指在屏幕上时不能复位`() {
        // 双指抬成单指：还有一根手指压着，绝不能把状态清零，
        // 否则 PC 会认为手指全离开，笔的悬停流立刻回来抢光标。
        assertFalse(
            TrackpadState.mustReset(
                mouseMode = true, contact = true,
                fingerCount = 1, action = TrackpadState.ACTION_POINTER_UP
            )
        )
    }

    @Test
    fun `按下和移动事件不触发复位`() {
        assertFalse(
            TrackpadState.mustReset(
                mouseMode = true, contact = true,
                fingerCount = 0, action = TrackpadState.ACTION_MOVE
            )
        )
        assertFalse(
            TrackpadState.mustReset(
                mouseMode = true, contact = true,
                fingerCount = 0, action = TrackpadState.ACTION_DOWN
            )
        )
    }

    @Test
    fun `数位板模式不复位触摸板状态`() {
        assertFalse(
            TrackpadState.mustReset(
                mouseMode = false, contact = true,
                fingerCount = 0, action = TrackpadState.ACTION_UP
            )
        )
    }

    @Test
    fun `悬停事件不算接触不复位`() {
        assertFalse(
            TrackpadState.mustReset(
                mouseMode = true, contact = false,
                fingerCount = 0, action = TrackpadState.ACTION_UP
            )
        )
    }

    // ---- 手指数计算 ----

    @Test
    fun `抬起一根后剩余手指数直接取统计值`() {
        // 双指 → 抬起一根：统计循环已排除抬起的那根，剩 1 根
        assertEquals(1, TrackpadState.fingersAfterPointerUp(1))
    }

    @Test
    fun `最后一根手指抬起后剩余为零`() {
        assertEquals(0, TrackpadState.fingersAfterPointerUp(0))
        // 防御：异常输入不返回负数
        assertEquals(0, TrackpadState.fingersAfterPointerUp(-1))
    }

    @Test
    fun `结束类事件判定`() {
        assertTrue(TrackpadState.isEndAction(TrackpadState.ACTION_UP))
        assertTrue(TrackpadState.isEndAction(TrackpadState.ACTION_CANCEL))
        assertTrue(TrackpadState.isEndAction(TrackpadState.ACTION_POINTER_UP))
        assertFalse(TrackpadState.isEndAction(TrackpadState.ACTION_DOWN))
        assertFalse(TrackpadState.isEndAction(TrackpadState.ACTION_MOVE))
        assertFalse(TrackpadState.isEndAction(TrackpadState.ACTION_POINTER_DOWN))
    }
}
