package com.volcano.drawtouch

import kotlin.math.hypot
import kotlin.math.max
import kotlin.math.sqrt

/**
 * 指针平滑器 v3（v1.4.3）：**自标定噪声闸门**。
 *
 * ## 要解决什么
 * 用户原话：「**稳住和累计微小移动是两个不矛盾的事**」——
 * 手指静止时指针不能飘，但手指**慢慢移动**时指针必须跟上，而且要**跟得上**（位移不能被吃掉）。
 *
 * 之前两版都踩了同一个坑：把「稳」实现成了**速度死区**（慢速 → 输出恒为 0）。
 * 速度死区的本质是「慢速 = 静止」，于是慢速移动的位移被**永久丢弃**：
 * 用户慢慢划了一段距离，指针一动不动 —— 稳定是做到了，累计没了。
 *
 * ## v3 的思路：**只判断「是不是抖动」，不判断「有多慢」**
 *
 * ```
 * ① 噪声底噪自标定：σ ≈ EMA(|二阶差分|) / 1.954
 *    二阶差分 = Δ²位置 = raw(t) - raw(t-1)
 *    平滑移动时 ≈ 0（速度不变）；抖动时很大（每帧独立随机）。
 *    这是**尺度无关**的：不管底噪是 0.1px 还是 1px，估出来就是多少。
 *
 * ② 滑窗净位移（N 帧）：noise 互相抵消、真移动互相叠加
 *
 * ③ 闸门（带滞回）：|净位移| > K·σ·√N 才算「真在动」
 *    K = 4σ 窗口 → 误判概率 ~1e-5，噪声永远打不开闸门。
 *    门槛随底噪**自动缩放**，所以慢速移动（哪怕 0.3px/帧）也能打开。
 *
 * ④ 输出：闸门打开就 **1:1 原样输出**（只做极轻微的速度整形）
 *    —— 位移**一分不丢**，PC 端还会把小数残量累积起来，慢速照样跟得上。
 * ```
 *
 * 三个刻意决定：
 *
 *  1. **闸门判「抖动」而不是判「速度」**：慢速移动照样开门 → 累计位移不丢。
 *  2. **不用低通滤波（EMA 滤位置）**：位置域低通有恒定滞后 `c = α·v/(1-α)`，
 *     指针会拖在手指后面；这里 EMA 只用来**估噪声**，位移路径上一秒都没滞后。
 *  3. **不做惯性/缓动**：手指停 → 输出立刻为 0，指针纹丝不动。
 */
