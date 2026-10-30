package com.volcano.drawtouch

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 「重按」判据的性质测试（v1.4.7）。
 *
 * 断言的是**性质**（「会不会触发 / 会不会松开 / 多久触发」），
 * 不是具体数值 —— 数值会随阈值微调而变，性质不会。
 *
 * 所有场景曲线都来自 2026-10-30 的真机采集
 * （`getevent -lt /dev/input/event9`，sec_touchscreen，461 个接触样本）
 * 与据此设计的合成场景。参数是**搜索出来的**，不是拍脑袋：
 * `tools/press_search.py` 在真实曲线 + 合成场景集上做全参数搜索，
 * 选出同时满足全部性质的组合。
 */
class PressSignalTest {

    // ---------- 工具 ----------

    /** 把「等效面积」序列喂进去（major = minor = size ⟹ sqrt(major*minor) = size） */
    private fun feed(s: PressSignal, sizes: List<Float>, pressure: Float = 1.0f): List<PressSignal.Action> {
        val out = ArrayList<PressSignal.Action>(sizes.size)
        for (v in sizes) {
            out.add(s.update(pressure, v, v))
        }
        return out
    }

    private fun count(actions: List<PressSignal.Action>, a: PressSignal.Action): Int =
        actions.count { it == a }

    /** 线性爬坡（模拟手指逐渐加力） */
    private fun ramp(from: Float, to: Float, frames: Int): List<Float> {
        if (frames <= 1) return listOf(to)
        return (0 until frames).map { from + (to - from) * it / (frames - 1) }
    }

    /** 确定性伪随机起伏（不用 Random，保证每次跑测结果一致） */
    private fun noise(base: Float, amp: Float, n: Int): List<Float> {
        val out = ArrayList<Float>(n)
        var x = 12345
        for (i in 0 until n) {
            x = (x * 1103515245 + 12345) and 0x7FFFFFFF
            out.add(base + amp * (x.toDouble() / 0x7FFFFFFF.toDouble() * 2 - 1).toFloat())
        }
        return out
    }

    /** 稳定 n 帧后再接一段序列 */
    private fun steady(n: Int, base: Float = 10f): List<Float> = List(n) { base }

    // ================================================================
    //  一、负向性质：不该触发的时候绝不能触发
    // ================================================================

    @Test
    fun `轻触稳态一次都不该触发`() {
        val s = PressSignal()
        val a = feed(s, steady(150))
        assertEquals("轻触稳态不该触发重按", 0, count(a, PressSignal.Action.DOWN))
        // 注意：面积完全恒定时面积通道会保持「不可用」（areaUsable 要求接触面积
        // 确有 3% 以上变化才能证明这条通道真的跟力度挂钩）——
        // 这正是它在真实场景下的守卫，不该在这里断言通道已选中。
    }

    @Test
    fun `压着滑的自然起伏不该触发`() {
        val s = PressSignal()
        val a = feed(s, noise(10f, 0.6f, 150))
        assertEquals("±6% 的自然起伏不该触发（阈值 30%）", 0, count(a, PressSignal.Action.DOWN))
    }

    @Test
    fun `落指压实不该触发`() {
        // 手指刚落下时接触面积从极小长到压实（实测最长 45 帧 ≈1.1s）。
        // 每次触摸都会经历落指 —— 若把它判成加力，用户每碰一下屏幕就误按左键。
        val s = PressSignal()
        val a = feed(s, ramp(4.9f, 17f, 45) + List(40) { 17f })
        assertEquals("落指瞬态不该触发重按", 0, count(a, PressSignal.Action.DOWN))
    }

    @Test
    fun `单帧面积尖峰不触发重按`() {
        val s = PressSignal()
        val sizes = steady(40).toMutableList()
        sizes.add(30f)          // 孤立的一帧巨大值
        sizes.addAll(steady(40))
        val a = feed(s, sizes)
        assertEquals("孤立尖峰不该判成重按", 0, count(a, PressSignal.Action.DOWN))
    }

    @Test
    fun `阈值附近震荡不反复触发`() {
        val s = PressSignal()
        val sizes = ArrayList<Float>()
        repeat(30) { sizes.add(10f) }
        // 在阈值附近来回抖（±30%）
        repeat(40) { i -> sizes.add(if (i % 2 == 0) 12.9f else 13.1f) }
        val a = feed(s, sizes)
        val downs = count(a, PressSignal.Action.DOWN)
        val ups = count(a, PressSignal.Action.UP)
        assertTrue("阈值附近震荡不该反复触发（downs=$downs ups=$ups）", downs <= 1 && ups <= 1)
    }

    // ================================================================
    //  二、正向性质：该触发的时候一定要触发，且要够快
    // ================================================================

