package com.volcano.drawtouch

import android.content.Context
import kotlin.math.cos
import kotlin.math.sin
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.util.Log
import android.view.MotionEvent
import android.view.View

/**
 * 全屏画布：抓取 S Pen（TOOL_TYPE_STYLUS）的落笔/移动/抬起/悬停，
 * 计算归一化坐标与压感/倾斜，回调给外部（→ 网络发送），同时在手机屏上画出轨迹供即时反馈。
 *
 * 已验证（SM-S9080 / Android 16）：S Pen 的落笔/移动/抬起都会以 TOOL_TYPE_STYLUS
 * 正常送达 onTouchEvent，toolType 过滤不是问题。
 *
 * ── 操作框（frame）────────────────────────────────────────────
 * 手机上画一个「操作框」，桌面端画一个同样形状的框，两者一一对应：
 *   · frameL/frameT —— 操作框左上角在书写区里的相对位置(0..1)
 *   · frameS        —— 操作框相对书写区的缩放(0.05..1)
 * 框的形状始终与书写区一致（等比缩放），所以「手机上的框」与「桌面上的框」
 * 永远是同一个宽高比 —— 捏合后也保持一样。
 *
 * 笔的坐标按**操作框**归一化，而不是按整个书写区：
 * 于是「框内的一笔」正好落在「桌面框内」，可以精准地只写进桌面上那一小块区域。
 */
class PenCanvasView(context: Context) : View(context) {

    /** type: Protocol.TYPE_PEN_*；x/y 为归一化 0..1；buttons: bit0=barrel, bit1=eraser */
    var listener: ((type: Int, x: Float, y: Float, pressure: Float, tiltX: Float, tiltY: Float, buttons: Int) -> Unit)? = null

    /** 诊断：把最后一条事件文本抛给界面显示 */
    var onDebug: ((String) -> Unit)? = null

    /**
     * 手指平移回调：(dx, dy) 为「操作框宽/高」的相对位移。
     * 数位板模式下手指滑动=移动操作框（手机框与桌面框一起动）。
     */
    var onPan: ((Float, Float) -> Unit)? = null

    /**
     * 鼠标模式下的手指回调：(kind, dx, dy)。
     * kind 见 Protocol.MOUSE_*；dx/dy 为相对书写区的归一化位移（仅 MOVE 使用）。
     */
    var onMouse: ((Int, Float, Float) -> Unit)? = null

    /**
     * 双指捏合回调：(scaleFactor, focusX, focusY)。
     * scaleFactor 是相对量（>1 放大 / <1 缩小）；focus 是捏合中心在**操作框内**的归一化坐标。
     * 只在数位板模式下触发（鼠标模式下没有范围框，缩放无意义）。
     */
    var onZoom: ((Float, Float, Float) -> Unit)? = null

    /**
     * 书写区尺寸变化回调。上下控制栏会挤压中间书写区，
     * 尺寸变了必须重新告诉 PC 手机端的宽高比，否则等比映射会算错。
     */
    var onSurfaceSizeChanged: ((Int, Int) -> Unit)? = null

    /** 操作框状态变化回调：(frameL, frameT, scale)，用于持久化 */
    var onFrameChanged: ((Float, Float, Float) -> Unit)? = null

    /**
     * 工作模式：Protocol.MODE_TABLET / Protocol.MODE_MOUSE。
     * 两种模式下手指的用途**互斥**，绝不既拖框又控鼠标：
     *   MODE_TABLET —— 手指只走 onPan（拖操作框），不发任何鼠标帧；
     *   MODE_MOUSE  —— 手指只走 onMouse（当触摸板），不发任何平移帧。
     */
    var workMode: Int = Protocol.MODE_TABLET
        set(value) {
            if (field == value) return
            field = value
            resetFinger()
            invalidate()
        }

    // ---- 操作框状态（相对书写区，0..1）----
    private var frameL = 0f
    private var frameT = 0f
    private var frameS = 1f

    val frameLeft: Float get() = frameL
    val frameTop: Float get() = frameT
    val frameScale: Float get() = frameS

    /** 恢复之前保存的操作框（重开 App 时用） */
    fun restoreFrame(l: Float, t: Float, s: Float) {
        frameS = s.coerceIn(MIN_FRAME, 1f)
        // 不钳制 frameL/frameT 到 [0,1]：操作框要能移出书写区、去到任意显示器，
        // 真正的边界由 PC 端按虚拟屏幕约束，两端只保持「相对量」一致即可。
        frameL = l
        frameT = t
        invalidate()
    }

    /** 复位：操作框恢复为整个书写区 */
    fun resetFrame() {
        frameL = 0f; frameT = 0f; frameS = 1f
        invalidate()
        onFrameChanged?.invoke(frameL, frameT, frameS)
    }

    /**
     * 压感增益。内核声明 ABS_PRESSURE 量程 0~4095，但 S Pen 实际书写只到 ~500，
     * 直接上报的话压感只有 0.1 左右，在绘图软件里几乎看不出粗细变化。
     * 默认 3.0 把常用书写区间拉到 0.3~1.0，可在界面上按 ± 调整。
     */
    var pressureGain: Float = 3.0f

    /** 是否转发悬停事件（关掉可大幅降低数据量） */
    var sendHover: Boolean = true

    // ---- 触摸板模式配置（MainActivity 从 prefs 读入，实时生效）----
    /** 光标速度倍率（1.0=原速），作用在 MOUSE_MOVE 的归一化位移上 */
    var cursorSpeed: Float = 1.0f
    /** 滚动比例倍率（1.0=原速），作用在 MOUSE_SCROLL 的归一化位移上 */
    var scrollScale: Float = 1.0f
    /** 触摸板区域背景色 */
    var trackpadColor: Int = 0xFF20242B.toInt()

    // ---- 触觉反馈 / 重按（2026-10-30）----
    /** 震动反馈总开关（设置页可关；关掉后所有马达反馈都是空操作） */
    var hapticsEnabled: Boolean = true
        set(v) { field = v; Haptics.enabled = v }
    /** 重按 = 按下左键：手指用力按屏幕即按下左键拖动，回落即松开 */
    var pressToDragEnabled: Boolean = true
    /** 重按判定阈值（0~1 的压力）：按下 / 松开用滞回，避免在阈值附近抖动 */
    var pressDownThreshold: Float = 0.55f
    var pressUpThreshold: Float = 0.42f
    /**
     * 重按判定阈值（接触面积）：相对**近期静息水平**高出多少 % 算重按。
     *
     * v1.4.6 语义变更：以前是「比手势开头的固定基线大多少 %」（默认 200 = ×2.0），
     * 在「手指压着滑」的触摸板用法下永远够不到（落下时本就带着力）。
     * 现在判「按压过程中相对静息水平**涨了多少**」—— 这才是真正在变的量。
     *
     * v1.4.7：静息水平改为「只吃非上冲帧的 P20」，落指瞬态不再污染它；
     * 释放条件改为「回落到本次加力的触发线以下」，松手就能松开（不再粘键）。
     * 默认 30%：实测静息段涨幅全在 ±10%（偶发 +27%），加力段是连续的 +39%~+68%。
     */
    var pressAreaRisePct: Float = 30f
    /**
     * 确认帧数：连续多少帧超过阈值才判「重按」（v1.4.8 起可在设置页调）。
     *
     * - 1 帧 = 最快最灵敏，但任何单帧抖动都会触发；
     * - 3 帧 = 默认（实测最短的一次真实加力只有 3 帧，再大就会漏掉「快速按一下」）；
     * - 6 帧 ≈ 0.15s = 最迟钝，几乎不会被误触发但明显感觉延迟。
     */
    var pressConfirmFrames: Int = 3
    /**
     * 松开余量（%）：力度要比**触发线**低这么多才算松手（v1.4.8 起可在设置页调）。
     *
     * - 0% = 力度一低于触发线就松（最灵敏）；
     * - 5% = 默认，形成 25% 的滞回带，既不抖也不粘；
     * - 15% = 要落回触发线下方 15% 才松（最不容易粘键，但松得迟）。
     *
     * 安全兜底（见 [PressSignal.RELEASE_LINE_FLOOR]）：释放线永远不低于
     * 「静息水平 × 1.05」，所以无论怎么调都不会把功能调成「松不掉」。
     */
    var pressReleaseSlackPct: Float = 5f
    /**
     * 重按信号源：压力通道 + 接触面积通道自动仲裁（见 [PressSignal]）。
     * 本机压力恒为 1.00（无压感），实际靠**接触面积**判定：
     * 手指压得越重，接触椭圆越大。
     */
    private val pressSignal = PressSignal()
    /** 当前是否处于「重按拖动」中（左键按着） */
    private var pressDragging = false

    // ---- 手指轨迹（跟手提示：手指划到哪一眼能看见，抬手后自动淡出）----
    /** 是否显示手指动作轨迹 */
    var showFingerTrail: Boolean = true
    /** 轨迹从画出到完全消失的时长(ms)；<= 0 视为关闭 */
    var trailLifeMs: Long = 800L
    /** 轨迹颜色；0 = 按模式自动选（深色触摸板用亮色，白底数位板用深蓝） */
    var trailColor: Int = 0

    // ---- 手势死区（可在设置页调整，0 = 关闭）----
    // （捏合死区字段已删除：2026-10-29 捏合判定放宽为「两指沿同一直线反向运动」，
    //   判定时不再看跨度变化，跨度死区彻底退场）
    /**
     * 移动死区（dp）：手指按下后，累计位移超过它才开始驱动**双指滚动 / 范围框拖动**。
     * 手抖时不会误触，移动手势起步更稳。
     *
     * v1.4.3：从 8dp 降到 3dp，并且**不再作用于指针移动**——
     * 指针的「稳」改由[PointerSmoother]的自标定噪声闸门负责（它只判「抖动 vs 真在动」，
     * 不掐速度，所以慢速移动照样跟得上）；死区再叠一层只会让慢速拖动又慢半拍。
     */
    var moveDeadZoneDp: Float = 3f

    /** dp → px（按当前屏幕密度换算） */
    private fun dpPx(dp: Float): Float = dp * resources.displayMetrics.density

    /**
     * 指针平滑器（自标定噪声闸门，v1.4.3）：
     * 只判断「这一下是抖动还是真在动」，**不判断有多慢** ——
     * 所以慢速移动照样跟得上（位移一分不丢），而抖动不会让指针飘。
     */
    private val pointerSmoother = PointerSmoother(PointerSmoother.Profile.STABLE)

    /**
     * 指针平滑档位：STABLE（稳，默认）/ NORMAL（标准）/ OFF（关闭，完全 1:1）。
     * 设置页切换后立即生效。
     */
    var pointerSmoothProfile: String = "STABLE"
        set(v) {
            field = v
            runCatching { pointerSmoother.setProfile(PointerSmoother.Profile.valueOf(v)) }
        }

    /** 指针闸门诊断（节流 1 秒）：底噪估计 / 闸门状态 / 窗口净位移 —— 调手感时看这行 */
    private var ptrGateLogAt = 0L

    private fun logPointerGate() {
        val now = android.os.SystemClock.uptimeMillis()
        if (now - ptrGateLogAt < 1000L) return
        ptrGateLogAt = now
        onDebug?.invoke(pointerSmoother.debugInfo() + "｜接触面 " + pressSignal.sizeDiag())
    }

    /** 触摸板：双指滑动的死区闸门（相对移动，不补回） */
    private val trackpadScrollGate = MoveDeadZone { dpPx(moveDeadZoneDp) }

    /** 数位板：手指拖操作框的死区闸门（绝对映射，需补回死区量保持 1:1） */
    private val panGate = MoveDeadZone(catchUp = true) { dpPx(moveDeadZoneDp) }

    /**
     * 手机屏上本地笔迹的颜色。落笔（DOWN）时取当时的值写进 paint，
     * 所以换色只影响**之后的新笔**，已经画好的笔保留各自的颜色 —— 与 PC 端一致。
     */
    var strokeColor: Int = Color.BLACK

