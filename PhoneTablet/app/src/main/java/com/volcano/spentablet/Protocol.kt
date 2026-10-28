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
    /** 缩放帧：双指捏合 → 缩放 PC 上的操作范围框 */
    const val TYPE_ZOOM: Int = 0x09
    /** 复位帧：把操作范围框恢复成整个书写区 */
    const val TYPE_RESET: Int = 0x0A
    /** INFO 帧：PC 主机主动下发的版本号/计算机名/候选 IP（供手机端拓扑图展示） */
    const val TYPE_INFO: Int = 0x0C
    /** 备注帧：数位板模式下控制「桌面笔迹」叠加层。payload[0]=action: 0=关 1=开 2=撤销一笔 3=清空 */
    const val TYPE_NOTE: Int = 0x0D
    /** 截图请求（上行）：PC 截取当前范围框画面，经 TYPE_SHOT 分片回传 */
    const val TYPE_SHOT_REQ: Int = 0x0E
    /** 截图数据（下行，分片）：头部为 totalLen(4字节) + seq(2字节)，其后是数据，seq 从 0 起 */
    const val TYPE_SHOT: Int = 0x0F
    /** 激光笔帧：触摸板模式下把桌面指针变成激光笔。payload[0]=开关(0/1)，payload[1]=颜色索引 */
    const val TYPE_HL: Int = 0x10
    /** 截屏保存请求（上行）：payload[0]=范围(0=桌面范围框 1=整块虚拟屏幕)，[1]=存到 PC，[2]=存到手机相册 */
    const val TYPE_SAVE_REQ: Int = 0x11
    /** 截屏数据（下行分片，与 TYPE_SHOT 同格式）：totalLen(u32) + seq(u16) + data */
    const val TYPE_SAVE: Int = 0x12

    // ---- 备注动作 ----
    const val NOTE_OFF: Int = 0
    const val NOTE_ON: Int = 1
    const val NOTE_UNDO: Int = 2
    const val NOTE_CLEAR: Int = 3
    /** 切换笔迹颜色：arg = 预设颜色索引（与 PC 端 NoteOverlay.Presets 顺序一致） */
    const val NOTE_COLOR: Int = 4

    // ---- 工作模式 ----
    const val MODE_TABLET: Int = 0   // 数位板：S Pen 在框内作画，手指拖动框
    const val MODE_MOUSE: Int = 1    // 鼠标：取消框，手指当触摸板

    // ---- 鼠标动作 ----
    const val MOUSE_MOVE: Int = 0
    const val MOUSE_LEFT_DOWN: Int = 1
    const val MOUSE_LEFT_UP: Int = 2
    const val MOUSE_LEFT_CLICK: Int = 3
    const val MOUSE_RIGHT_CLICK: Int = 4
    /** 滚轮：dx/dy 是相对书写区的位移（手指上滑为负） */
    const val MOUSE_SCROLL: Int = 5
    /** 捏合缩放：factor>1 放大（双指张开），<1 缩小 */
    const val MOUSE_ZOOM: Int = 6
    /** 中键点击 */
    const val MOUSE_MIDDLE_CLICK: Int = 7
    /** 后退（浏览器） */
    const val MOUSE_BACK: Int = 8
    /** 前进（浏览器） */
    const val MOUSE_FORWARD: Int = 9
    /** 音量 + */
    const val MOUSE_VOL_UP: Int = 10
    /** 音量 - */
    const val MOUSE_VOL_DOWN: Int = 11
    /** 静音 */
    const val MOUSE_VOL_MUTE: Int = 12
    /** 任务切换：Alt+Tab */
    const val MOUSE_TASK_NEXT: Int = 13
    /** 任务反向切换：Alt+Shift+Tab */
    const val MOUSE_TASK_PREV: Int = 14
    /**
     * 手指是否正落在触摸板上（dx=1 在 / 0 离开）。
     * 触摸板模式下 S Pen 的悬停流会不停把桌面光标拽回笔的位置，
     * 手指的相对移动被反复覆盖 —— 手指往右下微动、光标却往左跑就是这个原因。
     * 手指一按上来，PC 端就让 S Pen 彻底让位（光标只跟手指）。
     */
    const val MOUSE_FINGER: Int = 15
    /**
     * 左键双击（点两下）。
     * 单独占一个动作码，是为了让 PC 端用**一次 SendInput** 把 下-上-下-上 四个事件全发出去：
     * 拆成两次 MOUSE_LEFT_CLICK 时，两次网络往返之间可能超过系统的双击时间，被当成两次单击。
     */
    const val MOUSE_LEFT_DOUBLE: Int = 16

    const val DEFAULT_PORT: Int = 6000

    /** 比例模式：与手机<br>0=拉伸铺满目标区域 1=保持手机宽高比（居中留边） */
    const val ASPECT_STRETCH: Int = 0
    const val ASPECT_KEEP: Int = 1

    private const val PAYLOAD_LEN = 24
    /** 握手帧 payload 长度（7 个 float） */
    private const val HELLO_LEN = 28

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

    /**
     * 心跳/保活帧：type=0，服务器忽略但不关闭连接，用于维持 adb reverse 隧道活跃。
     *
     * payload[0..3] 带上「手机已发出的鼠标帧数」（int LE）：PC 收到后与它自己
     * 实际收到的鼠标帧数比对，差值过大就说明帧在链路上丢了（半死连接），
     * PC 会主动断开重连。传 0 表示不参与比对。
     */
    fun buildNoop(mouseSent: Int = 0): ByteArray {
        val buf = ByteBuffer.allocate(6 + PAYLOAD_LEN).order(ByteOrder.LITTLE_ENDIAN)
        buf.put(MAGIC1)
        buf.put(MAGIC2)
        buf.put(VERSION)
        buf.put(0x00.toByte())
        buf.putShort(PAYLOAD_LEN.toShort())
        buf.putInt(mouseSent)
        for (i in 4 until PAYLOAD_LEN) buf.put(0)
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

    /**
     * 缩放帧：双指捏合。
     * payload: [0]=scaleFactor(float, >1 放大 / <1 缩小)
     *          [4]=focusX(float, 捏合中心在书写区的归一化 X)
     *          [8]=focusY(float, 归一化 Y)
     * PC 会以该焦点为锚点缩放，焦点处的内容不会跑位。
     */
    fun buildZoom(scale: Float, focusX: Float, focusY: Float): ByteArray {
        val buf = ByteBuffer.allocate(6 + PAYLOAD_LEN).order(ByteOrder.LITTLE_ENDIAN)
        buf.put(MAGIC1)
        buf.put(MAGIC2)
        buf.put(VERSION)
        buf.put(TYPE_ZOOM.toByte())
        buf.putShort(PAYLOAD_LEN.toShort())
        buf.putFloat(scale)
        buf.putFloat(focusX)
        buf.putFloat(focusY)
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

    /** 复位帧：操作框恢复成整个书写区（PC 端同步复位，保证两端形状一致）。 */
    fun buildReset(): ByteArray {
        val buf = ByteBuffer.allocate(6 + PAYLOAD_LEN).order(ByteOrder.LITTLE_ENDIAN)
        buf.put(MAGIC1)
        buf.put(MAGIC2)
        buf.put(VERSION)
        buf.put(TYPE_RESET.toByte())
        buf.putShort(PAYLOAD_LEN.toShort())
        for (i in 0 until PAYLOAD_LEN) buf.put(0)
        return buf.array()
    }

    /**
     * 备注帧：控制 PC 桌面笔迹叠加层。
     * payload[0] = action（见 NOTE_*）；payload[1] = arg。
     * 目前只有 NOTE_COLOR 用到 arg（预设颜色的索引），其余动作 arg 填 0。
     * payload 长度固定 PAYLOAD_LEN，老版本 PC 端只读 action，仍可兼容。
     */
    fun buildNote(action: Int, arg: Float = 0f): ByteArray {
        val buf = ByteBuffer.allocate(6 + PAYLOAD_LEN).order(ByteOrder.LITTLE_ENDIAN)
        buf.put(MAGIC1)
        buf.put(MAGIC2)
        buf.put(VERSION)
        buf.put(TYPE_NOTE.toByte())
        buf.putShort(PAYLOAD_LEN.toShort())
        buf.putFloat(action.toFloat())
        buf.putFloat(arg)
        for (i in 8 until PAYLOAD_LEN) buf.put(0)
        return buf.array()
    }

    /** 截图请求：PC 截取当前范围框画面并分片回传。 */
    fun buildShotReq(): ByteArray {
        val buf = ByteBuffer.allocate(6 + PAYLOAD_LEN).order(ByteOrder.LITTLE_ENDIAN)
        buf.put(MAGIC1)
        buf.put(MAGIC2)
        buf.put(VERSION)
        buf.put(TYPE_SHOT_REQ.toByte())
        buf.putShort(PAYLOAD_LEN.toShort())
        for (i in 0 until PAYLOAD_LEN) buf.put(0)
        return buf.array()
    }

    /**
     * 截屏保存请求：把「桌面范围框内容 + 桌面笔迹」合成一张 PNG 存起来。
     * [full]      true = 抓整块虚拟屏幕（多显示器全包），false = 只抓桌面范围框
     * [savePc]    存到 PC（目录在 PC 端配置里）
     * [savePhone] 传回手机存进相册
     */
    fun buildSaveReq(full: Boolean, savePc: Boolean, savePhone: Boolean): ByteArray {
        val buf = ByteBuffer.allocate(6 + PAYLOAD_LEN).order(ByteOrder.LITTLE_ENDIAN)
        buf.put(MAGIC1)
        buf.put(MAGIC2)
        buf.put(VERSION)
        buf.put(TYPE_SAVE_REQ.toByte())
        buf.putShort(PAYLOAD_LEN.toShort())
        buf.putFloat(if (full) 1f else 0f)
        buf.putFloat(if (savePc) 1f else 0f)
        buf.putFloat(if (savePhone) 1f else 0f)
        for (i in 3 until PAYLOAD_LEN / 4) buf.putFloat(0f)
        return buf.array()
    }

    /**
     * 激光笔帧：开关桌面上的激光笔（把指针变成激光笔）。
     * payload[0] = on(0/1)，payload[1] = 颜色索引（0 黄 / 1 绿 / 2 粉 / 3 蓝）。
     */
    fun buildHighlighter(on: Boolean, colorIdx: Int = 0): ByteArray {
        val buf = ByteBuffer.allocate(6 + PAYLOAD_LEN).order(ByteOrder.LITTLE_ENDIAN)
        buf.put(MAGIC1)
        buf.put(MAGIC2)
        buf.put(VERSION)
        buf.put(TYPE_HL.toByte())
        buf.putShort(PAYLOAD_LEN.toShort())
        buf.putFloat(if (on) 1f else 0f)
        buf.putFloat(colorIdx.toFloat())
        for (i in 8 until PAYLOAD_LEN) buf.put(0)
        return buf.array()
    }

    /**
     * 握手帧：连上后立刻发送，也用于设置变更后通知 PC。
     * payload(28 字节):
     *   [0]=surfaceW(float) 书写区宽  [4]=surfaceH(float) 书写区高
     *   [8]=aspectMode(float)        [12]=workMode(float)
     *   [16]=frameL [20]=frameT [24]=frameScale   ← 手机端操作框状态
     * PC 据此恢复出**与手机端同形状同位置**的桌面范围框。
     */
    fun buildHello(
        surfaceW: Float,
        surfaceH: Float,
        aspectMode: Int,
        workMode: Int = MODE_TABLET,
        frameL: Float = 0f,
        frameT: Float = 0f,
        frameScale: Float = 1f
    ): ByteArray {
        val buf = ByteBuffer.allocate(6 + HELLO_LEN).order(ByteOrder.LITTLE_ENDIAN)
        buf.put(MAGIC1)
        buf.put(MAGIC2)
        buf.put(VERSION)
        buf.put(TYPE_HELLO.toByte())
        buf.putShort(HELLO_LEN.toShort())
        buf.putFloat(surfaceW)
        buf.putFloat(surfaceH)
        buf.putFloat(aspectMode.toFloat())
        buf.putFloat(workMode.toFloat())
        buf.putFloat(frameL)
        buf.putFloat(frameT)
        buf.putFloat(frameScale)
        return buf.array()
    }
}
