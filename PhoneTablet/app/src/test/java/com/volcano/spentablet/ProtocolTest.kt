package com.volcano.spentablet

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * 协议帧编码回归测试（纯 JVM，不需要 Android 设备）。
 * 帧结构：'S''P' + ver(1) + type(1) + len(u16 LE) + payload(len)。
 * 与 PC 端 tests/protocol_smoke.py 的字节级断言互为镜像。
 */
class ProtocolTest {

    private fun ByteBuffer.u8() = this.get().toInt() and 0xFF

    private fun header(buf: ByteBuffer): Triple<Int, Int, Int> {
        buf.order(ByteOrder.LITTLE_ENDIAN)
        assertEquals('S'.code, buf.u8())
        assertEquals('P'.code, buf.u8())
        val ver = buf.u8()
        val type = buf.u8()
        val len = buf.short.toInt() and 0xFFFF
        assertEquals(Protocol.VERSION.toInt(), ver)
        return Triple(type, len, buf.position())
    }

    @Test
    fun `pen 帧 - 头部与 24 字节负载`() {
        val f = Protocol.buildPen(Protocol.TYPE_PEN_DOWN, 0.25f, 0.75f, 0.5f, -30f, 45f, 1)
        val buf = ByteBuffer.wrap(f)
        val (type, len, off) = header(buf)
        assertEquals(Protocol.TYPE_PEN_DOWN, type)
        assertEquals(24, len)
        assertEquals(6 + len, f.size)
        assertEquals(0.25f, buf.float, 1e-6f)
        assertEquals(0.75f, buf.float, 1e-6f)
        assertEquals(0.5f, buf.float, 1e-6f)
        assertEquals(-30f, buf.float, 1e-6f)
        assertEquals(45f, buf.float, 1e-6f)
        assertEquals(1, buf.short.toInt() and 0xFFFF) // buttons
        assertEquals(0, buf.short.toInt())             // reserved
    }

    @Test
    fun `mouse 帧 - kind 放在 payload0 且小端`() {
        val f = Protocol.buildMouse(Protocol.MOUSE_ZOOM, 1.25f, 0f)
        val buf = ByteBuffer.wrap(f)
        val (type, len, _) = header(buf)
        assertEquals(Protocol.TYPE_MOUSE, type)
        assertEquals(24, len)
        buf.position(6)
        assertEquals(Protocol.MOUSE_ZOOM.toFloat(), buf.float, 1e-6f)
        assertEquals(1.25f, buf.float, 1e-6f)
        assertEquals(0f, buf.float, 1e-6f)
    }

    @Test
    fun `zoom 帧 - scale 与焦点坐标`() {
        val f = Protocol.buildZoom(0.8f, 0.3f, 0.6f)
        val buf = ByteBuffer.wrap(f)
        val (type, _, _) = header(buf)
        assertEquals(Protocol.TYPE_ZOOM, type)
        buf.position(6)
        assertEquals(0.8f, buf.float, 1e-6f)
        assertEquals(0.3f, buf.float, 1e-6f)
        assertEquals(0.6f, buf.float, 1e-6f)
    }

    @Test
    fun `hello 帧 - 28 字节 7 个 float`() {
        val f = Protocol.buildHello(1080f, 2400f, Protocol.ASPECT_KEEP, Protocol.MODE_MOUSE, 0.1f, 0.2f, 1.5f)
        val buf = ByteBuffer.wrap(f)
        val (type, len, _) = header(buf)
        assertEquals(Protocol.TYPE_HELLO, type)
        assertEquals(28, len)
        assertEquals(6 + 28, f.size)
        assertEquals(1080f, buf.float, 1e-4f)
        assertEquals(2400f, buf.float, 1e-4f)
        assertEquals(Protocol.ASPECT_KEEP.toFloat(), buf.float, 1e-6f)
        assertEquals(Protocol.MODE_MOUSE.toFloat(), buf.float, 1e-6f)
        assertEquals(0.1f, buf.float, 1e-6f)
        assertEquals(0.2f, buf.float, 1e-6f)
        assertEquals(1.5f, buf.float, 1e-6f)
    }

