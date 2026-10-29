package com.volcano.spentablet

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import kotlin.math.abs

/**
 * 指针平滑器 v3（自标定噪声闸门）测试。
 *
 * 要保证的性质：
 *  1. **静止不飘**：手指不动（只有噪声）→ 输出恒为 0
 *  2. **慢速必须能动**（v1.4.2 的回归）：慢速持续移动 → 指针必须跟上，
 *     且总位移不能被吃掉（用户原话：「稳住和累计微小移动是两个不矛盾的事」）
 *  3. **噪声不放行成爬行**：随机方向的噪声 → 总输出接近 0
 *  4. **快速 1:1**：快速划动 → 输出 = 输入
 *  5. **停手即停**：输入停止 → 下一帧输出立即为 0
 *  6. **方向不歪**：等比例压缩，不会把斜向位移掰成横/竖
 *  7. **噪声底噪自标定**：估出来的 σ 应接近真实噪声
 */
class PointerSmootherTest {

    private fun smoother(p: PointerSmoother.Profile = PointerSmoother.Profile.STABLE) =
        PointerSmoother(p)

    @Test
    fun `手指静止时指针纹丝不动`() {
        val s = smoother()
        val noise = floatArrayOf(0.3f, -0.2f, 0.25f, -0.3f, 0.2f, -0.1f, 0.28f, -0.25f)
        for (n in noise) {
            val out = s.feed(n, -n * 0.5f)
            assertEquals("静止时不该有任何输出", 0f, out.dx, 1e-6f)
            assertEquals(0f, out.dy, 1e-6f)
        }
    }

    @Test
    fun `慢速持续移动指针必须跟上`() {
        // v1.4.2 的回归：速度死区把「慢速」直接判成静止 → 用户慢慢划一段距离，指针一动不动。
        val s = smoother()
        var out = 0f
        repeat(100) { out += s.feed(0.4f, 0f).dx }   // 0.4px/帧 的慢速移动
        assertTrue("慢速移动总位移过小，指针没跟上：$out", out > 15f)
    }

    @Test
    fun `慢速移动的位移不能被吃掉`() {
        // 输入 0.5px/帧 × 100 帧 = 50px，输出必须占大头（只允许起步那几帧的固定偏移）
        val s = smoother()
        var out = 0f
        repeat(100) { out += s.feed(0.5f, 0f).dx }
        assertTrue("慢速位移丢失过多（输入 50px，输出 $out）", out > 35f)
        assertTrue("慢速位移不应被放大（输入 50px，输出 $out）", out <= 50f)
    }

    @Test
    fun `随机噪声不会被放行成爬行`() {
        // 噪声幅度刻意超过旧「单帧死区 0.6px」：旧实现会把这些帧全部放行，
        // PC 端再把小数累积起来 → 指针慢慢爬。
        val s = smoother()
        val noise = floatArrayOf(
            0.9f, -0.7f, 0.5f, -0.95f, 0.8f, -0.6f, 0.45f, -0.85f,
            0.7f, -0.55f, 0.65f, -0.9f, 0.55f, -0.75f, 0.85f, -0.5f,
        )
        var totalAbs = 0f
        var signed = 0f
        for (n in noise) {
            val out = s.feed(n, 0f)
            totalAbs += abs(out.dx)
            signed += out.dx
        }
        assertTrue("噪声总输出应被压到极小，实际=$totalAbs", totalAbs < 0.5f)
        assertTrue("噪声不该产生单向漂移，signed=$signed", abs(signed) < 0.5f)
    }

    @Test
    fun `手指落下的瞬态跳变不能驱动指针`() {
        val s = smoother()
        s.feed(8f, 0f)              // 落下瞬间的坐标跳变
        var out = 0f
        repeat(40) { out += s.feed(0f, 0f).dx }
        assertEquals("落下瞬态不该驱动指针", 0f, out, 1e-6f)
    }

    @Test
    fun `窗口没填满时同号噪声突发不能开门`() {
        // 旧漏洞：count=2 时门槛按 √2 缩小，两个同号样本就能骗开门（net=1.81 > thr=1.30）
        val s = smoother()
        var out = 0f
        out += s.feed(0.9f, 0f).dx
        out += s.feed(0.9f, 0f).dx
        out += s.feed(-0.9f, 0f).dx
        out += s.feed(-0.9f, 0f).dx
        assertEquals("样本数不足时不该开门", 0f, out, 1e-6f)
    }

    @Test
    fun `快速划动完全 1 比 1`() {
        val s = smoother()
        val out = s.feed(20f, 0f)
        assertEquals("快速移动必须是 1:1", 20f, out.dx, 1e-4f)
    }

    @Test
    fun `停手立即停 没有惯性`() {
        val s = smoother()
        s.feed(20f, 0f)                 // 先快速划一下
        val stopped = s.feed(0f, 0f)    // 手指停住
        assertEquals("停手后不该再有输出", 0f, stopped.dx, 1e-6f)
        assertEquals(0f, stopped.dy, 1e-6f)
    }

    @Test
    fun `压缩是等比例的 方向不歪`() {
        val s = smoother()
        val out = s.feed(2f, 2f)
        assertEquals(out.dx, out.dy, 1e-5f)
    }

    @Test
    fun `高速段总位移守恒`() {
        val s = smoother()
        var out = 0f
        repeat(50) { out += s.feed(20f, 0f).dx }
        assertEquals("高速段总位移必须守恒", 1000f, out, 1e-3f)
    }

    @Test
    fun `关闭档位完全透传`() {
        val s = smoother(PointerSmoother.Profile.OFF)
        val out = s.feed(3f, 4f)
        assertEquals(3f, out.dx, 1e-6f)
        assertEquals(4f, out.dy, 1e-6f)
    }

    @Test
    fun `标准档比稳定档更早跟上`() {
        val s1 = smoother(PointerSmoother.Profile.STABLE)
        val s2 = smoother(PointerSmoother.Profile.NORMAL)
        var o1 = 0f
        var o2 = 0f
        repeat(40) {
            o1 += s1.feed(0.5f, 0f).dx
            o2 += s2.feed(0.5f, 0f).dx
        }
        assertTrue("标准档应更早跟上：stable=$o1 normal=$o2", o2 > o1)
    }

    @Test
    fun `噪声底噪能自标定`() {
        // 纯噪声 σ≈0.3（二阶差分法），估出来的 σ 应在同一量级
        val s = smoother()
        val rnd = floatArrayOf(
            0.31f, -0.28f, 0.22f, -0.35f, 0.29f, -0.18f, 0.33f, -0.26f,
            0.24f, -0.31f, 0.27f, -0.2f, 0.3f, -0.29f, 0.25f, -0.32f,
        )
        repeat(40) { rnd.forEach { s.feed(it, it * 0.3f) } }
        val info = s.debugInfo()
        // debugInfo 里带 σ，直接断言它在合理区间（0.05 ~ 1.5）
        val sigma = info.substringAfter("底噪σ=").substringBefore("px").toFloat()
        assertTrue("噪声估计应在合理量级，实际=$sigma", sigma in 0.05f..1.5f)
    }

    @Test
    fun `斜向慢速移动两个轴都要动`() {
        val s = smoother()
        var dx = 0f
        var dy = 0f
        repeat(60) {
            val o = s.feed(0.3f, 0.3f)
            dx += o.dx
            dy += o.dy
        }
        assertTrue("斜向慢速 X 轴必须动：$dx", dx > 5f)
        assertTrue("斜向慢速 Y 轴必须动：$dy", dy > 5f)
        assertEquals("斜向不能被掰成单轴", dx, dy, 1e-3f)
    }
}
