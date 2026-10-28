package com.volcano.spentablet

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 单指点击 / 双击 / 双击拖拽 的时序回归测试。
 *
 * 为什么必须测：
 *   · 「双击拖拽」一旦少发一次 DRAG_UP，桌面左键就永久按住 —— 鼠标看起来"拖什么都在选中"，只能重启 host；
 *   · 双击第二下没撤掉挂起的单击 → 会先点一下再拖，选区被取消、或点进了别的格子；
 *   · 窗口时长写错 → 双击被拆成两次单击，双击打开文件就失效。
 *
 * 详见 [TapDrag] 的类注释。
 */
class TapDragTest {

    /** 固定阈值，避免依赖屏幕密度导致测试随设备漂移 */
    private fun machine() = TapDrag(
        doubleTapWindowMs = 300L,
        tapTimeoutMs = 320L,
        doubleTapSlopPx = 48f,
        dragSlopPx = 12f,
    )

    @Test
    fun `单击抬手返回待发单击`() {
        val m = machine()
        assertEquals(TapDrag.Act.NONE, m.onDown(1000L, 100f, 100f))
        assertEquals(TapDrag.Act.PENDING_SINGLE, m.onUp(1050L, 100f, 100f))
    }

    @Test
    fun `双击窗口内同位置第二下要先撤掉挂起的单击`() {
        val m = machine()
        m.onDown(1000L, 100f, 100f)
        m.onUp(1050L, 100f, 100f)
        // 250ms 后同一位置再按下：这是双击的第二下
        assertEquals(TapDrag.Act.CANCEL_PENDING, m.onDown(1300L, 102f, 101f))
        // 短按后松手且没有移动 → 双击
        assertEquals(TapDrag.Act.DOUBLE_CLICK, m.onUp(1360L, 102f, 101f))
    }

    @Test
    fun `超出双击窗口的第二下是新的单击`() {
        val m = machine()
        m.onDown(1000L, 100f, 100f)
        m.onUp(1050L, 100f, 100f)
        // 301ms > 300ms 窗口
        assertEquals(TapDrag.Act.NONE, m.onDown(1351L, 100f, 100f))
        assertEquals(TapDrag.Act.PENDING_SINGLE, m.onUp(1400L, 100f, 100f))
    }

    @Test
    fun `第二下离得太远不算双击`() {
        val m = machine()
        m.onDown(1000L, 100f, 100f)
        m.onUp(1050L, 100f, 100f)
        // 横向偏 60px > 48px 容差
        assertEquals(TapDrag.Act.NONE, m.onDown(1200L, 160f, 100f))
        assertEquals(TapDrag.Act.PENDING_SINGLE, m.onUp(1250L, 160f, 100f))
    }

    @Test
    fun `双击后按住并移动就按下左键开始拖拽`() {
        val m = machine()
        m.onDown(1000L, 100f, 100f)
        m.onUp(1050L, 100f, 100f)
        m.onDown(1200L, 100f, 100f)
        // 只挪 5px：还没到拖拽阈值，不能按左键，否则双击就被吃成拖拽
        assertEquals(TapDrag.Act.NONE, m.onMove(105f, 100f))
        assertFalse(m.isDragging)
        // 挪到 20px：越过 12px 阈值 → 按下左键
        assertEquals(TapDrag.Act.DRAG_DOWN, m.onMove(120f, 100f))
        assertTrue(m.isDragging)
        // 不能重复按下
        assertEquals(TapDrag.Act.NONE, m.onMove(200f, 100f))
        assertTrue(m.isDragging)
        // 松手 → 必须补发抬起
        assertEquals(TapDrag.Act.DRAG_UP, m.onUp(1500L, 200f, 100f))
        assertFalse(m.isDragging)
    }

    @Test
    fun `拖拽中陀螺仪抖动不能反复按放左键`() {
        val m = machine()
        m.onDown(1000L, 100f, 100f)
        m.onUp(1050L, 100f, 100f)
        m.onDown(1200L, 100f, 100f)
        assertEquals(TapDrag.Act.DRAG_DOWN, m.onMove(100f, 130f))
        repeat(20) {
            assertEquals(TapDrag.Act.NONE, m.onMove(100f + it, 130f - it))
        }
        assertEquals(TapDrag.Act.DRAG_UP, m.onUp(1600L, 130f, 100f))
    }

    @Test
    fun `第二下按住超时才松手按单击处理`() {
        val m = machine()
        m.onDown(1000L, 100f, 100f)
        m.onUp(1050L, 100f, 100f)
        m.onDown(1200L, 100f, 100f)
        // 按住 400ms > tapTimeoutMs(320) 且原地没动
        assertEquals(TapDrag.Act.SINGLE_CLICK, m.onUp(1600L, 100f, 100f))
    }

    @Test
    fun `多指介入时复位必须补发左键抬起`() {
        val m = machine()
        m.onDown(1000L, 100f, 100f)
        m.onUp(1050L, 100f, 100f)
        m.onDown(1200L, 100f, 100f)
        m.onMove(150f, 100f)
        assertTrue(m.isDragging)
        // 双指落下 → 手势作废
        assertEquals(TapDrag.Act.DRAG_UP, m.reset())
        assertFalse(m.isDragging)
        // 复位后再抬起不能又发一次（否则 PC 会多收一个 LEFT_UP，虽然无害但要保证幂等）
        assertEquals(TapDrag.Act.PENDING_SINGLE, m.onUp(1300L, 150f, 100f))
    }

    @Test
    fun `复位后旧的一次点击不会和新手势凑成双击`() {
        val m = machine()
        m.onDown(1000L, 100f, 100f)
        m.onUp(1050L, 100f, 100f)
        assertEquals(TapDrag.Act.NONE, m.reset())
        // 复位清掉了 lastTapAt，所以紧接着的按下是新的单击而不是双击第二下
        assertEquals(TapDrag.Act.NONE, m.onDown(1100L, 100f, 100f))
    }
}
