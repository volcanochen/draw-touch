package com.volcano.spentablet

import android.app.Activity
import android.content.SharedPreferences
import android.graphics.Color
import android.os.Bundle
import android.text.Editable
import android.text.TextWatcher
import android.util.Log
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.view.WindowManager
import android.widget.Button
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.TextView

class MainActivity : Activity() {

    private lateinit var net: NetClient
    private lateinit var status: TextView
    private lateinit var hostEdit: EditText
    private lateinit var bar: LinearLayout
    private lateinit var prefs: SharedPreferences
    private lateinit var dbg: TextView
    private lateinit var canvas: PenCanvasView
    private lateinit var gainText: TextView

    private var host = "127.0.0.1"
    private val port = Protocol.DEFAULT_PORT
    @Volatile
    private var alive = true

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        prefs = getSharedPreferences("spentablet", MODE_PRIVATE)
        host = prefs.getString("host", "127.0.0.1") ?: "127.0.0.1"

        val root = FrameLayout(this)

        canvas = PenCanvasView(this)
        canvas.pressureGain = prefs.getFloat("gain", 3.0f)
        root.addView(canvas, ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))

        bar = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(16, 16, 16, 16)
            setBackgroundColor(Color.parseColor("#CC222222"))
        }
        status = TextView(this).apply {
            textSize = 14f
            setTextColor(Color.WHITE)
            text = "未连接（点我设置 PC IP）"
            setPadding(0, 0, 0, 8)
        }
        hostEdit = EditText(this).apply {
            setText(host)
            hint = "PC IP（USB+adb 用 127.0.0.1）"
            setTextColor(Color.WHITE)
            setHintTextColor(Color.parseColor("#AAAAAA"))
        }
        val connectBtn = Button(this).apply { text = "连接" }

        // 压感增益调节
        val gainRow = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        gainText = TextView(this).apply {
            textSize = 13f
            setTextColor(Color.WHITE)
            setPadding(0, 0, 0, 8)
            text = "压感增益 ${String.format("%.1f", canvas.pressureGain)}"
        }
        val minusBtn = Button(this).apply { text = "增益 -" }
        val plusBtn = Button(this).apply { text = "增益 +" }
        gainRow.addView(minusBtn)
        gainRow.addView(plusBtn)

        bar.addView(status)
        bar.addView(hostEdit)
        bar.addView(connectBtn)
        bar.addView(gainText)
        bar.addView(gainRow)
        bar.visibility = View.GONE
        root.addView(
            bar,
            FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply {
                gravity = Gravity.TOP
            }
        )

        dbg = TextView(this).apply {
            textSize = 12f
            setTextColor(Color.parseColor("#FFDDDD"))
            setBackgroundColor(Color.parseColor("#AA000000"))
            setPadding(12, 8, 12, 8)
            text = "等待事件…"
        }
        root.addView(
            dbg,
            FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply {
                gravity = Gravity.BOTTOM
            }
        )

        canvas.listener = { type, x, y, p, tx, ty, b ->
            net.send(Protocol.buildPen(type, x, y, p, tx, ty, b))
        }
        canvas.onDebug = { s ->
            runOnUiThread { dbg.text = "$s\n${canvas.debugSummary()}" }
        }

        status.setOnClickListener {
            bar.visibility = if (bar.visibility == View.GONE) View.VISIBLE else View.GONE
        }
        hostEdit.addTextChangedListener(object : TextWatcher {
            override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) {}
            override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) {}
            override fun afterTextChanged(s: Editable?) { host = s?.toString()?.ifBlank { "127.0.0.1" } ?: "127.0.0.1" }
        })
        connectBtn.setOnClickListener {
            prefs.edit().putString("host", host).apply()
            restartNet()
            bar.visibility = View.GONE
        }
        minusBtn.setOnClickListener {
            canvas.pressureGain = (canvas.pressureGain - 0.5f).coerceIn(0.5f, 8f)
            prefs.edit().putFloat("gain", canvas.pressureGain).apply()
            gainText.text = "压感增益 ${String.format("%.1f", canvas.pressureGain)}"
        }
        plusBtn.setOnClickListener {
            canvas.pressureGain = (canvas.pressureGain + 0.5f).coerceIn(0.5f, 8f)
            prefs.edit().putFloat("gain", canvas.pressureGain).apply()
            gainText.text = "压感增益 ${String.format("%.1f", canvas.pressureGain)}"
        }

        setContentView(root)

        net = NetClient(host, port)
        net.start()

        Thread {
            while (alive) {
                val sum = canvas.debugSummary()
                val err = net.lastError
                runOnUiThread {
                    val head = if (net.connected) "已连接 $host:$port" else "未连接（点我设置 PC IP）"
                    val tail = " 发送${net.sentFrames} 丢弃${net.droppedFrames}" +
                            (if (err != null) "\n错误: $err" else "")
                    status.text = head
                    dbg.text = "$tail\n$sum"
                }
                try { Thread.sleep(500) } catch (_: InterruptedException) { break }
            }
        }.start()
    }

    private fun restartNet() {
        if (::net.isInitialized) net.stop()
        net = NetClient(host, port)
        net.start()
    }

    override fun onDestroy() {
        alive = false
        if (::net.isInitialized) net.stop()
        super.onDestroy()
    }
}
