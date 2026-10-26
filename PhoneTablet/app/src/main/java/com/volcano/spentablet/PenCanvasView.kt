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
 */
class PenCanvasView(context: Context) : View(context) {

    /** type: Protocol.TYPE_PEN_*；x/y 为归一化 0..1；buttons: bit0=barrel, bit1=eraser */
    var listener: ((type: Int, x: Float, y: Float, pressure: Float, tiltX: Float, tiltY: Float, buttons: Int) -> Unit)? = null

    /** 诊断：把最后一条事件文本抛给界面显示 */
    var onDebug: ((String) -> Unit)? = null

    /**
     * 手指平移回调：(dx, dy) 为相对绘制面的归一化位移。
     * 数位板模式下手指滑动=移动 PC 上的操作范围框；S Pen 才用于绘制。
     */
    var onPan: ((Float, Float) -> Unit)? = null

    /**
     * 工作模式：Protocol.MODE_TABLET / MODE_MOUSE。
     * 两种模式下手指的用途**互斥**，绝不既拖框又控鼠标：
     *   MODE_TABLET —— 手指只走 onPan（拖范围框），不发任何鼠标帧；
     *   MODE_MOUSE  —— 手指只走 onMouse（当触摸板），不发任何平移帧。
     */
    var workMode: Int = Protocol.MODE_TABLET
        set(value) {
            if (field == value) return
            field = value
            resetFinger()
        }

    /**
     * 鼠标模式下的手指回调：(kind, dx, dy)。
     * kind 见 Protocol.MOUSE_*；dx/dy 为相对绘制面的归一化位移（仅 MOVE 使用）。
     */
    var onMouse: ((Int, Float, Float) -> Unit)? = null

    /**
     * 压感增益。内核声明 ABS_PRESSURE 量程 0~4095，但 S Pen 实际书写只到 ~500，
     * 直接上报的话压感只有 0.1 左右，在绘图软件里几乎看不出粗细变化。
     * 默认 3.0 把常用书写区间拉到 0.3~1.0，可在界面上按 ± 调整。
     */
    var pressureGain: Float = 3.0f

    /** 是否转发悬停事件（关掉可大幅降低数据量） */
    var sendHover: Boolean = true

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

    /** 敲击判定阈值（随屏幕密度换算成 px） */
    private val tapSlopPx: Float get() = 12f * resources.displayMetrics.density
    private val doubleTapSlopPx: Float get() = 48f * resources.displayMetrics.density

    // ---- 诊断计数：index = toolType(0..4)，第二维 = down/move/up/hover ----
    private val counters = Array(5) { IntArray(4) }

    override fun onSizeChanged(w: Int, h: Int, oldw: Int, oldh: Int) {
        super.onSizeChanged(w, h, oldw, oldh)
        bitmap?.recycle()
        if (w > 0 && h > 0) {
            bitmap = Bitmap.createBitmap(w, h, Bitmap.Config.ARGB_8888)
            canvas = Canvas(bitmap!!)
            canvas!!.drawColor(Color.WHITE)
        }
    }

    override fun onDraw(c: Canvas) {
        bitmap?.let { c.drawBitmap(it, 0f, 0f, null) }
    }

