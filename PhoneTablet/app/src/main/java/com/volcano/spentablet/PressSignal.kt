package com.volcano.spentablet

import kotlin.math.sqrt

/**
 * 「重按」检测信号源（v1.4.4 → v1.4.7）。
 *
 * ## 通道一：压力 `MotionEvent.getPressure()`
 * 绝大多数电容屏不提供真实压感（值恒为 1.00）——本机实测就是这种。
 * 另外本机的 `sec_touchscreen` **根本没有 ABS_MT_PRESSURE 轴**
 * （`getevent` 采集 461 个接触样本里 0 条压力事件）→ 真压感这条路不存在。
 *
 * ## 通道二：接触面积 `MotionEvent.getTouchMajor() / getTouchMinor()`
 * 手指按得越重，**电容椭圆的投影面积越大**。驱动只要上报
 * `ABS_MT_TOUCH_MAJOR / ABS_MT_MINOR` 就有这条通道（本机的
 * `sec_touchscreen` 明确声明了这两个轴，范围 0~255）。
 *
 * ## v1.4.7 的核心：判「变化量」，且参考水平必须是**真实的静息水平**
 *
 * 用户要的是（2026-10-30 原话）：
 * > 「要是识别按压的时候的变化，当有一定程度变化（相对值）就识别，而不是绝对值。」
 *
 * 所以判据是「相对近期静息水平涨了多少」：
 *
 * ```
 * 触发：size ≥ 静息水平 × (1 + 涨幅阈值)
 * 释放：size < 触发线 × 0.95      （力度回落到这次加力之前 → 加力结束）
 * ```
 *
 * 但「静息水平」取什么，是这一版真正要解决的问题。
 * 实测（2026-10-30 真机采集 461 个接触样本）暴露了三个硬伤，
 * 每一个都对应一次真实的失败：
 *
 * ### 硬伤一：落指瞬态会把静息水平拉到极低 → 每次触摸都误触发
 * 手指刚落下时接触面积从「几乎没接触」长到「压实」，
 * 实测最长可达 **45 帧（≈1.1s）**，这段是**上冲**。
 * 若把它当成静息水平，参考值会低到 7 左右 → 阈值只有 9 → **第 15 帧就误触发**，
 * 而后按钮一路按到用户抬手（实测按住 679 帧 = 16.6 秒）。
 *
 * ⇒ **趋势守卫**：只把「相比 N 帧前没有在上涨」的帧喂给静息窗口。
 * 上冲（落指、加力）一律不喂 → 静息水平只由「稳定/回落」的帧决定。
 * 开头 N 帧没有历史可比，**一律当作上冲**（这是硬伤一的真正根因）。
 *
 * ### 硬伤二：一次加力持续 0.5~1 秒，会把静息水平自己顶上去 → 越按越不灵
 * 实测：真实加力要 **20~40 帧**才长出来（0.5~1s），
 * 而 v1.4.6 的静息窗口只有 30 帧 —— 等窗口攒满，**加力已经长完了**，
 * 参考水平追着涨上去，涨幅被吃掉 → 触发被推迟到第 138 帧（3.4 秒），
 * 甚至完全漏掉。
 *
 * ⇒ **窗口拉长到 45 帧**，且**按住期间静息水平只降不升**
 * （一次加力可能持续好几秒，若静息水平跟着爬，「按住」就会被自己解除）。
 *
 * ### 硬伤三：释放条件是「跌破静息 12%」→ 松手了按钮还按着
 * 用户松手时力度回到静息水平（不是低于静息 12%），
 * 按旧规则**永远够不到释放线** → 粘键。
 *
 * ⇒ 释放改成「**力度回落到这次加力的触发线以下**」：
 * 触发线 = 静息水平 × (1+涨幅)，释放线 = 触发线 × 0.95。
 * 语义直白：**按下去之后只要松回平时的力度就算松手**。
 *
 * ## 持续确认 + 滞回
 * - 必须**连续** confirmFrames 帧超过阈值才判重按（瞬时尖峰不算）；
 * - 释放线比触发线低 5%，形成滞回带，避免边界反复横跳。
 *
 * ## 已知取舍（信号层面的硬限制，不是 bug）
 * 「落下瞬间就使劲」与「落指瞬态」在接触面积上是**同一种形状**，
 * 物理上不可区分。本版的选择是：**宁可漏掉，也不要每次触摸都误触发**
 * （漏了用户再按一次；错触则每次触摸都乱按左键，功能直接不可用）。
 * 稳下来之后再加力（滑动中加力、按下拖拽）都能正常识别。
 */
