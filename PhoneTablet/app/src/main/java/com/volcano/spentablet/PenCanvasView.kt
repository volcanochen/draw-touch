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
 */
class PenCanvasView(context: Context) : View(context) {

    /** type: Protocol.TYPE_PEN_*；x/y 为归一化 0..1；buttons: bit0=barrel, bit1=eraser */
    var listener: ((type: Int, x: Float, y: Float, pressure: Float, tiltX: Float, tiltY: Float, buttons: Int) -> Unit)? = null

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

    private fun handleEvent(e: MotionEvent, contact: Boolean) {
        val n = e.pointerCount
        for (i in 0 until n) {
            val tt = e.getToolType(i)
            Log.d("SpenTablet", "evt tool=$tt action=${e.actionMasked} contact=$contact")
            if (tt != MotionEvent.TOOL_TYPE_STYLUS) continue

            val x = e.getX(i)
            val y = e.getY(i)
            val nx = if (width > 0) x / width else 0f
            val ny = if (height > 0) y / height else 0f
            val pressure = e.getPressure(i)
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

            val action = e.actionMasked
            val type = when {
                contact && action == MotionEvent.ACTION_DOWN -> Protocol.TYPE_PEN_DOWN
                contact && action == MotionEvent.ACTION_UP -> Protocol.TYPE_PEN_UP
                contact -> Protocol.TYPE_PEN_MOVE
                else -> Protocol.TYPE_PEN_HOVER
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
}