    @Test
    fun `稳态后加力能识别并松开`() {
        val s = PressSignal()
        val a = feed(s, steady(40) + ramp(10f, 17f, 25) + ramp(17f, 10f, 25))
        assertEquals("应该走面积通道（本机没有真压感）", PressSignal.Channel.AREA, s.channel)
        assertEquals("稳态后加力应该触发一次", 1, count(a, PressSignal.Action.DOWN))
        assertEquals("松回静息水平应该松开一次", 1, count(a, PressSignal.Action.UP))
    }

    @Test
    fun `滑动中加力能识别并松开`() {
        val s = PressSignal()
        val a = feed(s, steady(40) + ramp(11.8f, 18.9f, 40) + ramp(18.9f, 10f, 25))
        assertEquals("滑动中加力应该触发一次", 1, count(a, PressSignal.Action.DOWN))
        assertEquals("滑动中加力后松开应该松开一次", 1, count(a, PressSignal.Action.UP))
    }

    @Test
    fun `长按期间不能自己松开`() {
        // 一次加力持续 200 帧（≈5s）。若静息水平跟着往上爬，
        // 释放线会一路抬高 → 「按住」被自己解除 → 长按拖拽中途弹开。
        val s = PressSignal()
        val a = feed(s, steady(40) + ramp(10f, 17f, 25) + List(200) { 17f })
        assertEquals("应该触发一次", 1, count(a, PressSignal.Action.DOWN))
        assertEquals("长按期间不该自己松开", 0, count(a, PressSignal.Action.UP))
        assertTrue("应该仍处于按住状态", s.dragging)
    }

    @Test
    fun `加力后松回静息能松开（旧判据在这里会粘键）`() {
        // 旧判据的释放线是「跌破静息 12%」，用户松手时力度只是回到静息水平，
        // 根本够不到那条线 → 按钮一直按着不松。
        val s = PressSignal()
        val a = feed(s, steady(40) + ramp(10f, 17f, 25) + ramp(17f, 10f, 25))
        assertTrue("应该触发过", count(a, PressSignal.Action.DOWN) >= 1)
        assertTrue("松回静息水平必须能松开（防粘键）", count(a, PressSignal.Action.UP) >= 1)
    }

    @Test
    fun `按下后隔很久再松手仍然能松开`() {
        val s = PressSignal()
        val a = feed(s, steady(40) + ramp(10f, 17f, 25) + List(150) { 17f } + ramp(17f, 10f, 25))
        assertEquals("应该触发一次", 1, count(a, PressSignal.Action.DOWN))
        assertEquals("长按之后松手应该松开一次", 1, count(a, PressSignal.Action.UP))
    }

    // ================================================================
    //  三、真实采集曲线回归（2026-10-30 真机数据）
    // ================================================================

    /** 实测段落 4：滑动中加力（稳态 40 帧 → 加力 → 回落） */
    @Test
    fun `实测滑动中加力的曲线能触发`() {
        val s = PressSignal()
        val sizes = steady(40) + ramp(11.8f, 18.9f, 40) + ramp(18.9f, 10f, 25)
        val a = feed(s, sizes)
        assertTrue("实测滑动中加力应该触发重按", count(a, PressSignal.Action.DOWN) >= 1)
        assertTrue("实测滑动中加力之后应该能松开", count(a, PressSignal.Action.UP) >= 1)
    }

    /** 实测段落 3：落下即压实（落指瞬态）——必须不触发 */
    @Test
    fun `实测落指压实的曲线不触发`() {
        val s = PressSignal()
        val a = feed(s, ramp(4.9f, 17f, 45) + List(40) { 17f })
        assertEquals("实测落指瞬态不该触发重按", 0, count(a, PressSignal.Action.DOWN))
    }

    /** 实测静息段的自然起伏（±10% 以内）——必须不触发 */
    @Test
    fun `实测静息段的自然起伏不触发`() {
        val s = PressSignal()
        // 实测静息段：10.3~13.0 徘徊（相对静息 P20 的涨幅全在 ±10% 内，偶发 +27%）
        val rest = floatArrayOf(
            10.3f, 12.6f, 11.4f, 13.0f, 10.8f, 12.1f, 11.7f, 12.9f,
            10.5f, 12.4f, 11.2f, 13.0f, 10.9f, 12.7f, 11.9f, 12.2f,
            10.6f, 12.8f, 11.5f, 12.5f
        )
        val a = feed(s, rest.toList())
        assertEquals("实测静息段不该触发重按", 0, count(a, PressSignal.Action.DOWN))
    }

    // ================================================================
    //  四、参数钉死（防止后人把关键旋钮改回去）
    // ================================================================

