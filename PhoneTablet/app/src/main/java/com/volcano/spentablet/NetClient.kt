package com.volcano.spentablet

import android.util.Log
import java.io.BufferedOutputStream
import java.io.DataOutputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.TimeUnit

/**
 * 维持到 PC 的 TCP 长连接。
 *
 * 设计要点（旧版的两个致命 bug 都在这里）：
 * 1) **绝不在调用方（UI 主线程）线程上做 socket 写**。旧版 send() 直接 write+flush，
 *    笔事件突发（60~120Hz）时一旦抛异常就把 out 置 null 并关 socket，
 *    UI 线程后续所有帧都会走进 “SEND skipped (out=null)” 被静默丢弃 ——
 *    这就是“只有悬停、没有落笔”的真凶（悬停帧多，总有几帧撞上有效窗口）。
 *    现在 send() 只做非阻塞入队，真正的 IO 在独立的写线程里完成。
 * 2) 所有异常都打日志，不再静默吞掉。
 * 3) 队列满时优先丢弃 MOVE/HOVER，绝不丢 DOWN/UP（否则 PC 端笔状态机会错乱）。
 */
class NetClient(
    private val host: String,
    private val port: Int,
    /** 握手帧的构造回调：每次连上、以及调用 sendHelloNow() 时都会取一次，保证拿到的是最新设置 */
    private val hello: () -> ByteArray
) {

    @Volatile
    var connected: Boolean = false
        private set

    private val queue = ArrayBlockingQueue<ByteArray>(CAPACITY)
    @Volatile
    private var running = false
    /** 写线程代次：stop() 后递增，旧线程据此自杀，杜绝「旧线程复活另开 socket」 */
    @Volatile
    private var generation = 0
    private var thread: Thread? = null

    /** 统计，供界面显示 */
    @Volatile var sentFrames = 0L
        private set
    /** 已发出的「鼠标帧」数量：写进心跳里，让 PC 判断帧有没有在链路上丢 */
    @Volatile var mouseFramesSent = 0L
        private set
    @Volatile var droppedFrames = 0L
        private set
    @Volatile var lastError: String? = null
        private set

    /**
     * 非空 = 拒绝再连这台 PC（协议版本不兼容等）。写线程每轮循环都检查，
     * 命中就直接退出不再重连 —— 版本对不上的两端硬连，只会出现查不出原因的怪象。
     * 置 null 恢复正常连接（用户手动改 PC 地址 / 清除阻止时用）。
     */
    @Volatile var holdReason: String? = null

    /** 收到 PC 主机下发的 INFO 帧（版本号/计算机名/候选 IP 文本）时回调。 */
    var onInfo: ((String) -> Unit)? = null
    /** 收到 PC 下发的完整截图（PNG 字节） */
    var onShot: ((ByteArray) -> Unit)? = null
    /** 截屏保存：PC 合成好的 PNG 分片（格式同 TYPE_SHOT），存进相册用 */
    var onSave: ((ByteArray) -> Unit)? = null
    private val shotBuf = java.io.ByteArrayOutputStream()
    private val saveBuf = java.io.ByteArrayOutputStream()

    /** 设置变更后主动重发握手帧，通知 PC 采用新的比例模式（无需断开重连）。 */
    fun sendHelloNow() {
        val f = hello()
        send(f)
        Log.i(TAG, "NET 重发 HELLO")
    }

    /**
     * 把底层异常翻译成「看得懂 + 能行动」的中文提示。
     * 之前只显示 "ConnectException: Connection refused"，用户完全不知道该做什么。
     */
    fun friendlyError(raw: String?): String {
        if (raw.isNullOrBlank()) return ""
        val s = raw.lowercase()
        return when {
            s.contains("refused") ->
                "连接被拒绝：PC 端没在监听，或被 Windows 防火墙拦了。\n" +
                        "→ 确认 PC 上 SpenTabletHost 已启动（托盘图标在右下角）\n" +
                        "→ 在 PC 端配置窗口点「允许防火墙」并同意 UAC 提权"
            s.contains("timeout") || s.contains("timed out") ->
                "连接超时：请求发出去了但没回应。\n" +
                        "→ USB 网络共享时 IP 要填 **PC 上那张 USB 网卡的 IP**（不是 WiFi IP）\n" +
                        "→ **最常见原因：PC 的 Windows 防火墙把入站连接静默丢了**\n" +
                        "   （表现是超时而不是「拒绝」）。请在 PC 端点「允许防火墙」\n" +
                        "→ 点「自动查找 PC」让手机自己扫出 PC 的地址"
            s.contains("no route") || s.contains("unreachable") || s.contains("network is unreachable") ->
                "网络不可达：PC 的 IP 在当前网络里根本不存在。\n" +
                        "→ 点「自动查找 PC」，或用 ipconfig 在 PC 上确认网卡 IP"
            s.contains("econnreset") || s.contains("reset by peer") ->
                "连接被重置：可能是 adb 隧道断了。\n" +
                        "→ 在 PC 上重跑 adb reverse tcp:6000 tcp:6000，并重启本 App"
            s.contains("bind") ->
                "本机端口被占用：把端口换一个（比如 6001）再试"
            else ->
                "连接失败：$raw\n→ 确认 PC IP/端口正确，且防火墙放行"
        }
    }

    fun start() {
        if (running) return
        running = true
        val gen = ++generation
        thread = Thread({ loop(gen) }, "net-writer").apply { isDaemon = true; start() }
    }

    fun stop() {
        running = false
        // 递增代次：让旧写线程即使被唤醒也立刻退出。
        // 否则 stop() 后再 start()，旧线程会在 running 重新为 true 时「复活」，
        // 另开一条 socket —— 两条写线程抢同一个队列，帧会写进其中一条（可能是死链路），
        // 表现就是「手机一直在发帧、PC 却一帧收不到」。
        generation++
        thread?.interrupt()
        thread = null
        synchronized(queue) { queue.clear() }
        connected = false
    }

    /** 非阻塞入队。队列满时丢掉最旧的一帧（优先保住 DOWN/UP）。 */
    fun send(data: ByteArray) {
        if (!running) return
        if (queue.offer(data)) return
        // 队列满：从队首找一帧可丢的（MOVE=2 / HOVER=4），给新帧腾位置
        synchronized(queue) {
            val it = queue.iterator()
            while (it.hasNext()) {
                val t = it.next()[3].toInt()
                if (t == Protocol.TYPE_PEN_MOVE || t == Protocol.TYPE_PEN_HOVER) {
                    it.remove()
                    droppedFrames++
                    break
                }
            }
            if (!queue.offer(data)) {
                // 实在放不下（全是 DOWN/UP 的极端情况），丢队首
                queue.poll()
                queue.offer(data)
                droppedFrames++
            }
        }
    }

    private fun loop(gen: Int) {
        while (running && gen == generation && holdReason == null) {
            var socket: Socket? = null
            try {
                socket = Socket()
                socket.connect(InetSocketAddress(host, port), 4000)
                socket.tcpNoDelay = true          // 笔迹要低延迟，禁用 Nagle
                // 读线程用无限超时：没有服务器下行帧时就一直阻塞等待，
                // 断线由写线程的写异常触发重连，避免每 3s 刷一条「读超时」日志
                socket.soTimeout = 0
                val out = DataOutputStream(BufferedOutputStream(socket.getOutputStream(), 4096))
                connected = true
                lastError = null
                Log.i(TAG, "NET 已连接 $host:$port")

                // 握手：把绘制面尺寸与比例偏好交给 PC，让它做正确的等比映射
                val hf = hello()
                out.write(hf)
                out.flush()
                Log.i(TAG, "NET 发送 HELLO（比例 ${hf[3]}）")

                // 启动读线程：接收 PC 下发的 INFO 帧（版本号/计算机名/候选 IP 等）
                val reader = Thread({ readLoop(socket) }, "net-reader").apply {
                    isDaemon = true
                    start()
                }

                var lastBeat = 0L
                while (running && gen == generation && holdReason == null) {
                    val frame = queue.poll(200, TimeUnit.MILLISECONDS)
                    if (frame != null) {
                        out.write(frame)
                        out.flush()
                        sentFrames++
                        if (frame[3] == Protocol.TYPE_MOUSE.toByte()) mouseFramesSent++
                        if (frame[3] == Protocol.TYPE_PEN_DOWN.toByte() ||
                            frame[3] == Protocol.TYPE_PEN_UP.toByte()
                        ) {
                            Log.d(TAG, "NET 发送 type=${frame[3]} 累计=$sentFrames")
                        }
                    }
                    val now = System.currentTimeMillis()
                    if (now - lastBeat >= 1000) {
                        // 心跳里带上「已发鼠标帧数」：PC 用它发现帧在链路上丢了
                        out.write(Protocol.buildNoop(mouseFramesSent.toInt()))
                        out.flush()
                        lastBeat = now
                    }
                }
                break
            } catch (e: Exception) {
                // 关键：旧版这里静默吞异常，导致永远查不出断连原因
                val msg = "${e.javaClass.simpleName}: ${e.message}"
                lastError = msg
                Log.w(TAG, "NET 异常 -> $msg")
            } finally {
                connected = false
                try { socket?.close() } catch (_: Exception) { }
                Log.w(TAG, "NET 断开，准备重连")
                // 代次已被 stop() 推进 → 旧线程到此为止，绝不复活、绝不再开一条 socket
                if (gen != generation) return
                if (running) {
                    try { Thread.sleep(400) } catch (_: InterruptedException) { return }
                }
            }
        }
    }

    companion object {
        private const val TAG = "SpenTablet"
        private const val CAPACITY = 512
    }

    /**
     * 读线程：解析 PC → 手机下发的帧。当前只关心 INFO 帧（版本号/计算机名/候选 IP），
     * 其它类型（目前 PC 不下发别的）忽略。与写线程共用同一个 socket 的两个独立流，互不影响。
     */
    private fun readLoop(socket: Socket) {
        try {
            val ins = socket.getInputStream()
            val header = ByteArray(6)
            while (running && holdReason == null) {
                if (!readExactly(ins, header, 6)) break
                if (header[0] != Protocol.MAGIC1.toByte() || header[1] != Protocol.MAGIC2.toByte()) {
                    Log.w(TAG, "NET 收到非法帧头，断开读线程")
                    break
                }
                val len = (header[4].toInt() and 0xFF) or ((header[5].toInt() and 0xFF) shl 8)
                if (len < 0 || len > 65535) break
                val payload = ByteArray(len)
                if (!readExactly(ins, payload, len)) break
                when (header[3].toInt() and 0xFF) {
                    Protocol.TYPE_INFO -> {
                        val text = payload.toString(Charsets.UTF_8)
                        Log.i(TAG, "NET 收到 INFO：$text")
                        onInfo?.invoke(text)
                    }
                    Protocol.TYPE_SHOT -> {
                        // 分片：[totalLen:uint32 LE][seq:uint16 LE][data...]
                        if (payload.size >= 6) {
                            val bb = java.nio.ByteBuffer.wrap(payload).order(java.nio.ByteOrder.LITTLE_ENDIAN)
                            val total = bb.int
                            val seq = bb.short.toInt() and 0xFFFF
                            val data = payload.copyOfRange(6, payload.size)
                            if (seq == 0) shotBuf.reset()
                            if (total > 0 && shotBuf.size() + data.size <= total + 65536) {
                                shotBuf.write(data)
                                if (shotBuf.size() >= total) {
                                    val png = shotBuf.toByteArray()
                                    shotBuf.reset()
                                    Log.i(TAG, "NET 收到截图 ${png.size} 字节")
                                    onShot?.invoke(png)
                                }
                            }
                        }
                    }
                    Protocol.TYPE_SAVE -> {
                        // 与 TYPE_SHOT 完全相同的分片格式，只是用途不同（存相册而不是当背景）
                        if (payload.size >= 6) {
                            val bb = java.nio.ByteBuffer.wrap(payload).order(java.nio.ByteOrder.LITTLE_ENDIAN)
                            val total = bb.int
                            val seq = bb.short.toInt() and 0xFFFF
                            val data = payload.copyOfRange(6, payload.size)
                            if (seq == 0) saveBuf.reset()
                            if (total > 0 && saveBuf.size() + data.size <= total + 65536) {
                                saveBuf.write(data)
                                if (saveBuf.size() >= total) {
                                    val png = saveBuf.toByteArray()
                                    saveBuf.reset()
                                    Log.i(TAG, "NET 收到截屏（待保存）${png.size} 字节")
                                    onSave?.invoke(png)
                                }
                            }
                        }
                    }
                    else -> { /* 其它类型暂忽略 */ }
                }
            }
        } catch (e: Exception) {
            if (running) Log.w(TAG, "NET 读线程异常：${e.message}")
        }
    }

    private fun readExactly(ins: java.io.InputStream, buf: ByteArray, count: Int): Boolean {
        var off = 0
        while (off < count) {
            val r = ins.read(buf, off, count - off)
            if (r <= 0) return false
            off += r
        }
        return true
    }
}