    @Test
    fun `note 帧 - action 与 arg`() {
        val f = Protocol.buildNote(Protocol.NOTE_COLOR, 2f)
        val buf = ByteBuffer.wrap(f)
        val (type, _, _) = header(buf)
        assertEquals(Protocol.TYPE_NOTE, type)
        buf.position(6)
        assertEquals(Protocol.NOTE_COLOR.toFloat(), buf.float, 1e-6f)
        assertEquals(2f, buf.float, 1e-6f)
    }

    @Test
    fun `激光笔帧 - 开关与颜色索引`() {
        val f = Protocol.buildHighlighter(true, 3)
        val buf = ByteBuffer.wrap(f)
        val (type, _, _) = header(buf)
        assertEquals(Protocol.TYPE_HL, type)
        buf.position(6)
        assertEquals(1f, buf.float, 1e-6f)
        assertEquals(3f, buf.float, 1e-6f)

        val off = Protocol.buildHighlighter(false)
        val b2 = ByteBuffer.wrap(off)
        header(b2)
        b2.position(6)
        assertEquals(0f, b2.float, 1e-6f)
    }

    @Test
    fun `shot 请求与 noop 与 mode 与 reset 帧`() {
        val req = Protocol.buildShotReq()
        val h1 = header(ByteBuffer.wrap(req))
        assertEquals(Protocol.TYPE_SHOT_REQ, h1.first)
        assertEquals(24, h1.second)

        val noop = Protocol.buildNoop()
        assertEquals(0, header(ByteBuffer.wrap(noop)).first)

        val mode = Protocol.buildMode(Protocol.MODE_TABLET)
        val b3 = ByteBuffer.wrap(mode)
        assertEquals(Protocol.TYPE_MODE, header(b3).first)
        b3.position(6)
        assertEquals(0f, b3.float, 1e-6f)

        val rst = Protocol.buildReset()
        assertEquals(Protocol.TYPE_RESET, header(ByteBuffer.wrap(rst)).first)
    }

    @Test
    fun `心跳帧带上已发鼠标帧数 - PC 靠它发现链路丢帧`() {
        val f = Protocol.buildNoop(123456)
        val buf = ByteBuffer.wrap(f)
        assertEquals(0, header(buf).first)          // type=0 心跳
        buf.position(6)
        assertEquals(123456, buf.int)                // payload[0..3] = 已发鼠标帧数
        assertEquals(24, f.size - 6)                 // 负载长度仍是 24

        // 不传时为 0（表示不参与比对）
        val f0 = Protocol.buildNoop()
        val b0 = ByteBuffer.wrap(f0)
        header(b0)
        b0.position(6)
        assertEquals(0, b0.int)
    }

    @Test
    fun `动作码契约 - 与 README 及 PC 端约定一致`() {
        // 这些码一旦变更就是破坏性协议变更，测试钉死
        assertEquals(0, Protocol.MOUSE_MOVE)
        assertEquals(1, Protocol.MOUSE_LEFT_DOWN)
        assertEquals(2, Protocol.MOUSE_LEFT_UP)
        assertEquals(3, Protocol.MOUSE_LEFT_CLICK)
        assertEquals(4, Protocol.MOUSE_RIGHT_CLICK)
        assertEquals(5, Protocol.MOUSE_SCROLL)
        assertEquals(6, Protocol.MOUSE_ZOOM)
        assertEquals(7, Protocol.MOUSE_MIDDLE_CLICK)
        assertEquals(8, Protocol.MOUSE_BACK)
        assertEquals(9, Protocol.MOUSE_FORWARD)
        assertEquals(15, Protocol.MOUSE_FINGER)
        assertEquals(16, Protocol.MOUSE_LEFT_DOUBLE)
        assertEquals(0x05, Protocol.TYPE_HELLO)
        assertEquals(0x07, Protocol.TYPE_MOUSE)
        assertEquals(0x10, Protocol.TYPE_HL)
        assertEquals(0x11, Protocol.TYPE_SAVE_REQ)
        assertEquals(0x12, Protocol.TYPE_SAVE)
    }
}
