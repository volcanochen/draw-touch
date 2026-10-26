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
class NetClient(private val host: String, private val port: Int) {

    @Volatile
    var connected: Boolean = false
        private set

    private val queue = ArrayBlockingQueue<ByteArray>(CAPACITY)
    @Volatile
    private var running = false
    private var thread: Thread? = null

    /** 统计，供界面显示 */
    @Volatile var sentFrames = 0L
        private set
    @Volatile var droppedFrames = 0L
        private set
    @Volatile var lastError: String? = null
        private set

    fun start() {
        if (running) return
        running = true
        thread = Thread({ loop() }, "net-writer").apply { isDaemon = true; start() }
    }

    fun stop() {
        running = false
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

    private fun loop() {
        while (running) {
            var socket: Socket? = null
            try {
                socket = Socket()
                socket.connect(InetSocketAddress(host, port), 4000)
                socket.tcpNoDelay = true          // 笔迹要低延迟，禁用 Nagle
                socket.soTimeout = 3000
                val out = DataOutputStream(BufferedOutputStream(socket.getOutputStream(), 4096))
                connected = true
                lastError = null
                Log.i(TAG, "NET 已连接 $host:$port")

                var lastBeat = 0L
                while (running) {
                    val frame = queue.poll(200, TimeUnit.MILLISECONDS)
                    if (frame != null) {
                        out.write(frame)
                        out.flush()
                        sentFrames++
                        if (frame[3] == Protocol.TYPE_PEN_DOWN.toByte() ||
                            frame[3] == Protocol.TYPE_PEN_UP.toByte()
                        ) {
                            Log.d(TAG, "NET 发送 type=${frame[3]} 累计=$sentFrames")
                        }
                    }
                    val now = System.currentTimeMillis()
                    if (now - lastBeat >= 1000) {
                        out.write(Protocol.buildNoop())
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
}
