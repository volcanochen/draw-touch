package com.volcano.drawtouch

import android.content.Context
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * 后台调试日志：原来显示在主界面上下两行（书写区信息 + 底部调试栏）的内容全部写到这里。
 * - 定量：内存缓冲，每 2 秒（或缓冲 >8KB）批量落盘一次，避免高频 IO；
 * - 循环：单文件超过 256KB 时轮转为 spen_debug.log.old（覆盖旧的），总占用恒定 ≤ ~512KB。
 * 文件位于 App 私有外部目录：Android/data/com.volcano.drawtouch/files/spen_debug.log
 */
object DebugLog {
    private const val MAX_BYTES = 256 * 1024
    private const val FLUSH_INTERVAL_MS = 2000L
    private const val FORCE_FLUSH_BYTES = 8 * 1024

    private var file: File? = null
    private val buf = StringBuilder()
    private var lastFlush = 0L
    private val throttleMap = HashMap<String, Long>()

    fun init(ctx: Context) {
        val dir = ctx.getExternalFilesDir(null) ?: ctx.filesDir
        file = File(dir, "spen_debug.log")
    }

    /** 追加一行（带时间戳，先进内存缓冲） */
    @Synchronized
    fun write(line: String) {
        val ts = SimpleDateFormat("MM-dd HH:mm:ss.SSS", Locale.US).format(Date())
        buf.append(ts).append(' ').append(line).append('\n')
        if (buf.length > FORCE_FLUSH_BYTES) flush()
        val now = System.currentTimeMillis()
        if (now - lastFlush >= FLUSH_INTERVAL_MS) flush()
    }

    /** 按 key 节流写入：同一 key 在 minIntervalMs 内只写第一条（高频事件用） */
    @Synchronized
    fun writeThrottled(key: String, line: String, minIntervalMs: Long = 300L) {
        val now = System.currentTimeMillis()
        val last = throttleMap[key] ?: 0L
        if (now - last >= minIntervalMs) {
            throttleMap[key] = now
            write(line)
        }
    }

    @Synchronized
    fun flush() {
        val f = file ?: run { buf.setLength(0); return }
        lastFlush = System.currentTimeMillis()
        try {
            if (f.exists() && f.length() > MAX_BYTES) {
                val old = File(f.parentFile, "spen_debug.log.old")
                old.delete()
                f.renameTo(old)
            }
            f.appendText(buf.toString())
        } catch (_: Throwable) {
        } finally {
            buf.setLength(0)
        }
    }
}
