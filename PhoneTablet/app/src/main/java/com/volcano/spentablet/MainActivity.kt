package com.volcano.spentablet

import android.app.Activity
import android.content.SharedPreferences
import android.graphics.Color
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.RectF
import android.os.Build
import android.os.Bundle
import android.text.Editable
import android.text.TextWatcher
import android.util.Log
import android.graphics.BitmapFactory
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.view.ViewGroup
import android.view.WindowManager
import android.widget.Button
import android.widget.CheckBox
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.ImageButton
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.RadioButton
import android.widget.RadioGroup
import android.widget.ScrollView
import android.widget.TextView

/**
 * 手机端主界面：全屏画布 + 主界面顶部一排图标按钮（方向切换 / 连接状态 / 设置）。
 *
 * 连接状态：左上角一个绿/红小圆点（连上绿、未连红），点「连接状态」按钮弹出网络拓扑图，
 * 展示 PC IP、客户端版本号、网络连接方式、手机 IP 与手机名，并列出局域网里其它桌面，点击可切换。
 *
 * 设置：独立全屏页面，点齿轮打开、按返回或点「完成」关闭（不再有「收起」折叠逻辑）。
 */
class MainActivity : Activity() {

    private lateinit var net: NetClient
    private lateinit var status: TextView
    private lateinit var hostEdit: EditText
    private lateinit var portEdit: EditText
    private lateinit var topBar: LinearLayout

    /** 主界面顶部左侧的连接状态点（绿=已连 红=未连），点击打开网络拓扑 */
    private lateinit var statusDot: View
    /** 方向切换图标按钮（主界面独立按钮） */
    private lateinit var rotateBtn: ImageButton
    /** 操作方式切换按钮：图标即当前模式（鼠标=触摸板 / 笔=数位板），点击切换 */
    private lateinit var modeBtn: ImageButton
    /** 使用指南（问号）按钮 */
    private lateinit var helpBtn: ImageButton
    /** 设置（齿轮）按钮 */
    private lateinit var settingsBtn: ImageButton

    // ---- 触摸板模式 UI（滚轮条 + 左/中/右键）----
    private lateinit var trackpadOverlay: android.widget.FrameLayout
    // ---- 数位板模式工具条（备注/清除/截图）----
    private lateinit var tabletToolbar: android.widget.LinearLayout
    /** 数位板模式：左侧浮出的笔迹颜色色板 */
    private lateinit var notePalette: LinearLayout
    /** 当前笔迹颜色索引（对应 [noteColors]） */
    private var noteColorIdx = 0
    /** 截图预览（全屏覆盖） */
    private var shotView: ImageView? = null
    /** 桌面备注开启中 */
    private var noteOn = false

    /** 独立设置页（全屏覆盖层，默认隐藏；返回键 / 完成按钮关闭） */
    private lateinit var settingsPage: LinearLayout
    /** 设置页里的可滚动正文（所有设置控件都在这里） */
    private lateinit var details: LinearLayout

    /** 当前是否横屏（决定控制栏的紧凑度） */
    private var landscape = false

    /**
     * 顶部任务栏**固定**高度(dp)：竖屏 56 / 横屏 44。
     * 与滚轮条的 topMargin=56dp 对齐；绝不能随状态文字内容变化（用户明确要求过）。
     */
    private fun topBarHeightDp() = if (landscape) 44 else 56

    /** 屏幕方向模式：0=跟随陀螺仪(四向) 1=横屏(双向陀螺仪) 2=竖屏(双向陀螺仪) */
    private var orientMode = 0
    private lateinit var prefs: SharedPreferences
    private lateinit var canvas: PenCanvasView
    private lateinit var gainText: TextView
    /** 设置面板里的 IP 提示行（mDNS 发现结果也显示在这里） */
    private lateinit var ipText: TextView
    /** 「发现 PC（mDNS 自动）」按钮 */
    private lateinit var discoverBtn: Button

    /** mDNS 自动发现：已经用发现结果连过 PC，避免反复切换目标 */
    private var mdnsConnected = false

    private var host = "127.0.0.1"
    private var port = Protocol.DEFAULT_PORT
    /** 见 Protocol.ASPECT_* */
    private var aspectMode = Protocol.ASPECT_KEEP
    /** 见 Protocol.MODE_*：数位板（默认）/ 鼠标。决定手指的用途，两者互斥 */
    private var workMode = Protocol.MODE_MOUSE

    /** PC 主机下发的 INFO：版本号 / 计算机名 / 候选 IP（拓扑图展示用） */
    private var pcVersion = "—"
    private var pcName = "—"
    private var pcIps = ""

    @Volatile
    private var alive = true

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        prefs = getSharedPreferences("spentablet", MODE_PRIVATE)
        DebugLog.init(this)
        host = prefs.getString("host", "127.0.0.1") ?: "127.0.0.1"
        port = prefs.getInt("port", Protocol.DEFAULT_PORT)
        aspectMode = prefs.getInt("aspect", Protocol.ASPECT_KEEP)
        workMode = prefs.getInt("mode", Protocol.MODE_MOUSE) // 默认鼠标模式
        orientMode = prefs.getInt("orient", 0)

