package com.volcano.drawtouch

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 手势死区逻辑单元测试。
 *
 * 覆盖三个需求：
 *  1. 双指捏合死区：微小捏合不干扰双指滑动（相对阈值 + 绝对阈值双重判定）
 *  2. 移动类手势开始移动的死区：手抖不产生位移，越过阈值后 1:1 跟随
 *  3. 死区数值可配置：阈值来自 provider，改配置立即生效
 */
class GestureDeadZoneTest {

    // ---------- 移动死区 ----------

    @Test
    fun `死区内不产生任何位移`() {
        val gate = MoveDeadZone { 10f }
        // 累计 6px（< 10px 阈值）：应全部被吃掉
        var out = gate.feed(3f, 4f)   // 距离 5
        assertTrue("死区内应无输出", out.isZero)
        out = gate.feed(3f, 4f)        // 累计 10px，仍在阈值内
        assertTrue("累计等于阈值仍应无输出", out.isZero)
    }

    @Test
    fun `越过阈值后只输出超出部分`() {
        val gate = MoveDeadZone { 10f }
        gate.feed(3f, 4f)             // 累计 5，未越界
        val out = gate.feed(6f, 8f)   // 累计 15，越界 → 超出 5px，方向 6:8 → 3:4
        assertEquals(3f, out.dx, 0.001f)
        assertEquals(4f, out.dy, 0.001f)
    }

    @Test
    fun `越过阈值后 1 1 跟随`() {
        val gate = MoveDeadZone { 10f }
        gate.feed(20f, 0f)            // 越过阈值
        val out = gate.feed(7f, -3f)
        assertEquals("越过阈值后应原样透传", 7f, out.dx, 0.001f)
        assertEquals(-3f, out.dy, 0.001f)
    }

    @Test
    fun `catchUp 模式越过阈值时补回死区内的位移`() {
        val gate = MoveDeadZone(catchUp = true) { 10f }
        gate.feed(3f, 4f)             // 累计 5，未越界
        val out = gate.feed(6f, 8f)   // 累计 15 → 全部输出，保证 1:1
        assertEquals("catchUp 应输出完整累计量", 9f, out.dx, 0.001f)
        assertEquals(12f, out.dy, 0.001f)
    }

    @Test
    fun `阈值 0 等价于无死区`() {
        val gate = MoveDeadZone { 0f }
        val out = gate.feed(3f, 4f)
        assertEquals(3f, out.dx, 0.001f)
        assertEquals(4f, out.dy, 0.001f)
    }

    @Test
    fun `reset 后死区重新生效`() {
        val gate = MoveDeadZone { 10f }
        gate.feed(50f, 0f)            // 越过阈值 → started
        assertTrue(gate.isStarted())
        gate.reset()
        assertFalse("reset 后应回到未启动状态", gate.isStarted())
        val out = gate.feed(3f, 4f)
        assertTrue("reset 后小位移应重新被吃掉", out.isZero)
    }

    @Test
    fun `阈值可动态调整`() {
        var threshold = 100f
        val gate = MoveDeadZone { threshold }
        assertTrue(gate.feed(10f, 0f).isZero)   // 阈值 100，被吃掉
        threshold = 5f                           // 配置改小
        val out = gate.feed(10f, 0f)
        assertFalse("阈值改小后应立即生效", out.isZero)
        // 手指总共移动 20px，阈值 5px → 输出 20-5=15px（累计量一次性结算）
        assertEquals(15f, out.dx, 0.001f)
    }

    // ---------- 捏合死区 ----------

    @Test
    fun `微小捏合不判定为捏合`() {
        // 起始跨度 100px，变化 8px：相对 8% < 15%，且绝对 8px > 6px 阈值也不行 → 不判定
        assertFalse(
            "相对阈值内不应判为捏合",
            PinchDeadZone.engaged(100f, 108f, 0.15f, 6f)
        )
    }

    @Test
    fun `相对变化够大才判为捏合`() {
        // 起始 100px，变化 20px = 20% > 15%，绝对 20px > 6px → 判定为捏合
        assertTrue(
            "20% 变化应判为捏合",
            PinchDeadZone.engaged(100f, 120f, 0.15f, 6f)
        )
    }

    @Test
    fun `绝对阈值兜底小跨度场景`() {
        // 两指靠得近：起始跨度只有 40px，变化 10px = 25% > 15%，但绝对变化 10px < 20px 阈值 → 不判定
        assertFalse(
            "绝对阈值应拦截小跨度的轻微变化",
            PinchDeadZone.engaged(40f, 50f, 0.15f, 20f)
        )
        // 变化 25px，超过绝对阈值 → 判定
        assertTrue(
            "超过绝对阈值应判为捏合",
            PinchDeadZone.engaged(40f, 65f, 0.15f, 20f)
        )
    }

    @Test
    fun `起始跨度无效时不判定`() {
        assertFalse(PinchDeadZone.engaged(0f, 100f, 0.15f, 6f))
        assertFalse(PinchDeadZone.engaged(1f, 100f, 0.15f, 6f))
    }

    @Test
    fun `捏合迟滞退出`() {
        // 回到起点附近（< 绝对阈值一半）→ 交还给双指滑动
        assertTrue(
            "跨度回到起点附近应释放捏合",
            PinchDeadZone.released(100f, 102f, 6f)
        )
        // 仍然明显偏离 → 保持捏合
        assertFalse(
            "跨度仍明显偏离应保持捏合",
            PinchDeadZone.released(100f, 130f, 6f)
        )
    }

    @Test
    fun `捏合死区参数可配置`() {
        // 阈值放宽到 40%：20% 的变化不再判定为捏合
        assertFalse(
            "相对阈值放宽后不应判定",
            PinchDeadZone.engaged(100f, 120f, 0.40f, 6f)
        )
        // 绝对阈值放宽到 30px：20px 变化不再判定
        assertFalse(
            "绝对阈值放宽后不应判定",
            PinchDeadZone.engaged(100f, 120f, 0.15f, 30f)
        )
    }
}