class PressSignal(
    /** 面积通道：相对静息水平高出多少算「重按」，如 0.30 = 高出 30% */
    var areaRiseDown: Float = DEFAULT_AREA_RISE_DOWN,
    /** 静息窗口：多少帧算「近期」（45 帧 ≈ 1.1s @41Hz） */
    var restWindow: Int = DEFAULT_REST_WINDOW,
    /** 静息水平就绪所需的**有效样本**数（只统计「非上冲」的帧） */
    var restMinSamples: Int = DEFAULT_REST_MIN_SAMPLES,
    /** 连续多少帧超过阈值才判「重按」 */
    var confirmFrames: Int = DEFAULT_CONFIRM_FRAMES,
    /** 连续多少帧回落到释放线以下才判「松开」 */
    var releaseFrames: Int = DEFAULT_RELEASE_FRAMES,
    /** 至少观察多少帧才开始仲裁通道 */
    var probeFrames: Int = DEFAULT_PROBE_FRAMES,
    /** 趋势守卫：跟多少帧前比 */
    var trendDelay: Int = DEFAULT_TREND_DELAY,
    /** 趋势守卫：比 N 帧前高出多少算「在上涨」 */
    var trendEps: Float = DEFAULT_TREND_EPS,
    /** 面积通道：面积变化至少要超过这个比例，通道才被认为可用 */
    var areaMinVariation: Float = DEFAULT_AREA_MIN_VARIATION,
    /**
     * 面积通道：释放滞回。释放线 = 触发线 × (1 - releaseSlack)。
     *
     * v1.4.8 改成**可调**（设置页「松开滞回」）：0 = 力度一低于触发线就松（最灵敏），
     * 30% = 要落回触发线下方 30% 才松（最不容易粘键，但可能松得太迟）。
     */
    var releaseSlack: Float = DEFAULT_RELEASE_SLACK,
    /** 压力通道：重按阈值 */
    var pressureDown: Float = DEFAULT_PRESSURE_DOWN,
    /** 压力通道：松开阈值 */
    var pressureUp: Float = DEFAULT_PRESSURE_UP,
) {

    enum class Action { NONE, DOWN, UP }

    enum class Channel {
        /** 两条通道都不可用 → 重按功能静默停用 */
        NONE,
        PRESSURE,
        AREA,
    }

    /** 当前选中的通道 */
    var channel: Channel = Channel.NONE
        private set

    /** 压力观测范围（自检用） */
    var pressureMin: Float = NO_SAMPLE
        private set
    var pressureMax: Float = 0f
        private set

    /** 接触尺寸观测范围（自检用） */
    var sizeMin: Float = NO_SAMPLE
        private set
    var sizeMax: Float = 0f
        private set

    /** 近期静息水平（有效帧的 P20，跟随手指走） */
    var restLevel: Float = 0f
        private set

    /** 实际使用的参考基线（面积通道 = 近期静息水平） */
    var baseline: Float = 0f
        private set

    /** 静息水平是否已就绪（有效样本攒够） */
    var restReady: Boolean = false
        private set

    /** 当前是否处于「按住左键」状态 */
    var dragging: Boolean = false
        private set

    private var sizeEma = 0f
    private var haveSize = false
    private var frames = 0
    private var pressureSamples = 0
    private var aboveCount = 0
    private var belowCount = 0
    private var gestureTriggered = false

    /** 近期接触尺寸样本（环形缓冲）—— 只装「非上冲」的帧 */
    private val restWindowBuf = ArrayList<Float>(DEFAULT_REST_WINDOW + 4)
    /** sizeEma 的近期历史（用于趋势判断） */
    private val hist = ArrayList<Float>(DEFAULT_TREND_DELAY + 4)

    /** 新手势开始：重新学静息水平 */
    fun reset() {
        channel = Channel.NONE
        pressureMin = NO_SAMPLE
        pressureMax = 0f
        sizeMin = NO_SAMPLE
        sizeMax = 0f
        restLevel = 0f
        baseline = 0f
        restReady = false
        sizeEma = 0f
        haveSize = false
        frames = 0
        pressureSamples = 0
        aboveCount = 0
        belowCount = 0
        gestureTriggered = false
        restWindowBuf.clear()
        hist.clear()
        dragging = false
    }

    /** 外部收尾（手指离开 / 手势重置）：把「按住」状态清掉，但不重置静息水平 */
    fun releaseDrag() {
        dragging = false
        aboveCount = 0
        belowCount = 0
    }

    /**
     * 喂一帧的触摸数据，返回本帧的动作。
     *
     * @param pressure `MotionEvent.getPressure(idx)`
     * @param major    `MotionEvent.getTouchMajor(idx)`
     * @param minor    `MotionEvent.getTouchMinor(idx)`
     */
    fun update(pressure: Float, major: Float, minor: Float): Action {
        frames++

        // ---- 压力通道：记录范围 ----
        if (pressure < pressureMin) pressureMin = pressure
        if (pressure > pressureMax) pressureMax = pressure
        pressureSamples++

        // ---- 面积通道：等效直径 = 椭圆长短轴的几何平均（minor 缺失时退回 major）----
        val size = when {
            major <= 0f -> 0f
            minor > 0f -> sqrt(major * minor)
            else -> major
        }
        if (size > 0f) {
            sizeEma = if (!haveSize) size else sizeEma + SIZE_EMA_ALPHA * (size - sizeEma)
            haveSize = true
            if (sizeEma < sizeMin) sizeMin = sizeEma
            if (sizeEma > sizeMax) sizeMax = sizeEma

            // ---- 趋势守卫：这帧是不是「在往上冲」----
            // 开头 trendDelay 帧没有历史可比 → 一律当作上冲（落指瞬态的根）
            hist.add(sizeEma)
            while (hist.size > trendDelay + 1) hist.removeAt(0)
            val rising = if (hist.size <= trendDelay) {
                true
            } else {
                val prev = hist[0]
                sizeEma > prev * (1f + trendEps)
            }

            // ---- 静息水平：只吃「非上冲」的帧 ----
            // 按住期间**整个冻结**：
            //   ① 不能被这次加力污染（加力期是上冲，本来也不会被喂）；
            //   ② 长按（可能持续好几秒）时稳态帧会持续进窗口，
            //      若让静息水平跟着爬，「按住」就会被自己解除 → 长按拖拽中途弹开。
            if (!rising && !dragging) {
                restWindowBuf.add(sizeEma)
                while (restWindowBuf.size > restWindow) restWindowBuf.removeAt(0)
                if (restWindowBuf.size >= restMinSamples) {
                    restLevel = percentile(restWindowBuf, REST_PERCENTILE)
                    restReady = true
                }
            }
            baseline = restLevel
        }

        // ---- 仲裁：压力优先，其次面积；判不出来就一直等 ----
        if (frames >= probeFrames) {
            if (pressureUsable()) {
                if (channel != Channel.PRESSURE) {
                    channel = Channel.PRESSURE
                    aboveCount = 0
                    belowCount = 0
                }
            } else if (channel == Channel.NONE && areaUsable()) {
                channel = Channel.AREA
            }
        }

        return when (channel) {
            Channel.PRESSURE -> {
                if (!dragging) {
                    if (pressure >= pressureDown) aboveCount++ else aboveCount = 0
                    if (aboveCount >= confirmFrames) {
                        dragging = true
                        gestureTriggered = true
                        aboveCount = 0
                        Action.DOWN
                    } else Action.NONE
                } else {
                    if (pressure <= pressureUp) belowCount++ else belowCount = 0
                    if (belowCount >= releaseFrames) {
                        dragging = false
                        belowCount = 0
                        Action.UP
                    } else Action.NONE
                }
            }
            Channel.AREA -> {
                val ref = baseline
                if (!dragging) {
                    // 触发条件：相对静息水平「涨了」多少（相对值，不是绝对值）
                    val thr = ref * (1f + areaRiseDown)
                    if (ref > 0f && sizeEma >= thr) aboveCount++ else aboveCount = 0
                    if (aboveCount >= confirmFrames) {
                        dragging = true
                        gestureTriggered = true
                        aboveCount = 0
                        Action.DOWN
                    } else Action.NONE
                } else {
                    // 释放条件：力度回落到**这次加力的触发线以下**。
                    // （旧规则是「跌破静息 12%」——用户松手时力度只是回到静息水平，
                    //  根本够不到那条线 → 按钮一直按着不松 = 粘键。）
                    //
                    // v1.4.8 安全钳制：释放线**不得低于「静息水平 × 1.05」**。
                    // 原因：这两个参数是用户可调的，若「松开余量」调大、
                    // 或「触发阈值(涨幅)」调小，释放线会掉到静息水平以下；
                    // 而用户松手时力度只回到静息水平附近 → **永远够不到 → 粘键**。
                    // 钳制之后，无论用户怎么调都不会把功能调成「松不掉」。
                    val thr = releaseLine()
                    if (ref > 0f && sizeEma < thr) belowCount++ else belowCount = 0
                    if (belowCount >= releaseFrames) {
                        dragging = false
                        belowCount = 0
                        Action.UP
                    } else Action.NONE
                }
            }
            Channel.NONE -> Action.NONE
        }
    }

    /**
     * 压力通道是否可用。三条都要满足：
     * 1. 样本足够；
     * 2. 压力确实有变化，且幅度足够大（排除小幅噪声）；
     * 3. **必须存在低于按下阈值的轻触样本** ——
     *    伪压感机器常年报 0.9~1.0，变化量够但全程都在阈值之上，
     *    判成压力通道就等于「永远按着」，一碰就误触。
     */
    private fun pressureUsable(): Boolean {
        if (pressureSamples < 2) return false
        if (pressureMax - pressureMin < PRESSURE_MIN_DELTA) return false
        return pressureMin < pressureDown
    }

    /** 面积通道是否可用：静息水平已就绪，且接触面积确有变化 */
    private fun areaUsable(): Boolean {
        if (!restReady || restLevel <= 0f) return false
        if (sizeMax <= 0f || sizeMin >= NO_SAMPLE) return false
        return sizeMax > sizeMin * (1f + areaMinVariation)
    }

    /** 自检结论（一行，给 debug 面板看） */
    fun diag(): String {
        val p = if (pressureSamples == 0) "无数据"
        else String.format("%.2f", pressureMin) + "~" + String.format("%.2f", pressureMax)
        val hasSize = sizeMax > 0f && sizeMin < NO_SAMPLE
        val s = if (hasSize) String.format("%.1f", sizeMin) + "~" + String.format("%.1f", sizeMax) else "无数据"
        val rise = if (restLevel > 0f) (sizeEma - restLevel) / restLevel else 0f
        return when (channel) {
            Channel.PRESSURE -> "重按通道：压力（$p），重按=按下左键已生效"
            Channel.AREA -> "重按通道：接触面积（$s，静息" + String.format("%.1f", restLevel) +
                "，当前+" + String.format("%.0f", rise * 100f) + "%，门槛+" +
                String.format("%.0f", areaRiseDown * 100f) + "%），重按=按下左键已生效"
            Channel.NONE -> "重按通道：压力（$p）、接触面积（$s）→ 无可用力度信号，已停用"
        }
    }

    /** 接触面积的实时快照（调试用）：当前值 / 观测范围 / 静息水平 / 相对涨幅 / 通道 */
    fun sizeDiag(): String {
        if (sizeMax <= 0f || sizeMin >= NO_SAMPLE) return "无数据"
        val rise = if (restLevel > 0f) (sizeEma - restLevel) / restLevel else 0f
        val thr = if (restLevel > 0f) restLevel * (1f + areaRiseDown) else 0f
        val rel = releaseLine()
        return String.format(
            "%.1f（范围 %.1f~%.1f，静息 %.1f，触发线 %.1f，松开线 %.1f，涨幅 %+.0f%%，%s）",
            sizeEma, sizeMin, sizeMax, restLevel, thr, rel, rise * 100f, channel)
    }

    /**
     * 当前的松开线（力度要落到它以下才算松手）。
     * 与 [update] 里用的是同一套公式（含安全钳制），保证诊断显示的就是真实生效值。
     */
    fun releaseLine(): Float {
        if (restLevel <= 0f) return 0f
        val trigLine = restLevel * (1f + areaRiseDown)
        val floorLine = restLevel * (1f + RELEASE_LINE_FLOOR)
        return maxOf(trigLine * (1f - releaseSlack), floorLine)
    }

    companion object {
        /**
         * 面积通道默认阈值：**相对静息水平高出 30%** 算重按。
         *
         * ## 阈值依据（真机实测，2026-10-30 采集 461 个接触样本）
         *
         * 采集方式：`getevent -lt /dev/input/event9`（sec_touchscreen），
         * 采集轻触 / 普通按 / 用力按 / 压着滑 / 滑动中加力 共 6 组动作，
         * 面积取 `sqrt(TOUCH_MAJOR × TOUCH_MINOR)` 再做 EMA(0.25) 平滑。
         *
         * ```
         * 静息（轻触/压着滑）面积   ≈ 9.5 ~ 11.5
         * 加力时峰值面积            ≈ 16 ~ 21
         * 相对静息(P20)的涨幅分布： p25 = -1%   p50 = +8%   p75 = +30%   p90 = +52%
         * ```
         *
         * 逐帧看：**静息段涨幅全部在 ±10% 内**（偶发 +27%），
         * **加力段是连续的一段 +39% ~ +68%**。
         * 所以 30% 卡在两者之间：高于静息段的偶发值(+27%)，低于加力段的下沿(+39%)。
         */
        const val DEFAULT_AREA_RISE_DOWN = 0.30f
        const val DEFAULT_AREA_MIN_VARIATION = 0.03f
        const val DEFAULT_PRESSURE_DOWN = 0.55f
        const val DEFAULT_PRESSURE_UP = 0.42f

        /**
         * 静息窗口：45 帧。
         *
         * 实测采样率 **≈41Hz**（中位间隔 24.3ms，不是 60Hz）。
         * 为什么不是 30 帧：一次真实加力要 **20~40 帧**才长出来（0.5~1s），
         * 窗口若比加力时长短，窗口里就全是加力后的高值 → 静息水平追着涨上去
         * → 涨幅被吃掉 → 触发推迟到第 138 帧（3.4 秒），甚至完全漏掉。
         * 45 帧(1.1s) 能罩住「加力中但还没长满」的阶段。
         */
        const val DEFAULT_REST_WINDOW = 45
        /**
         * 静息水平就绪所需的有效样本数：6 帧（≈0.15s）。
         *
         * 只统计「非上冲」的帧 —— 落指瞬态期间一个有效样本都没有，
         * 所以这条门槛不会误伤落指；同时保证稳下来后很快就能判重按。
         */
        const val DEFAULT_REST_MIN_SAMPLES = 6
        /**
         * 连续多少帧超过阈值才算重按。
         *
         * 实测：**最短的一次加力只维持 3 帧**（≈73ms @41Hz），
         * 所以要 4 帧会漏掉「快速按一下」这种最常见的操作 → 取 3。
         * 3 帧既能滤掉单帧尖峰，又不会漏掉快速加力。
         */
        const val DEFAULT_CONFIRM_FRAMES = 3
        /** 连续多少帧回落到释放线以下才算松开 */
        const val DEFAULT_RELEASE_FRAMES = 2
        /** 至少观察多少帧才开始仲裁通道 */
        const val DEFAULT_PROBE_FRAMES = 4

        /**
         * 趋势守卫：跟 4 帧前比（≈0.1s）。
         * 4 帧是「EM 平滑后还能看出方向」的最短跨度：单帧变化会被噪声淹没。
         */
        const val DEFAULT_TREND_DELAY = 4
        /**
         * 趋势守卫阈值：比 4 帧前高出 3% 就算「在上涨」。
         * 3% 远大于实测的帧间噪声（EMA 后约 1~2%），
         * 又远小于真实加力的斜率（实测 0.18/帧 ≈ 6%/帧）。
         */
        const val DEFAULT_TREND_EPS = 0.03f

        /** 静息水平取窗口内的 P20 分位数（抗离群低值，比 min 稳） */
        const val REST_PERCENTILE = 0.20f

        /**
         * 释放滞回：**释放线 = 触发线 × (1 - 5%)**。
         * 触发线在静息水平之上 30%，释放线在它下面 5% → 形成 25% 的滞回带，
         * 既能避免边界反复横跳，又保证「松回平时力度」就能松开。
         *
         * v1.4.8 起这个值可在设置页调（0%~30%），本常量只作为默认值。
         */
        const val DEFAULT_RELEASE_SLACK = 0.05f

        /**
         * 释放线安全下限：释放线至少要比**静息水平**高这么多（5%）。
         *
         * 「触发阈值(涨幅)」和「松开余量」都是用户可调的，
         * 两个都往极端调时释放线会掉到静息水平以下 —— 而用户松手时
         * 力度只会回到静息水平附近（实测静息 9.5~11.5，静息水平取的是 P20），
         * 于是永远够不到释放线 → **粘键**（左键按着不放）。
         * 钳制到「静息 × 1.05」后：回落一定够得到 → 任何调法都不会粘键。
         */
        const val RELEASE_LINE_FLOOR = 0.05f

        /** 压力通道可用性：变化幅度小于它就判「没有真压感」 */
        const val PRESSURE_MIN_DELTA = 0.15f

        /** 接触尺寸的平滑系数（EMA） */
        const val SIZE_EMA_ALPHA = 0.25f

        /** 「还没有样本」的占位（sizeMin / pressureMin 初值必须是 +∞，否则恒为 0） */
        const val NO_SAMPLE = Float.MAX_VALUE

        /** 分位数（p ∈ [0,1]，线性插值） */
        fun percentile(values: List<Float>, p: Float): Float {
            if (values.isEmpty()) return 0f
            val sorted = values.sorted()
            if (sorted.size == 1) return sorted[0]
            val pos = p * (sorted.size - 1)
            val lo = pos.toInt().coerceIn(0, sorted.size - 1)
            val hi = (lo + 1).coerceAtMost(sorted.size - 1)
            val frac = pos - lo
            return sorted[lo] + (sorted[hi] - sorted[lo]) * frac
        }
    }
}