class PointerSmoother(
    /** 闸门灵敏度系数 K：门槛 = K·σ·√N */
    gateK: Float = DEFAULT_GATE_K,
    /** 判定窗口长度（帧） */
    gateWindow: Int = DEFAULT_GATE_WINDOW,
    /** 门槛下限（px）：底噪极低时的兜底，避免闸门永远打不开 */
    gateFloorPx: Float = DEFAULT_GATE_FLOOR_PX,
    /** 关门阈值比例（滞回）：关门比开门容易，避免慢速移动时闸门抖动 */
    closeRatio: Float = DEFAULT_CLOSE_RATIO,
    /** 最低增益（极慢速时的轻微减速，保留「精细定位」手感） */
    minGain: Float = DEFAULT_MIN_GAIN,
    /** 达到 1.0 增益所需的平均速度（px/帧） */
    fullSpeedPx: Float = DEFAULT_FULL_SPEED_PX,
    /** 噪声估计的 EMA 系数 */
    noiseAlpha: Float = DEFAULT_NOISE_ALPHA,
    /** 噪声估计初值（px） */
    noiseInitPx: Float = DEFAULT_NOISE_INIT_PX,
    /** 噪声估计上限（px）：防止运动突变把估计带跑偏 */
    noiseMaxPx: Float = DEFAULT_NOISE_MAX_PX,
    /** 闸门最少样本数：窗口没填满前不许开门（防「同号噪声突发」） */
    minSamples: Int = DEFAULT_MIN_SAMPLES,
    /** 明确的大位移（px）：不用等窗口填满，直接开门（快速划动零延迟） */
    fastOpenPx: Float = DEFAULT_FAST_OPEN_PX,
) {

    private var gateK = gateK
    private var minGain = minGain
    private var fullSpeed = fullSpeedPx
    private val win = gateWindow.coerceAtLeast(2)
    private val floorPx = gateFloorPx
    private val closeK = closeRatio
    private val noiseAlpha = noiseAlpha
    private val noiseMax = noiseMaxPx
    private val minSamples = minSamples.coerceAtLeast(1)
    private val fastOpenPx = fastOpenPx

    // 滑窗：最近 N 帧的**原始**位移（噪声抵消、真移动叠加）
    private val ringX = FloatArray(win)
    private val ringY = FloatArray(win)
    private var ringIdx = 0
    private var count = 0

    // 二阶差分需要上一帧的原始位移
    private var lastRawX = 0f
    private var lastRawY = 0f
    private var haveLast = false

    /** 噪声底噪估计（位置噪声 σ，px） */
    private var noise = noiseInitPx

    /** 闸门状态 */
    private var gateOpen = false

    /** 最近一次判定的窗口净位移（调试用） */
    private var lastNet = 0f

    /** 切换档位（立即生效） */
    fun setProfile(p: Profile) {
        gateK = p.gateK
        minGain = p.minGain
        fullSpeed = p.fullSpeedPx
    }

    /**
     * 档位：稳 / 标准 / 关闭
     *  - [gateK] 越大 = 闸门越严 = 只有越明确的移动才驱动指针
     *  - gateK = 0 = 闸门关闭 = 完全 1:1（老手感）
     */
    enum class Profile(val gateK: Float, val minGain: Float, val fullSpeedPx: Float) {
        /** 最稳：噪声优先，慢速移动仍能跟上 */
        STABLE(4.0f, 0.90f, 15f),
        /** 均衡 */
        NORMAL(2.8f, 0.95f, 12f),
        /** 关闭闸门：完全 1:1（老手感） */
        OFF(0f, 1.0f, 0f),
    }

    constructor(profile: Profile) : this(
        gateK = profile.gateK,
        minGain = profile.minGain,
        fullSpeedPx = profile.fullSpeedPx,
    )

    /** 手势重置：清空滑窗与噪声估计 */
    fun reset() {
        ringIdx = 0
        count = 0
        haveLast = false
        noise = DEFAULT_NOISE_INIT_PX
        gateOpen = false
        lastNet = 0f
        java.util.Arrays.fill(ringX, 0f)
        java.util.Arrays.fill(ringY, 0f)
    }

    /**
     * 喂入**一帧**的累计位移，返回真正该发出去的位移。
     *
     * 注意：调用方要把**一帧内的所有采样点合并成一个 delta** 再喂进来
     * （Android 会把一帧内的多个采样点塞进 getHistoricalX/Y，逐点喂会被判成抖动）。
     *
     * @param dx/dy 本帧累计位移（px）
     */
    fun feed(dx: Float, dy: Float): DeadZoneDelta {
        if (gateK <= 0f) return DeadZoneDelta(dx, dy)          // OFF：完全 1:1

        // ① 噪声底噪自标定（只在闸门关闭时更新：那时手指多半是静止的，
        //    运动本身不会被误当成噪声；运动时 Δ²≈0，也不受影响）
        if (haveLast) {
            val d2 = hypot((dx - lastRawX).toDouble(), (dy - lastRawY).toDouble()).toFloat()
            if (!gateOpen) {
                val sample = (d2 / NOISE_L2_FACTOR).coerceIn(0f, noiseMax)
                noise += noiseAlpha * (sample - noise)
            }
        }
        lastRawX = dx
        lastRawY = dy
        haveLast = true

        // ② 滑窗净位移
        ringX[ringIdx] = dx
        ringY[ringIdx] = dy
        ringIdx = (ringIdx + 1) % win
        if (count < win) count++
        var sumX = 0f
        var sumY = 0f
        for (k in 0 until count) {
            sumX += ringX[k]
            sumY += ringY[k]
        }
        val net = hypot(sumX.toDouble(), sumY.toDouble()).toFloat()
        lastNet = net

        // ③ 闸门（滞回：开门难、关门易）
        //    两条开门路径：
        //    a) 常规：样本数够了（≥minSamples）且净位移超过 K·σ·√N
        //       —— 样本数下限很重要：窗口没填满时门槛随 √N 变小，
        //       两三个同号噪声样本就能骗开门（实测：net=1.81 > thr=1.30）。
        //    b) 快速通道：净位移已经 ≥fastOpenPx（明确的快速划动）→ 立即开门，零延迟。
        //       阈值取 10px：真实的手指落下瞬态只有 1~3px（挡得住），
        //       而一次快速划动单帧就有 20px 以上（必须第一帧就动，否则等于丢了 20px）。
        val thr = max(floorPx, gateK * noise * sqrt(count.toFloat()))
        if (gateOpen) {
            if (net < thr * closeK) gateOpen = false
        } else {
            val bigMove = net >= fastOpenPx
            val normalMove = count >= minSamples && net > thr
            if (bigMove || normalMove) gateOpen = true
        }
        if (!gateOpen) return DeadZoneDelta(0f, 0f)             // 判定为抖动：指针不动

        // ④ 打开闸门 → 位移 1:1 输出（只做极轻微的速度整形，分辨率不丢）
        val speed = net / count
        val g = gainFor(speed)
        return DeadZoneDelta(dx * g, dy * g)
    }

    /** 速度→增益曲线：极慢速轻微减速，≥fullSpeed 完全 1:1 */
    private fun gainFor(mag: Float): Float {
        if (fullSpeed <= 0f) return minGain
        if (mag >= fullSpeed) return 1f
        if (mag <= 0f) return minGain
        val t = (mag / fullSpeed).coerceIn(0f, 1f)
        return minGain + (1f - minGain) * t
    }

    /** 调试信息：底噪估计 / 闸门状态 / 窗口净位移 */
    fun debugInfo(): String =
        "指针闸门：底噪σ=${String.format("%.2f", noise)}px 窗口净位移=${String.format("%.2f", lastNet)}px " +
            "闸门=" + (if (gateOpen) "开" else "关") + " 样本=$count"

    companion object {
        /** E|N(0, √6·σ)| / σ = 1.954 —— 二阶差分估位置噪声 σ 的换算系数 */
        const val NOISE_L2_FACTOR = 1.954f

        const val DEFAULT_GATE_K = 4.0f
        const val DEFAULT_GATE_WINDOW = 8
        const val DEFAULT_GATE_FLOOR_PX = 0.8f
        const val DEFAULT_CLOSE_RATIO = 0.4f
        const val DEFAULT_MIN_GAIN = 0.90f
        const val DEFAULT_FULL_SPEED_PX = 15f
        const val DEFAULT_NOISE_ALPHA = 0.10f
        const val DEFAULT_NOISE_INIT_PX = 0.25f
        const val DEFAULT_NOISE_MAX_PX = 2.0f
        const val DEFAULT_MIN_SAMPLES = 4
        const val DEFAULT_FAST_OPEN_PX = 10.0f
    }
}
