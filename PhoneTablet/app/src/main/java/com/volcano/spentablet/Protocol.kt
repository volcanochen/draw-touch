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

    const val DEFAULT_PORT: Int = 6000

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
}
