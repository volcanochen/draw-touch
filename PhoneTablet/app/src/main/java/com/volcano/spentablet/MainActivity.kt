package com.volcano.spentablet

import android.app.Activity
import android.content.SharedPreferences
import android.graphics.Color
import android.os.Bundle
import android.text.Editable
import android.text.TextWatcher
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.view.WindowManager
import android.widget.Button
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.util.Log
import android.widget.TextView

class MainActivity : Activity() {

    private lateinit var net: NetClient
    private lateinit var status: TextView
    private lateinit var hostEdit: EditText
    private lateinit var bar: LinearLayout
    private lateinit var prefs: SharedPreferences

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

        val canvas = PenCanvasView(this)
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
        bar.addView(status)
        bar.addView(hostEdit)
        bar.addView(connectBtn)
        bar.visibility = View.GONE
        root.addView(
            bar,
            FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply {
                gravity = Gravity.TOP
            }
        )

        canvas.listener = { type, x, y, p, tx, ty, b ->
            Log.d("SpenTablet", "listener type=$type x=$x y=$y p=$p -> send")
            net.send(Protocol.buildPen(type, x, y, p, tx, ty, b))
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
            net.stop()
            net = NetClient(host, port)
            net.start()
            bar.visibility = View.GONE
        }

        setContentView(root)

        net = NetClient(host, port)
        net.start()

        Thread {
            while (alive) {
                runOnUiThread {
                    status.text = if (net.connected) "已连接 $host:$port" else "未连接（点我设置 PC IP）"
                }
                try { Thread.sleep(500) } catch (_: InterruptedException) { break }
            }
        }.start()
    }

    override fun onDestroy() {
        alive = false
        net.stop()
        super.onDestroy()
    }
}
