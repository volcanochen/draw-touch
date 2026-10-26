package com.volcano.spentablet

import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * 手机 <-> PC 的二进制通信协议（小端）。
 *
 * 帧: magic('S','P') + version(1) + type(1) + length(2,uint16) + payload(length)
 *
 * 笔事件 payload(24 字节):
 *   x(float32, 归一化 0..1, 左->右)
 *   y(float32, 归一化 0..1, 上->下)
 *   pressure(float32, 0..1)
 *   tiltX(float32, 角度 -90..90)
 *   tiltY(float32, 角度 -90..90)
 *   buttons(uint16: bit0=侧键/barrel, bit1=橡皮/反向)
 *   reserved(uint16)
 */
object Protocol {
    const val MAGIC1: Byte = 0x53 // 'S'
    const val MAGIC2: Byte = 0x50 // 'P'
    const val VERSION: Byte = 0x01

    const val TYPE_PEN_DOWN: Int = 0x01
    const val TYPE_PEN_MOVE: Int = 0x02
    const val TYPE_PEN_UP: Int = 0x03
    const val TYPE_PEN_HOVER: Int = 0x04
    /** 握手帧：手机上报“绘制面”的真实像素宽高与比例偏好，让 PC 不必再把手机比例拉伸到桌面比例 */
    const val TYPE_HELLO: Int = 0x05
    /** 平移帧：手指在手机上滑动 → 移动 PC 上的“操作范围框” */
    const val TYPE_PAN: Int = 0x06
    /** 鼠标帧：鼠标模式下手指当触摸板用（移动/左键/右键） */
    const val TYPE_MOUSE: Int = 0x07
    /** 模式帧：手机通知 PC 当前是数位板模式还是鼠标模式 */
    const val TYPE_MODE: Int = 0x08

    // ---- 工作模式 ----
    const val MODE_TABLET: Int = 0   // 数位板：S Pen 在框内作画，手指拖动框
    const val MODE_MOUSE: Int = 1    // 鼠标：取消框，手指当触摸板

    // ---- 鼠标动作 ----
    const val MOUSE_MOVE: Int = 0
    const val MOUSE_LEFT_DOWN: Int = 1
    const val MOUSE_LEFT_UP: Int = 2
    const val MOUSE_LEFT_CLICK: Int = 3
    const val MOUSE_RIGHT_CLICK: Int = 4

    const val DEFAULT_PORT: Int = 6000

    /** 比例模式：与手机<br>0=拉伸铺满目标区域 1=保持手机宽高比（居中留边） */
    const val ASPECT_STRETCH: Int = 0
    const val ASPECT_KEEP: Int = 1

    private const val PAYLOAD_LEN = 24

    fun buildPen(
        type: Int,
        x: Float,
        y: Float,
        pressure: Float,
        tiltX: Float,
        tiltY: Float,
        buttons: Int
    ): ByteArray {
        val buf = ByteBuffer.allocate(6 + PAYLOAD_LEN).order(ByteOrder.LITTLE_ENDIAN)
        buf.put(MAGIC1)
        buf.put(MAGIC2)
        buf.put(VERSION)
        buf.put(type.toByte())
        buf.putShort(PAYLOAD_LEN.toShort())
        buf.putFloat(x)
        buf.putFloat(y)
        buf.putFloat(pressure)
        buf.putFloat(tiltX)
        buf.putFloat(tiltY)
        buf.putShort((buttons and 0xFFFF).toShort())
        buf.putShort(0) // reserved
        return buf.array()
    }

    /** 心跳/保活帧：type=0，服务器忽略但不关闭连接，用于维持 adb reverse 隧道活跃。 */
    fun buildNoop(): ByteArray {
        val buf = ByteBuffer.allocate(6 + PAYLOAD_LEN).order(ByteOrder.LITTLE_ENDIAN)
        buf.put(MAGIC1)
        buf.put(MAGIC2)
        buf.put(VERSION)
        buf.put(0x00.toByte())
        buf.putShort(PAYLOAD_LEN.toShort())
        for (i in 0 until PAYLOAD_LEN) buf.put(0)
        return buf.array()
    }

    /**
     * 平移帧：dx/dy 是相对手机绘制面的归一化位移（如滑过半屏宽 = 0.5），
     * PC 端按“框自身尺寸”的比例移动操作范围框。
     */
    fun buildPan(dx: Float, dy: Float): ByteArray {
        val buf = ByteBuffer.allocate(6 + PAYLOAD_LEN).order(ByteOrder.LITTLE_ENDIAN)
        buf.put(MAGIC1)
        buf.put(MAGIC2)
        buf.put(VERSION)
        buf.put(TYPE_PAN.toByte())
        buf.putShort(PAYLOAD_LEN.toShort())
        buf.putFloat(dx)
        buf.putFloat(dy)
        for (i in 8 until PAYLOAD_LEN) buf.put(0)
        return buf.array()
    }

    /** 鼠标动作帧：kind 见 MOUSE_*，dx/dy 为归一化位移（仅 MOUSE_MOVE 使用）。 */
    fun buildMouse(kind: Int, dx: Float, dy: Float): ByteArray {
        val buf = ByteBuffer.allocate(6 + PAYLOAD_LEN).order(ByteOrder.LITTLE_ENDIAN)
        buf.put(MAGIC1)
        buf.put(MAGIC2)
        buf.put(VERSION)
        buf.put(TYPE_MOUSE.toByte())
        buf.putShort(PAYLOAD_LEN.toShort())
        buf.putFloat(kind.toFloat())
        buf.putFloat(dx)
        buf.putFloat(dy)
        for (i in 12 until PAYLOAD_LEN) buf.put(0)
        return buf.array()
    }

    /** 模式帧：切换数位板/鼠标模式时通知 PC。 */
    fun buildMode(mode: Int): ByteArray {
        val buf = ByteBuffer.allocate(6 + PAYLOAD_LEN).order(ByteOrder.LITTLE_ENDIAN)
        buf.put(MAGIC1)
        buf.put(MAGIC2)
        buf.put(VERSION)
        buf.put(TYPE_MODE.toByte())
        buf.putShort(PAYLOAD_LEN.toShort())
        buf.putFloat(mode.toFloat())
        for (i in 4 until PAYLOAD_LEN) buf.put(0)
        return buf.array()
    }

    /**
     * 握手帧：连上后立刻发送，也用于设置变更后通知 PC。
     * payload: [0]=surfaceW(float) [4]=surfaceH(float) [8]=aspectMode(float)
     *          [12]=workMode(float, MODE_TABLET=0 / MODE_MOUSE=1) 其余保留为 0
     */
    fun buildHello(surfaceW: Float, surfaceH: Float, aspectMode: Int, workMode: Int = MODE_TABLET): ByteArray {
        val buf = ByteBuffer.allocate(6 + PAYLOAD_LEN).order(ByteOrder.LITTLE_ENDIAN)
        buf.put(MAGIC1)
        buf.put(MAGIC2)
        buf.put(VERSION)
        buf.put(TYPE_HELLO.toByte())
        buf.putShort(PAYLOAD_LEN.toShort())
        buf.putFloat(surfaceW)
        buf.putFloat(surfaceH)
        buf.putFloat(aspectMode.toFloat())
        buf.putFloat(workMode.toFloat())
        for (i in 16 until PAYLOAD_LEN) buf.put(0)
        return buf.array()
    }
}