    fun clear() {
        bitmap?.eraseColor(Color.WHITE)
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

    private fun handleEvent(e: MotionEvent, contact: Boolean) {
        val n = e.pointerCount
        val action = e.actionMasked

        for (i in 0 until n) {
            val tt = e.getToolType(i)

            // 手指：不绘制。用途由工作模式决定，两种模式互斥
            if (tt == MotionEvent.TOOL_TYPE_FINGER || tt == MotionEvent.TOOL_TYPE_MOUSE) {
                val px = e.getX(i)
                val py = e.getY(i)
                if (workMode == Protocol.MODE_MOUSE) handleFingerMouse(action, px, py)
                else handleFingerPan(action, px, py)
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
            val nx = if (width > 0) x / width else 0f
            val ny = if (height > 0) y / height else 0f
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

            // 在手机屏上画出轨迹（悬停不画）
            if (contact) {
                when (type) {
                    Protocol.TYPE_PEN_DOWN -> {
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
            }

            if (!contact && !sendHover) {
                onDebug?.invoke("${EvtDump.actionName(e.action)} ${EvtDump.toolName(tt)} (悬停未发送)")
                return
            }

            onDebug?.invoke(
                "${EvtDump.actionName(e.action)} ${EvtDump.toolName(tt)} " +
                        "p=${String.format("%.2f", pressure)} gain=${String.format("%.1f", pressureGain)}"
            )
            listener?.invoke(type, nx, ny, pressure, tiltX, tiltY, buttons)
        }
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

    // ---------- 数位板模式：手指 = 拖动 PC 上的操作范围框 ----------
    private fun handleFingerPan(action: Int, px: Float, py: Float) {
        when (action) {
            MotionEvent.ACTION_DOWN -> {
                panLastX = px; panLastY = py; panning = true
                onDebug?.invoke("手指按下：拖动操作范围框")
            }
            MotionEvent.ACTION_MOVE -> {
                if (panning) {
                    val dx = (px - panLastX) / (if (width > 0) width else 1)
                    val dy = (py - panLastY) / (if (height > 0) height else 1)
                    panLastX = px; panLastY = py
                    onPan?.invoke(dx, dy)
                    onDebug?.invoke("移动操作范围框 dx=${String.format("%.2f", dx)} dy=${String.format("%.2f", dy)}")
                }
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                panning = false
                onDebug?.invoke("手指抬起")
            }
        }
    }

    // ---------- 鼠标模式：手指 = 触摸板（滑动移动光标 / 单击=左键 / 双击=右键） ----------
    private fun handleFingerMouse(action: Int, px: Float, py: Float) {
        val now = android.os.SystemClock.uptimeMillis()
        when (action) {
            MotionEvent.ACTION_DOWN -> {
                fingerDown = true
                fingerLastX = px; fingerLastY = py
                fingerMoved = 0f
                fingerDownAt = now
                tapCandidate = true
                // 上一次待发的单击先撤掉：如果这是双击的第二下，就不该再发左键
                handler.removeCallbacks(singleTapAction)

                val nearInTime = now - lastTapAt <= DOUBLE_TAP_WINDOW_MS
                val nearInSpace = kotlin.math.hypot(
                    (px - lastTapX).toDouble(), (py - lastTapY).toDouble()
                ) <= doubleTapSlopPx
                secondTap = nearInTime && nearInSpace
                if (secondTap) {
                    lastTapAt = 0L
                    onDebug?.invoke("第二次敲击（抬手 → 右键）")
                } else {
                    onDebug?.invoke("手指按下：鼠标模式（滑动=移动光标）")
                }
            }
            MotionEvent.ACTION_MOVE -> {
                if (!fingerDown) return
                val dx = px - fingerLastX
                val dy = py - fingerLastY
                fingerLastX = px; fingerLastY = py
                fingerMoved += kotlin.math.hypot(dx.toDouble(), dy.toDouble()).toFloat()

                if (fingerMoved > tapSlopPx) {
                    // 已经滑动起来 → 就是移动光标，不是敲击
                    tapCandidate = false
                    secondTap = false
                    lastTapAt = 0L
                    handler.removeCallbacks(singleTapAction)
                }
                val nx = dx / (if (width > 0) width else 1)
                val ny = dy / (if (height > 0) height else 1)
                onMouse?.invoke(Protocol.MOUSE_MOVE, nx, ny)
                onDebug?.invoke("移动光标 dx=${String.format("%.3f", nx)} dy=${String.format("%.3f", ny)}")
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                fingerDown = false
                val dur = now - fingerDownAt
                val isTap = tapCandidate && fingerMoved <= tapSlopPx && dur <= TAP_TIMEOUT_MS
                if (!isTap) {
                    onDebug?.invoke("手指抬起（滑动，已当触摸板移动）")
                    return
                }
                if (secondTap) {
                    secondTap = false
                    lastTapAt = 0L
                    onMouse?.invoke(Protocol.MOUSE_RIGHT_CLICK, 0f, 0f)
                    onDebug?.invoke("双击 → 鼠标右键")
                } else {
                    // 单击要等一个双击窗口，避免误判成双击时已经把左键发出去了
                    lastTapAt = now
                    lastTapX = px; lastTapY = py
                    handler.postDelayed(singleTapAction, DOUBLE_TAP_WINDOW_MS)
                    onDebug?.invoke("单击待定（${DOUBLE_TAP_WINDOW_MS}ms 内再敲一下 = 右键）")
                }
            }
        }
    }

    override fun onTouchEvent(event: MotionEvent): Boolean {
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
    }
}