        // 结构：FrameLayout
        //   content —— 上控制栏(fixed) + 书写区(weight=1) + 下状态栏(fixed)
        //   settingsPage —— 全屏设置页，浮在 content 之上，默认 GONE
        val root = FrameLayout(this)
        val content = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }

        canvas = PenCanvasView(this)
        canvas.pressureGain = prefs.getFloat("gain", 3.0f)
        canvas.sendHover = prefs.getBoolean("hover", true)
        canvas.workMode = workMode
        canvas.restoreFrame(
            prefs.getFloat("frameL", 0f),
            prefs.getFloat("frameT", 0f),
            prefs.getFloat("frameS", 1f)
        )
        content.addView(canvas, LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f
        ))

        // ---------- 顶部控制栏 ----------
        topBar = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(16, 10, 16, 6)
            setBackgroundColor(Color.parseColor("#E6222222"))
        }
        // 第一行：状态点 + 状态文字 + 右侧一排图标按钮
        val topRow = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        statusDot = View(this).apply {
            val s = dp(14)
            layoutParams = LinearLayout.LayoutParams(s, s).apply { setMargins(0, 0, 10, 0) }
            setBackgroundColor(Color.parseColor("#FF4444")) // 默认红：未连接
        }
        status = TextView(this).apply {
            textSize = 14f
            setTextColor(Color.WHITE)
            text = "未连接（点此设置）"
            // 单行 + 省略号：错误信息再长也不许把顶栏撑高（书写区尺寸一变，PC 端映射就跟着重算）
            setSingleLine(true)
            ellipsize = android.text.TextUtils.TruncateAt.END
            layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
        }
        // 操作方式切换按钮：图标即当前模式（鼠标 / 笔），点击切换（状态与按钮融合）
        modeBtn = ImageButton(this).apply {
            background = null
            setPadding(8, 8, 8, 8)
            layoutParams = LinearLayout.LayoutParams(dp(40), dp(40))
            contentDescription = "操作方式"
            setOnClickListener { cycleWorkMode() }
        }
        rotateBtn = ImageButton(this).apply {
            setImageResource(R.drawable.ic_rotate)
            background = null
            setPadding(8, 8, 8, 8)
            layoutParams = LinearLayout.LayoutParams(dp(40), dp(40))
            contentDescription = "屏幕方向"
            setOnClickListener { cycleOrientation() }
        }
        helpBtn = ImageButton(this).apply {
            setImageResource(R.drawable.ic_help)
            background = null
            setPadding(8, 8, 8, 8)
            layoutParams = LinearLayout.LayoutParams(dp(40), dp(40))
            contentDescription = "使用指南"
            setOnClickListener { showGuide() }
        }
        settingsBtn = ImageButton(this).apply {
            setImageResource(R.drawable.ic_settings)
            background = null
            setPadding(8, 8, 8, 8)
            layoutParams = LinearLayout.LayoutParams(dp(40), dp(40))
            contentDescription = "设置"
            setOnClickListener { openSettings() }
        }
        topRow.addView(statusDot)
        topRow.addView(status)
        topRow.addView(modeBtn)
        topRow.addView(rotateBtn)
        topRow.addView(helpBtn)
        topRow.addView(settingsBtn)
        // 固定满宽：状态文字(weight=1)占中间，四个图标恒定右对齐
        topRow.layoutParams = LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT
        )

        // 顶部栏只有一行：状态点 + 图标按钮。原来的书写区信息行 / 底部调试栏已移除，
        // 这些调试信息改为后台定量循环写入文件（见 DebugLog）。
        topBar.addView(topRow)
        // 高度**写死**（竖屏 56dp / 横屏 44dp）：以前 WRAP_CONTENT，状态文字一换行/变空，
        // 顶栏就跟着变高变矮，书写区尺寸抖动 → PC 端宽高比重算 → 范围框跳一下。
        content.addView(
            topBar, 0,
            LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(topBarHeightDp()))
        )

        // ---------- 独立设置页（默认隐藏）----------
        settingsPage = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(Color.parseColor("#F2222222"))
            visibility = View.GONE
        }
        val setHeader = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            setPadding(16, 14, 16, 10)
            setBackgroundColor(Color.parseColor("#CC111111"))
        }
        val setTitle = TextView(this).apply {
            text = "设置"
            textSize = 18f
            setTextColor(Color.WHITE)
            layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
        }
        val doneBtn = Button(this).apply { text = "完成" }
        setHeader.addView(setTitle)
        setHeader.addView(doneBtn)
        settingsPage.addView(setHeader)

        details = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(16, 8, 16, 16)
        }
        buildSettingsControls()
        val setScroll = ScrollView(this).apply {
            isFillViewport = true
            addView(details)
        }
        settingsPage.addView(setScroll)

        root.addView(content, FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
        root.addView(settingsPage, FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))

        // 画布事件回调（与之前一致）
        canvas.listener = { type, x, y, p, tx, ty, b -> net.send(Protocol.buildPen(type, x, y, p, tx, ty, b)) }
        // 原来显示在底部调试栏的笔/手指事件 → 后台日志（按事件节流）
        canvas.onDebug = { s -> DebugLog.writeThrottled("evt", s.replace("\n", " | ")) }
        canvas.onPan = { dx, dy -> net.send(Protocol.buildPan(dx, dy)) }
        canvas.onMouse = { kind, dx, dy -> net.send(Protocol.buildMouse(kind, dx, dy)) }
        canvas.onZoom = { scale, fx, fy -> net.send(Protocol.buildZoom(scale, fx, fy)) }
        canvas.onSurfaceSizeChanged = { _, _ -> if (::net.isInitialized) net.sendHelloNow() }
        canvas.onFrameChanged = { l, t, s ->
            prefs.edit().putFloat("frameL", l).putFloat("frameT", t).putFloat("frameS", s).apply()
        }

        status.setOnClickListener { openSettings() }             // 文字（未连接原因）→ 设置
        statusDot.setOnClickListener { openTopology() }           // 绿/红点 → 网络拓扑
        doneBtn.setOnClickListener { closeSettings() }

        // ---------- 触摸板 UI（滚轮条 + 底部三键，仅触摸板模式可见）----------
        buildTrackpadUi(root)
        // ---------- 数位板工具条（备注 / 清除 / 截图）----------
        buildTabletToolbar(root)
        // 应用触摸板配置（速度/滚动比例/手势/颜色）
        applyTrackpadPrefs()
        // 四指点击等手势 → 切换模式
        canvas.onSwitchMode = { runOnUiThread { cycleWorkMode() } }

        setContentView(root)
        applyOrientationMode()
        updateModeBtn() // 模式按钮图标 = 当前模式
        updateModeUI()  // 触摸板 UI / 数位板工具条显隐

        // 第二个参数是「连接建立后要发的 Hello 帧」；顺带在这里同步一次笔迹颜色
        net = NetClient(host, port) {
            if (::net.isInitialized) {
                net.send(Protocol.buildNote(Protocol.NOTE_COLOR, noteColorIdx.toFloat()))
            }
            helloFrame()
        }
        // 接收 PC 下发的 INFO（版本号 / 计算机名 / 候选 IP）
        net.onInfo = { text -> parsePcInfo(text) }
        // 接收 PC 下发的截图（PNG）
        net.onShot = { png -> runOnUiThread { showShot(png) } }
        net.start()

        // 开机静默 mDNS 发现：找到唯一一台 PC 且当前没连上 → 直接连上，用户零操作
        startMdnsDiscovery(manual = false)

        Thread {
            var tick = 0
            while (alive) {
                val sum = canvas.debugSummary()
                val err = net.lastError
                val w = surfaceW(); val h = surfaceH()
                val aspect = if (h > 0) String.format("%.3f", w / h) else "?"
                val modeName = if (workMode == Protocol.MODE_MOUSE) "触摸板" else "数位板"
                runOnUiThread {
                    if (net.connected) {
                        statusDot.setBackgroundColor(Color.parseColor("#33DD55")) // 绿：已连
                        status.text = "" // 绿点即已连接，无需文字
                        status.setTextColor(Color.WHITE)
                    } else {
                        statusDot.setBackgroundColor(Color.parseColor("#FF4444")) // 红：未连
                        val friendly = net.friendlyError(err)
                        status.text = if (friendly.isEmpty()) "未连接｜$modeName（点此设置）"
                                      else "未连接：${friendly.substringBefore('\n')}"
                        status.setTextColor(Color.parseColor("#FFFF6B6B"))
                    }
                }
                // 原来 infoText / dbg 两行字的内容 → 每 2 秒定量写入后台日志文件
                if (tick++ % 4 == 0) {
                    val state = if (net.connected) "已连接" else "未连接"
                    val errPart = if (err != null) "｜错误详情：$err" else ""
                    DebugLog.write(
                        "[stat] $state $host:$port｜书写区 ${w.toInt()}x${h.toInt()} 宽高比 $aspect｜模式 $modeName｜" +
                                "${canvas.frameSummary()}｜发送 ${net.sentFrames} 丢弃 ${net.droppedFrames}｜" +
                                sum.replace("\n", " | ") + errPart
                    )
                }
                try { Thread.sleep(500) } catch (_: InterruptedException) { break }
            }
        }.start()
    }

    /** 构建设置页里的所有控件（IP / 比例 / 模式 / 增益 / 复位 等，不再含方向切换与捏合按钮） */
    private fun buildSettingsControls() {
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
        val findBtn = Button(this).apply { text = "扫描网段找 PC（备用）" }
        discoverBtn = Button(this).apply { text = "发现 PC（mDNS 自动，推荐）" }
        ipText = TextView(this).apply {
            textSize = 12f
            setTextColor(Color.parseColor("#FFBBBBBB"))
            setPadding(0, 4, 0, 4)
        }

        val aspectTitle = TextView(this).apply {
            textSize = 13f; setTextColor(Color.WHITE)
            text = "比例映射（决定笔在 PC 上的覆盖范围）"
            setPadding(0, 8, 0, 2)
        }
        val radioKeep = RadioButton(this).apply {
            id = View.generateViewId()
            text = "保持手机比例（居中留边，笔迹不变形）"
            setTextColor(Color.WHITE)
        }
        val radioStretch = RadioButton(this).apply {
            id = View.generateViewId()
            text = "拉伸铺满（铺满整屏，但圆形会变椭圆）"
            setTextColor(Color.WHITE)
        }
        val aspectGroup = RadioGroup(this).apply {
            addView(radioKeep); addView(radioStretch)
            check(if (aspectMode == Protocol.ASPECT_KEEP) radioKeep.id else radioStretch.id)
        }

        val modeTitle = TextView(this).apply {
            textSize = 13f; setTextColor(Color.WHITE)
            text = "手指的作用（两种模式互斥）"
            setPadding(0, 10, 0, 2)
        }
        val radioTablet = RadioButton(this).apply {
            id = View.generateViewId()
            text = "数位板：显示范围框，S Pen 在框内作画，手指滑动=移动范围框"
            setTextColor(Color.WHITE)
        }
        val radioMouse = RadioButton(this).apply {
            id = View.generateViewId()
            text = "触摸板（默认）：取消范围框，手指滑动=移动光标，右侧滚轮条，底部三键"
            setTextColor(Color.WHITE)
        }
        val modeGroup = RadioGroup(this).apply {
            addView(radioTablet); addView(radioMouse)
            check(if (workMode == Protocol.MODE_MOUSE) radioMouse.id else radioTablet.id)
        }

        val resetFrameBtn = Button(this).apply { text = "复位操作框（恢复整个书写区）" }

        val hoverBox = CheckBox(this).apply {
            text = "转发悬停移动（关掉更省资源）"
            setTextColor(Color.WHITE)
            isChecked = canvas.sendHover
            setPadding(0, 4, 0, 4)
        }

        val gainRow = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        gainText = TextView(this).apply {
            textSize = 13f; setTextColor(Color.WHITE)
            setPadding(0, 4, 8, 4)
            text = "压感增益 ${String.format("%.1f", canvas.pressureGain)}"
        }
        val minusBtn = Button(this).apply { text = "增益 −" }
        val plusBtn = Button(this).apply { text = "增益 +" }
        gainRow.addView(minusBtn)
        gainRow.addView(plusBtn)

        details.addView(rowHost)
        details.addView(connectBtn)
        details.addView(discoverBtn)
        details.addView(findBtn)
        details.addView(ipText)
        details.addView(aspectTitle)
        details.addView(aspectGroup)
        details.addView(modeTitle)
        details.addView(modeGroup)
        details.addView(resetFrameBtn)
        details.addView(hoverBox)
        details.addView(gainText)
        details.addView(gainRow)

        // ---------- 触摸板配置（速度 / 滚动比例 / 手势 / 颜色）----------
        fun tpTitle(t: String) = TextView(this).apply {
            textSize = 13f; setTextColor(Color.WHITE)
            text = t; setPadding(0, dp(14), 0, 2)
        }
        fun minusPlus(label: String, get: () -> Float, set: (Float) -> Unit, step: Float, min: Float, max: Float): LinearLayout {
            val tv = TextView(this).apply {
                textSize = 14f; setTextColor(Color.WHITE); setPadding(0, 4, 8, 4)
                text = String.format("%s  %.1f", label, get())
            }
            val row = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
            fun mk(delta: Float): Button = Button(this).apply {
                text = if (delta < 0) "−" else "+"
                setOnClickListener {
                    set((get() + delta).coerceIn(min, max))
                    tv.text = String.format("%s  %.1f", label, get())
                }
            }
            row.addView(mk(-step))
            row.addView(tv, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
            row.addView(mk(step))
            return row
        }
        details.addView(tpTitle("光标速度"))
        details.addView(minusPlus("速度", { canvas.cursorSpeed }, {
            canvas.cursorSpeed = it; prefs.edit().putFloat("tpSpeed", it).apply()
        }, 0.1f, 0.2f, 3.0f))
        details.addView(tpTitle("滚动比例（0.2 ~ 30）"))
        details.addView(minusPlus("滚动", { canvas.scrollScale }, {
            canvas.scrollScale = it; prefs.edit().putFloat("tpScroll", it).apply()
        }, 0.5f, 0.2f, 30.0f))

        details.addView(tpTitle("触摸板手势（点击切换动作）"))
        fun gestureRow(name: String, key: String, def: Int, allowed: IntArray): Button =
            Button(this).apply {
                text = "$name：${canvas.actionName(prefs.getInt(key, def))}"
                setOnClickListener {
                    val next = cycleGesture(key, prefs.getInt(key, def), allowed)
                    text = "$name：${canvas.actionName(next)}"
                }
            }
        // 可配置动作：点击类（含音量/任务切换这类"按一下"的动作）
        val tapActions = intArrayOf(0, 3, 4, 5, 6, 7, 9, 10, 11, 12, 13, 14)
        // 滑动类（左右滑）：8=后退/前进（按方向二选一），其余动作一次滑动触发一次
        val swipeActions = intArrayOf(0, 8, 6, 7, 3, 4, 5, 9, 10, 11, 12, 13, 14)
        details.addView(gestureRow("双指滑动", "g2Swipe", 1, intArrayOf(0, 1, 2)))
        details.addView(gestureRow("双指捏合", "g2Pinch", 2, intArrayOf(0, 1, 2, 10, 11, 12, 13, 14)))
        details.addView(gestureRow("双指点击", "g2Tap", 4, tapActions))
        details.addView(gestureRow("三指点击", "g3Tap", 5, tapActions))
        details.addView(gestureRow("三指左右滑", "g3Swipe", 8, swipeActions))
        details.addView(gestureRow("四指左右滑", "g4Swipe", 0, swipeActions))
        details.addView(gestureRow("四指点击", "g4Tap", 9, intArrayOf(0, 9)))

        details.addView(tpTitle("右侧滚轮条"))
        // 无极滚动：抬手后按动量继续滚一阵再停（双击滚轮条切换）
        details.addView(Button(this).apply {
            fun refresh() {
                text = if (wheelInertia) "滚动模式：无极滚动" else "滚动模式：普通滚动"
            }
            refresh()
            setOnClickListener {
                wheelInertia = !wheelInertia
                prefs.edit().putBoolean("wheelInertia", wheelInertia).apply()
                refresh()
            }
        })
        details.addView(minusPlus("动量", { wheelMomentum }, {
            wheelMomentum = it; prefs.edit().putFloat("wheelMomentum", it).apply()
        }, 0.1f, 0.2f, 3.0f))

        details.addView(tpTitle("触摸板颜色"))
        val colorRow = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        val colorChoices = linkedMapOf(
            "暗灰" to 0xFF20242B.toInt(), "纯黑" to 0xFF000000.toInt(),
            "深蓝" to 0xFF17223B.toInt(), "墨绿" to 0xFF16302B.toInt(), "酒红" to 0xFF3B1723.toInt())
        for ((name, col) in colorChoices) {
            colorRow.addView(Button(this).apply {
                text = name
                setBackgroundColor(col)
                setTextColor(Color.WHITE)
                layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
                    .apply { setMargins(dp(2), 0, dp(2), 0) }
                setOnClickListener {
                    prefs.edit().putInt("tpColor", col).apply()
                    canvas.trackpadColor = col
                    canvas.invalidate()
                }
            })
        }
        details.addView(colorRow)

        // ---------- 版本信息与更新 ----------
        verText = TextView(this).apply {
            textSize = 12f
            setTextColor(Color.parseColor("#FF999999"))
            setPadding(0, dp(14), 0, 2)
            text = versionLine()
        }
        val updateBtn = Button(this).apply { text = "检查更新（打开浏览器）" }
        val homeBtn = Button(this).apply { text = "项目主页 / PC 端下载" }
        updateBtn.setOnClickListener {
            try {
                startActivity(android.content.Intent(
                    android.content.Intent.ACTION_VIEW,
                    android.net.Uri.parse("https://github.com/volcanochen/drawing-phone/releases/latest")))
            } catch (e: Throwable) { Log.w("SpenTablet", "打开浏览器失败: $e") }
        }
        homeBtn.setOnClickListener {
            try {
                startActivity(android.content.Intent(
                    android.content.Intent.ACTION_VIEW,
                    android.net.Uri.parse("https://github.com/volcanochen/drawing-phone")))
            } catch (e: Throwable) { Log.w("SpenTablet", "打开浏览器失败: $e") }
        }
        details.addView(verText)
        details.addView(updateBtn)
        details.addView(homeBtn)

        hostEdit.addTextChangedListener(object : TextWatcher {
            override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) {}
            override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) {}
            override fun afterTextChanged(s: Editable?) {
                host = s?.toString()?.ifBlank { "127.0.0.1" } ?: "127.0.0.1"
            }
        })
        findBtn.setOnClickListener {
            findBtn.isEnabled = false
            findBtn.text = "正在扫描本机 USB 网卡所在网段…"
            ipText.text = "本机 IP：${NetworkScan.describeLocalIps()}\n扫描中，请稍候…"
            NetworkScan.findHost(port) { found ->
                runOnUiThread {
                    findBtn.isEnabled = true
                    findBtn.text = "扫描网段找 PC（备用）"
                    if (found != null) {
                        host = found
                        hostEdit.setText(found)
                        prefs.edit().putString("host", found).apply()
                        ipText.text = "找到 PC：$found（已填入）\n正在连接…"
                        restartNet()
                    } else {
                        ipText.text = "本机 IP：${NetworkScan.describeLocalIps()}\n" +
                                "没扫到开着 $port 端口的电脑。\n" +
                                "请确认：① PC 上已启动 SpenTabletHost；② 已开 USB 网络共享；" +
                                "③ PC 防火墙已放行该端口（PC 端配置窗口点「允许防火墙」）。"
                    }
                }
            }
        }
        discoverBtn.setOnClickListener {
            discoverBtn.isEnabled = false
            discoverBtn.text = "正在搜索局域网中的 PC…"
            ipText.text = "mDNS 搜索中…（PC 上需已启动 SpenTabletHost）"
            startMdnsDiscovery(manual = true)
        }
        connectBtn.setOnClickListener {
            port = portEdit.text?.toString()?.toIntOrNull() ?: Protocol.DEFAULT_PORT
            prefs.edit().putString("host", host).putInt("port", port).apply()
            restartNet()
        }
        aspectGroup.setOnCheckedChangeListener { _, id ->
            aspectMode = if (id == radioKeep.id) Protocol.ASPECT_KEEP else Protocol.ASPECT_STRETCH
            prefs.edit().putInt("aspect", aspectMode).apply()
            net.sendHelloNow()
        }
        modeGroup.setOnCheckedChangeListener { _, id ->
            val m = if (id == radioMouse.id) Protocol.MODE_MOUSE else Protocol.MODE_TABLET
            setWorkMode(m) // 与主界面模式按钮共用：同步图标 + 通知 PC
        }
        resetFrameBtn.setOnClickListener {
            canvas.resetFrame()
            net.send(Protocol.buildReset())
            Log.i("SpenTablet", "操作框已复位")
        }
        hoverBox.setOnCheckedChangeListener { _, checked ->
            canvas.sendHover = checked
            prefs.edit().putBoolean("hover", checked).apply()
        }
        minusBtn.setOnClickListener { setGain(canvas.pressureGain - 0.5f) }
        plusBtn.setOnClickListener { setGain(canvas.pressureGain + 0.5f) }
    }

    // ---------- 设置页开关 ----------
    /** 设置页底部的版本信息行（打开时刷新，PC 版本来自 INFO 帧） */
    private lateinit var verText: TextView

    private fun versionLine(): String {
        val verName = try {
            packageManager.getPackageInfo(packageName, 0).versionName ?: "?"
        } catch (_: Throwable) { "?" }
        return "SpenTablet 手机端 v$verName｜PC 端 v$pcVersion（$pcName）"
    }

    private fun openSettings() {
        if (::verText.isInitialized) verText.text = versionLine()
        // 重建一遍设置项：默认值迁移 / 手势切换后的最新值要反映到界面上
        details.removeAllViews()
        buildSettingsControls()
        settingsPage.visibility = View.VISIBLE
        stopInertia()
        updateModeUI()   // 触摸板浮动按钮（滚轮条/三键）在设置页里要藏起来
    }

    private fun closeSettings() {
        settingsPage.visibility = View.GONE
        updateModeUI()
    }

    override fun onBackPressed() {
        if (settingsPage.visibility == View.VISIBLE) {
            closeSettings()
            return
        }
        super.onBackPressed()
    }

    // ---------- 屏幕方向（主界面图标按钮）----------
    // ---------- 操作方式切换（主界面按钮，图标即状态）----------
    /** 点击切换 鼠标(触摸板) ↔ 数位板，按钮图标同步 */
    private fun cycleWorkMode() {
        val m = if (workMode == Protocol.MODE_MOUSE) Protocol.MODE_TABLET else Protocol.MODE_MOUSE
        setWorkMode(m)
    }

    private fun setWorkMode(m: Int) {
        if (m == workMode) return
        workMode = m
        canvas.workMode = m
        prefs.edit().putInt("mode", m).apply()
        if (::net.isInitialized) {
            // 切出数位板模式时关掉桌面备注
            if (m == Protocol.MODE_MOUSE && noteOn) toggleNote(false)
            net.send(Protocol.buildMode(m))
        }
        updateModeBtn()
        updateModeUI()
        Log.i("SpenTablet", "切换工作模式 -> ${if (m == Protocol.MODE_MOUSE) "触摸板" else "数位板"}")
    }

    /** 模式相关 UI 显隐：触摸板=滚轮条+三键；数位板=工具条（备注/清除/截图） */
    private fun updateModeUI() {
        val mouse = workMode == Protocol.MODE_MOUSE
        // 设置页打开时，触摸板/数位板的浮动按钮一律收起（不挡着设置项）
        val covered = ::settingsPage.isInitialized && settingsPage.visibility == View.VISIBLE
        if (::trackpadOverlay.isInitialized) {
            trackpadOverlay.visibility = if (mouse && !covered) View.VISIBLE else View.GONE
        }
        if (::tabletToolbar.isInitialized) {
            tabletToolbar.visibility = if (!mouse && !covered) View.VISIBLE else View.GONE
        }
        // 笔迹颜色色板：只在数位板模式出现，其它界面一律隐藏
        if (::notePalette.isInitialized) {
            notePalette.visibility = if (!mouse && !covered) View.VISIBLE else View.GONE
        }
    }

    /** 模式按钮图标 = 当前模式（状态与按钮融合） */
    private fun updateModeBtn() {
        val mouse = workMode == Protocol.MODE_MOUSE
        modeBtn.setImageResource(if (mouse) R.drawable.ic_mouse else R.drawable.ic_pen)
        modeBtn.contentDescription = if (mouse) "当前：触摸板模式（点击切换为数位板）"
                                   else "当前：数位板模式（点击切换为触摸板）"
    }

    /** 问号按钮：操作控制指南（含 PC 端下载链接） */
    private fun showGuide() {
        val link = "https://github.com/volcanochen/drawing-phone/releases"
        val html = """
            <b>【自动连接（通常什么都不用做）】</b><br>
            · PC 端启动后，打开本 App 就会<b>自动连上</b>（mDNS 自动发现）<br>
            · 左上角 <b><font color="#33DD55">绿点</font></b> = 已连接，<b><font color="#FF4444">红点</font></b> = 未连接<br>
            · 点左上角圆点可查看<b>网络拓扑</b>、切换桌面 PC<br><br>
            <b>【需要手动操作的场合（很少）】</b><br>
            · <b>切换到另一台 PC</b>：点绿点 → 「局域网里的桌面」点击切换<br>
            · <b>自动发现失败</b>（路由器禁用组播等）：点齿轮 → 填 IP → 连接，<br>
              　或用「扫描网段找 PC」；USB 数据线 + adb 转发时填 127.0.0.1<br><br>
            <b>【触摸板模式（默认，手指=触摸板）】</b><br>
            · 单指滑动 = 移动光标；单击 = 左键；双击 = 右键<br>
            · 右侧滚轮条滑动 = 滚动，<b>双击滚轮条</b>切换普通滚动 / 无极滚动（动量可在设置里调）<br>
            · 底部浮动圆钮 = 左 / 中 / 右键<br>
            · 双指滑动 = 滚动；双指捏合 = 缩放（Ctrl+滚轮）<br>
            · 双指点击 = 右键；三指点击 = 中键；三指左右滑 = 前进/后退<br>
            · 手势动作可在设置里自定义：还可设为音量加减 / 静音 / 任务切换<br><br>
            <b>【数位板模式（S Pen 作画）】</b><br>
            · 笔在橙色框内 = 在 PC 上作画（压感 / 倾斜）<br>
            · 单指滑动 = 移动范围框；双指捏合 = 缩放范围框<br>
            · 框可以拖到任意显示器（含多屏）<br>
            · 底部浮动圆钮：<b>备注</b>（笔迹画在桌面固定位置，换色只影响之后的笔，手机与桌面同步）/ <b>撤销</b>（点击撤一笔、长按清空）/ <b>$shotBtnLabel</b>（捕获：桌面框内容设为手机画布背景，可继续在上面画；再点完成恢复）<br>
            · 左侧浮出色板 = 选择备注笔迹颜色<br><br>
            <b>【屏幕方向】</b>循环按钮：陀螺仪全向 / 横屏双朝向 / 竖屏双朝向<br><br>
            <b>【PC 端下载】</b>（点击打开）：<br>
            <a href="$link">$link</a>
        """.trimIndent()
        val tv = TextView(this).apply {
            text = if (android.os.Build.VERSION.SDK_INT >= 24)
                android.text.Html.fromHtml(html, android.text.Html.FROM_HTML_MODE_LEGACY)
            else @Suppress("DEPRECATION") android.text.Html.fromHtml(html)
            textSize = 14f
            setTextColor(Color.WHITE)
            setLinkTextColor(Color.parseColor("#66CCFF"))
            movementMethod = android.text.method.LinkMovementMethod.getInstance()
            setPadding(dp(20), dp(12), dp(20), dp(12))
        }
        val scroll = android.widget.ScrollView(this).apply { addView(tv) }
        android.app.AlertDialog.Builder(this)
            .setTitle("使用指南")
            .setView(scroll)
            .setPositiveButton("知道了", null)
            .show()
    }

    private fun cycleOrientation() {
        orientMode = (orientMode + 1) % 3
        applyOrientationMode()
        prefs.edit().putInt("orient", orientMode).apply()
    }

    private fun parsePcInfo(text: String) {
        val map = text.split("&").mapNotNull { kv ->
            val p = kv.split("=", limit = 2)
            if (p.size == 2) p[0] to p[1] else null
        }.toMap()
        pcVersion = map["ver"] ?: pcVersion
        pcName = map["name"]?.let { try { java.net.URLDecoder.decode(it, "UTF-8") } catch (_: Throwable) { it } }
            ?: pcName
        pcIps = map["ips"]?.let { try { java.net.URLDecoder.decode(it, "UTF-8") } catch (_: Throwable) { it } } ?: pcIps
        Log.i("SpenTablet", "PC INFO → ver=$pcVersion name=$pcName ips=$pcIps")
    }

    private fun openTopology() {
        val method = NetworkScan.networkMethod(host)
        val phoneIp = NetworkScan.primaryLocalIp()
        val phoneName = Build.MODEL ?: "Android"
        val others = MdnsDiscovery.current()
        TopologyDialog.show(
            this,
            TopologyDialog.TopologyData(
                pcIp = host, pcName = pcName, pcVersion = pcVersion,
                method = method, phoneIp = phoneIp, phoneName = phoneName,
                otherPcs = others
            )
        ) { pc -> connectToPc(pc, "拓扑切换") }
    }

    override fun onConfigurationChanged(newConfig: android.content.res.Configuration) {
        super.onConfigurationChanged(newConfig)
        val land = newConfig.orientation == android.content.res.Configuration.ORIENTATION_LANDSCAPE
        if (land == landscape) return
        landscape = land
        topBar.setPadding(if (land) 12 else 16, if (land) 2 else 10, if (land) 12 else 16, if (land) 2 else 6)
        // 顶栏高度跟着方向换到另一档固定值（不随文字变化）
        (topBar.layoutParams as? LinearLayout.LayoutParams)?.let {
            it.height = dp(topBarHeightDp())
            topBar.layoutParams = it
        }
        DebugLog.write("[orient] ${if (land) "横屏" else "竖屏"}｜书写区 ${surfaceW().toInt()}x${surfaceH().toInt()}｜${canvas.frameSummary()}")
    }

    /** 方向说明文字（设置页/提示用） */
    private fun orientText(mode: Int) = when (mode) {
        1 -> "屏幕方向：横屏（陀螺仪在两个横屏朝向间自动翻转，点此切换）"
        2 -> "屏幕方向：竖屏（陀螺仪在两个竖屏朝向间自动翻转，点此切换）"
        else -> "屏幕方向：跟随系统陀螺仪（四个朝向，点此切换）"
    }

    private fun applyOrientationMode() {
        requestedOrientation = when (orientMode) {
            // 横屏/竖屏均带 SENSOR：同组两个朝向由陀螺仪自动切换
            1 -> android.content.pm.ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
            2 -> android.content.pm.ActivityInfo.SCREEN_ORIENTATION_SENSOR_PORTRAIT
            else -> android.content.pm.ActivityInfo.SCREEN_ORIENTATION_FULL_SENSOR
        }
    }

    private fun setGain(v: Float) {
        canvas.pressureGain = v.coerceIn(0.5f, 8f)
        prefs.edit().putFloat("gain", canvas.pressureGain).apply()
        gainText.text = "压感增益 ${String.format("%.1f", canvas.pressureGain)}"
    }

    // ================= 触摸板模式 UI（右侧滚轮条 + 底部左/中/右键）=================
    private fun buildTrackpadUi(root: FrameLayout) {
        trackpadOverlay = FrameLayout(this)
        trackpadOverlay.visibility = View.GONE // 初始按当前模式在 updateModeUI 里定

        // —— 右侧虚拟滚轮：手指上下滑 = 滚动滚轮（带横纹，滚动时纹路跟着走） ——
        val wheel = WheelStripView(this)
        wheel.layoutParams = FrameLayout.LayoutParams(
            dp(52), ViewGroup.LayoutParams.MATCH_PARENT, Gravity.END).apply {
            // 高度限制在触摸区域之内：上方让开顶栏，下方让开左/中/右键条
            topMargin = dp(56)
            bottomMargin = dp(72)
        }
        wheel.setOnTouchListener { v, ev ->
            val strip = v as WheelStripView
            when (ev.actionMasked) {
                MotionEvent.ACTION_DOWN -> {
                    val now = android.os.SystemClock.uptimeMillis()
                    if (now - wheelLastDownAt < 280L) {
                        // 双击滚轮条 → 普通滚动 / 无极滚动 切换
                        wheelInertia = !wheelInertia
                        prefs.edit().putBoolean("wheelInertia", wheelInertia).apply()
                        strip.inertia = wheelInertia
                        DebugLog.write("[wheel] ${if (wheelInertia) "无极滚动" else "普通滚动"}")
                    }
                    wheelLastDownAt = now
                    stopInertia()
                    v.tag = ev.y
                    wheelLastY = ev.y
                    wheelLastT = now
                    true
                }
                MotionEvent.ACTION_MOVE -> {
                    val last = (v.tag as? Float) ?: ev.y
                    val dy = ev.y - last
                    v.tag = ev.y
                    if (canvas.height > 0) {
                        canvas.sendWheel(dy / canvas.height)
                        strip.scrollBy(dy)      // 横纹跟着手指走，像真的滚轮
                        // 记录抬手瞬间的速度（用于无极滚动的惯性）
                        val now = android.os.SystemClock.uptimeMillis()
                        val dt = (now - wheelLastT).coerceAtLeast(1)
                        wheelVel = (ev.y - wheelLastY) / canvas.height.toFloat() * (16f / dt)
                        wheelLastY = ev.y
                        wheelLastT = now
                    }
                    true
                }
                MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                    // 无极滚动：抬手后按动量继续滚，逐渐衰减停下
                    if (wheelInertia && kotlin.math.abs(wheelVel) > 0.0008f) startInertia()
                    true
                }
                else -> true
            }
        }
        wheel.inertia = wheelInertia
        // —— 底部三个浮动圆钮：左键 / 中键 / 右键 ——
        val keysBar = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            layoutParams = FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT,
                Gravity.BOTTOM or Gravity.CENTER_HORIZONTAL).apply { bottomMargin = dp(10) }
        }
        fun keyBtn(label: String, kind: Int): Button = Button(this).apply {
            text = label
            layoutParams = LinearLayout.LayoutParams(dp(64), dp(64))
                .apply { setMargins(dp(6), 0, dp(6), 0) }
            background = floatBtnBg()
            setTextColor(Color.WHITE)
            setOnClickListener { canvas.sendClick(kind) }
        }
        keysBar.addView(keyBtn("左", Protocol.MOUSE_LEFT_CLICK))
        keysBar.addView(keyBtn("中", Protocol.MOUSE_MIDDLE_CLICK))
        keysBar.addView(keyBtn("右", Protocol.MOUSE_RIGHT_CLICK))

        trackpadOverlay.addView(wheel)
        trackpadOverlay.addView(keysBar)
        root.addView(trackpadOverlay, FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
    }

    // ---- 无极滚动（惯性）----
    private var wheelLastDownAt = 0L
    private var wheelLastY = 0f
    private var wheelLastT = 0L
    /** 抬手瞬间的滚动速度（归一化/16ms） */
    private var wheelVel = 0f
    private val inertiaHandler = android.os.Handler(android.os.Looper.getMainLooper())
    /** 惯性开始时刻（用于硬性限时） */
    private var inertiaStartAt = 0L
    /** 本轮惯性的停止阈值（按起始速度比例算，甩多快都在同样时间内停） */
    private var inertiaStopVel = 0.0006f

    private val inertiaTask = object : Runnable {
        override fun run() {
            // 三个条件任一满足即停：关掉无极滚动 / 超过最长时长 / 速度掉到阈值以下。
            // 以前只有 0.92 的衰减 + 极小阈值，甩一下要滚 1 秒多，手感像"不按停就一直滚"。
            if (!wheelInertia) { stopInertia(); return }
            val now = android.os.SystemClock.uptimeMillis()
            if (now - inertiaStartAt > inertiaMaxMs ||
                kotlin.math.abs(wheelVel) < inertiaStopVel) {
                wheelVel = 0f
                return
            }
            canvas.sendWheel(wheelVel)
            wheelVel *= inertiaDecay       // 每帧衰减（0.86：约 0.3 秒内滑停）
            inertiaHandler.postDelayed(this, inertiaTickMs)
        }
    }

    private fun startInertia() {
        wheelVel *= wheelMomentum.coerceIn(0.2f, 3.0f)
        // 起始速度越大，停止阈值按比例抬高 → 惯性时长与甩动幅度基本无关
        inertiaStopVel = maxOf(0.0006f, kotlin.math.abs(wheelVel) * 0.04f)
        inertiaStartAt = android.os.SystemClock.uptimeMillis()
        inertiaHandler.removeCallbacks(inertiaTask)
        inertiaHandler.postDelayed(inertiaTask, inertiaTickMs)
    }

    private fun stopInertia() {
        inertiaHandler.removeCallbacks(inertiaTask)
        wheelVel = 0f
    }

    /** 浮动按钮外观：半透明圆角小钮（不占满整条，浮在画面上） */
    private fun floatBtnBg() = android.graphics.drawable.GradientDrawable().apply {
        setColor(Color.parseColor("#AA2A2A2A"))
        cornerRadius = dp(32).toFloat()
        setStroke(1, Color.parseColor("#66FFFFFF"))
    }

    /**
     * 右侧虚拟滚轮条：圆角底 + 等间距横纹（模仿滚轮的沟槽）。
     * 滚动时横纹跟着手指上下平移（[scrollBy]），看起来真的在滚。
     * [inertia] = 无极滚动模式：纹路画得更亮，提示当前是惯性滚动。
     */
    private inner class WheelStripView(context: android.content.Context) : View(context) {
        private val bgPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
            color = Color.parseColor("#33FFFFFF")
            style = Paint.Style.FILL
        }
        private val linePaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
            color = Color.parseColor("#88FFFFFF")
            strokeWidth = 2f * resources.displayMetrics.density
            style = Paint.Style.STROKE
        }
        private val rect = RectF()
        /** 横纹的滚动偏移(px)，取模后循环平移 */
        private var offset = 0f
        /** 无极滚动模式下纹路更亮（视觉上区分两种滚动模式） */
        var inertia = false
            set(value) { field = value; invalidate() }
        private val gapPx get() = 13f * resources.displayMetrics.density
        private val radiusPx get() = 10f * resources.displayMetrics.density
        private val padPx get() = 9f * resources.displayMetrics.density

        fun scrollBy(dyPx: Float) {
            offset += dyPx
            invalidate()
        }

        override fun onDraw(c: Canvas) {
            super.onDraw(c)
            val w = width.toFloat()
            val h = height.toFloat()
            if (w <= 0f || h <= 0f) return
            rect.set(0f, 0f, w, h)
            c.drawRoundRect(rect, radiusPx, radiusPx, bgPaint)
            val gap = gapPx
            if (gap <= 1f) return
            linePaint.color = Color.parseColor(if (inertia) "#CCFFFFFF" else "#88FFFFFF")
            // 让横纹在 [0,gap) 内循环，滚动时整片纹路跟着手指移动
            var y = ((offset % gap) + gap) % gap
            while (y < h) {
                c.drawLine(padPx, y, w - padPx, y, linePaint)
                y += gap
            }
        }
    }

    // ================= 数位板工具条（备注 / 清除 / 截图）=================
    private fun buildTabletToolbar(root: FrameLayout) {
        tabletToolbar = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            layoutParams = FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT,
                Gravity.BOTTOM or Gravity.CENTER_HORIZONTAL).apply { bottomMargin = dp(10) }
        }
        fun toolBtn(label: String): Button = Button(this).apply {
            text = label
            layoutParams = LinearLayout.LayoutParams(dp(66), dp(64))
                .apply { setMargins(dp(6), 0, dp(6), 0) }
            background = floatBtnBg()
            setTextColor(Color.WHITE)
        }
        noteBtn = toolBtn("备注").apply {
            setOnClickListener { toggleNote(!noteOn) }
        }
        val undoBtn = toolBtn("撤销").apply {
            setOnClickListener {
                net.send(Protocol.buildNote(Protocol.NOTE_UNDO)) // 点击=撤销一笔
            }
            setOnLongClickListener {
                net.send(Protocol.buildNote(Protocol.NOTE_CLEAR)) // 长按=清空
                true
            }
        }
        shotBtn = toolBtn("捕获").apply {
            setOnClickListener { onShotButton() }
        }
        tabletToolbar.addView(noteBtn)
        tabletToolbar.addView(undoBtn)
        tabletToolbar.addView(shotBtn)
        tabletToolbar.visibility = View.GONE
        root.addView(tabletToolbar) // layoutParams 已带 Gravity.BOTTOM
        // 左侧悬浮的笔迹颜色色板（仅数位板模式可见）
        buildNotePalette(root)
    }

    /** 预设笔迹颜色（顺序必须与 PC 端 NoteOverlay.Presets 完全一致） */
    private val noteColors = intArrayOf(
        0xFFFF3C3C.toInt(), // 红
        0xFFFFBE28.toInt(), // 橙
        0xFFFAF03C.toInt(), // 黄
        0xFF3CDC6E.toInt(), // 绿
        0xFF50BEFF.toInt(), // 蓝
        0xFFC86EFF.toInt(), // 紫
        0xFFFAFAFA.toInt()  // 白
    )

    /**
     * 屏幕左侧浮出的一列颜色块：点一下就换备注笔迹颜色（索引发给 PC）。
     * 只在数位板模式出现，其它界面一律隐藏，避免挡住书写区。
     */
    private fun buildNotePalette(root: FrameLayout) {
        notePalette = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            layoutParams = FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT,
                Gravity.START or Gravity.CENTER_VERTICAL).apply { leftMargin = dp(6) }
            setBackgroundColor(Color.parseColor("#66222222"))
            setPadding(dp(4), dp(5), dp(4), dp(5))
            visibility = View.GONE
        }
        noteColorIdx = prefs.getInt("noteColor", 0).coerceIn(0, noteColors.lastIndex)
        // 手机屏上画的笔迹也用同一个颜色（落笔时锁定，之后换色不影响已画的笔）
        canvas.strokeColor = noteColors[noteColorIdx]
        repeat(noteColors.size) { i ->
            val swatch = View(this).apply {
                layoutParams = LinearLayout.LayoutParams(dp(26), dp(26)).apply {
                    setMargins(0, dp(3), 0, dp(3))
                }
                background = android.graphics.drawable.GradientDrawable().apply {
                    setColor(noteColors[i])
                    cornerRadius = dp(5).toFloat()
                    setStroke(if (i == noteColorIdx) dp(3) else 1,
                        if (i == noteColorIdx) Color.WHITE else Color.parseColor("#99777777"))
                }
                setOnClickListener { selectNoteColor(i) }
            }
            notePalette.addView(swatch)
        }
        root.addView(notePalette)
    }

    /** 选中一个笔迹颜色：高亮该色块，并把颜色索引发给 PC */
    private fun selectNoteColor(idx: Int) {
        noteColorIdx = idx
        for (i in 0 until notePalette.childCount) {
            val v = notePalette.getChildAt(i)
            v.background = android.graphics.drawable.GradientDrawable().apply {
                setColor(noteColors[i])
                cornerRadius = dp(5).toFloat()
                setStroke(if (i == idx) dp(3) else 1,
                    if (i == idx) Color.WHITE else Color.parseColor("#99777777"))
            }
        }
        prefs.edit().putInt("noteColor", idx).apply()
        // 手机端本地笔迹同步换色（只影响之后落的新笔），并下发索引给 PC 端叠加层
        canvas.strokeColor = noteColors[idx]
        if (::net.isInitialized) net.send(Protocol.buildNote(Protocol.NOTE_COLOR, idx.toFloat()))
        DebugLog.write("[note] 笔迹颜色 → 预设 #$idx")
    }

    private lateinit var noteBtn: Button
    private lateinit var shotBtn: Button
    /** 截图当前处于「已捕获」状态（按钮变成清空） */
    private var shotCaptured = false

    /** 桌面备注开关：开启后 S Pen 直接在 PC 桌面固定位置画笔迹（不随范围框移动） */
    private fun toggleNote(on: Boolean) {
        if (!::net.isInitialized) return
        noteOn = on
        net.send(Protocol.buildNote(if (on) Protocol.NOTE_ON else Protocol.NOTE_OFF))
        if (on) net.send(Protocol.buildNote(Protocol.NOTE_COLOR, noteColorIdx.toFloat()))
        noteBtn.text = if (on) "备注✓" else "备注"
        noteBtn.isEnabled = true
        DebugLog.write("[note] ${if (on) "开启" else "关闭"}")
    }

    /** 截图按钮状态机：捕获 → 完成 → 捕获…（捕获期间锁定范围框） */
    private fun onShotButton() {
        if (!shotCaptured) {
            if (::net.isInitialized) net.send(Protocol.buildShotReq())
            canvas.frameLocked = true
            shotBtn.text = "完成"
            shotCaptured = true
        } else {
            closeShot()
        }
    }

    /**
     * PC 传回的范围框截图：直接设成书写区的**背景**（不是弹出新窗口），
     * 之后照样能在上面写写画画，笔迹叠在截图上面。
     */
    private fun showShot(png: ByteArray) {
        val bmp = BitmapFactory.decodeByteArray(png, 0, png.size)
        if (bmp == null) {
            DebugLog.write("[shot] 解码失败 ${png.size}B")
            return
        }
        canvas.shotBackground = bmp
        DebugLog.write("[shot] 已设为背景 ${bmp.width}x${bmp.height}")
    }

    /** 清除截图背景（按钮变回「捕获」，同时解除范围框锁定） */
    private fun closeShot() {
        canvas.shotBackground = null
        if (shotCaptured) {
            canvas.frameLocked = false
            shotCaptured = false
            shotBtn.text = "捕获"
        }
    }

    /** 指南/其它文案里描述捕获按钮的当前名称 */
    private val shotBtnLabel: String get() = if (shotCaptured) "完成" else "捕获"

    // ================= 触摸板配置（速度/滚动/手势/颜色）=================
    /** 无极滚动：抬手后按动量继续滚（双击右侧滚轮条切换） */
    private var wheelInertia = false
    /** 无极滚动的动量倍率（越大惯性滑得越远） */
    private var wheelMomentum = 1.0f

    /** 惯性每帧衰减系数（越小停得越快） */
    private val inertiaDecay = 0.86f
    /** 惯性滑动的调度间隔(ms) */
    private val inertiaTickMs = 16L
    /** 惯性最长时长(ms)：到点无条件停，杜绝长尾滚动 */
    private val inertiaMaxMs = 600L

    private fun applyTrackpadPrefs() {
        // 默认值迁移（只做一次）：tpDefaults2 → 速度 1.5 / 滚动 3.0；tpDefaults3 → 滚动上限放宽后默认 5.0
        if (!prefs.getBoolean("tpDefaults2", false)) {
            prefs.edit()
                .putBoolean("tpDefaults2", true)
                .putFloat("tpSpeed", 1.5f)
                .putFloat("tpScroll", 3.0f)
                .apply()
        }
        if (!prefs.getBoolean("tpDefaults3", false)) {
            prefs.edit()
                .putBoolean("tpDefaults3", true)
                .putFloat("tpScroll", 5.0f)
                .apply()
        }
        canvas.cursorSpeed = prefs.getFloat("tpSpeed", 1.5f)
        canvas.scrollScale = prefs.getFloat("tpScroll", 5.0f)
        canvas.trackpadColor = prefs.getInt("tpColor", 0xFF20242B.toInt())
        canvas.g2Swipe = prefs.getInt("g2Swipe", 1)
        canvas.g2Pinch = prefs.getInt("g2Pinch", 2)
        canvas.g2Tap = prefs.getInt("g2Tap", 4)
        canvas.g3Tap = prefs.getInt("g3Tap", 5)
        canvas.g3Swipe = prefs.getInt("g3Swipe", 8)
        canvas.g4Tap = prefs.getInt("g4Tap", 9)
        canvas.g4Swipe = prefs.getInt("g4Swipe", 0)
        wheelInertia = prefs.getBoolean("wheelInertia", false)
        wheelMomentum = prefs.getFloat("wheelMomentum", 1.0f)
    }

    /** 循环切换一个手势槽的动作码（设置页按钮用） */
    private fun cycleGesture(key: String, current: Int, allowed: IntArray): Int {
        val idx = (allowed.indexOf(current) + 1).let { if (it >= allowed.size) 0 else it }
        val next = allowed[idx]
        prefs.edit().putInt(key, next).apply()
        applyTrackpadPrefs()
        return next
    }

    private fun dp(v: Int) = (v * resources.displayMetrics.density).toInt()

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

    private fun startMdnsDiscovery(manual: Boolean) {
        MdnsDiscovery.start(this, object : MdnsDiscovery.Listener {
            override fun onServicesChanged(list: List<MdnsDiscovery.Found>) {
                runOnUiThread {
                    if (list.isEmpty()) {
                        if (manual) ipText.text = "没发现 PC。\n请确认 PC 上 SpenTabletHost 已启动，且手机与 PC 在同一网络（热点/同一 WiFi）。"
                        return@runOnUiThread
                    }
                    if (manual) {
                        discoverBtn.isEnabled = true
                        discoverBtn.text = "发现 PC（mDNS 自动，推荐）"
                        if (list.size == 1) {
                            connectToPc(list[0], "mDNS 发现")
                        } else {
                            showPcChooser(list)
                        }
                    } else if (!mdnsConnected && (!::net.isInitialized || !net.connected)) {
                        mdnsConnected = true
                        connectToPc(list[0], "mDNS 自动发现")
                    }
                }
            }

            override fun onError(message: String) {
                runOnUiThread {
                    if (manual) {
                        discoverBtn.isEnabled = true
                        discoverBtn.text = "发现 PC（mDNS 自动，推荐）"
                        ipText.text = "mDNS 自动发现不可用：$message\n可改用「扫描网段找 PC」。"
                    }
                }
            }
        })
    }

    private fun showPcChooser(list: List<MdnsDiscovery.Found>) {
        val names = list.map { it.label }.toTypedArray()
        android.app.AlertDialog.Builder(this)
            .setTitle("选择要连接的电脑")
            .setItems(names) { _, which -> connectToPc(list[which], "mDNS 发现") }
            .setNegativeButton("取消", null)
            .show()
    }

    /** 连到发现到的 PC：候选地址逐个探测 → 填 IP/端口 → 记 prefs → 重连 */
    private fun connectToPc(f: MdnsDiscovery.Found, tag: String) {
        val candidates = f.candidates.ifEmpty { listOf(f.host) }
        Thread {
            var picked: String? = null
            for (c in candidates) {
                if (probeTcp(c, f.port)) {
                    picked = c
                    break
                }
                Log.i("SpenTablet", "$tag 候选 $c:${f.port} 连不上，换下一个")
            }
            val target = picked ?: candidates.first()
            runOnUiThread {
                host = target
                port = f.port
                hostEdit.setText(target)
                portEdit.setText(f.port.toString())
                prefs.edit().putString("host", target).putInt("port", f.port).apply()
                ipText.text = if (picked != null) {
                    "${tag}：${f.serviceName}\n$target:${f.port} 可达，正在连接…"
                } else {
                    "${tag}：${f.serviceName}\n候选 ${candidates.joinToString("、")} 都连不上，" +
                            "仍尝试 $target:${f.port}"
                }
                Log.i("SpenTablet", "$tag → ${f.serviceName} $target:${f.port}")
                restartNet()
            }
        }.apply { isDaemon = true }.start()
    }

    private fun probeTcp(hostStr: String, portNum: Int, timeoutMs: Int = 1200): Boolean = try {
        java.net.Socket().use {
            it.connect(java.net.InetSocketAddress(hostStr, portNum), timeoutMs)
            true
        }
    } catch (e: Throwable) {
        false
    }

    private fun restartNet() {
        if (::net.isInitialized) net.stop()
        net = NetClient(host, port) { helloFrame() }
        net.onInfo = { text -> parsePcInfo(text) }
        net.start()
        Log.i("SpenTablet", "重连 $host:$port")
    }

    override fun onDestroy() {
        alive = false
        DebugLog.write("[life] onDestroy")
        DebugLog.flush()
        MdnsDiscovery.stop()
        if (::net.isInitialized) net.stop()
        super.onDestroy()
    }
}