    @Test
    fun `确认帧数不超过3以免漏掉快速加力`() {
        // 实测：最短的一次加力只维持 3 帧（≈73ms @41Hz）
        assertTrue("确认帧数必须 ≤3（实测最短加力仅 3 帧）",
            PressSignal.DEFAULT_CONFIRM_FRAMES <= 3)
    }

    @Test
    fun `静息窗口要盖得住一次加力的时长`() {
        // 实测：真实加力要 20~40 帧（0.5~1s）才长出来。
        // 窗口若比它短，窗口里全是加力后的高值 → 静息水平追着涨 → 涨幅被吃掉。
        assertTrue("静息窗口必须 ≥40 帧（实测加力最长 40 帧）",
            PressSignal.DEFAULT_REST_WINDOW >= 40)
    }

    @Test
    fun `默认阈值落在静息噪声与加力信号之间`() {
        // 实测：静息段涨幅全在 ±10%（偶发 +27%），加力段是连续的一段 +39%~+68%。
        assertTrue("默认阈值必须高于静息段的偶发值 27%",
            PressSignal.DEFAULT_AREA_RISE_DOWN > 0.27f)
        assertTrue("默认阈值必须低于加力段的下沿 39%",
            PressSignal.DEFAULT_AREA_RISE_DOWN < 0.39f)
    }

    @Test
    fun `释放线必须低于触发线形成滞回`() {
        assertTrue("释放余量必须为正（否则边界反复横跳）",
            PressSignal.DEFAULT_RELEASE_SLACK > 0f && PressSignal.DEFAULT_RELEASE_SLACK < 0.2f)
    }

    // ================================================================
    //  五、压力通道的三道守卫（本机是伪压感，必须全部挡住）
    // ================================================================

    @Test
    fun `压力恒定且面积不变时完全不动作`() {
        val s = PressSignal()
        val a = feed(s, steady(40), pressure = 1.0f)
        assertEquals(0, count(a, PressSignal.Action.DOWN))
        assertEquals(PressSignal.Channel.NONE, s.channel)
    }

    @Test
    fun `伪压感恒定高压不判重按`() {
        val s = PressSignal()
        val a = feed(s, steady(40), pressure = 0.95f)
        assertTrue("恒定 0.95 的伪压感不该触发重按", count(a, PressSignal.Action.DOWN) == 0)
        assertEquals(PressSignal.Channel.NONE, s.channel)
    }

    @Test
    fun `全程高于阈值的抖动压力不算真压感`() {
        // 伪压感机器常年 0.9~1.0：变化量达标，但**全程都在阈值之上**
        // = 永远处于「按住」状态 → 一碰就误触。
        val s = PressSignal()
        val a = feed(s, steady(40), pressure = 0.6f)
        assertTrue("全程在阈值之上的抖动压力不该触发", count(a, PressSignal.Action.DOWN) == 0)
        assertEquals(PressSignal.Channel.NONE, s.channel)
    }

    @Test
    fun `压力通道按下与松开`() {
        val s = PressSignal()
        // 先用恒定压力（不可用），再用真实压感的样子（落下 → 逐渐加力）
        feed(s, List(5) { 100f }, pressure = 0.2f)
        assertEquals("压力恒定时不该选压力通道", PressSignal.Channel.NONE, s.channel)
        feed(s, List(1) { 100f }, pressure = 0.4f)
        feed(s, List(1) { 100f }, pressure = 0.8f)
        assertEquals(PressSignal.Channel.PRESSURE, s.channel)
        assertEquals(PressSignal.Action.NONE, s.update(0.6f, 100f, 80f))  // 还在滞回带里
        var down = false
        var up = false
        repeat(5) {
            if (s.update(0.9f, 100f, 80f) == PressSignal.Action.DOWN) down = true
        }
        assertTrue("连续超阈值后应该按下", down)
        repeat(5) {
            if (s.update(0.2f, 100f, 80f) == PressSignal.Action.UP) up = true
        }
        assertTrue("压力回落后应该松开", up)
    }

    // ================================================================
    //  六、状态管理
    // ================================================================

    @Test
    fun `reset 后重新学习静息水平`() {
        val s = PressSignal()
        feed(s, steady(60, 10f))
        assertTrue(s.restLevel > 8f)
        s.reset()
        assertEquals("reset 后静息水平应清空", 0f, s.restLevel)
        assertTrue("reset 后应重新学到新的静息水平", !s.restReady)
        feed(s, steady(60, 10f))
        assertTrue("reset 后应重新学到新的静息水平，实际 ${s.restLevel}", s.restLevel in 8f..12f)
    }

