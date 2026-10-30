package com.volcano.drawtouch

import android.view.MotionEvent

/**
 * 诊断用：把 MotionEvent 的所有关键字段打成一串文本。
 * 用来判断 S Pen 的接触事件到底有没有到达 App、以什么 toolType / source 到达。
 */
object EvtDump {

    fun actionName(a: Int): String = when (a) {
        MotionEvent.ACTION_DOWN -> "DOWN"
        MotionEvent.ACTION_UP -> "UP"
        MotionEvent.ACTION_MOVE -> "MOVE"
        MotionEvent.ACTION_CANCEL -> "CANCEL"
        MotionEvent.ACTION_POINTER_DOWN -> "PTR_DOWN"
        MotionEvent.ACTION_POINTER_UP -> "PTR_UP"
        MotionEvent.ACTION_HOVER_ENTER -> "HOVER_ENTER"
        MotionEvent.ACTION_HOVER_MOVE -> "HOVER_MOVE"
        MotionEvent.ACTION_HOVER_EXIT -> "HOVER_EXIT"
        MotionEvent.ACTION_BUTTON_PRESS -> "BTN_PRESS"
        MotionEvent.ACTION_BUTTON_RELEASE -> "BTN_RELEASE"
        MotionEvent.ACTION_SCROLL -> "SCROLL"
        else -> "RAW_$a"
    }

    fun toolName(t: Int): String = when (t) {
        MotionEvent.TOOL_TYPE_UNKNOWN -> "UNKNOWN(0)"
        MotionEvent.TOOL_TYPE_FINGER -> "FINGER(1)"
        MotionEvent.TOOL_TYPE_STYLUS -> "STYLUS(2)"
        MotionEvent.TOOL_TYPE_MOUSE -> "MOUSE(3)"
        MotionEvent.TOOL_TYPE_ERASER -> "ERASER(4)"
        else -> "OTHER($t)"
    }

    fun sourceName(s: Int): String {
        val sb = StringBuilder()
        if (s and android.view.InputDevice.SOURCE_STYLUS != 0) sb.append("STYLUS|")
        if (s and android.view.InputDevice.SOURCE_TOUCHSCREEN != 0) sb.append("TOUCHSCREEN|")
        if (s and android.view.InputDevice.SOURCE_MOUSE != 0) sb.append("MOUSE|")
        if (s and android.view.InputDevice.SOURCE_TOUCHPAD != 0) sb.append("TOUCHPAD|")
        if (s and android.view.InputDevice.SOURCE_CLASS_POINTER != 0) sb.append("CLASS_POINTER|")
        if (sb.isEmpty()) sb.append("NONE|")
        return "0x${Integer.toHexString(s)}(${sb.toString().trimEnd('|')})"
    }

    fun s(e: MotionEvent): String {
        val sb = StringBuilder()
        sb.append(actionName(e.action))
        sb.append(" masked=").append(e.actionMasked)
        sb.append(" src=").append(sourceName(e.source))
        sb.append(" pc=").append(e.pointerCount)
        sb.append(" btn=0x").append(Integer.toHexString(e.buttonState))
        sb.append(" cls=").append(e.classification)
        sb.append(" dev=").append(e.device?.name ?: "?")
        sb.append(" id=").append(e.device?.id ?: -1)
        for (i in 0 until e.pointerCount) {
            sb.append(" {p").append(i)
            sb.append(" id=").append(e.getPointerId(i))
            sb.append(" tool=").append(toolName(e.getToolType(i)))
            sb.append(" x=").append(String.format("%.0f", e.getX(i)))
            sb.append(" y=").append(String.format("%.0f", e.getY(i)))
            sb.append(" prs=").append(String.format("%.2f", e.getPressure(i)))
            sb.append(" size=").append(String.format("%.2f", e.getSize(i)))
            sb.append(" touchMajor=").append(String.format("%.1f", e.getTouchMajor(i)))
            sb.append(" dist=").append(String.format("%.2f", e.getAxisValue(MotionEvent.AXIS_DISTANCE, i)))
            sb.append(" tilt=").append(String.format("%.2f", e.getAxisValue(MotionEvent.AXIS_TILT, i)))
            sb.append(" orient=").append(String.format("%.2f", e.getAxisValue(MotionEvent.AXIS_ORIENTATION, i)))
            sb.append("}")
        }
        return sb.toString()
    }
}
