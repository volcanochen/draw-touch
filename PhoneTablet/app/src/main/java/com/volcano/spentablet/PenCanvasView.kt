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
    }
}
