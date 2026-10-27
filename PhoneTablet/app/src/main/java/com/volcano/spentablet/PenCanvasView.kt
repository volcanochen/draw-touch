package com.volcano.spentablet

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
    var g3Swipe: Int = 8   // 三指左右滑：后退/前进
    var g4Tap: Int = 9     // 四指点击：切换模式
    var g4Swipe: Int = 0   // 四指滑动：无
    /** 四指等手势触发「切换模式」时回调给 MainActivity */
    var onSwitchMode: (() -> Unit)? = null

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
    private var tapCandidate = false    // 当前按下有没有可能算“敲击”
    private var secondTap = false       // 本次按下是否是双击的第二下
    private var lastTapAt = 0L
    private var lastTapX = 0f
    private var lastTapY = 0f
    private val handler = android.os.Handler(android.os.Looper.getMainLooper())
    private val singleTapAction = Runnable {
        onMouse?.invoke(Protocol.MOUSE_LEFT_CLICK, 0f, 0f)
        onDebug?.invoke("单击 → 鼠标左键")
    }

    /** 双指捏合检测器 */
    private val scaleDetector = android.view.ScaleGestureDetector(context, ScaleListener())
    private var pinching = false

    /** 敲击判定阈值（随屏幕密度换算成 px） */
    private val tapSlopPx: Float get() = 12f * resources.displayMetrics.density
    private val doubleTapSlopPx: Float get() = 48f * resources.displayMetrics.density

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
    }

    fun clear() {
        bitmap?.eraseColor(Color.TRANSPARENT)
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

        // 鼠标模式：手指要**整体**处理（多点触控必须拿到所有手指的坐标）
        if (contact && workMode == Protocol.MODE_MOUSE && countFingers(e) > 0) {
            handleTrackpad(e)
        }

        for (i in 0 until n) {
            val tt = e.getToolType(i)

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
                            canvas?.drawPoint(x, y, paint)
                        }
                        Protocol.TYPE_PEN_MOVE -> {
                            if (hasLast) {
                                canvas?.drawLine(lastX, lastY, x, y, paint)
                                lastX = x; lastY = y
                            }
                        }
                        Protocol.TYPE_PEN_UP -> hasLast = false
                    }
                    invalidate()
                } else {
                    hasLast = false
                }
            }

            if (!contact && !sendHover) {
                onDebug?.invoke("${EvtDump.actionName(e.action)} ${EvtDump.toolName(tt)} (悬停未发送)")
                return
            }

            onDebug?.invoke(
                "${EvtDump.actionName(e.action)} ${EvtDump.toolName(tt)} " +
                        "p=${String.format("%.2f", pressure)} gain=${String.format("%.1f", pressureGain)}"
            )
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
        tapCandidate = false
        secondTap = false
        lastTapAt = 0L
        handler.removeCallbacks(singleTapAction)
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
                onDebug?.invoke("手指按下：拖动操作框")
            }
            MotionEvent.ACTION_MOVE -> {
                if (panning && width > 0 && height > 0) {
                    // 位移相对书写区 → 与桌面框 1:1 跟随
                    val dx = (px - panLastX) / width.toFloat()
                    val dy = (py - panLastY) / height.toFloat()
                    // 不钳制到 [0,1]：让操作框能移出书写区、去到屏幕任意位置（含其它显示器）。
                    // PC 端会按虚拟屏幕约束实际位置；这里只把原始位移发给 PC，保证两端相对量一致。
                    val nl = frameL + dx
                    val nt = frameT + dy
                    val adx = nl - frameL
                    val ady = nt - frameT
                    frameL = nl; frameT = nt
                    panLastX = px; panLastY = py
                    invalidate()
                    onPan?.invoke(adx, ady)
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
     *   单指点击   → 左键；单指双击 → 右键
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
            }

            MotionEvent.ACTION_MOVE -> {
                tpActiveFingers = cnt
                if (cnt >= 2) {
                    val dCx = cx - tpLastCx
                    val dCy = cy - tpLastCy
                    val dSpan = span - tpLastSpan
                    tpMoved += kotlin.math.abs(dCx) + kotlin.math.abs(dCy) + kotlin.math.abs(dSpan)
                    if (cnt >= 4) {
                        // 四指左右滑（可配置动作）
                        val totalDx = cx - tpStartX
                        if (g4Swipe != 0 && kotlin.math.abs(totalDx) > SWIPE_PX &&
                            kotlin.math.abs(cy - tpStartY) < SWIPE_PX) {
                            val now = android.os.SystemClock.uptimeMillis()
                            if (now - lastSwipeActionAt > SWIPE_ACTION_COOLDOWN_MS) {
                                lastSwipeActionAt = now
                                dispatchSwipe(g4Swipe, "四指滑动", totalDx > 0)
                            }
                            tpStartX = cx
                        }
                    } else if (cnt == 3) {
                        // 三指左右滑（可配置动作：后退/前进、切换模式、音量、任务切换……）
                        val totalDx = cx - tpStartX
                        if (g3Swipe != 0 && kotlin.math.abs(totalDx) > SWIPE_PX &&
                            kotlin.math.abs(cy - tpStartY) < SWIPE_PX) {
                            val now = android.os.SystemClock.uptimeMillis()
                            if (now - lastSwipeActionAt > SWIPE_ACTION_COOLDOWN_MS) {
                                lastSwipeActionAt = now
                                dispatchSwipe(g3Swipe, "三指滑动", totalDx > 0)
                            }
                            tpStartX = cx
                        }
                    } else {
                        val relSpan = if (tpLastSpan > 1f) kotlin.math.abs(dSpan) / tpLastSpan else 0f
                        if (relSpan > PINCH_REL && g2Pinch != 0) {
                            // 捏合 → 按「双指捏合」槽位配置的动作分发（默认缩放）
                            // 音量/任务切换这类一次性动作要节流，否则一次捏合会连发几十次
                            val factor = if (tpLastSpan > 1f) span / tpLastSpan else 1f
                            val now = android.os.SystemClock.uptimeMillis()
                            if (g2Pinch == 2) {
                                dispatchPinch(g2Pinch, factor)      // 缩放：连续量，每帧都发
                            } else if (now - lastPinchActionAt > PINCH_ACTION_COOLDOWN_MS) {
                                lastPinchActionAt = now
                                dispatchPinch(g2Pinch, if (factor > 1f) 1.06f else 0.94f)
                            }
                        } else if (g2Swipe != 0 && (kotlin.math.abs(dCx) > 0.5f || kotlin.math.abs(dCy) > 0.5f)) {
                            // 双指滑动 → 滚轮（滚动比例可配置）
                            // 与光标移动同理：横纵都用 h 归一化，横向/纵向滚动速度才一致
                            onMouse?.invoke(Protocol.MOUSE_SCROLL, dCx / h * scrollScale, dCy / h * scrollScale)
                            onDebug?.invoke("双指滑动 dx=${String.format("%.3f", dCx / h)} dy=${String.format("%.3f", dCy / h)} → 滚动")
                        }
                    }
                    tpLastCx = cx
                    tpLastCy = cy
                    tpLastSpan = span
                } else {
                    // 单指 → 移动光标（速度倍率可配置）
                    //
                    // 两个关键修正：
                    // ① 不再丢步：以前有 0.01 的归一化阈值（≈7px），慢速移动时每个事件的
                    //    位移都掉在阈值里被丢弃，基准点却已经更新 → 表现为「不动」，
                    //    偶尔攒够一次才发一跳 → 又「飘」。现在每个位移都发出去。
                    // ② 提升采样率：Android 会把同一帧内的高频采样点合并进
                    //    getHistoricalX/Y（不单独派发事件），只吃最新点会丢掉中间的采样，
                    //    慢速移动就变成了几段折线。这里逐个历史点补发。
                    val idx = if (tpFingerId >= 0) e.findPointerIndex(tpFingerId) else -1
                    val hs = if (idx >= 0) e.historySize else 0
                    var prevX = tpSingleX
                    var prevY = tpSingleY
                    if (idx >= 0) {
                        for (k in 0 until hs) {
                            val hx = e.getHistoricalX(idx, k)
                            val hy = e.getHistoricalY(idx, k)
                            tpMoved += kotlin.math.abs(hx - prevX) + kotlin.math.abs(hy - prevY)
                            sendMouseMove(hx - prevX, hy - prevY)
                            prevX = hx; prevY = hy
                        }
                    }
                    val dx = cx - prevX
                    val dy = cy - prevY
                    tpMoved += kotlin.math.abs(dx) + kotlin.math.abs(dy)
                    sendMouseMove(dx, dy)
                    tpSingleX = cx
                    tpSingleY = cy
                }
            }

            MotionEvent.ACTION_POINTER_UP -> {
                tpActiveFingers = maxOf(0, cnt - 1)
                // 从多指回到单指：重置单指基准，避免光标突然跳一下
                tpSingleX = cx
                tpSingleY = cy
                tpLastCx = cx
                tpLastCy = cy
                tpLastSpan = span
            }

            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                tpActiveFingers = 0
                tpFingerId = -1
                val dur = android.os.SystemClock.uptimeMillis() - tpStartTime
                val isTap = tpMoved < tapSlopPx * 1.5f && dur < TAP_TIMEOUT_MS
                when {
                    isTap && tpMaxPointers >= 4 -> {
                        if (g4Tap == 9) onSwitchMode?.invoke()
                        onDebug?.invoke("四指点击 → ${actionName(g4Tap)}")
                    }
                    isTap && tpMaxPointers == 3 -> {
                        dispatchAction(g3Tap, "三指点击")
                    }
                    isTap && tpMaxPointers == 2 -> {
                        dispatchAction(g2Tap, "双指点击")
                    }
                    isTap && tpMaxPointers == 1 -> {
                        // 单击=左键；快速双击=右键（与原有行为一致）
                        val now = android.os.SystemClock.uptimeMillis()
                        val nearInTime = now - lastTapAt <= DOUBLE_TAP_WINDOW_MS
                        val nearInSpace = kotlin.math.hypot(
                            (cx - lastTapX).toDouble(), (cy - lastTapY).toDouble()
                        ) <= doubleTapSlopPx
                        if (nearInTime && nearInSpace) {
                            lastTapAt = 0L
                            onMouse?.invoke(Protocol.MOUSE_RIGHT_CLICK, 0f, 0f)
                            onDebug?.invoke("双击 → 鼠标右键")
                        } else {
                            lastTapAt = now
                            lastTapX = cx
                            lastTapY = cy
                            handler.postDelayed(singleTapAction, DOUBLE_TAP_WINDOW_MS)
                        }
                    }
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
        private const val TAG = "SpenTablet"
        private const val TAP_TIMEOUT_MS = 320        // 按太久就不算敲击
        private const val DOUBLE_TAP_WINDOW_MS = 280L // 双击窗口；同时也是单击的延迟
        /** 操作框最小缩放（与 PC 端 MappingConfig.MIN_SCALE 保持一致） */
        private const val MIN_FRAME = 0.05f
        /** 三指左右滑的触发阈值(px) */
        private const val SWIPE_PX = 140f
        /** 判定为「捏合」而不是「滚动」的相对跨度变化阈值 */
        private const val PINCH_REL = 0.12f
        /** 捏合触发一次性动作（音量/任务切换等）的冷却时间(ms) */
        private const val PINCH_ACTION_COOLDOWN_MS = 320L
        /** 左右滑动触发一次性动作的冷却时间(ms)：一次滑动只触发一次，不会连发 */
        private const val SWIPE_ACTION_COOLDOWN_MS = 450L
    }
}