    // ---- 手势动作映射（槽位 → 动作码）----
    // 动作码：0=无 1=滚轮 2=缩放 3=左键 4=右键 5=中键 6=后退 7=前进 8=后退/前进(方向) 9=切换模式
    var g2Swipe: Int = 1   // 双指滑动：滚轮
    var g2Pinch: Int = 2   // 双指捏合：缩放（独立槽位，与双指滑动分开）
    var g2Tap: Int = 4     // 双指点击：右键
    var g3Tap: Int = 5     // 三指点击：中键
    // 三指 / 四指滑动：左右、上下各拆成独立槽位（每个方向可单独配置动作）
    var g3L: Int = 7   // 三指右滑：前进
    var g3R: Int = 6   // 三指左滑：后退
    var g3U: Int = 5   // 三指上滑：中键
    var g3D: Int = 3   // 三指下滑：左键
    var g4L: Int = 9   // 四指右滑：切换模式
    var g4R: Int = 0   // 四指左滑：无
    var g4U: Int = 10  // 四指上滑：音量+
    var g4D: Int = 11  // 四指下滑：音量-
    var g4Tap: Int = 9   // 四指点击：切换模式
    /** 四指等手势触发「切换模式」时回调给 MainActivity */
    var onSwitchMode: (() -> Unit)? = null

    /**
     * 某个鼠标动作（点击类：左/中/右键等）被触发时回调，用于在手机端对应的浮动按钮上
     * 给出视觉反馈（闪烁高亮），让用户看清「这次手势打中了哪个键」。
     * [code] 即 Protocol.MOUSE_* 动作码。
     */
    var onActionVisual: ((Int) -> Unit)? = null

    /** 手指/笔刚按下屏幕（每次手势一次）。MainActivity 用来打断无极滚动的惯性 */
    var onTouchDown: (() -> Unit)? = null

    /**
     * 手指是否正落在触摸板上（true=落下，false=离开）。
     * 触摸板模式下用来通知 PC「S Pen 让位」——
     * 笔只要在平板上（哪怕只是放着），它的悬停流就会一帧一帧把桌面光标拽回笔的位置，
     * 手指的相对移动被反复覆盖，表现为「手指往右下微动，光标却往左跑」。
     */
    var onFingerState: ((Boolean) -> Unit)? = null

    /** 截图捕获后锁定：忽略手指拖框/捏合，直到截图清除 */
    var frameLocked: Boolean = false

    /** 发送滚轮（滚轮条用）：dyNorm 为相对书写区高度的归一化位移 */
    fun sendWheel(dyNorm: Float) {
        onMouse?.invoke(Protocol.MOUSE_SCROLL, 0f, dyNorm * scrollScale)
    }

    /** 发送点击（底部按键用）：kind 见 Protocol.MOUSE_LEFT/RIGHT/MIDDLE_CLICK */
    fun sendClick(kind: Int) {
        onMouse?.invoke(kind, 0f, 0f)
    }

    /** 鼠标左键「按下」（按住不放 = 拖拽 / 涂荧光） */
    fun sendLeftDown() {
        onMouse?.invoke(Protocol.MOUSE_LEFT_DOWN, 0f, 0f)
    }

    /** 鼠标左键「抬起」 */
    fun sendLeftUp() {
        onMouse?.invoke(Protocol.MOUSE_LEFT_UP, 0f, 0f)
    }

    /**
     * 发送光标相对位移（dxPx / dyPx 为屏幕像素）。
     *
     * 横纵**用同一个除数**（书写区高度）归一化：以前 dx 除以宽、dy 除以高，
     * 手机是竖屏（宽!=高），同样的手指位移横向和纵向算出来的归一化值不一样，
     * 光标就表现为横竖速度差好几倍。统一后：手指划过多少像素，光标横竖都走同样的比例。
     * （PC 端也统一乘同一个系数 VirtualH，见 TcpPenServer.HandleMouse）
     */
    private fun sendMouseMove(dxPx: Float, dyPx: Float) {
        if (dxPx == 0f && dyPx == 0f) return
        val base = if (height > 0) height.toFloat() else 1f
        onMouse?.invoke(Protocol.MOUSE_MOVE, dxPx / base * cursorSpeed, dyPx / base * cursorSpeed)
    }

    private var bitmap: Bitmap? = null
    private var canvas: Canvas? = null

    /**
     * 一条笔迹：落笔时锁定的颜色 + 点序列（x,y 交替存放）。
     * 平时画到 [bitmap] 上是增量（每事件 O(1)），记录点列只为了**撤销/清空**时能
     * 在手机屏上把对应笔迹一并去掉（与 PC 端叠加层行为一致）。
     */
    private class Stroke(val color: Int) {
        /** 点序列（x,y 交替存放） */
        val pts = mutableListOf<Float>()
        /** 每个点的落笔时刻 —— 画笔模式按它淡出 */
        val ts = mutableListOf<Long>()
        val size: Int get() = ts.size
    }
    private val strokes = ArrayList<Stroke>()
    private var curStroke: Stroke? = null

    /**
     * 画笔模式（笔迹只画在手机上、不进桌面）：开关由 MainActivity 设置。
     * 这个模式下笔画**按时间淡出消失** —— 手机当草稿纸用，不留一堆残迹，
     * 也正因如此「撤销 / 清空」就没意义了（笔画本来就会自己没），按钮会一并隐藏。
     */
    var localInk: Boolean = false
    /** 画笔模式下笔迹从画出到完全消失的时长(ms)；0 = 不淡出（一直留着） */
    var localInkFadeMs: Long = 6000L

    /** 当前是否走「笔迹淡出」路径（画笔模式 + 淡出时长 > 0） */
    private val fadingInk: Boolean get() = localInk && localInkFadeMs > 0

    /**
     * 画笔模式的笔迹：不写进位图层，直接按时间戳逐段画，**每一段按自己的年龄变淡**，
     * 整笔过期就丢掉。手机当草稿纸用 —— 写完不用管，过一会儿自己干净。
     *
     * 只要还有笔画活着就 postInvalidateOnAnimation() 接着画下一帧，
     * 全部消失后自然停止重绘（静止时零开销）。
     */
    private fun drawFadingInk(c: Canvas) {
        val now = android.os.SystemClock.uptimeMillis()
        val life = localInkFadeMs.coerceAtLeast(1L).toFloat()
        var alive = false
        var i = 0
        while (i < strokes.size) {
            val s = strokes[i]
            val n = s.size
            // 整笔都过期（连最后一个点都超龄）→ 丢掉，不用再画
            if (n == 0 || now - s.ts[n - 1] > localInkFadeMs) {
                strokes.removeAt(i)
                continue
            }
            alive = true
            paint.color = s.color
            if (n == 1) {
                val a = (1f - (now - s.ts[0]) / life).coerceIn(0f, 1f)
                paint.alpha = (a * 255f).toInt()
                c.drawPoint(s.pts[0], s.pts[1], paint)
            } else {
                for (k in 1 until n) {
                    val a = (1f - (now - s.ts[k]) / life).coerceIn(0f, 1f)
                    paint.alpha = (a * 255f).toInt()
                    c.drawLine(s.pts[(k - 1) * 2], s.pts[(k - 1) * 2 + 1],
                        s.pts[k * 2], s.pts[k * 2 + 1], paint)
                }
            }
            i++
        }
        paint.alpha = 255
        if (alive) postInvalidateOnAnimation()
    }

    private val paint = Paint().apply {
        color = Color.BLACK
        strokeWidth = 4f
        isAntiAlias = true
        style = Paint.Style.STROKE
        strokeCap = Paint.Cap.ROUND
        strokeJoin = Paint.Join.ROUND
    }

    /** 操作框边框：明确告诉用户「只有这一块会被映射到 PC」 */
    private val borderPaint = Paint().apply {
        color = Color.parseColor("#FF2F80ED")
        style = Paint.Style.STROKE
        strokeWidth = 5f
        isAntiAlias = true
    }

    private var lastX = 0f
    private var lastY = 0f
    private var hasLast = false
    private var lastHoverAt = 0L
    private var moveLogCount = 0

    // 手指平移状态（数位板模式）
    private var panLastX = 0f
    private var panLastY = 0f
    private var panning = false

    // 手指鼠标状态（鼠标模式）
    private var fingerDown = false
    private var fingerLastX = 0f
    private var fingerLastY = 0f
    private var fingerMoved = 0f        // 本次按下的累计位移(px)
    private var fingerDownAt = 0L
    private val handler = android.os.Handler(android.os.Looper.getMainLooper())

    /**
     * 单指敲击判定：单击 / 双击 / 双击不松手拖拽。
     *
     * 判定逻辑全在 [TapDrag] 里（纯 JVM 可单测），这里只把结果翻译成协议帧。
     * 阈值按屏幕密度换算，跟手手感才一致。
     */
    private val tapDrag = TapDrag(
        doubleTapWindowMs = DOUBLE_TAP_WINDOW_MS,
        tapTimeoutMs = TAP_TIMEOUT_MS.toLong(),
        doubleTapSlopPx = 48f * resources.displayMetrics.density,
        dragSlopPx = 12f * resources.displayMetrics.density,
    )

    /**
     * 重按（力度）→ 左键按下 / 松开。
     *
     * Android 的 MotionEvent 有两条可能表达「用力」的通道：
     *  ① `getPressure()` —— 真实压感（0~1）。大量机型恒为 1.0（本机实测就是）。
     *  ② `getTouchMajor()/getTouchMinor()` —— **接触椭圆的尺寸**：
     *     手指压得越重，接触面越大。驱动上报 ABS_MT_TOUCH_MAJOR/MINOR 就有这条通道
     *     （本机的 sec_touchscreen 明确声明了这两个轴）。
     *
     * [PressSignal] 自动仲裁：压力可用就用压力，否则退化到接触面积。
     *
     * v1.4.6：面积判据改成「**相对近期静息水平涨了多少**」（相对值），
     * 不再是「相对一个冻结的历史基线涨了几倍」——
     * 触摸板就是「手指压着滑」，落下时本就带着力，固定基线偏高会导致永远触发不了。
     *
     * v1.4.7：释放条件不再由外部传（改成「回落到本次加力的触发线以下」，
     * 见 [PressSignal.releaseSlack]），所以这里不再设置 areaRiseUp。
     */
    private fun updatePressDrag(e: android.view.MotionEvent, idx: Int) {
        if (!pressToDragEnabled || idx < 0) return
        pressSignal.pressureDown = pressDownThreshold
        pressSignal.pressureUp = pressUpThreshold
        // 触发阈值：相对近期静息水平高出多少 % 算重按（设置项直接是百分比）
        pressSignal.areaRiseDown = (pressAreaRisePct / 100f).coerceAtLeast(0.02f)
        // 确认帧数 + 松开滞回（v1.4.8 设置项可调）
        pressSignal.confirmFrames = pressConfirmFrames.coerceIn(1, 12)
        pressSignal.releaseSlack = (pressReleaseSlackPct / 100f).coerceIn(0f, 0.5f)
        val action = pressSignal.update(
            e.getPressure(idx),
            e.getTouchMajor(idx),
            e.getTouchMinor(idx),
        )
        when (action) {
            PressSignal.Action.DOWN -> {
                pressDragging = true
                onMouse?.invoke(Protocol.MOUSE_LEFT_DOWN, 0f, 0f)
                onActionVisual?.invoke(Protocol.MOUSE_LEFT_CLICK)
                Haptics.pressDown()          // 重一点的一下
                onDebug?.invoke("重按 → 按下左键：" + pressSignal.diag())
            }
            PressSignal.Action.UP -> {
                pressDragging = false
                onMouse?.invoke(Protocol.MOUSE_LEFT_UP, 0f, 0f)
                Haptics.pressUp()                // 更轻更短的一下
                onDebug?.invoke("力度回落 → 松开左键：" + pressSignal.diag())
            }
            PressSignal.Action.NONE -> Unit
        }
    }

    /**
     * 重按识别的**实时状态**（供设置页「查看当前识别状态」按钮使用）。
     *
     * 用户自己调参数时需要立刻看到「现在走的是哪条通道 / 静息水平多少 /
     * 当前涨幅多少 / 门槛在哪」，否则只能靠体感猜。
     */
    fun pressDiagText(): String {
        val state = if (pressToDragEnabled) "识别已开启" else "识别已关闭"
        return "$state｜${pressSignal.diag()}｜${pressSignal.sizeDiag()}"
    }

