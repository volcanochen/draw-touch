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
import android.widget.CheckBox
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.RadioButton
import android.widget.RadioGroup
import android.widget.TextView

/**
 * 手机端主界面：全屏画布 + 可折叠的设置面板。
 *
 * 设置面板提供：
 *  - PC 主机 IP / 端口
 *  - 比例模式：保持手机宽高比（居中留边） vs 拉伸铺满桌面区域
 *  - 是否转发悬停事件（关掉可显著降低数据量）
 *  - S Pen 压感增益
 * 所有设置持久化到 SharedPreferences；比例模式变更会通过 HELLO 帧即时通知 PC。
 */
class MainActivity : Activity() {

    private lateinit var net: NetClient
    private lateinit var status: TextView
    private lateinit var hostEdit: EditText
    private lateinit var portEdit: EditText
    private lateinit var panel: LinearLayout
    /** 设置面板里可折叠的正文部分（标题栏 status 始终可见，用它来展开/收起） */
    private lateinit var details: LinearLayout
    private lateinit var prefs: SharedPreferences
    private lateinit var dbg: TextView
    private lateinit var canvas: PenCanvasView
    private lateinit var gainText: TextView
    private lateinit var infoText: TextView

    private var host = "127.0.0.1"
    private var port = Protocol.DEFAULT_PORT
    /** 见 Protocol.ASPECT_* */
    private var aspectMode = Protocol.ASPECT_KEEP
    /** 见 Protocol.MODE_*：数位板（默认）/ 鼠标。决定手指的用途，两者互斥 */
    private var workMode = Protocol.MODE_TABLET

    @Volatile
    private var alive = true

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        prefs = getSharedPreferences("spentablet", MODE_PRIVATE)
        host = prefs.getString("host", "127.0.0.1") ?: "127.0.0.1"
        port = prefs.getInt("port", Protocol.DEFAULT_PORT)
        aspectMode = prefs.getInt("aspect", Protocol.ASPECT_KEEP)
        workMode = prefs.getInt("mode", Protocol.MODE_TABLET)

