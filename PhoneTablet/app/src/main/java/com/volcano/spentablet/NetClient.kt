package com.volcano.spentablet

import java.io.BufferedOutputStream
import java.io.DataOutputStream
import java.net.InetSocketAddress
import java.net.Socket
import android.util.Log

/**
 * 维持到 PC 的 TCP 长连接，断线自动重连。发送在主线程调用，内部加锁并捕获异常。
 */
class NetClient(private val host: String, private val port: Int) {

    @Volatile
    var connected: Boolean = false
        private set

    private var socket: Socket? = null
    private var out: DataOutputStream? = null
    private val lock = Any()
    @Volatile
    private var running = false

    fun start() {
        running = true
        Thread { runLoop() }.start()
    }

    fun stop() {
        running = false
        synchronized(lock) {
            connected = false
            out = null
            try { socket?.close() } catch (_: Exception) { }
            socket = null
        }
    }

    private fun runLoop() {
        while (running) {
            try {
                val s = Socket()
                s.connect(InetSocketAddress(host, port), 4000)
                s.soTimeout = 1000
                synchronized(lock) {
                    socket = s
                    out = DataOutputStream(BufferedOutputStream(s.getOutputStream(), 8192))
                    connected = true
                }
                // 维持线程：周期性心跳保活（adb reverse 空闲隧道易断），断线由读返回 -1 或异常触发
                val input = s.getInputStream()
                val tmp = ByteArray(1)
                var lastBeat = System.currentTimeMillis()
                while (running && s.isConnected && !s.isClosed) {
                    try {
                        val n = input.read(tmp)
                        if (n == -1) break
                    } catch (_: java.net.SocketTimeoutException) {
                        // 读超时：下面发心跳
                    }
                    val now = System.currentTimeMillis()
                    if (now - lastBeat >= 1000) {
                        send(Protocol.buildNoop())
                        lastBeat = now
                    }
                }
            } catch (_: Exception) {
                // 连接失败，下一轮重试
            } finally {
                synchronized(lock) {
                    connected = false
                    out = null
                    try { socket?.close() } catch (_: Exception) { }
                    socket = null
                }
                if (running) Thread.sleep(500)
            }
        }
    }

    fun send(data: ByteArray) {
        synchronized(lock) {
            try {
                out?.let {
                    Log.d("SpenTablet", "SEND bytes=${data.size} connected=$connected")
                    it.write(data)
                    it.flush()
                } ?: Log.d("SpenTablet", "SEND skipped (out=null)")
            } catch (_: Exception) {
                connected = false
                out = null
                try { socket?.close() } catch (_: Exception) { }
                socket = null
            }
        }
    }
}