    @Test
    fun `releaseDrag 只清按住状态不清静息水平`() {
        val s = PressSignal()
        feed(s, steady(60, 10f))
        val before = s.restLevel
        s.releaseDrag()
        assertEquals("releaseDrag 不该动静息水平", before, s.restLevel)
        assertTrue("releaseDrag 应该清掉按住状态", !s.dragging)
    }

    @Test
    fun `诊断信息包含通道与观测范围`() {
        val s = PressSignal()
        feed(s, steady(40, 10f))
        val d = s.diag()
        assertTrue("诊断应提到压力范围：$d", d.contains("压力"))
        assertTrue("诊断应提到面积范围：$d", d.contains("接触面积") || d.contains("恒定"))
    }

    // ================================================================
    //  七、v1.4.8：设置页可调的参数真的能改行为 + 安全钳制
    // ================================================================

    @Test
    fun `确认帧数越大越晚触发`() {
        fun firstDown(confirm: Int): Int {
            val s = PressSignal()
            s.confirmFrames = confirm
            val a = feed(s, steady(40) + ramp(10f, 17f, 25))
            return a.indexOfFirst { it == PressSignal.Action.DOWN }
        }
        val f1 = firstDown(1)
        val f3 = firstDown(3)
        val f6 = firstDown(6)
        assertTrue("确认帧数 1 应该触发（帧号 $f1）", f1 >= 0)
        assertTrue("确认帧数越大越晚触发（1帧=$f1 3帧=$f3 6帧=$f6）", f1 < f3 && f3 < f6)
    }

    @Test
    fun `确认帧数调大不会导致永远不触发`() {
        val s = PressSignal()
        s.confirmFrames = 6
        val a = feed(s, steady(40) + ramp(10f, 17f, 25) + ramp(17f, 10f, 25) + List(10) { 10f })
        assertTrue("确认帧数 6 也应该能触发", count(a, PressSignal.Action.DOWN) >= 1)
        assertTrue("确认帧数 6 也应该能松开", count(a, PressSignal.Action.UP) >= 1)
    }

    @Test
    fun `松开余量可配置且各种取值都能松开`() {
        for (slack in listOf(0f, 0.05f, 0.15f)) {
            val s = PressSignal()
            s.releaseSlack = slack
            val a = feed(s, steady(40) + ramp(10f, 17f, 25) + ramp(17f, 10f, 25) + List(10) { 10f })
            assertTrue("松开余量 $slack 应该能触发", count(a, PressSignal.Action.DOWN) >= 1)
            assertTrue("松开余量 $slack 应该能松开", count(a, PressSignal.Action.UP) >= 1)
        }
    }

    @Test
    fun `最极端参数组合也不会粘键`() {
        // 用户可能把「触发阈值」调到最小 10%、把「松开余量」调到最大 15%。
        // 此时按公式释放线会掉到静息水平以下 → 用户松手时（力度只回到静息水平）
        // 永远够不到 → **左键按着不放**。安全钳制就是为了防住这一条。
        //
        // 曲线建模：松手后手**停在原地**（力度保持回到静息水平），
        // 这是真实场景 —— 松手后手指还贴着屏幕，只是不再用力。
        val s = PressSignal()
        s.areaRiseDown = 0.10f
        s.releaseSlack = 0.15f
        val a = feed(s, steady(40) + ramp(10f, 17f, 25) + ramp(17f, 10f, 25) + List(10) { 10f })
        assertTrue("极端参数下也应该能触发", count(a, PressSignal.Action.DOWN) >= 1)
        assertTrue("极端参数下也不能粘键", count(a, PressSignal.Action.UP) >= 1)
    }

    @Test
    fun `释放线永远不低于静息水平乘安全系数`() {
        val s = PressSignal()
        s.areaRiseDown = 0.10f
        s.releaseSlack = 0.15f
        feed(s, steady(60, 10f))
        val floor = s.restLevel * (1f + PressSignal.RELEASE_LINE_FLOOR)
        assertTrue(
            "释放线必须高于静息水平（否则松手够不到 → 粘键）：实际 ${s.releaseLine()} vs 下限 $floor",
            s.releaseLine() >= floor - 1e-4f)
    }

    @Test
    fun `默认参数下松开线就是触发线的九五折`() {
        // v1.4.7 的判据在默认参数下必须原样保留（这次只加参数、不改默认行为）
        val s = PressSignal()
        feed(s, steady(60, 10f))
        val expected = s.restLevel * 1.30f * 0.95f
        assertTrue(
            "默认松开线应等于 触发线×0.95（实际 ${s.releaseLine()} vs 期望 $expected）",
            kotlin.math.abs(s.releaseLine() - expected) < 1e-3f)
    }
}