    /**
     * 重按自检结论只报一次（每次 App 启动一次），免得刷屏：
     * 让用户一眼知道「这台机器到底能不能识别重按、走的哪条通道」，
     * 两条通道都不可用时重按功能会自己停用。
     */
    private var pressDiagLogged = false
    private fun logPressureDiag() {
        if (pressDiagLogged || !pressToDragEnabled) return
        pressDiagLogged = true
        onDebug?.invoke(pressSignal.diag())
    }

    /**
     * 手指离开 / 手势重置：重按拖着的状态必须收尾（补发左键抬起 + 轻震动）。
     *
     * v1.4.8：改成 public —— 设置页关闭「按压识别」时必须立刻松开左键，
     * 否则用户关掉开关后左键还一直按着（要等下一次抬手才恢复）。
     */
    fun releasePressDrag(reason: String) {
        if (!pressDragging) return
        pressDragging = false
        pressSignal.releaseDrag()
        onMouse?.invoke(Protocol.MOUSE_LEFT_UP, 0f, 0f)
        Haptics.pressUp()
        onDebug?.invoke("$reason → 松开左键（重按收尾）")
    }

    /** 单击的延迟派发：等双击窗口过去、确认没有第二下，才发左键 */
    private val singleTapAction = Runnable {
        onMouse?.invoke(Protocol.MOUSE_LEFT_CLICK, 0f, 0f)
        Haptics.click()          // 模拟鼠标微动开关的那一下「哒」
        onDebug?.invoke("单击 → 鼠标左键")
    }

    /** 双指捏合检测器 */
    private val scaleDetector = android.view.ScaleGestureDetector(context, ScaleListener())
    private var pinching = false

    /** 敲击判定阈值（随屏幕密度换算成 px） */
    private val tapSlopPx: Float get() = 12f * resources.displayMetrics.density

    // ---- 手指轨迹 ----
    /** 一根手指的轨迹点。同屏可能同时有两三根手指，所以按 pointerId 分开存，避免连线串成折线 */
    private class Trail {
        val xs = ArrayList<Float>()
        val ys = ArrayList<Float>()
        val ts = ArrayList<Long>()
        val size: Int get() = ts.size
        fun clear() { xs.clear(); ys.clear(); ts.clear() }
        /** 只保留最近 [TRAIL_MAX_POINTS] 个点：长时间划动也不会把内存吃掉 */
        fun trim() {
            while (ts.size > TRAIL_MAX_POINTS) {
                ts.removeAt(0); xs.removeAt(0); ys.removeAt(0)
            }
        }
    }