        // 结构：FrameLayout 里放两层
        //   ① content —— 上控制栏(固定高) + 中间书写区(weight=1) + 下状态栏(固定高)
        //   ② panel   —— 设置详情，**浮在书写区之上**（展开/收起不改变书写区尺寸）
        //
        // 为什么设置面板要浮起来：如果面板参与布局，展开时会把书写区挤扁，
        // 手机端宽高比一变，PC 上的桌面框形状就跟着突变（实测会被压成横条）。
        // 浮起来之后，书写区尺寸恒定 → 两端的框形状永远稳定一致。
        val root = FrameLayout(this)
        val content = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }

        canvas = PenCanvasView(this)
        canvas.pressureGain = prefs.getFloat("gain", 3.0f)
        canvas.sendHover = prefs.getBoolean("hover", true)
        canvas.workMode = workMode
        // 恢复上次保存的操作框（手机框与桌面框形状/位置保持一致）
        canvas.restoreFrame(
            prefs.getFloat("frameL", 0f),
            prefs.getFloat("frameT", 0f),
            prefs.getFloat("frameS", 1f)
        )
        // weight=1 让书写区吃掉除上下控制栏以外的全部剩余高度
        content.addView(canvas, LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f
        ))

        // ---------- 顶部设置面板 ----------
        // 注意：面板容器本身必须常驻可见，否则“点标题展开”这个开关自己也被藏起来了，
        // 用户就永远打不开设置。真正收起的是里面的 details。
        panel = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(16, 16, 16, 16)
            setBackgroundColor(Color.parseColor("#E6222222"))
        }
        details = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            visibility = View.GONE
        }

        status = TextView(this).apply {
            textSize = 14f
            setTextColor(Color.WHITE)
            text = "设置（点此展开/收起）"
            setPadding(0, 0, 0, 8)
        }

        val rowHost = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        hostEdit = EditText(this).apply {
            setText(host)
            hint = "PC IP（USB+adb 用 127.0.0.1）"
            setSingleLine(true)
            setTextColor(Color.WHITE)
            setHintTextColor(Color.parseColor("#AAAAAA"))
        }
        portEdit = EditText(this).apply {
            setText(port.toString())
            hint = "端口"
            setSingleLine(true)
            setTextColor(Color.WHITE)
            setHintTextColor(Color.parseColor("#AAAAAA"))
        }
        rowHost.addView(hostEdit, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 2f))
        rowHost.addView(portEdit, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))

        val connectBtn = Button(this).apply { text = "连接" }

        // ---- 比例模式 ----
        val aspectTitle = TextView(this).apply {
            textSize = 13f
            setTextColor(Color.WHITE)
            text = "比例映射（决定笔在 PC 上的覆盖范围）"
            setPadding(0, 8, 0, 2)
        }
        val radioKeep = RadioButton(this).apply {
            id = View.generateViewId()   // 必须显式给 id，否则 RadioGroup.check() 不生效
            text = "保持手机比例（居中留边，笔迹不变形）"
            setTextColor(Color.WHITE)
        }
        val radioStretch = RadioButton(this).apply {
            id = View.generateViewId()
            text = "拉伸铺满（铺满整屏，但圆形会变椭圆）"
            setTextColor(Color.WHITE)
        }
        val aspectGroup = RadioGroup(this).apply {
            addView(radioKeep)
            addView(radioStretch)
            check(if (aspectMode == Protocol.ASPECT_KEEP) radioKeep.id else radioStretch.id)
        }

        // ---- 工作模式（手指的用途，两种互斥） ----
        val modeTitle = TextView(this).apply {
            textSize = 13f
            setTextColor(Color.WHITE)
            text = "手指的作用（两种模式互斥）"
            setPadding(0, 10, 0, 2)
        }
        val radioTablet = RadioButton(this).apply {
            id = View.generateViewId()
            text = "数位板（默认）：显示范围框，S Pen 在框内作画，手指滑动=移动范围框"
            setTextColor(Color.WHITE)
        }
        val radioMouse = RadioButton(this).apply {
            id = View.generateViewId()
            text = "鼠标：取消范围框，手指滑动=移动光标，单击=左键，双击=右键"
            setTextColor(Color.WHITE)
        }
        val modeGroup = RadioGroup(this).apply {
            addView(radioTablet)
            addView(radioMouse)
            check(if (workMode == Protocol.MODE_MOUSE) radioMouse.id else radioTablet.id)
        }

        val resetFrameBtn = Button(this).apply { text = "复位操作框（恢复整个书写区）" }
        val pinchBtn = Button(this).apply { text = "模拟一次捏合（缩小 25%）" }
        val collapseBtn = Button(this).apply { text = "收起设置（回到书写）" }

        val hoverBox = CheckBox(this).apply {
            text = "转发悬停移动（关掉更省资源）"
            setTextColor(Color.WHITE)
            isChecked = canvas.sendHover
            setPadding(0, 4, 0, 4)
        }

        // ---- 压感增益 ----
        val gainRow = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        gainText = TextView(this).apply {
            textSize = 13f
            setTextColor(Color.WHITE)
            setPadding(0, 4, 8, 4)
            text = "压感增益 ${String.format("%.1f", canvas.pressureGain)}"
        }
        val minusBtn = Button(this).apply { text = "增益 −" }
        val plusBtn = Button(this).apply { text = "增益 +" }
        gainRow.addView(minusBtn)
        gainRow.addView(plusBtn)

        infoText = TextView(this).apply {
            textSize = 12f
            setTextColor(Color.parseColor("#FFBBBBBB"))
            setPadding(0, 4, 0, 4)
            text = ""
            setLines(2)   // 固定两行 → 上控制栏高度恒定 → 书写区尺寸不会被挤变形
        }

        details.addView(rowHost)
        details.addView(connectBtn)
        details.addView(aspectTitle)
        details.addView(aspectGroup)
        details.addView(modeTitle)
        details.addView(modeGroup)
        details.addView(resetFrameBtn)
        details.addView(pinchBtn)
        details.addView(collapseBtn)
        details.addView(hoverBox)
        details.addView(gainText)
        details.addView(gainRow)

        // ---- 上控制栏（固定高度）----
        val topBar = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(16, 10, 16, 6)
            setBackgroundColor(Color.parseColor("#E6222222"))
        }
        topBar.addView(status)
        topBar.addView(infoText)
        // index 0：上控制栏必须在画布**之前**，否则会被 weight=1 的画布挤到屏幕外
        content.addView(
            topBar, 0,
            LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT)
        )

        // ---- 下状态栏（固定高度）----
        dbg = TextView(this).apply {
            textSize = 12f
            setTextColor(Color.parseColor("#FFDDDD"))
            setBackgroundColor(Color.parseColor("#AA000000"))
            setPadding(12, 6, 12, 6)
            setLines(4)   // 固定四行 → 下状态栏高度恒定
            text = if (workMode == Protocol.MODE_MOUSE)
                "鼠标模式：手指滑动=移动光标，单击=左键，双击=右键\n等待事件…"
            else
                "数位板模式：S Pen 作画｜单指滑动=移动范围框，双指捏合=缩放\n等待事件…"
        }
        val bottomBar = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(0, 0, 0, 0)
        }
        bottomBar.addView(
            dbg,
            LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT)
        )
        content.addView(
            bottomBar,
            LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT)
        )

        // 内容层铺满；设置面板作为覆盖层浮在书写区上方
        root.addView(
            content,
            FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT)
        )

        // ---- 设置详情（浮层，默认收起）----
        panel.addView(details)
        root.addView(
            panel,
            FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply {
                gravity = Gravity.TOP
            }
        )

        canvas.listener = { type, x, y, p, tx, ty, b ->
            net.send(Protocol.buildPen(type, x, y, p, tx, ty, b))
        }
        canvas.onDebug = { s ->
            runOnUiThread { dbg.text = "$s\n${canvas.debugSummary()}" }
        }
        // 手指滑动 → 数位板模式下移动 PC 上的操作范围框
        canvas.onPan = { dx, dy -> net.send(Protocol.buildPan(dx, dy)) }
        // 手指 → 鼠标模式下的触摸板动作（移动 / 左键 / 右键）
        canvas.onMouse = { kind, dx, dy -> net.send(Protocol.buildMouse(kind, dx, dy)) }
        // 双指捏合 → 缩放 PC 上的操作范围框
        canvas.onZoom = { scale, fx, fy -> net.send(Protocol.buildZoom(scale, fx, fy)) }
        // 书写区尺寸变了（展开/收起设置面板会挤压中间区域）→ 重发握手帧让 PC 重算等比映射。
        // 注意：首次布局发生在 setContentView() 里，那时 net 还没初始化，必须判空。
        canvas.onSurfaceSizeChanged = { _, _ -> if (::net.isInitialized) net.sendHelloNow() }
        // 操作框状态变化 → 存到 prefs（重开 App 后能恢复，手机框与桌面框形状仍然一致）
        canvas.onFrameChanged = { l, t, s ->
            prefs.edit().putFloat("frameL", l).putFloat("frameT", t).putFloat("frameS", s).apply()
        }

        status.setOnClickListener {
            details.visibility = if (details.visibility == View.GONE) View.VISIBLE else View.GONE
        }
        // 注意：展开时面板是浮层，会盖住顶部状态栏，所以**不能**靠「点空白处收起」，
        // 必须在面板底部放一个明确的「收起设置」按钮。
        hostEdit.addTextChangedListener(object : TextWatcher {
            override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) {}
            override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) {}
            override fun afterTextChanged(s: Editable?) { host = s?.toString()?.ifBlank { "127.0.0.1" } ?: "127.0.0.1" }
        })
        connectBtn.setOnClickListener {
            port = portEdit.text?.toString()?.toIntOrNull() ?: Protocol.DEFAULT_PORT
            prefs.edit().putString("host", host).putInt("port", port).apply()
            restartNet()
            details.visibility = View.GONE
        }
        aspectGroup.setOnCheckedChangeListener { _, id ->
            aspectMode = if (id == radioKeep.id) Protocol.ASPECT_KEEP else Protocol.ASPECT_STRETCH
            prefs.edit().putInt("aspect", aspectMode).apply()
            net.sendHelloNow()          // 立即通知 PC，不用断线重连
        }
        modeGroup.setOnCheckedChangeListener { _, id ->
            val m = if (id == radioMouse.id) Protocol.MODE_MOUSE else Protocol.MODE_TABLET
            if (m == workMode) return@setOnCheckedChangeListener
            workMode = m
            canvas.workMode = m          // setter 会清掉上一种模式的手势状态
            prefs.edit().putInt("mode", m).apply()
            net.send(Protocol.buildMode(m))
            Log.i("SpenTablet", "切换工作模式 -> ${if (m == Protocol.MODE_MOUSE) "鼠标" else "数位板"}")
        }
        resetFrameBtn.setOnClickListener {
            canvas.resetFrame()
            net.send(Protocol.buildReset())
            Log.i("SpenTablet", "操作框已复位")
        }
        collapseBtn.setOnClickListener { details.visibility = View.GONE }
        pinchBtn.setOnClickListener {
            canvas.simulatePinch(0.75f)
            Log.i("SpenTablet", "模拟捏合 → ${canvas.frameSummary()}")
        }
        hoverBox.setOnCheckedChangeListener { _, checked ->
            canvas.sendHover = checked
            prefs.edit().putBoolean("hover", checked).apply()
        }
        minusBtn.setOnClickListener { setGain(canvas.pressureGain - 0.5f) }
        plusBtn.setOnClickListener { setGain(canvas.pressureGain + 0.5f) }

        setContentView(root)

        net = NetClient(host, port) { helloFrame() }
        net.start()

        Thread {
            while (alive) {
                val sum = canvas.debugSummary()
                val err = net.lastError
                val w = surfaceW(); val h = surfaceH()
                val aspect = if (h > 0) String.format("%.3f", w / h) else "?"
                val modeName = if (workMode == Protocol.MODE_MOUSE) "鼠标" else "数位板"
                runOnUiThread {
                    status.text = (if (net.connected) "已连接 $host:$port" else "未连接") +
                            "｜$modeName（点此设置）"
                    infoText.text = "书写区 ${w.toInt()}×${h.toInt()} 宽高比 $aspect｜模式 $modeName\n" +
                            "${canvas.frameSummary()}｜发送 ${net.sentFrames} 丢弃 ${net.droppedFrames}"
                    val tail = if (err != null) "错误: $err" else canvas.debugSummary().let { "" }
                    dbg.text = if (err != null) tail else "$sum"
                }
                try { Thread.sleep(500) } catch (_: InterruptedException) { break }
            }
        }.start()
    }

    private fun setGain(v: Float) {
        canvas.pressureGain = v.coerceIn(0.5f, 8f)
        prefs.edit().putFloat("gain", canvas.pressureGain).apply()
        gainText.text = "压感增益 ${String.format("%.1f", canvas.pressureGain)}"
    }

    private fun surfaceW(): Float {
        val w = canvas.width
        return if (w > 0) w.toFloat() else resources.displayMetrics.widthPixels.toFloat()
    }

    private fun surfaceH(): Float {
        val h = canvas.height
        return if (h > 0) h.toFloat() else resources.displayMetrics.heightPixels.toFloat()
    }

    /** 握手帧：书写区尺寸 + 比例偏好 + 工作模式 + 操作框状态（PC 据此还原同形状的桌面框） */
    private fun helloFrame(): ByteArray =
        Protocol.buildHello(
            surfaceW(), surfaceH(), aspectMode, workMode,
            canvas.frameLeft, canvas.frameTop, canvas.frameScale
        )

    private fun restartNet() {
        if (::net.isInitialized) net.stop()
        net = NetClient(host, port) { helloFrame() }
        net.start()
        Log.i("SpenTablet", "重连 $host:$port")
    }

    override fun onDestroy() {
        alive = false
        if (::net.isInitialized) net.stop()
        super.onDestroy()
    }
}