    private val trails = HashMap<Int, Trail>()
    /** 轨迹采样最小步长(dp)与最小间隔(ms)：太密没有观感收益，只是白费内存 */
    private val trailMinStepPx: Float get() = dpPx(1.5f)
    private val trailPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        strokeCap = Paint.Cap.ROUND
        strokeJoin = Paint.Join.ROUND
    }

    // ---- 鼠标模式：多点触控（模拟笔记本触摸板）----
    /** 本次手势中出现过的最多手指数：1=单指 2=双指 3=三指 */
    private var tpMaxPointers = 0
    private var tpLastCx = 0f
    private var tpLastCy = 0f
    private var tpLastSpan = 0f
    private var tpStartX = 0f
    private var tpStartY = 0f
    private var tpStartTime = 0L
    private var tpMoved = 0f
    private var tpSingleX = 0f
    private var tpSingleY = 0f
    /** 当前压在屏幕上的手指数量（>=2 时光标不许动） */
    private var tpActiveFingers = 0
    /** 鼠标模式下 S Pen 是否处于按下状态 */
    private var tpPenDown = false
    /** 触摸板主手指的 pointerId（取历史采样点用） */
    private var tpFingerId = -1
    /** 捏合型一次性动作（音量/任务切换）的上次触发时间，用于节流 */
    private var lastPinchActionAt = 0L
    /** 滑动型一次性动作（三指/四指左右滑）的上次触发时间，用于节流 */
    private var lastSwipeActionAt = 0L

    /**
     * 双指手势态（触摸板模式）：
     *  0 = 未定 —— 不输出，每帧尝试判定；
     *  1 = 缩放（两指都动 + 沿同一直线**反向**运动即进入，幅度不必对称；之后只看两指间距）；
     *  2 = 滚动（轴锁三态：竖锁 / 横锁 / 斜向双轴）；
     *  3 = 单指跟随（一指静止 → 忽略静止指，按单指滑动移动光标）。
     * 判成 1/2/3 后本手势固定；**进入移动识别（2/3）后不再处理缩放**（用户规格）。
     */
    private var tp2State = 0
    /**
     * 捏合锁存（2026-10-29 用户规格）：本次触摸序列一旦进入过捏合态，
     * **直到全部手指离开前不再发任何移动指令**——两指都还在时不退出缩放态、
     * 抬掉一指后剩下的手指只更新基准不驱动光标。
     * 全部手指抬起（ACTION_UP）/ 新手势按下 / 切模式时解锁。
     */
    private var tpPinchLatched = false
    /** 第二根手指落下的时刻：给捏合判定留启动窗口，窗口内不锁单指跟随（防止抢跑） */
    private var tpTwoFingerAt = 0L
    /** 双指跟踪：两指的 pointerId、上帧坐标与累计位移（缩放前提 / 单指跟随判定用） */
    private var tpIdA = -1
    private var tpIdB = -1
    private var tpAccumA = 0f
    private var tpAccumB = 0f
    /** 两指自手势开始的累计位移**向量**（捏合判定「反向运动」用，2026-10-29 规格） */
    private var tpVecAX = 0f
    private var tpVecAY = 0f
    private var tpVecBX = 0f
    private var tpVecBY = 0f
    private var tpPrevAX = 0f
    private var tpPrevAY = 0f
    private var tpPrevBX = 0f
    private var tpPrevBY = 0f

    /**
     * 双指滚动的轴锁：0 未锁（含「还没判」和「判成斜向」）/ 1 只横滚 / 2 只竖滚。
     * 与 [tpScrollAxisDecided] 配合使用：判定一旦做出就**不再改判**（含斜向）——
     * 否则斜着滑时手指轻微偏一点竖/横就会被锁成单轴，
     * 表现为「起手能斜着走，滑一小段就被锁死」（2026-10-30 用户反馈修正）。
     */
    private var tpScrollAxis = 0
    /** 轴锁是否已经判定过（true = 本次手势固定，不再重新判定） */
    private var tpScrollAxisDecided = false
    /**
     * 判定轴锁用的**滑动窗口**：最近 RECENT_FRAMES 帧的质心净位移（px）。
     * 为什么不用「从手势开始的累计位移」：起手那一下的抖动会被永久记住，
     * 方向噪声就能把手势锁死到错误的轴（2026-10-30 用户反馈「横向、斜向都用不了」）。
     * 用最近几帧的净位移，判的是**当前真正在往哪滑**。
     */
    private val recentX = FloatArray(RECENT_FRAMES)
    private val recentY = FloatArray(RECENT_FRAMES)
    private var recentIdx = 0
    private var recentCount = 0
    private var recentSumX = 0f
    private var recentSumY = 0f

    private fun resetRecentWindow() {
        recentX.fill(0f)
        recentY.fill(0f)
        recentIdx = 0
        recentCount = 0
        recentSumX = 0f
        recentSumY = 0f
    }

    /** 喂入一帧质心位移，滚动窗口前移（保持最近 RECENT_FRAMES 帧的和）。 */
    private fun pushRecent(dx: Float, dy: Float) {
        recentSumX += dx - recentX[recentIdx]
        recentSumY += dy - recentY[recentIdx]
        recentX[recentIdx] = dx
        recentY[recentIdx] = dy
        recentIdx = (recentIdx + 1) % RECENT_FRAMES
        if (recentCount < RECENT_FRAMES) recentCount++
    }
    /** 手势开始时的双指跨度（保留观测；捏合判定已于 2026-10-29 改为「两指反向运动」，不再用它） */
    private var tpStartSpan = 0f

    /**
     * 工作模式切换时调用：把触摸板 / 拖框手势的中间状态全部清零，
     * 并补发「左键抬起 / 手指离开触摸板」，防止切换瞬间按键卡死、让位状态残留。
     */
    fun resetGestures() {
        if (tpPenDown) {
            onMouse?.invoke(Protocol.MOUSE_LEFT_UP, 0f, 0f)
            tpPenDown = false
        }
        if (tpActiveFingers > 0) {
            onMouse?.invoke(Protocol.MOUSE_FINGER, 0f, 0f) // 通知 PC：手指已全部离开触摸板
        }
        tpActiveFingers = 0
        tpMaxPointers = 0
        tpMoved = 0f
        tpFingerId = -1
        tp2State = 0
        tpPinchLatched = false   // 手指全清：捏合锁存解除
        tpIdA = -1
        tpIdB = -1
        tpAccumA = 0f
        tpAccumB = 0f
        tpVecAX = 0f; tpVecAY = 0f; tpVecBX = 0f; tpVecBY = 0f
        tpStartSpan = 0f
        panning = false
        hasLast = false
        // 双击拖拽可能正按着左键，切模式前必须松开
        releaseTapDrag("切模式")
        // 死区闸门也要清干净，避免切换模式后残留上一模式的死区状态
        pointerSmoother.reset()
        trackpadScrollGate.reset()
        panGate.reset()
        clearTrail()
    }

    /**
     * 手势作废：左键若还按着（双击拖拽中）必须补发抬起，否则桌面鼠标会一直处于按住状态
     * —— 表现是「点什么都在拖选」，只能重启 host 才能恢复。
     */
    private fun releaseTapDrag(why: String) {
        if (tapDrag.reset() == TapDrag.Act.DRAG_UP) {
            onMouse?.invoke(Protocol.MOUSE_LEFT_UP, 0f, 0f)
            onDebug?.invoke("$why → 补发松开左键")
        }
        handler.removeCallbacks(singleTapAction)
    }

    /**
     * 兜底复位：屏幕上一根**手指**都不剩了。
     *
     * 为什么不能用 pointerCount 判断：笔按在屏幕上时 pointerCount 仍 > 0，
     * 而 `handleTrackpad` 只数手指，于是「笔按着 + 最后一根手指抬起」这一帧
     * 会在 `cnt == 0` 处直接 return —— 状态一个都不清：
     *   · 手机端 tpActiveFingers 卡在非零 → 之后所有笔事件被"笔让位"逻辑 continue 掉；
     *   · PC 端 _fingerOnPad 永远停在 true → 连悬停都丢。
     * 结果就是「心跳正常、界面有响应，但光标一动不动」，只能切模式/重启才好。
     *
     * 这里只清触摸板手势状态，**不**补发左键抬起（左键可能真的被按钮按着）。
     */
    private fun resetTrackpadState() {
        if (tpActiveFingers == 0) return
        tpActiveFingers = 0
        tpMaxPointers = 0
        tpMoved = 0f
        tpFingerId = -1
        tp2State = 0
        tpPinchLatched = false   // 手指全清：捏合锁存解除
        tpIdA = -1
        tpIdB = -1
        tpAccumA = 0f
        tpAccumB = 0f
        tpVecAX = 0f; tpVecAY = 0f; tpVecBX = 0f; tpVecBY = 0f
        tpStartSpan = 0f
        pointerSmoother.reset()
        trackpadScrollGate.reset()
        // 兜底复位时双击拖拽可能还按着左键，一并松开
        releaseTapDrag("兜底复位")
        // onFingerState(false) 内部就会发 MOUSE_FINGER(0) 给 PC，不用再单独发一帧
        onFingerState?.invoke(false)
        onDebug?.invoke("手势状态兜底复位（手指已全部离开）")
    }

    // ---- 诊断计数：index = toolType(0..4)，第二维 = down/move/up/hover ----
    private val counters = Array(5) { IntArray(4) }

    /**
     * 笔坐标归一化：**整个书写区**（手机端的操作框始终撑满，保持最大可操作范围）。
     * 捏合缩放只作用于 PC 上那个桌面框，手机这边不动。
     */
    private fun normX(x: Float): Float {
        val w = if (width > 0) width else 1
        return (x / w).coerceIn(0f, 1f)
    }

    private fun normY(y: Float): Float {
        val h = if (height > 0) height else 1
        return (y / h).coerceIn(0f, 1f)
    }

    override fun onSizeChanged(w: Int, h: Int, oldw: Int, oldh: Int) {
        super.onSizeChanged(w, h, oldw, oldh)
        bitmap?.recycle()
        // 尺寸变了，旧笔迹的坐标已不在同一坐标系 —— 与旧行为一致直接丢弃，
        // 同时清掉笔画记录，避免「撤销」又把坐标已失效的笔迹画回来
        strokes.clear()
        curStroke = null
        if (w > 0 && h > 0) {
            bitmap = Bitmap.createBitmap(w, h, Bitmap.Config.ARGB_8888)
            canvas = Canvas(bitmap!!)
            // 笔迹层是**透明底**：背景（白底 / 桌面截图）在 onDraw 里先画，笔迹叠在上面
            canvas!!.drawColor(Color.TRANSPARENT, android.graphics.PorterDuff.Mode.CLEAR)
            // 书写区尺寸变了（上下控制栏挤压 / 转屏）必须通知 PC 重算等比映射
            if (w != oldw || h != oldh) onSurfaceSizeChanged?.invoke(w, h)
        }
    }

    /**
     * 桌面范围框的截图：设为书写区的**背景**（不是弹出窗口），
     * 设好之后照样能在上面写字/画画，笔迹叠在截图之上。
     */
    var shotBackground: Bitmap? = null
        set(value) {
            field?.recycle()
            field = value
            invalidate()
        }

    override fun onDraw(c: Canvas) {
        if (workMode == Protocol.MODE_MOUSE) {
            // 触摸板模式：不显示白底笔迹层，用可配置的触摸板底色
            c.drawColor(trackpadColor)
        } else {
            // 背景：有桌面截图就用截图（铺满），否则白底
            val bg = shotBackground
            if (bg != null && !bg.isRecycled) {
                c.drawBitmap(bg, null, android.graphics.Rect(0, 0, width, height), null)
            } else {
                c.drawColor(Color.WHITE)
            }
            bitmap?.let { c.drawBitmap(it, 0f, 0f, null) }
        }
        // 操作框：**撑满整个书写区**。
        // 用户的可操作范围始终是最大的一块；捏合只把 PC 上那个桌面框缩小，
        // 两端只要求宽高比一致，所以笔迹落在桌面框里时形状不会变形。
        val h = borderPaint.strokeWidth / 2f
        c.drawRect(h, h, width - h, height - h, borderPaint)
        // 画笔模式：笔迹会自己淡掉，这里按时间逐段画（不写进位图层）
        if (fadingInk) drawFadingInk(c)
        // 手指轨迹画在最上层（跟手提示，抬手后自动淡出）
        drawFingerTrail(c)
    }

    // ================= 手指轨迹 =================

    /** 记录手指轨迹点（每根手指一条，按 pointerId 分开） */
    private fun recordFingerTrail(e: MotionEvent) {
        if (!showFingerTrail || trailLifeMs <= 0) return
        val action = e.actionMasked
        val now = android.os.SystemClock.uptimeMillis()
        var added = false
        for (i in 0 until e.pointerCount) {
            val tt = e.getToolType(i)
            if (tt != MotionEvent.TOOL_TYPE_FINGER && tt != MotionEvent.TOOL_TYPE_MOUSE) continue
            // 抬起的那根已经离开触摸板了，别再画它
            if (action == MotionEvent.ACTION_POINTER_UP && i == e.actionIndex) continue
            val tr = trails.getOrPut(e.getPointerId(i)) { Trail() }
            // 新手势落指：清掉这根手指上一次的残影，避免新旧两段被连成一条直线
            if ((action == MotionEvent.ACTION_DOWN || action == MotionEvent.ACTION_POINTER_DOWN) &&
                i == e.actionIndex
            ) tr.clear()
            val x = e.getX(i)
            val y = e.getY(i)
            if (tr.size > 0) {
                val last = tr.size - 1
                val dx = x - tr.xs[last]
                val dy = y - tr.ys[last]
                // 采样节流：位移和间隔都太小就不加点
                if (dx * dx + dy * dy < trailMinStepPx * trailMinStepPx && now - tr.ts[last] < 12) continue
            }
            tr.xs.add(x); tr.ys.add(y); tr.ts.add(now)
            tr.trim()
            added = true
        }
        if (added) invalidate()
    }

    /**
     * 画轨迹并按时间淡出。
     * 只要还有没消失的点就 postInvalidateOnAnimation() 继续下一帧；点全部过期后自然停止，
     * 静止时零刷新开销（不会常驻耗电）。
     */
    private fun drawFingerTrail(c: Canvas) {
        if (trails.isEmpty()) return
        val now = android.os.SystemClock.uptimeMillis()
        val life = trailLifeMs.coerceAtLeast(1L).toFloat()
        val base = if (trailColor != 0) trailColor
        else if (workMode == Protocol.MODE_MOUSE) 0xFF7FE0FF.toInt()   // 深色触摸板 → 亮青
        else 0xFF2E7DFF.toInt()                                        // 白底/截图 → 深蓝
        val w0 = dpPx(3.5f)
        var alive = false
        val it = trails.entries.iterator()
        while (it.hasNext()) {
            val tr = it.next().value
            // 淘汰过期点（ts 递增，从头删即可）
            while (tr.size > 0 && now - tr.ts[0] > trailLifeMs) {
                tr.ts.removeAt(0); tr.xs.removeAt(0); tr.ys.removeAt(0)
            }
            if (tr.size == 0) { it.remove(); continue }
            alive = true
            for (k in 1 until tr.size) {
                val a = (1f - (now - tr.ts[k]) / life).coerceIn(0f, 1f)
                trailPaint.color = base
                trailPaint.alpha = (a * 205f).toInt()
                trailPaint.strokeWidth = w0 * (0.3f + 0.7f * a)   // 越新越粗
                c.drawLine(tr.xs[k - 1], tr.ys[k - 1], tr.xs[k], tr.ys[k], trailPaint)
            }
            // 指尖圆点：一眼看到"手指现在在哪"
            val last = tr.size - 1
            val a = (1f - (now - tr.ts[last]) / life).coerceIn(0f, 1f)
            trailPaint.color = base
            trailPaint.alpha = (a * 255f).toInt()
            trailPaint.style = Paint.Style.FILL
            c.drawCircle(tr.xs[last], tr.ys[last], w0 * 1.15f, trailPaint)
            trailPaint.style = Paint.Style.STROKE
        }
        if (alive) postInvalidateOnAnimation()
    }

    /** 立刻清掉所有轨迹（切模式 / 复位时用） */
    fun clearTrail() {
        if (trails.isEmpty()) return
        trails.clear()
        invalidate()
    }

    /** 清空手机屏上全部笔迹，同时清掉笔画记录（撤销/清空与 PC 端同步用） */
    fun clear() {
        strokes.clear()
        curStroke = null
        bitmap?.eraseColor(Color.TRANSPARENT)
        invalidate()
    }

    /** 撤销最后一笔（手机屏），与 PC 端「撤销」按钮行为保持一致 */
    fun undoStroke() {
        if (strokes.isEmpty()) return
        strokes.removeAt(strokes.size - 1)
        redrawStrokes()
    }

    /**
     * 按笔画记录重画整个笔迹层。
     * 只在撤销/清空这类低频操作时调用；平时书写走增量绘制（每事件 O(1)），不重刷整层。
     */
    private fun redrawStrokes() {
        val bmp = bitmap ?: return
        val cv = canvas ?: return
        bmp.eraseColor(Color.TRANSPARENT)
        for (s in strokes) {
            paint.color = s.color
            if (s.pts.size == 2) {
                cv.drawPoint(s.pts[0], s.pts[1], paint)
            } else {
                var k = 2
                while (k < s.pts.size) {
                    cv.drawLine(s.pts[k - 2], s.pts[k - 1], s.pts[k], s.pts[k + 1], paint)
                    k += 2
                }
            }
        }
        invalidate()
    }

    fun debugSummary(): String {
        val names = arrayOf("UNKNOWN", "FINGER", "STYLUS", "MOUSE", "ERASER")
        val sb = StringBuilder()
        for (t in 0..4) {
            val c = counters[t]
            if (c[0] + c[1] + c[2] + c[3] == 0) continue
            sb.append("${names[t]} D${c[0]} M${c[1]} U${c[2]} H${c[3]}\n")
        }
        if (sb.isEmpty()) sb.append("（还没有任何事件）")
        return sb.toString().trimEnd('\n')
    }

    /** 桌面范围框状态的一行文字（给界面显示） */
    fun frameSummary(): String {
        return String.format("桌面范围 ×%.2f 位置(%.0f%%,%.0f%%)",
            frameS, frameL * 100f, frameT * 100f)
    }

    private fun handleEvent(e: MotionEvent, contact: Boolean) {
        val n = e.pointerCount
        val action = e.actionMasked

        // 每次手势落下的第一下：通知外部（用于打断无极滚动的惯性）
        if (contact && action == MotionEvent.ACTION_DOWN) onTouchDown?.invoke()

        // 手指轨迹：两种模式都画（数位板拖框 / 触摸板移光标），只记手指不记笔
        if (contact) recordFingerTrail(e)

        // 鼠标模式：手指要**整体**处理（多点触控必须拿到所有手指的坐标）
        if (contact && workMode == Protocol.MODE_MOUSE) {
            val fingers = countFingers(e)
            if (fingers > 0) {
                handleTrackpad(e)
            } else if (TrackpadState.mustReset(true, contact, fingers, action)) {
                // 兜底：一根手指都不剩了（笔可能还按着屏幕，所以 pointerCount 不为 0）。
                // 必须在派发笔事件**之前**复位，否则 tpActiveFingers 卡住会让下面
                // 所有笔事件被"笔让位"逻辑吞掉 —— 这就是「手机突然怎么动都不动」的根因。
                resetTrackpadState()
            }
        }

        for (i in 0 until n) {
            val tt = e.getToolType(i)

            // 触摸板模式下 S Pen **一概不响应**（接触、拖动、悬停全都不发）：
            // 光标完全归手指。以前笔在无手指时仍会把事件发给 PC → 光标被笔拽走，
            // 手指刚挪过去又被拉回来，看起来像「指针反向跑」。
            if (workMode == Protocol.MODE_MOUSE &&
                tt != MotionEvent.TOOL_TYPE_FINGER && tt != MotionEvent.TOOL_TYPE_MOUSE
            ) continue

            // 鼠标模式：多指手势进行中，S Pen 绝不能动光标
            //（笔在屏幕上一碰，光标就会跑，非常干扰双指滚动/捏合）
            if (workMode == Protocol.MODE_MOUSE && tpActiveFingers >= 2 && contact) {
                if (tpPenDown) {
                    onMouse?.invoke(Protocol.MOUSE_LEFT_UP, 0f, 0f)
                    tpPenDown = false
                }
                continue
            }

            // 手指：不绘制。用途由工作模式决定，两种模式互斥
            if (tt == MotionEvent.TOOL_TYPE_FINGER || tt == MotionEvent.TOOL_TYPE_MOUSE) {
                // 鼠标模式已在上面整体处理过了
                if (workMode == Protocol.MODE_MOUSE) continue
                // 两指及以上：交给双指捏合检测器，单指拖框让位，避免两个手势互相打架
                if (n >= 2) {
                    panning = false
                    continue
                }
                val px = e.getX(i)
                val py = e.getY(i)
                handleFingerPan(action, px, py)
                continue
            }

            // 悬停节流：内核 hover 可达 120Hz+，没必要全发，限制到 ~60Hz
            if (!contact) {
                val now = android.os.SystemClock.uptimeMillis()
                if (now - lastHoverAt < 16) return
                lastHoverAt = now
            }

            val x = e.getX(i)
            val y = e.getY(i)
            val nx = normX(x)
            val ny = normY(y)
            val rawP = if (contact) e.getPressure(i) else 0f
            val pressure = (rawP * pressureGain).coerceIn(0f, 1f)
            val tilt = e.getAxisValue(MotionEvent.AXIS_TILT, i)        // 0..PI/2
            val orient = e.getAxisValue(MotionEvent.AXIS_ORIENTATION, i) // -PI..PI
            val rad2deg = 180.0 / Math.PI
            val tiltX = (tilt * cos(orient) * rad2deg).toFloat()
            val tiltY = (tilt * sin(orient) * rad2deg).toFloat()

            var buttons = 0
            if ((e.buttonState and MotionEvent.BUTTON_STYLUS_SECONDARY) != 0) buttons = buttons or 0x1
            if (e.getToolType(i) == MotionEvent.TOOL_TYPE_ERASER ||
                (e.buttonState and MotionEvent.BUTTON_STYLUS_PRIMARY) != 0
            ) buttons = buttons or 0x2

            val type: Int
            val slot: Int
            when {
                contact && action == MotionEvent.ACTION_DOWN -> {
                    type = Protocol.TYPE_PEN_DOWN; slot = 0
                }
                contact && action == MotionEvent.ACTION_UP -> {
                    type = Protocol.TYPE_PEN_UP; slot = 2
                }
                contact -> {
                    type = Protocol.TYPE_PEN_MOVE; slot = 1
                }
                else -> {
                    type = Protocol.TYPE_PEN_HOVER; slot = 3
                }
            }
            if (tt in 0..4) counters[tt][slot]++

            if (contact) {
                // 只打 DOWN/UP，MOVE 抽样打，避免 120Hz 刷爆 logcat
                if (type != Protocol.TYPE_PEN_MOVE || (moveLogCount++ and 15) == 0) {
                    Log.d(TAG, "EVT ${EvtDump.actionName(e.action)} ${EvtDump.toolName(tt)} " +
                            "rawP=$rawP p=$pressure -> type=$type")
                }
            }

            // 在手机屏上画出轨迹（悬停不画；触摸板模式下笔=鼠标笔，不画轨迹）
            if (contact) {
                if (workMode == Protocol.MODE_TABLET) {
                    when (type) {
                        Protocol.TYPE_PEN_DOWN -> {
                            // 每一笔落笔时锁定当前颜色：这一笔整条都用这个颜色画
                            paint.color = strokeColor
                            lastX = x; lastY = y; hasLast = true
                            val s = Stroke(strokeColor)
                            val now = android.os.SystemClock.uptimeMillis()
                            s.pts.add(x); s.pts.add(y); s.ts.add(now)
                            strokes.add(s)
                            curStroke = s
                            // 画笔模式（会淡出）不写进位图层：图层是"永久"的，写进去就没法淡了
                            if (!fadingInk) canvas?.drawPoint(x, y, paint)
                        }
                        Protocol.TYPE_PEN_MOVE -> {
                            if (hasLast) {
                                if (!fadingInk) canvas?.drawLine(lastX, lastY, x, y, paint)
                                curStroke?.let {
                                    it.pts.add(x); it.pts.add(y)
                                    it.ts.add(android.os.SystemClock.uptimeMillis())
                                }
                                lastX = x; lastY = y
                            }
                        }
                        Protocol.TYPE_PEN_UP -> {
                            curStroke = null
                            hasLast = false
                        }
                    }
                    invalidate()
                } else {
                    hasLast = false
                }
            }

            // 悬停帧发送条件：设置里开了「悬停转发」，且**不在触摸板模式**。
            // 触摸板模式下 S Pen 的悬停流绝不能发——PC 端会把光标一帧一帧绝对定位回笔的位置，
            // 手指一抬（让位结束）光标就被拽回去，表现为「切回触摸板后控制不了鼠标」。
            // 触摸板模式下笔只在笔尖接触屏幕（点击/拖动光标）时才动光标。
            if (!contact && (!sendHover || workMode == Protocol.MODE_MOUSE)) {
                onDebug?.invoke("${EvtDump.actionName(e.action)} ${EvtDump.toolName(tt)} (悬停未发送)")
                return
            }

            onDebug?.invoke(
                "${EvtDump.actionName(e.action)} ${EvtDump.toolName(tt)} " +
                        "xy=(${x.toInt()},${y.toInt()}) view=${width}x$height " +
                        "nx=${String.format("%.2f", nx)} ny=${String.format("%.2f", ny)} " +
                        "p=${String.format("%.2f", pressure)} gain=${String.format("%.1f", pressureGain)}"
            )
            // 视图还没布局好（宽/高为 0）时，归一化分母取 1 → nx 被钳到 1，
            // 光标/笔迹会**全部钉死在范围框右缘**（上下能动、左右钉死 —— 用户报过的症状）。
            // 这时宁可不发，等 onSizeChanged 布局完成后再恢复。
            if (width <= 0 || height <= 0) {
                Log.w(TAG, "EVT 丢弃：书写区尺寸无效 ${width}x$height")
                continue
            }
            // 触摸板模式：手指正压在板上时，S Pen **完全让位**（一个笔事件都不发）。
            // 原因：PC 端把每个笔事件都当成「绝对定位光标」，
            // 笔的悬停流（笔在平板上时每秒 60 帧）会不断把光标拽回笔的位置，
            // 手指的相对移动刚动一下就被覆盖 → 看起来就是「指针反向跑」。
            if (workMode == Protocol.MODE_MOUSE && tpActiveFingers > 0) {
                tpPenDown = (type == Protocol.TYPE_PEN_DOWN)
                continue
            }
            if (workMode == Protocol.MODE_MOUSE) {
                tpPenDown = (type == Protocol.TYPE_PEN_DOWN)
            }
            listener?.invoke(type, nx, ny, pressure, tiltX, tiltY, buttons)
        }
    }

    /**
     * 双指捏合 → 缩放操作框。
     * 用系统 ScaleGestureDetector 而不是自己算两点距离：它内置了
     * 双指跨度阈值（避免刚落下时的抖动被当成缩放）与焦点计算，手感更稳。
     *
     * 缩放公式与 PC 端 MappingConfig.Zoom 完全一致，
     * 所以「手机上的框」和「桌面上的框」每一帧都严丝合缝。
     */
    private inner class ScaleListener : android.view.ScaleGestureDetector.SimpleOnScaleGestureListener() {
        override fun onScaleBegin(d: android.view.ScaleGestureDetector): Boolean {
            // 鼠标模式下没有范围框，捏合不做任何事
            if (workMode != Protocol.MODE_TABLET) return false
            pinching = true
            panning = false          // 捏合时停止单指拖框，两个手势不打架
            onDebug?.invoke("双指捏合：缩放操作框")
            return true
        }

        override fun onScale(d: android.view.ScaleGestureDetector): Boolean {
            if (!pinching) return false
            return applyZoom(d.scaleFactor, d.focusX, d.focusY)
        }

        override fun onScaleEnd(d: android.view.ScaleGestureDetector) {
            pinching = false
            onFrameChanged?.invoke(frameL, frameT, frameS)
        }
    }

    /**
     * 缩放操作框的核心算法（onScale 与模拟捏合共用，保证两条路径结果完全一致）。
     * factor：本次的相对缩放量；focusX/focusY：捏合焦点（视图像素坐标）。
     */
    private fun applyZoom(factor: Float, focusX: Float, focusY: Float): Boolean {
        if (frameLocked) return false // 截图捕获后锁定
        val W = width.toFloat(); val H = height.toFloat()
        if (W <= 0f || H <= 0f) return false

        // 捏合焦点在书写区里的相对位置（书写区 ↔ 桌面框是 1:1 的，所以它同时也是焦点在框内的位置）
        val rfx = (focusX / W).coerceIn(0f, 1f)
        val rfy = (focusY / H).coerceIn(0f, 1f)

        // 目标缩放：夹在 [MIN_FRAME, 1]，并把「实际生效的比例」算出来发给 PC
        val sOld = frameS
        val s2 = (sOld * factor).coerceIn(MIN_FRAME, 1f)
        val applied = s2 / sOld
        frameS = s2

        // 以焦点为锚点收缩桌面框：焦点处的桌面位置保持不动
        // （公式与 PC 端 MappingConfig.Zoom 完全一致）
        // 不钳制到 [0,1]：锚点收缩后框可能落到书写区外（去了别的显示器），由 PC 端按虚拟屏幕约束
        val nl = frameL + rfx * (sOld - s2)
        val nt = frameT + rfy * (sOld - s2)
        frameL = nl; frameT = nt
        invalidate()

        onZoom?.invoke(applied, rfx, rfy)
        onDebug?.invoke("缩放 ×${String.format("%.3f", applied)} → ${frameSummary()}")
        return true
    }

    /**
     * 模拟一次双指捏合（焦点取操作框中心）。走的是和真实捏合**完全相同**的代码路径，
     * 方便在只有一根手指时也能验证「手机 → PC」的缩放链路是否通。
     */
    fun simulatePinch(factor: Float) {
        if (workMode != Protocol.MODE_TABLET) return
        val cx = width / 2f
        val cy = height / 2f
        applyZoom(factor, cx, cy)
        onFrameChanged?.invoke(frameL, frameT, frameS)
    }

    /** 切换模式时把手势状态清干净，避免上一种模式的残留状态串到下一种 */
    private fun resetFinger() {
        panning = false
        fingerDown = false
        releaseTapDrag("切模式")
    }

    /** 手势动作码 → 实际动作（配置分发） */
    private fun dispatchAction(code: Int, tag: String) {
        onDebug?.invoke("$tag → ${actionName(code)}")
        when (code) {
            1 -> onMouse?.invoke(Protocol.MOUSE_SCROLL, 0f, -0.15f * scrollScale) // 点击滚一格
            3 -> onMouse?.invoke(Protocol.MOUSE_LEFT_CLICK, 0f, 0f)
            4 -> onMouse?.invoke(Protocol.MOUSE_RIGHT_CLICK, 0f, 0f)
            5 -> onMouse?.invoke(Protocol.MOUSE_MIDDLE_CLICK, 0f, 0f)
            6 -> onMouse?.invoke(Protocol.MOUSE_BACK, 0f, 0f)
            7 -> onMouse?.invoke(Protocol.MOUSE_FORWARD, 0f, 0f)
            9 -> onSwitchMode?.invoke()
            10 -> onMouse?.invoke(Protocol.MOUSE_VOL_UP, 0f, 0f)
            11 -> onMouse?.invoke(Protocol.MOUSE_VOL_DOWN, 0f, 0f)
            12 -> onMouse?.invoke(Protocol.MOUSE_VOL_MUTE, 0f, 0f)
            13 -> onMouse?.invoke(Protocol.MOUSE_TASK_NEXT, 0f, 0f)
            14 -> onMouse?.invoke(Protocol.MOUSE_TASK_PREV, 0f, 0f)
        }
        // 任何由手势触发的鼠标动作都在手机端对应浮动按钮上闪一下（看见「打中了哪个键」）
        onActionVisual?.invoke(code)
    }

    /**
     * 左右滑动专用分发：back = 手指向右滑。
     * 动作 8「后退/前进」按滑动方向二选一；6/7 是固定方向；其余动作交给 [dispatchAction]。
     */
    private fun dispatchSwipe(code: Int, tag: String, back: Boolean) {
        when (code) {
            8 -> onMouse?.invoke(
                if (back) Protocol.MOUSE_BACK else Protocol.MOUSE_FORWARD, 0f, 0f)
            6 -> onMouse?.invoke(Protocol.MOUSE_BACK, 0f, 0f)
            7 -> onMouse?.invoke(Protocol.MOUSE_FORWARD, 0f, 0f)
            else -> dispatchAction(code, tag)
        }
        onDebug?.invoke("$tag${if (back) "（向右）" else "（向左）"} → ${actionName(code)}")
    }

    /**
     * 方向滑动（三指/四指 左右/上下）分发：按方向算出 back 后复用 [dispatchSwipe]。
     * 视觉反馈由 [dispatchAction] 末尾的 onActionVisual 统一触发（鼠标按钮类动作
     * 会在手机端对应浮动键上闪一下），这里不再重复触发，避免按钮卡在亮色。
     */
    private fun dispatchDirSwipe(code: Int, tag: String, right: Boolean, up: Boolean) {
        dispatchSwipe(code, tag, right)
    }

    /** 捏合专用分发：factor 是本次的相对缩放量（>1 张开 / <1 捏拢） */
    private fun dispatchPinch(code: Int, factor: Float) {
        onDebug?.invoke("双指捏合 ×${String.format("%.3f", factor)} → ${actionName(code)}")
        when (code) {
            1 -> onMouse?.invoke(Protocol.MOUSE_SCROLL, 0f, (factor - 1f) * 2f * scrollScale)
            2 -> onMouse?.invoke(Protocol.MOUSE_ZOOM, factor, 0f)
            10 -> onMouse?.invoke(Protocol.MOUSE_VOL_UP, 0f, 0f)
            11 -> onMouse?.invoke(Protocol.MOUSE_VOL_DOWN, 0f, 0f)
            12 -> onMouse?.invoke(Protocol.MOUSE_VOL_MUTE, 0f, 0f)
            13 -> onMouse?.invoke(Protocol.MOUSE_TASK_NEXT, 0f, 0f)
            14 -> onMouse?.invoke(Protocol.MOUSE_TASK_PREV, 0f, 0f)
            3 -> onMouse?.invoke(Protocol.MOUSE_LEFT_CLICK, 0f, 0f)
            4 -> onMouse?.invoke(Protocol.MOUSE_RIGHT_CLICK, 0f, 0f)
            5 -> onMouse?.invoke(Protocol.MOUSE_MIDDLE_CLICK, 0f, 0f)
            6 -> onMouse?.invoke(Protocol.MOUSE_BACK, 0f, 0f)
            7 -> onMouse?.invoke(Protocol.MOUSE_FORWARD, 0f, 0f)
            9 -> onSwitchMode?.invoke()
        }
    }

    fun actionName(code: Int) = when (code) {
        1 -> "滚轮"; 2 -> "缩放"; 3 -> "左键"; 4 -> "右键"; 5 -> "中键"
        6 -> "后退"; 7 -> "前进"; 8 -> "后退/前进"; 9 -> "切换模式"
        10 -> "音量+"; 11 -> "音量-"; 12 -> "静音"; 13 -> "下一任务"; 14 -> "上一任务"
        else -> "无"
    }

    // ---------- 数位板模式：手指 = 拖动操作框（手机框与桌面框一起动） ----------
    private fun handleFingerPan(action: Int, px: Float, py: Float) {
        if (frameLocked) { // 截图捕获后锁定范围框，不支持移动
            panning = false
            return
        }
        when (action) {
            MotionEvent.ACTION_DOWN -> {
                panLastX = px; panLastY = py; panning = true
                // 移动死区：手指按下后累计位移超过阈值才开始拖框（手抖不误触）
                panGate.reset()
                onDebug?.invoke("手指按下：拖动操作框")
            }
            MotionEvent.ACTION_MOVE -> {
                if (panning && width > 0 && height > 0) {
                    // 位移相对书写区 → 与桌面框 1:1 跟随
                    // 死区用 catchUp 模式：越过阈值后补回死区内的位移，
                    // 保证操作框与手指始终严格 1:1（不会有固定偏移）
                    val gate = panGate.feed(px - panLastX, py - panLastY)
                    if (!gate.isZero) {
                        val dx = gate.dx / width.toFloat()
                        val dy = gate.dy / height.toFloat()
                        // 不钳制到 [0,1]：让操作框能移出书写区、去到屏幕任意位置（含其它显示器）。
                        // PC 端会按虚拟屏幕约束实际位置；这里只把原始位移发给 PC，保证两端相对量一致。
                        val nl = frameL + dx
                        val nt = frameT + dy
                        val adx = nl - frameL
                        val ady = nt - frameT
                        frameL = nl; frameT = nt
                        onPan?.invoke(adx, ady)
                    }
                    panLastX = px; panLastY = py
                    invalidate()
                }
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                panning = false
                onFrameChanged?.invoke(frameL, frameT, frameS)
                onDebug?.invoke("手指抬起")
            }
        }
    }

    // ---------- 鼠标模式：手指 = 笔记本触摸板（多点触控） ----------
    private fun countFingers(e: MotionEvent): Int {
        var c = 0
        for (i in 0 until e.pointerCount) {
            val tt = e.getToolType(i)
            if (tt == MotionEvent.TOOL_TYPE_FINGER || tt == MotionEvent.TOOL_TYPE_MOUSE) c++
        }
        return c
    }

    /**
     * 触摸板手势（全部用手指，多点一起处理）：
     *   单指滑动   → 移动光标
     *   单指点击   → 左键
     *   单指双击   → 双击（左键 ×2，不是右键）
     *   双击不松手 → 按住左键拖动 = 拖拽 / 框选文字（同 Windows 精密触摸板的 tap-drag）
     *   双指滑动   → 滚轮（上下=纵向滚动，左右=横向滚动）
     *   双指捏合   → 缩放（映射成 Ctrl+滚轮，绘图软件里就是缩放画布）
     *   双指点击   → 右键（同 Windows 精密触摸板）
     *   三指点击   → 中键
     *   三指左右滑 → 后退 / 前进
     */
    private fun handleTrackpad(e: MotionEvent) {
        val action = e.actionMasked
        var cnt = 0
        var sumX = 0f
        var sumY = 0f
        var minX = Float.MAX_VALUE
        var maxX = -Float.MAX_VALUE
        var minY = Float.MAX_VALUE
        var maxY = -Float.MAX_VALUE
        // 抬起的那根手指在事件里仍占一个位置，必须排除，否则质心/跨度都会算错
        val lifting = if (action == MotionEvent.ACTION_POINTER_UP) e.actionIndex else -1
        for (i in 0 until e.pointerCount) {
            if (i == lifting) continue
            val tt = e.getToolType(i)
            if (tt != MotionEvent.TOOL_TYPE_FINGER && tt != MotionEvent.TOOL_TYPE_MOUSE) continue
            val x = e.getX(i)
            val y = e.getY(i)
            cnt++
            sumX += x
            sumY += y
            if (x < minX) minX = x
            if (x > maxX) maxX = x
            if (y < minY) minY = y
            if (y > maxY) maxY = y
        }
        if (cnt == 0) return
        val cx = sumX / cnt
        val cy = sumY / cnt
        val span = kotlin.math.hypot((maxX - minX).toDouble(), (maxY - minY).toDouble()).toFloat()
        val w = if (width > 0) width else 1
        val h = if (height > 0) height else 1

        when (action) {
            MotionEvent.ACTION_DOWN -> {
                tpActiveFingers = 1
                tpMaxPointers = 1
                tpFingerId = e.getPointerId(e.actionIndex)
                tpStartX = cx
                tpStartY = cy
                tpStartTime = android.os.SystemClock.uptimeMillis()
                tpMoved = 0f
                tpSingleX = cx
                tpSingleY = cy
                tpLastCx = cx
                tpLastCy = cy
                tpLastSpan = span
                tpStartSpan = span
                tp2State = 0
                tpPinchLatched = false   // 全新手势：捏合锁存解除
                tpIdA = -1
                tpIdB = -1
                tpAccumA = 0f
                tpAccumB = 0f
                tpVecAX = 0f; tpVecAY = 0f; tpVecBX = 0f; tpVecBY = 0f
                tpScrollAxis = 0
                tpScrollAxisDecided = false   // 新手势：轴锁重新判定
                resetRecentWindow()
                // 新手势 → 重按自检重新开始（重新学压力范围 / 接触面积基线）
                pressSignal.reset()
                Haptics.resetScrollTick()
                // 新手势开始 → 移动死区重新生效（手抖不再让光标乱飘）
                pointerSmoother.reset()
                trackpadScrollGate.reset()
                // 手指一落上来，S Pen 就让位（PC 端收到这个信号后会松开笔的按键）
                tpPenDown = false
                onFingerState?.invoke(true)
                // 这一下是「双击」的第二下吗？是就立刻撤掉挂起的单击 ——
                // 否则会先点一下（取消选区 / 点进别的格子）再开始拖，用户会觉得位置偏了。
                if (tapDrag.onDown(tpStartTime, cx, cy) == TapDrag.Act.CANCEL_PENDING) {
                    handler.removeCallbacks(singleTapAction)
                }
            }

            MotionEvent.ACTION_POINTER_DOWN -> {
                tpActiveFingers = cnt
                if (cnt > tpMaxPointers) tpMaxPointers = cnt
                // 第二根手指落下 = 进入多点手势：光标停住，并把笔可能按住的左键松开
                if (cnt == 2 && tpPenDown) {
                    onMouse?.invoke(Protocol.MOUSE_LEFT_UP, 0f, 0f)
                    tpPenDown = false
                }
                tpLastCx = cx
                tpLastCy = cy
                tpLastSpan = span
                tpStartSpan = span     // 进入多点手势：以当前跨度为捏合判定基准
                tpScrollAxis = 0       // 新的多点手势：滚动轴锁重新判定
                tpScrollAxisDecided = false
                resetRecentWindow()
                // 轴锁/滑动的基准点 = **双指手势开始时**的质心。
                // 以前沿用第一根手指 ACTION_DOWN 的位置：两指摆好后质心天然偏移半指间距
                // （30~60px，远大于 10dp 锁轴阈值），第一帧 MOVE 就把轴锁锁死到由
                // 「手指摆放几何」决定的方向 —— 横着摆的两指想竖滚会被锁成横轴（上下没反应），
                // 竖着摆则左右没反应；竖滑带一点横漂就成了「上下变左右」。
                // 现在以当前质心重置基准，轴锁衡量的就只有**真正的滚动位移**。
                tpStartX = cx
                tpStartY = cy
                // 双指跟踪初始化：按 pointerId 分别累计两指的位移
                // （「捏合必须两指都有位移」「一指静止 → 单指跟随」都靠它判定）
                if (cnt == 2) {
                    tpIdA = e.getPointerId(0)
                    tpIdB = e.getPointerId(1)
                    tpAccumA = 0f
                    tpAccumB = 0f
                    tpVecAX = 0f; tpVecAY = 0f; tpVecBX = 0f; tpVecBY = 0f
                    tpPrevAX = e.getX(0)
                    tpPrevAY = e.getY(0)
                    tpPrevBX = e.getX(1)
                    tpPrevBY = e.getY(1)
                    // 捏合启动窗口起点：窗口内不锁单指跟随（捏合起手常常一指先动，
                    // 若立刻锁成单指跟随，用户就会看到「缩放和移动混在一起」）
                    tpTwoFingerAt = android.os.SystemClock.uptimeMillis()
                }
                // 捏合锁存中：不重置状态机（保持缩放态），新落的手指并进来后
                // 仍按「只看间距」输出缩放，绝不退回滚动/单指发移动。
                if (!tpPinchLatched) tp2State = 0
                // 多指介入 → 单指敲击 / 双击拖拽全部作废（左键若按着就松开）
                releaseTapDrag("多指介入")
                // 双指手势重新开始 → 滚动死区重新计算
                trackpadScrollGate.reset()
            }

            MotionEvent.ACTION_MOVE -> {
                tpActiveFingers = cnt
                if (cnt >= 2) {
                    val dCx = cx - tpLastCx
                    val dCy = cy - tpLastCy
                    val dSpan = span - tpLastSpan
                    pushRecent(dCx, dCy)   // 轴锁判定用：最近几帧的净位移
                    tpMoved += kotlin.math.abs(dCx) + kotlin.math.abs(dCy) + kotlin.math.abs(dSpan)
                    if (cnt >= 3) {
                        // 三指 / 四指滑动：左右、上下各拆成独立槽位，按主方向分发配置好的动作
                        val totalDx = cx - tpStartX
                        val totalDy = cy - tpStartY
                        if (kotlin.math.hypot(totalDx.toDouble(), totalDy.toDouble()) > SWIPE_PX) {
                            // 方向判定**偏向横向**（用户反馈：横滑时手会自然带一点上下漂移，
                            // 等幅比较经常把横滑误判成上下）。纵向幅度要明显大于横向
                            // （超过 SWIPE_VERT_BIAS 倍）才判上下，其余一律按左右分发。
                            val adx = kotlin.math.abs(totalDx)
                            val ady = kotlin.math.abs(totalDy)
                            val vert = ady > adx * SWIPE_VERT_BIAS
                            val right = totalDx > 0f
                            val up = totalDy < 0f
                            val code = if (cnt == 3) {
                                when {
                                    !vert && right -> g3L
                                    !vert -> g3R
                                    up -> g3U
                                    else -> g3D
                                }
                            } else {
                                when {
                                    !vert && right -> g4L
                                    !vert -> g4R
                                    up -> g4U
                                    else -> g4D
                                }
                            }
                            if (code != 0) {
                                val now = android.os.SystemClock.uptimeMillis()
                                if (now - lastSwipeActionAt > SWIPE_ACTION_COOLDOWN_MS) {
                                    lastSwipeActionAt = now
                                    Haptics.action()   // 多指滑动动作被识别
                                    dispatchDirSwipe(code, if (cnt == 3) "三指" else "四指", right, up)
                                }
                            }
                            // 一次完整滑动只触发一次：发完后把基准点移到当前位置
                            tpStartX = cx
                            tpStartY = cy
                        }
                    } else {
                        // ---- 双指分支：三态状态机（缩放 / 滚动 / 单指跟随）----
                        // 用户规格（2026-10-29 下午修订）：
                        //  · 捏合判定放宽：两指**沿同一直线反向运动**即判捏合——幅度不必对称，
                        //    只要不是一指静止；判成捏合后**过程只看两指间距**（输出缩放），
                        //    不要求维持运动，也不回退到滚动；
                        //  · 只有一指动 → 忽略静止指，按单指滑动移动光标；
                        //  · 进入移动识别（滚动 / 单指跟随）后，本手势不再处理缩放；
                        //  · 滚动方向三态：近似竖 → 锁竖轴只出 dy；近似横 → 锁横轴只出 dx；
                        //    斜向（允许一点偏角）→ 不锁轴，横纵同时输出。
                        val iA = if (tpIdA >= 0) e.findPointerIndex(tpIdA) else -1
                        val iB = if (tpIdB >= 0) e.findPointerIndex(tpIdB) else -1
                        if (iA >= 0 && iB >= 0) {
                            val dax = e.getX(iA) - tpPrevAX
                            val day = e.getY(iA) - tpPrevAY
                            val dbx = e.getX(iB) - tpPrevBX
                            val dby = e.getY(iB) - tpPrevBY
                            tpAccumA += kotlin.math.hypot(dax.toDouble(), day.toDouble()).toFloat()
                            tpAccumB += kotlin.math.hypot(dbx.toDouble(), dby.toDouble()).toFloat()
                            tpVecAX += dax; tpVecAY += day
                            tpVecBX += dbx; tpVecBY += dby
                            tpPrevAX = e.getX(iA); tpPrevAY = e.getY(iA)
                            tpPrevBX = e.getX(iB); tpPrevBY = e.getY(iB)

                            // 未定态 → 尝试判定一次；判成 1/2/3 后本手势固定。
                            // 判定优先级（2026-10-29 修订）：
                            //  · **捏合（反向运动）随时可判、最优先**——起手一指先动也不会被污染；
                            //  · 滚动 / 单指跟随都以「质心位移」为依据，会被捏合起手的单指先动污染，
                            //    所以要等**启动窗口**（150ms）过后才允许锁定：窗口内只认捏合，
                            //    窗口过了再按质心位移判滚动 / 单指跟随（正常滚动手感只晚 2~3 帧）。
                            if (tp2State == 0) {
                                val both = TwoFingerLogic.bothMoving(
                                    tpAccumA, tpAccumB, dpPx(TP2_BOTH_MIN_DP))
                                val windowOver =
                                    android.os.SystemClock.uptimeMillis() - tpTwoFingerAt > TP2_FOLLOW_DELAY_MS
                                when {
                                    both && TwoFingerLogic.opposing(
                                        tpVecAX, tpVecAY, tpVecBX, tpVecBY, dpPx(TP2_BOTH_MIN_DP)) -> {
                                        tp2State = 1
                                        tpPinchLatched = true   // 锁存：本序列不再发移动指令
                                        Haptics.gestureStart()  // 捏合被识别 → 震一下确认
                                        onDebug?.invoke("双指 → 捏合（两指反向运动）")
                                    }
                                    windowOver && TwoFingerLogic.singleFingerFollow(
                                        tpAccumA, tpAccumB, dpPx(TP2_BOTH_MIN_DP), dpPx(10f)) -> {
                                        tp2State = 3
                                        Haptics.gestureStart()
                                        onDebug?.invoke("双指 → 单指跟随（忽略静止指）")
                                    }
                                    windowOver && (
                                        maxOf(kotlin.math.abs(cx - tpStartX), kotlin.math.abs(cy - tpStartY)) > dpPx(10f)) -> {
                                        tp2State = 2
                                        // 轴锁**只判一次**：判成横/竖就锁该轴，判成斜向就一直双轴输出。
                                        // 这里只是「进入滚动态」，真正的轴锁判定在下面按累计位移达标时进行
                                        // （进入时位移往往还不够，判了会把噪声方向锁死）。
                                        val ax = TwoFingerLogic.decideAxisOrNull(
                                            recentSumX, recentSumY, dpPx(SCROLL_WINDOW_MIN_DP), SCROLL_AXIS_BIAS)
                                        tpScrollAxis = ax ?: 0
                                        tpScrollAxisDecided = ax != null
                                        Haptics.gestureStart()   // 滚动被识别 → 震一下
                                        Haptics.resetScrollTick()
                                        onDebug?.invoke("双指 → 滚动 axis=$tpScrollAxis")
                                    }
                                }
                            }

                            when (tp2State) {
                                1 -> if (g2Pinch != 0) {
                                    // 捏合 → 按「双指捏合」槽位配置的动作分发（默认缩放）
                                    // 音量/任务切换这类一次性动作要节流，否则一次捏合会连发几十次
                                    val now = android.os.SystemClock.uptimeMillis()
                                    if (g2Pinch == 2) {
                                        // 缩放状态内**没有死区**（用户规格 2026-10-30）：
                                        // 死区只在「激活捏合」的判定里起作用（两指都要有累计位移、
                                        // 且必须是反向运动），一旦进入缩放状态就不再过滤 ——
                                        // 跨度只要变了就按 1:1 发一帧，再小的移动也会响应。
                                        // 基准取「上一帧跨度」：逐帧相对量相乘即总变化，
                                        // 既不丢位移也不会累积漂移（只有跨度真的没变才不发）。
                                        val factor = if (tpLastSpan > 1f) span / tpLastSpan else 1f
                                        if (factor != 1f) dispatchPinch(g2Pinch, factor)
                                    } else if (now - lastPinchActionAt > PINCH_ACTION_COOLDOWN_MS) {
                                        lastPinchActionAt = now
                                        val f = if (tpLastSpan > 1f) span / tpLastSpan else 1f
                                        dispatchPinch(g2Pinch, if (f > 1f) 1.06f else 0.94f)
                                    }
                                }
                                3 -> {
                                    // 单指跟随：跟累计位移大的那根手指移动光标（忽略静止指）
                                    val pdx: Float
                                    val pdy: Float
                                    if (tpAccumA >= tpAccumB) { pdx = dax; pdy = day }
                                    else { pdx = dbx; pdy = dby }
                                    val sm = pointerSmoother.feed(pdx, pdy)
                                    if (!sm.isZero) sendMouseMove(sm.dx, sm.dy)
                                }
                                2 -> if (g2Swipe != 0 && (kotlin.math.abs(dCx) > 0.5f || kotlin.math.abs(dCy) > 0.5f)) {
                                    // 滚动（只在已判定为滚动态时输出：未定态不发任何东西，
                                    // 否则捏合启动窗口期的质心移动会被当成滚动漏出去 —— 用户规格
                                    // 「缩放动作进行中，不响应移动手势」的前半段就靠这个保证）。
                                    // 轴锁只在「还没判定」时判定，且**只判一次**：
                                    // 斜着滑 → 判成 FREE → 本次手势一直保持双轴输出；
                                    // 明显横/竖 → 锁单轴。判过就绝不改判（用户规格：
                                    // 激活了斜向移动就应该一直能全方向移动，2026-10-30）。
                                    if (!tpScrollAxisDecided) {
                                        // 判定时机看**累计位移**（手势已经明确开始了才判，慢速滑动
                                        // 也不会漏判）；方向看**最近窗口**（避免起手抖动被永久记住）。
                                        // 只用窗口位移当门槛时，滑得慢就永远达不到门槛 → 一直停在
                                        // 「未判定」= 双轴透传，表现就是竖向没锁住（2026-10-30）。
                                        val travelled = maxOf(
                                            kotlin.math.abs(cx - tpStartX), kotlin.math.abs(cy - tpStartY))
                                        if (travelled >= dpPx(SCROLL_AXIS_MIN_DP)) {
                                            val ax = TwoFingerLogic.decideAxisOrNull(
                                                recentSumX, recentSumY, dpPx(SCROLL_WINDOW_MIN_DP), SCROLL_AXIS_BIAS)
                                            if (ax != null) {
                                                tpScrollAxis = ax
                                                tpScrollAxisDecided = true
                                            }
                                        }
                                    } else if (tpScrollAxis != TwoFingerLogic.AXIS_FREE) {
                                        // 已锁单轴：只有「最近位移明确指向另一轴」才允许改判一次
                                        // （门槛更高 30dp、bias 更严 3.0，避免滑着滑着又锁回去）。
                                        // 斜向 FREE 不走这里 —— 激活斜向后就一直是全方向（用户规格）。
                                        val ax = TwoFingerLogic.decideAxisOrNull(
                                            recentSumX, recentSumY, dpPx(SCROLL_AXIS_SWITCH_DP), SCROLL_AXIS_SWITCH_BIAS)
                                        if (ax != null && ax != tpScrollAxis && ax != TwoFingerLogic.AXIS_FREE) {
                                            tpScrollAxis = ax
                                            onDebug?.invoke("双指滚动 → 换向 axis=$tpScrollAxis")
                                        }
                                    }
                                    val f = when (tpScrollAxis) {
                                        1 -> dCx to 0f
                                        2 -> 0f to dCy
                                        else -> dCx to dCy
                                    }
                                    val gate = trackpadScrollGate.feed(f.first, f.second)
                                    if (!gate.isZero) {
                                        // 方向取反（用户要求）：手指上滑 → 页面内容向下（传统滚轮方向）
                                        onMouse?.invoke(Protocol.MOUSE_SCROLL, -gate.dx / h * scrollScale, -gate.dy / h * scrollScale)
                                        Haptics.scrollTick()     // 滚动中的细震（节流，像触摸板的段落感）
                                        onDebug?.invoke("双指滑动 dx=${String.format("%.3f", gate.dx / h)} dy=${String.format("%.3f", gate.dy / h)} → 滚动 axis=$tpScrollAxis")
                                    }
                                }
                            }
                        }
                    }
                    tpLastCx = cx
                    tpLastCy = cy
                    tpLastSpan = span
                } else {
                    if (tpPinchLatched) {
                        // 捏合锁存中：捏合后抬掉一指，剩下的手指**不是**单指移动——
                        // 只更新基准点，不发移动、不进拖拽、不触发双击拖拽（用户规格：
                        // 触发缩放后直到手指离开，不应有任何移动指令）
                        tpSingleX = cx
                        tpSingleY = cy
                        tpLastCx = cx
                        tpLastCy = cy
                        tpLastSpan = span
                    } else {
                    // 双击后按住并移动 → 先按下左键，后面的位移就成了「拖动 / 框选」。
                    // 必须在发送位移**之前**按下：否则开头这一小段只是移动光标，选区起点会偏。
                    if (tapDrag.onMove(cx, cy) == TapDrag.Act.DRAG_DOWN && !pressDragging) {
                        onMouse?.invoke(Protocol.MOUSE_LEFT_DOWN, 0f, 0f)
                        onActionVisual?.invoke(Protocol.MOUSE_LEFT_CLICK)   // 左键按钮闪一下 = 已按住
                        Haptics.dragStart()
                        onDebug?.invoke("双击按住拖动 → 按下左键，开始选择")
                    }
                    // 单指 → 移动光标（速度倍率可配置）
                    //
                    // 两个关键修正：
                    // ① 不再丢步：以前有 0.01 的归一化阈值（≈7px），慢速移动时每个事件的
                    //    位移都掉在阈值里被丢弃，基准点却已经更新 → 表现为「不动」，
                    //    偶尔攒够一次才发一跳 → 又「飘」。现在每个位移都发出去。
                    // ② 提升采样率：Android 会把同一帧内的高频采样点合并进
                    //    getHistoricalX/Y（不单独派发事件），只吃最新点会丢掉中间的采样，
                    //    慢速移动就变成了几段折线。这里逐个历史点补发。
                    //
                    // 抖动过滤：交给[PointerSmoother]的噪声闸门（自标定底噪，
                    // 只判「抖动 vs 真在动」，慢速移动照样跟得上，位移不丢）。
                    val idx = if (tpFingerId >= 0) e.findPointerIndex(tpFingerId) else -1
                    // 重按 = 按下左键（移动途中直接用力度控制「按下 / 松开」）——
                    // 必须在发送位移**之前**处理：否则开头这一小段只是移动光标，选区起点会偏。
                    updatePressDrag(e, idx)
                    val hs = if (idx >= 0) e.historySize else 0
                    var prevX = tpSingleX
                    var prevY = tpSingleY
                    // 本帧的**累计**位移：Android 会把一帧内的多个采样点塞进历史点里，
                    // 逐点喂平滑器会被微死区误吃（每个采样点位移都很小），
                    // 所以先把本帧所有位移加起来，再作为一个 delta 过平滑器。
                    var frameDx = 0f
                    var frameDy = 0f
                    if (idx >= 0) {
                        for (k in 0 until hs) {
                            val hx = e.getHistoricalX(idx, k)
                            val hy = e.getHistoricalY(idx, k)
                            tpMoved += kotlin.math.abs(hx - prevX) + kotlin.math.abs(hy - prevY)
                            frameDx += hx - prevX
                            frameDy += hy - prevY
                            prevX = hx; prevY = hy
                        }
                    }
                    val dx = cx - prevX
                    val dy = cy - prevY
                    tpMoved += kotlin.math.abs(dx) + kotlin.math.abs(dy)
                    frameDx += dx
                    frameDy += dy
                    val sm = pointerSmoother.feed(frameDx, frameDy)
                    // 指针路径上**不再叠加起步死区**（v1.4.3）：
                    // 噪声闸门已经用「自标定底噪 + 滑窗净位移」把抖动和落下瞬态挡住了，
                    // 再叠一个固定死区，只会让慢速移动又慢半拍（用户反馈：
                    // 「慢慢移动好长一段距离，指针没动」——死区 + 速度死区叠加的结果）。
                    if (!sm.isZero) sendMouseMove(sm.dx, sm.dy)
                    logPointerGate()
                    tpSingleX = cx
                    tpSingleY = cy
                    }
                }
            }

            MotionEvent.ACTION_POINTER_UP -> {
                // 上面的统计循环已经排除了「抬起的那根」，cnt 本身就是剩下的手指数，
                // 不能再减 1 —— 否则双指抬成单指时 tpActiveFingers 会掉到 0，
                // 提前给 PC 发「手指全离开」，笔的悬停流立刻回来抢光标（表现为光标往回跳）。
                tpActiveFingers = TrackpadState.fingersAfterPointerUp(cnt)
                if (tpPinchLatched) {
                    // 捏合锁存中：手指抬起**不**重置状态机（保持 tp2State=1），
                    // 剩下的手指按上面的锁存分支只更新基准、不发移动。
                    // （2026-10-29 用户规格：触发缩放后直到手指离开，不应有任何移动指令）
                    tpStartSpan = span
                    tpSingleX = cx
                    tpSingleY = cy
                    tpLastCx = cx
                    tpLastCy = cy
                    tpLastSpan = span
                } else {
                    // 手指数变化：重置捏合判定基准（3 指收成 2 指后要能重新捏合）
                    tpStartSpan = span
                    tp2State = 0
                    tpIdA = -1
                    tpIdB = -1
                    tpAccumA = 0f
                    tpAccumB = 0f
                    tpVecAX = 0f; tpVecAY = 0f; tpVecBX = 0f; tpVecBY = 0f
                    tpScrollAxis = 0
                    tpScrollAxisDecided = false
                    resetRecentWindow()
                    // 从多指回到单指：重置单指基准，避免光标突然跳一下
                    tpSingleX = cx
                    tpSingleY = cy
                    tpLastCx = cx
                    tpLastCy = cy
                    tpLastSpan = span
                    // 双指期间的滚动/跟随死区全部清掉，回单指后重新计
                    trackpadScrollGate.reset()
                    pointerSmoother.reset()
                }
                // 从多指回到单指：重按状态收尾（左键不能挂着）
                releasePressDrag("手指数变化")
                // 最后一根手指离开触摸板 → S Pen 恢复控制光标
                if (tpActiveFingers == 0) onFingerState?.invoke(false)
            }

            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                tpActiveFingers = 0
                tpFingerId = -1
                tp2State = 0
                // 重按拖着左键就抬手了 → 补发抬起 + 轻震动（防鼠标永久按住）
                releasePressDrag("抬手")
                logPressureDiag()        // 压力自检结论（只报一次）
                tpPinchLatched = false   // 全部手指离开：捏合锁存解除
                tpIdA = -1
                tpIdB = -1
                tpAccumA = 0f
                tpAccumB = 0f
                tpVecAX = 0f; tpVecAY = 0f; tpVecBX = 0f; tpVecBY = 0f
                onFingerState?.invoke(false)
                val now = android.os.SystemClock.uptimeMillis()
                val dur = now - tpStartTime
                val isTap = tpMoved < tapSlopPx * 1.5f && dur < TAP_TIMEOUT_MS
                // 敲击判定要**无条件**走一遍：双击拖拽结束时左键还按着，
                // 漏了这一句鼠标就永久处于"按住"状态（点什么都在框选），只能重启 host。
                val act = tapDrag.onUp(now, cx, cy)

                if (act == TapDrag.Act.DRAG_UP) {
                    onMouse?.invoke(Protocol.MOUSE_LEFT_UP, 0f, 0f)
                    Haptics.dragEnd()
                    onDebug?.invoke("拖拽结束 → 松开左键")
                } else if (tpMaxPointers >= 2) {
                    // 多指手势：单指敲击的判定结果一律作废，只按手指数分发配置好的动作
                    releaseTapDrag("多指手势收尾")
                    when {
                        isTap && tpMaxPointers >= 4 -> {
                            if (g4Tap == 9) onSwitchMode?.invoke()
                            onDebug?.invoke("四指点击 → ${actionName(g4Tap)}")
                        }
                        isTap && tpMaxPointers == 3 -> dispatchAction(g3Tap, "三指点击")
                        isTap && tpMaxPointers == 2 -> dispatchAction(g2Tap, "双指点击")
                    }
                } else if (isTap && tpMaxPointers == 1) {
                    // 单指敲击：单击 → 左键；双击 → 左键双击（**不再映射成右键**）
                    when (act) {
                        TapDrag.Act.DOUBLE_CLICK -> {
                            onMouse?.invoke(Protocol.MOUSE_LEFT_DOUBLE, 0f, 0f)
                            onActionVisual?.invoke(Protocol.MOUSE_LEFT_DOUBLE)
                            Haptics.click()
                            onDebug?.invoke("双击 → 鼠标双击（左键 ×2）")
                        }
                        TapDrag.Act.SINGLE_CLICK -> {
                            onMouse?.invoke(Protocol.MOUSE_LEFT_CLICK, 0f, 0f)
                            Haptics.click()
                            onDebug?.invoke("单击 → 鼠标左键（按住太久）")
                        }
                        TapDrag.Act.PENDING_SINGLE ->
                            handler.postDelayed(singleTapAction, DOUBLE_TAP_WINDOW_MS)
                        else -> {}
                    }
                } else {
                    // 长按 / 划动：不是敲击，作废掉敲击判定，别让下一次点击被凑成双击
                    tapDrag.reset()
                }
                tpMaxPointers = 0
            }
        }
    }


    override fun onTouchEvent(event: MotionEvent): Boolean {
        // 先喂给双指捏合检测器（只有真接触的事件才有意义，悬停不参与）
        if (workMode == Protocol.MODE_TABLET) scaleDetector.onTouchEvent(event)
        handleEvent(event, contact = true)
        return true
    }

    override fun onHoverEvent(event: MotionEvent): Boolean {
        handleEvent(event, contact = false)
        return true
    }

    companion object {
        private const val TAG = "draw&touch"
        private const val TAP_TIMEOUT_MS = 320        // 按太久就不算敲击
        private const val DOUBLE_TAP_WINDOW_MS = 300L // 双击窗口；同时也是单击的延迟（与 Android 的 doubleTapTimeout 一致）
        /** 单根手指轨迹最多保留的点数（按 60Hz 算 ≈ 7 秒，够用且不涨内存） */
        private const val TRAIL_MAX_POINTS = 400
        /** 操作框最小缩放（与 PC 端 MappingConfig.MIN_SCALE 保持一致） */
        private const val MIN_FRAME = 0.05f
        /** 三指左右滑的触发阈值(px) */
        private const val SWIPE_PX = 140f
        /**
         * 上下滑判定偏置：纵向幅度要超过横向的这么多倍才判「上下」，
         * 其余一律判「左右」。用户反馈横滑常被误判成上下，所以故意偏向横向。
         */
        private const val SWIPE_VERT_BIAS = 1.6f

        /**
         * 双指**滚动**轴锁的方向偏置：竖向要超过横向的它倍才锁竖（横向同理），
         * 两轴相当 → 斜向不锁轴、双轴同时输出。
         * 2.0 时竖滑容易被判成斜向（竖滑时手指横向摆幅相对更大，横向则天然稳）
         * → 表现「横向锁得住、竖向锁不住」，所以收到 1.7 让两轴对称；
         * 1.7 仍保留 30°~60° 为斜向窗口（45° 斜滑照旧不锁），比三/四指的 1.6 更宽。
         */
        private const val SCROLL_AXIS_BIAS = 1.7f

        /**
         * 轴锁判定的**时机**门槛(dp)：手势累计位移超过它才判定方向（保证手势确实开始了）。
         * 方向本身取最近窗口的净位移，避免起手抖动被永久记住。
         * （曾只用窗口位移当门槛 → 滑得慢就永远达不到 → 停在「未判定」= 双轴透传，
         *   表现为竖向没锁住，2026-10-30）
         */
        private const val SCROLL_AXIS_MIN_DP = 24f
        /** 方向判定用的**窗口位移**门槛(dp)：最近几帧只要有这么明确的位移就能定方向。 */
        private const val SCROLL_WINDOW_MIN_DP = 8f
        /** 已锁单轴后想改判的门槛(dp)与偏置：明显换方向（如横滑改竖滑）才允许改判。 */
        private const val SCROLL_AXIS_SWITCH_DP = 30f
        private const val SCROLL_AXIS_SWITCH_BIAS = 3.0f
        /** 轴锁判定用的滑动窗口帧数（最近这么多帧的净位移） */
        private const val RECENT_FRAMES = 6

        /**
         * 双指「都有位移」的最低累计量(dp)：低于它视为静止指。
         * 这是**激活捏合**的门槛（也是单指跟随判定的前提）——
         * 死区只在激活阶段起作用；进入缩放状态后不再有任何过滤
         * （跨度一变就发，小的移动也响应，详见 tp2State == 1 分支，2026-10-30）。
         */
        private const val TP2_BOTH_MIN_DP = 3f
        /** 捏合启动窗口（ms）：窗口内不锁单指跟随，给反向运动判定留机会 */
        private const val TP2_FOLLOW_DELAY_MS = 150L
        /** 捏合触发一次性动作（音量/任务切换等）的冷却时间(ms) */
        private const val PINCH_ACTION_COOLDOWN_MS = 320L
        /** 左右滑动触发一次性动作的冷却时间(ms)：一次滑动只触发一次，不会连发 */
        private const val SWIPE_ACTION_COOLDOWN_MS = 450L
    }
}
