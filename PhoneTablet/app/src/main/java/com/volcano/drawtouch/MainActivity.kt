package com.volcano.drawtouch

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
import android.widget.Toast

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
    /** S Pen 悬浮提示（笔悬停在按钮上弹一句功能说明） */
    private lateinit var hoverTip: HoverTip

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
    /** 可拖动浮动面板：激光笔 + 左 / 中 / 右 键合并在一起 */
    private lateinit var trackpadDock: DraggablePanel
    private lateinit var hlBtn: Button
    /** 浮动面板里的左 / 中 / 右 键（手势触发时要在对应键上闪烁反馈） */
    private lateinit var tLeftBtn: Button
    private lateinit var tMidBtn: Button
    private lateinit var tRightBtn: Button
    // ---- 数位板模式工具条（备注/撤销/完成 + 色板，合并成一个可拖动面板）----
    private lateinit var tabletToolbar: android.widget.LinearLayout
    /** 工具条第二行的 7 个笔迹颜色块（高亮切换用） */
    private var noteColorRow: LinearLayout? = null
    /** 当前笔迹颜色索引（对应 [noteColors]） */
    private var noteColorIdx = 0
    /** 截图预览（全屏覆盖） */
    private var shotView: ImageView? = null
    /** 桌面备注开启中 */
    private var noteOn = false

    /** 独立设置页（全屏覆盖层，默认隐藏；返回键 / 完成按钮关闭） */
    private lateinit var settingsPage: LinearLayout
    /** 设置页正文容器（竖屏单列 / 横屏两列；转屏时整体重建） */
    private lateinit var settingsBody: FrameLayout

    /** 当前是否横屏（决定控制栏的紧凑度） */
    private var landscape = false

    /**
     * 顶部任务栏**固定**高度(dp)：竖屏 56 / 横屏 44。
     * 与滚轮条的 topMargin=56dp 对齐；绝不能随状态文字内容变化（用户明确要求过）。
     */
    private fun topBarHeightDp() = if (landscape) 44 else 56

    /** 屏幕方向模式：0=跟随系统(听系统的自动旋转) 1=横屏(双向陀螺仪) 2=竖屏(双向陀螺仪) */
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
    /** PC 上报的协议兼容版本（INFO 帧里的 pv=；0 = 老 PC 没报，视为不兼容） */
    private var pcProtoVer = 0
    /** 非空 = 双端协议版本不匹配：指示灯变黄、停止后续对接（不再重连） */
    private var protoMismatch: String? = null

    @Volatile
    private var alive = true

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        prefs = getSharedPreferences("drawtouch", MODE_PRIVATE)
        DebugLog.init(this)
        host = prefs.getString("host", "127.0.0.1") ?: "127.0.0.1"
        port = prefs.getInt("port", Protocol.DEFAULT_PORT)
        aspectMode = prefs.getInt("aspect", Protocol.ASPECT_KEEP)
        workMode = prefs.getInt("mode", Protocol.MODE_MOUSE) // 默认鼠标模式
        // 备注/画笔模式：记住用户的选择；没选过时数位板默认「备注」（笔迹直接上桌面）
        noteOn = prefs.getBoolean("noteOn", workMode == Protocol.MODE_TABLET)
        orientMode = prefs.getInt("orient", 0)

        // 结构：FrameLayout
        //   content —— 上控制栏(fixed) + 书写区(weight=1) + 下状态栏(fixed)
        //   settingsPage —— 全屏设置页，浮在 content 之上，默认 GONE
        val root = FrameLayout(this)
        val content = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        // 悬浮提示层：最早建好，后面各个按钮创建时就能直接挂上去（显示时会 bringToFront）
        hoverTip = HoverTip(root)

        canvas = PenCanvasView(this)
        Haptics.init(this)          // 触觉反馈：拿 Vibrator 实例（失败也不影响手势）
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
        // 笔悬停在这些图标上时弹一句说明（文案随状态取，模式按钮的说明会跟着当前模式变）
        hoverTip.attach(modeBtn) {
            if (workMode == Protocol.MODE_MOUSE) "切到「数位板」：笔在框内作画"
            else "切到「触摸板」：手指当鼠标用"
        }
        hoverTip.attach(rotateBtn) { "切换屏幕方向：竖屏 / 横屏 / 跟随系统" }
        hoverTip.attach(helpBtn) { "使用指南：各模式手势与按钮说明" }
        hoverTip.attach(settingsBtn) { "设置：连接、增益、死区、手势动作" }
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
            gravity = android.view.Gravity.CENTER_VERTICAL
            setPadding(dp(14), dp(8), dp(14), dp(8))
            setBackgroundColor(Color.parseColor("#CC111111"))
        }
        // v1.4.9：标题栏压成**一行**（标题 + 版本小字 + 完成按钮）。
        // 原来「设置」和版本小字是竖排两行，横屏时白白吃掉 1/4 的屏幕高度。
        val setTitle = TextView(this).apply {
            text = "设置"
            textSize = 18f
            setTextColor(Color.WHITE)
        }
        val setVer = TextView(this).apply {
            text = "v${BuildConfig.VERSION_NAME} (code ${BuildConfig.VERSION_CODE}) · 构建于 ${BuildConfig.BUILD_TIME}"
            textSize = 11f
            setTextColor(Color.LTGRAY)
            setPadding(dp(10), 0, dp(6), 0)
            // 单行 + 尾部省略：竖屏窄屏时版本号会折行，把标题栏撑成两行、白白吃掉高度。
            // 完整信息在「关于」分类里能看到。
            maxLines = 1
            ellipsize = android.text.TextUtils.TruncateAt.END
            layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
        }
        val doneBtn = Button(this).apply { text = "完成" }
        setHeader.addView(setTitle)
        setHeader.addView(setVer)
        setHeader.addView(doneBtn)
        settingsPage.addView(setHeader)

        // 设置页正文容器：竖屏单列 / 横屏两列（结构不同，转屏时整体重建）
        settingsBody = FrameLayout(this).apply {
            setPadding(dp(12), dp(6), dp(12), dp(10))
        }
        settingsPage.addView(settingsBody, LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f))
        rebuildSettings()

        root.addView(content, FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
        root.addView(settingsPage, FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))

        // v1.4.9：正文底部避开系统导航条（手势条），否则最后一张卡片会被压住。
        root.setOnApplyWindowInsetsListener { _, insets ->
            @Suppress("DEPRECATION", "NewApi")
            val bottom = if (android.os.Build.VERSION.SDK_INT >= 30)
                insets.getInsets(android.view.WindowInsets.Type.systemBars()).bottom
            else insets.systemWindowInsetBottom
            settingsBody.setPadding(dp(12), dp(6), dp(12), dp(10) + bottom)
            insets
        }

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
        // 任意触摸落下都打断无极滚动的惯性（滚轮条自己的 ACTION_DOWN 也会停）
        canvas.onTouchDown = { stopInertia() }
        // 手指是否压在触摸板上 → 告诉 PC「S Pen 让位」（否则笔的悬停流会抢光标）
        canvas.onFingerState = { on ->
            if (::net.isInitialized) {
                net.send(Protocol.buildMouse(Protocol.MOUSE_FINGER, if (on) 1f else 0f, 0f))
            }
        }
        // 手势触发的鼠标动作（如三指点击→中键）→ 在手机端对应的浮动键上闪烁，看得见「打中哪个键」
        canvas.onActionVisual = { code -> runOnUiThread { flashActionVisual(code) } }

        setContentView(root)
        applyOrientationMode()
        updateModeBtn() // 模式按钮图标 = 当前模式
        updateModeUI()  // 触摸板 UI / 数位板工具条显隐

        // 第二个参数是「连接建立后要发的 Hello 帧」；顺带在这里同步笔迹颜色与备注状态
        net = newNetClient()
        net.start()

        // 开机静默 mDNS 发现：找到唯一一台 PC 且当前没连上 → 直接连上，用户零操作
        startMdnsDiscovery(manual = false)

        // 开机静默检查更新：仅在发现新版本时弹窗提示，不打扰日常使用
        checkForUpdate(silent = true)

        Thread {
            var tick = 0
            while (alive) {
                val sum = canvas.debugSummary()
                val err = net.lastError
                val w = surfaceW(); val h = surfaceH()
                val aspect = if (h > 0) String.format("%.3f", w / h) else "?"
                val modeName = if (workMode == Protocol.MODE_MOUSE) "触摸板" else "数位板"
                runOnUiThread {
                    when {
                        // 黄：双端协议版本不兼容，已停止对接（不再重连，直到另一端升级）
                        protoMismatch != null -> {
                            statusDot.setBackgroundColor(Color.parseColor("#FFC107"))
                            status.text = protoMismatch
                            status.setTextColor(Color.parseColor("#FFFFC107"))
                        }
                        net.connected -> {
                            statusDot.setBackgroundColor(Color.parseColor("#33DD55")) // 绿：已连
                            status.text = "" // 绿点即已连接，无需文字
                            status.setTextColor(Color.WHITE)
                        }
                        else -> {
                            statusDot.setBackgroundColor(Color.parseColor("#FF4444")) // 红：未连
                            val friendly = net.friendlyError(err)
                            status.text = if (friendly.isEmpty()) "未连接｜$modeName（点此设置）"
                                          else "未连接：${friendly.substringBefore('\n')}"
                            status.setTextColor(Color.parseColor("#FFFF6B6B"))
                        }
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

    /**
     * 构建设置页里的所有控件。
     *
     * v1.4.9 重构：**按分类组织** —— 每个分类是一张统一结构的卡片
     * （标题 → 一句说明 → 内容区）；摆放由 [layoutSettingsSections] 决定
     * （竖屏单列；横屏两列且各自独立滚动，充分利用横向空间）。
     *
     * @return 分类卡片列表（顺序即显示顺序；横屏时按下标奇偶分左右两列）
     */
    private fun buildSettingsControls(): List<LinearLayout> {
        val sections = ArrayList<LinearLayout>()

        // ==================== 1. 连接 ====================
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

        sections.add(sectionCard(
            "连接",
            "PC 的地址与端口。USB 模式填 127.0.0.1；WiFi 模式建议用「自动发现」。"
        ) {
            it.addView(rowHost)
            it.addView(connectBtn)
            it.addView(discoverBtn)
            it.addView(findBtn)
            it.addView(ipText)
        })

        // ==================== 2. 模式与映射 ====================
        val aspectTitle = TextView(this).apply {
            textSize = 13f; setTextColor(Color.WHITE)
            text = "比例映射（笔在 PC 上的覆盖范围）"
            setPadding(0, 6, 0, 2)
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

        gainText = TextView(this).apply {
            textSize = 13f; setTextColor(Color.WHITE)
            setPadding(0, 8, 0, 2)
            text = "压感增益 ${String.format("%.1f", canvas.pressureGain)}"
        }
        val gainRow = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        val minusBtn = Button(this).apply { text = "增益 −" }
        val plusBtn = Button(this).apply { text = "增益 +" }
        gainRow.addView(minusBtn)
        gainRow.addView(plusBtn)

        sections.add(sectionCard(
            "模式与映射",
            "决定笔在 PC 上的覆盖范围，以及手指和 S Pen 各干什么。"
        ) {
            it.addView(aspectTitle)
            it.addView(aspectGroup)
            it.addView(modeTitle)
            it.addView(modeGroup)
            it.addView(resetFrameBtn)
            it.addView(hoverBox)
            it.addView(gainText)
            it.addView(gainRow)
        })

        // ==================== 3. 指针与滚动 ====================
        /**
         * 数值调节行：[−] 标签 数值 [+]
         *
         * v1.4.9：**数值单独一列**（不再是「标签 数值」挤在同一个 TextView 里）。
         * 否则标签一换行，数值就被顶到第二行、跟上一行的卡片边贴在一起
         * ——横屏两列时列更窄，这种情况必现。
         */
        fun minusPlus(label: String, get: () -> Float, set: (Float) -> Unit, step: Float, min: Float, max: Float,
                      valueFmt: String = "%.1f"): LinearLayout {
            fun fmt(v: Float) = String.format(valueFmt, v)
            val labelTv = TextView(this).apply {
                textSize = 14f; setTextColor(Color.WHITE)
                setPadding(dp(8), 4, dp(4), 4)
                text = label
            }
            val valueTv = TextView(this).apply {
                textSize = 14f; setTextColor(Color.parseColor("#FF7FD1FF"))
                setPadding(0, 4, dp(4), 4)
                text = fmt(get())
            }
            val row = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
            fun mk(delta: Float): Button = Button(this).apply {
                text = if (delta < 0) "−" else "+"
                setOnClickListener {
                    set((get() + delta).coerceIn(min, max))
                    valueTv.text = fmt(get())
                }
            }
            row.addView(mk(-step))
            row.addView(labelTv, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
            row.addView(valueTv, LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT))
            row.addView(mk(step))
            return row
        }

        val speedRow = minusPlus("光标速度", { canvas.cursorSpeed }, {
            canvas.cursorSpeed = it; prefs.edit().putFloat("tpSpeed", it).apply()
        }, 0.1f, 0.2f, 3.0f)
        val scrollRow = minusPlus("滚动比例", { canvas.scrollScale }, {
            canvas.scrollScale = it; prefs.edit().putFloat("tpScroll", it).apply()
        }, 0.5f, 0.2f, 30.0f)
        // 手势死区（防误触 / 防抖动）。捏合死区已删除：2026-10-29 捏合判定放宽为「两指反向运动」。
        val dzRow = minusPlus("拖框死区 dp", { canvas.moveDeadZoneDp }, {
            canvas.moveDeadZoneDp = it; prefs.edit().putFloat("tpMoveDz", it).apply()
        }, 1f, 0f, 30f)
        val smoothBtn = Button(this).apply {
            fun refresh() {
                text = when (canvas.pointerSmoothProfile) {
                    "STABLE" -> "指针平滑：稳（默认）"
                    "NORMAL" -> "指针平滑：标准"
                    else -> "指针平滑：关闭（1:1）"
                }
            }
            refresh()
            setOnClickListener {
                val next = when (canvas.pointerSmoothProfile) {
                    "STABLE" -> "NORMAL"
                    "NORMAL" -> "OFF"
                    else -> "STABLE"
                }
                canvas.pointerSmoothProfile = next
                prefs.edit().putString("ptrSmooth", next).apply()
                refresh()
            }
        }
        val wheelModeBtn = Button(this).apply {
            fun refresh() {
                text = if (wheelInertia) "滚动模式：无极滚动" else "滚动模式：普通滚动"
            }
            refresh()
            setOnClickListener {
                wheelInertia = !wheelInertia
                prefs.edit().putBoolean("wheelInertia", wheelInertia).apply()
                refresh()
            }
        }
        val momentumRow = minusPlus("滚轮动量", { wheelMomentum }, {
            wheelMomentum = it; prefs.edit().putFloat("wheelMomentum", it).apply()
        }, 0.1f, 0.2f, 3.0f)
        val colorTitle = TextView(this).apply {
            textSize = 13f; setTextColor(Color.WHITE)
            text = "触摸板颜色"
            setPadding(0, 8, 0, 2)
        }
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

        sections.add(sectionCard(
            "指针与滚动",
            "触摸板模式的手感：光标快慢、滚动比例、抖动过滤、滚轮惯性。滚轮动量 = 抬手后惯性滚动的时长倍率。"
        ) {
            it.addView(speedRow)
            it.addView(scrollRow)
            it.addView(dzRow)
            it.addView(smoothBtn)
            it.addView(wheelModeBtn)
            it.addView(momentumRow)
            it.addView(colorTitle)
            it.addView(colorRow)
        })

        // ==================== 4. 按压识别 ====================
        // v1.4.8：从「触觉反馈」里独立出来，做成自己的专区 ——
        // 用户要的是「一个按压识别的开关 + 一组能自己调的参数」。
        val pressNote = TextView(this).apply {
            textSize = 11f; setTextColor(Color.parseColor("#FF9E9E9E"))
            text = "手指压得越重，接触面积越大。触发阈值 = 比平时的力度高多少算「按下去」；" +
                "确认帧数 = 连续超几帧才认（越小越灵敏、越容易误触）；" +
                "松开余量 = 力度要比触发线低这么多才算松手（越大越不容易松，但松得迟）。"
        }
        val pressDiagView = TextView(this).apply {
            textSize = 12f; setTextColor(Color.parseColor("#FF999999"))
            setPadding(0, dp(6), 0, 2)
            text = "点下面按钮看实时状态"
        }
        val pressOnBtn = Button(this).apply {
            fun refresh() { text = if (canvas.pressToDragEnabled) "按压识别：开" else "按压识别：关" }
            refresh()
            setOnClickListener {
                canvas.pressToDragEnabled = !canvas.pressToDragEnabled
                prefs.edit().putBoolean("pressDragOn", canvas.pressToDragEnabled).apply()
                if (!canvas.pressToDragEnabled) canvas.releasePressDrag("按压识别已关闭")
                refresh()
                pressDiagView.text = canvas.pressDiagText()
            }
        }
        val pressRiseRow = minusPlus("触发阈值", { canvas.pressAreaRisePct }, {
            canvas.pressAreaRisePct = it
            prefs.edit().putFloat("pressAreaRisePct", canvas.pressAreaRisePct).apply()
        }, 5f, 10f, 120f, "%.0f%%")
        val pressConfirmRow = minusPlus("确认帧数", { canvas.pressConfirmFrames.toFloat() }, {
            canvas.pressConfirmFrames = it.toInt()
            prefs.edit().putInt("pressConfirmFrames", canvas.pressConfirmFrames).apply()
        }, 1f, 1f, 8f, "%.0f 帧")
        val pressSlackRow = minusPlus("松开余量", { canvas.pressReleaseSlackPct }, {
            canvas.pressReleaseSlackPct = it
            prefs.edit().putFloat("pressReleaseSlackPct", canvas.pressReleaseSlackPct).apply()
        }, 5f, 0f, 15f, "%.0f%%")
        val pressPressureRow = minusPlus("压力阈值", { canvas.pressDownThreshold }, {
            canvas.pressDownThreshold = it
            // 松开阈值跟随（比按下低一档，形成滞回，避免在阈值附近抖动）
            canvas.pressUpThreshold = (it * 0.76f).coerceIn(0.05f, 0.9f)
            prefs.edit().putFloat("pressDownTh", canvas.pressDownThreshold)
                .putFloat("pressUpTh", canvas.pressUpThreshold).apply()
        }, 0.05f, 0.05f, 0.95f, "%.2f")
        val pressDiagBtn = Button(this).apply {
            text = "查看当前识别状态"
            setOnClickListener {
                val d = canvas.pressDiagText()
                pressDiagView.text = d
                Toast.makeText(this@MainActivity, d, Toast.LENGTH_LONG).show()
            }
        }

        sections.add(sectionCard("按压识别（重按 = 按下左键）", null) {
            it.addView(pressNote)
            it.addView(pressOnBtn)
            it.addView(pressRiseRow)
            it.addView(pressConfirmRow)
            it.addView(pressSlackRow)
            it.addView(pressPressureRow)
            it.addView(pressDiagView)
            it.addView(pressDiagBtn)
        })

        // ==================== 5. 手势绑定 ====================
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

        sections.add(sectionCard(
            "手势绑定",
            "点每一项切换它绑定的动作（手指几指、往哪个方向划）。"
        ) {
            it.addView(gestureRow("双指滑动", "g2Swipe", 1, intArrayOf(0, 1, 2)))
            it.addView(gestureRow("双指捏合", "g2Pinch", 2, intArrayOf(0, 1, 2, 10, 11, 12, 13, 14)))
            it.addView(gestureRow("双指点击", "g2Tap", 4, tapActions))
            it.addView(gestureRow("三指点击", "g3Tap", 5, tapActions))
            it.addView(gestureRow("三指右滑", "g3L", 7, swipeActions))
            it.addView(gestureRow("三指左滑", "g3R", 6, swipeActions))
            it.addView(gestureRow("三指上滑", "g3U", 5, swipeActions))
            it.addView(gestureRow("三指下滑", "g3D", 3, swipeActions))
            it.addView(gestureRow("四指点击", "g4Tap", 9, intArrayOf(0, 9)))
            it.addView(gestureRow("四指右滑", "g4L", 9, intArrayOf(0, 9)))
            it.addView(gestureRow("四指左滑", "g4R", 0, intArrayOf(0, 9)))
            it.addView(gestureRow("四指上滑", "g4U", 10, intArrayOf(0, 9)))
            it.addView(gestureRow("四指下滑", "g4D", 11, intArrayOf(0, 9)))
        })

        // ==================== 6. 触觉反馈 ====================
        val hapticsBtn = Button(this).apply {
            fun refresh() { text = if (canvas.hapticsEnabled) "震动反馈：开" else "震动反馈：关" }
            refresh()
            setOnClickListener {
                canvas.hapticsEnabled = !canvas.hapticsEnabled
                prefs.edit().putBoolean("hapticsOn", canvas.hapticsEnabled).apply()
                if (canvas.hapticsEnabled) Haptics.click()   // 打开时震一下，立刻知道生效了
                refresh()
            }
        }
        val hapticStrengthRow = minusPlus("震动强度", { hapticStrengthPct }, {
            hapticStrengthPct = it
            Haptics.strengthScale = it / 100f
            prefs.edit().putInt("hapticStrength", it.toInt()).apply()
            Haptics.click()      // 立刻震一下让你听到/感到新强度
        }, 10f, 0f, 200f, "%.0f%%")
        val hapticForceBtn = Button(this).apply {
            fun refresh() {
                text = if (Haptics.ignoreSystemLimit) "覆盖系统震动强度限制：开" else "覆盖系统震动强度限制：关"
            }
            refresh()
            setOnClickListener {
                Haptics.ignoreSystemLimit = !Haptics.ignoreSystemLimit
                prefs.edit().putBoolean("hapticForce", Haptics.ignoreSystemLimit).apply()
                Haptics.click()
                refresh()
            }
        }

        sections.add(sectionCard(
            "触觉反馈",
            "马达震动。Android 会按系统「触摸反馈强度」缩放，这里可以绕开它。"
        ) {
            it.addView(hapticsBtn)
            it.addView(hapticStrengthRow)
            it.addView(hapticForceBtn)
        })

        // ==================== 7. 显示与截图 ====================
        val shotScopeBtn = Button(this).apply {
            fun refresh() { text = if (saveFull) "截图范围：整块屏幕" else "截图范围：桌面范围框" }
            refresh()
            setOnClickListener {
                saveFull = !saveFull
                prefs.edit().putBoolean("saveFull", saveFull).apply()
                refresh()
            }
        }
        val shotPcBtn = Button(this).apply {
            fun refresh() { text = if (saveToPc) "截图存到电脑：开" else "截图存到电脑：关" }
            refresh()
            setOnClickListener {
                saveToPc = !saveToPc
                prefs.edit().putBoolean("saveToPc", saveToPc).apply()
                refresh()
            }
        }
        val shotPhoneBtn = Button(this).apply {
            fun refresh() { text = if (saveToPhone) "截图存到手机相册：开" else "截图存到手机相册：关" }
            refresh()
            setOnClickListener {
                saveToPhone = !saveToPhone
                prefs.edit().putBoolean("saveToPhone", saveToPhone).apply()
                refresh()
            }
        }
        val trailBtn = Button(this).apply {
            fun refresh() { text = if (canvas.showFingerTrail) "手指轨迹：显示" else "手指轨迹：隐藏" }
            refresh()
            setOnClickListener {
                canvas.showFingerTrail = !canvas.showFingerTrail
                prefs.edit().putBoolean("trailOn", canvas.showFingerTrail).apply()
                if (!canvas.showFingerTrail) canvas.clearTrail()
                refresh()
            }
        }
        val trailLifeRow = minusPlus("轨迹停留", { canvas.trailLifeMs.toFloat() }, {
            canvas.trailLifeMs = it.toLong()
            prefs.edit().putInt("trailLife", it.toInt()).apply()
        }, 100f, 200f, 3000f, "%.0f ms")

        sections.add(sectionCard(
            "显示与截图",
            "截屏（范围框内容 + 桌面笔迹 → PNG）与手指轨迹跟手提示。"
        ) {
            it.addView(shotScopeBtn)
            it.addView(shotPcBtn)
            it.addView(shotPhoneBtn)
            it.addView(trailBtn)
            it.addView(trailLifeRow)
        })

        // ==================== 8. 关于 ====================
        verText = TextView(this).apply {
            textSize = 12f
            setTextColor(Color.parseColor("#FF999999"))
            setPadding(0, 2, 0, 2)
            text = versionLine()
        }
        val updateBtn = Button(this).apply { text = "检查更新" }
        val homeBtn = Button(this).apply { text = "项目主页 / PC 端下载" }
        updateBtn.setOnClickListener {
            // 直接比对 GitHub Release 最新版本；有新版本则从 Release 下载并安装
            checkForUpdate(silent = false)
        }
        homeBtn.setOnClickListener {
            try {
                startActivity(android.content.Intent(
                    android.content.Intent.ACTION_VIEW,
                    android.net.Uri.parse("https://github.com/volcanochen/drawing-phone")))
            } catch (e: Throwable) { Log.w("draw&touch", "打开浏览器失败: $e") }
        }

        sections.add(sectionCard("关于", "版本信息、检查更新与项目主页。") {
            it.addView(verText)
            // 完整的「版本号 + code + 构建时间」放在这里（标题栏只留一行省略版）
            it.addView(TextView(this).apply {
                text = "安装包：v${BuildConfig.VERSION_NAME} (code ${BuildConfig.VERSION_CODE}) · 构建于 ${BuildConfig.BUILD_TIME}"
                textSize = 11f
                setTextColor(Color.parseColor("#FF888888"))
                setPadding(0, 2, 0, 2)
            })
            it.addView(updateBtn)
            it.addView(homeBtn)
        })

        // ==================== 事件绑定 ====================
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
                                "请确认：① PC 上已启动 DrawTouchHost；② 已开 USB 网络共享；" +
                                "③ PC 防火墙已放行该端口（PC 端配置窗口点「允许防火墙」）。"
                    }
                }
            }
        }
        discoverBtn.setOnClickListener {
            discoverBtn.isEnabled = false
            discoverBtn.text = "正在搜索局域网中的 PC…"
            ipText.text = "mDNS 搜索中…（PC 上需已启动 DrawTouchHost）"
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
            Log.i("draw&touch", "操作框已复位")
        }
        hoverBox.setOnCheckedChangeListener { _, checked ->
            canvas.sendHover = checked
            prefs.edit().putBoolean("hover", checked).apply()
        }
        minusBtn.setOnClickListener { setGain(canvas.pressureGain - 0.5f) }
        plusBtn.setOnClickListener { setGain(canvas.pressureGain + 0.5f) }

        return sections
    }

    /**
     * 设置页的一个分类卡片 —— 所有分类**统一结构**：
     *   ① 标题（分类名）
     *   ② 一句说明（可选，灰字小字）
     *   ③ 内容区（各项控件）
     *
     * 这样每类的视觉层次一致，扫一眼就知道每一块是干什么的。
     */
    private fun sectionCard(title: String, note: String?, build: (LinearLayout) -> Unit): LinearLayout {
        val card = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(12), dp(9), dp(12), dp(9))
            background = android.graphics.drawable.GradientDrawable().apply {
                cornerRadius = dp(10).toFloat()
                setColor(Color.parseColor("#FF2C2C2C"))
                setStroke(dp(1), Color.parseColor("#26FFFFFF"))
            }
        }
        card.addView(TextView(this).apply {
            text = title
            textSize = 14f
            setTextColor(Color.parseColor("#FF7FD1FF"))
            setPadding(0, 0, 0, 2)
        })
        if (!note.isNullOrBlank()) {
            card.addView(TextView(this).apply {
                text = note
                textSize = 11f
                setTextColor(Color.parseColor("#FF9E9E9E"))
                setPadding(0, 0, 0, 6)
            })
        }
        val body = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        build(body)
        card.addView(body)
        return card
    }

    /**
     * 按当前方向摆放分类卡片：
     * - **竖屏**：单列，一列到底（一张 ScrollView）
     * - **横屏**：**两列并排**，各自独立滚动（充分利用横向空间，也不用为了对齐高度而留空白）
     *
     * 横屏两列按「下标奇偶」分配：
     * 左列 = 连接 / 指针与滚动 / 手势绑定 / 显示与截图
     * 右列 = 模式与映射 / 按压识别 / 触觉反馈 / 关于
     * （两列各自独立滚动，短的一列滚到底就停，不会被另一列拖着走）
     */
    private fun layoutSettingsSections(sections: List<LinearLayout>, land: Boolean) {
        settingsBody.removeAllViews()
        if (!land) {
            val col = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
            sections.forEach { col.addView(it, cardLayoutParams()) }
            settingsBody.addView(ScrollView(this).apply {
                isFillViewport = true
                addView(col)
            })
            return
        }
        val left = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        val right = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        sections.forEachIndexed { i, card ->
            (if (i % 2 == 0) left else right).addView(card, cardLayoutParams())
        }
        val cols = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        cols.addView(columnScroll(left), LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MATCH_PARENT, 1f))
        cols.addView(columnScroll(right), LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MATCH_PARENT, 1f))
        settingsBody.addView(cols)
    }

    /** 一列（横屏时用）：自带滚动，卡片之间留白 */
    private fun columnScroll(col: LinearLayout): ScrollView = ScrollView(this).apply {
        isFillViewport = true
        setPadding(0, 0, dp(4), 0)
        addView(col)
    }

    /** 分类卡片之间的间距（竖屏横屏一致） */
    private fun cardLayoutParams(): LinearLayout.LayoutParams =
        LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT)
            .apply { bottomMargin = dp(10) }

    /**
     * 重新构建设置页内容（打开设置页 / 转屏后都要重跑）。
     *
     * [land]：横竖屏判定。**转屏时必须显式传**，因为在 onConfigurationChanged 回调里
     * `resources.configuration` 还可能是**旧方向**，照它判断会把竖屏重建成两列
     * （症状：转屏后设置页变成两条被压扁的窄列）。
     * 不传时按当前 resources 判定（打开设置页的正常路径）。
     */
    private fun rebuildSettings(land: Boolean? = null) {
        // 默认值迁移 / 手势切换后的最新值要反映到界面上
        applyTrackpadPrefs()
        val sections = buildSettingsControls()
        layoutSettingsSections(
            sections,
            land ?: (resources.configuration.orientation == android.content.res.Configuration.ORIENTATION_LANDSCAPE)
        )
    }

    // ---------- 设置页开关 ----------
    /** 设置页底部的版本信息行（打开时刷新，PC 版本来自 INFO 帧） */
    private lateinit var verText: TextView

    private fun versionLine(): String {
        val verName = try {
            packageManager.getPackageInfo(packageName, 0).versionName ?: "?"
        } catch (_: Throwable) { "?" }
        return "draw&touch 手机端 v$verName｜PC 端 v$pcVersion（$pcName）"
    }

    /**
     * 检查 GitHub Release 是否有新版本（对比 latest 的 tag）。
     * [silent]=true（开机静默）：仅发现更新时弹窗，已最新/出错不打扰。
     * [silent]=false（手动点按钮）：无论有无更新都给反馈。
     * 注：[UpdateChecker.check] 的回调已在主线程，这里直接操作 UI 即可。
     */
    private fun checkForUpdate(silent: Boolean) {
        UpdateChecker.check(this) { info ->
            if (info != null) {
                offerUpdate(info)
            } else if (!silent) {
                android.widget.Toast.makeText(this, "已是最新版本", android.widget.Toast.LENGTH_SHORT).show()
            }
        }
    }

    /** 发现新版本：弹窗询问是否下载安装（避免不经同意就下载大文件） */
    private fun offerUpdate(info: UpdateChecker.UpdateInfo) {
        val notes = if (info.notes.isBlank()) "" else "\n\n更新内容：\n${info.notes.take(600)}"
        android.app.AlertDialog.Builder(this)
            .setTitle("发现新版本 v${info.version}")
            .setMessage("当前版本 ${UpdateChecker.currentVersion(this)}，可更新到 v${info.version}。$notes")
            .setPositiveButton("下载并安装") { _, _ -> doUpdate(info) }
            .setNegativeButton("稍后", null)
            .show()
    }

    /** 从 GitHub Release 下载 APK 并安装，过程中用进度弹窗显示状态 */
    private fun doUpdate(info: UpdateChecker.UpdateInfo) {
        val msg = TextView(this).apply {
            text = "准备中…"
            textSize = 14f
            setTextColor(Color.WHITE)
            setPadding(dp(20), dp(16), dp(20), dp(16))
        }
        android.app.AlertDialog.Builder(this)
            .setTitle("更新到 v${info.version}")
            .setView(msg)
            .setCancelable(false)
            .setNegativeButton("后台进行") { d, _ -> d.dismiss() }
            .show()
        UpdateChecker.downloadAndInstall(this, info) { status ->
            runOnUiThread { msg.text = status }
        }
    }

    private fun openSettings() {
        if (::verText.isInitialized) verText.text = versionLine()
        // 重建一遍设置项：默认值迁移 / 手势切换后的最新值要反映到界面上
        rebuildSettings()
        settingsPage.visibility = View.VISIBLE
        stopInertia()
        updateModeUI()   // 触摸板浮动按钮（滚轮条/三键）在设置页里要藏起来
    }

    private fun closeSettings() {
        settingsPage.visibility = View.GONE
        updateModeUI()
    }

    override fun onResume() {
        super.onResume()
        // 回到前台先把手势中间态清零。
        // 锁屏 / 切应用 / 下拉通知栏时系统可能吞掉「手指抬起 / ACTION_CANCEL」，
        // 残留的 tpActiveFingers 会让之后所有笔事件被"笔让位"逻辑吃掉，
        // 表现为「回来手机就动不了了」。
        if (::canvas.isInitialized) canvas.resetGestures()
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
        canvas.resetGestures() // 清手势中间态 + 补发左键抬起/手指离开，防切换瞬间按键卡死
        prefs.edit().putInt("mode", m).apply()
        if (::net.isInitialized) {
            net.send(Protocol.buildMode(m))
        }
        // 备注跟随模式走：
        //   数位板 → 恢复上次选的模式（没选过则默认「备注」：笔直接画在桌面框内）
        //   触摸板 → 关掉备注（备注是桌面的笔迹层，和光标控制互斥）
        if (m == Protocol.MODE_TABLET) {
            toggleNote(prefs.getBoolean("noteOn", true))
        } else if (noteOn) {
            toggleNote(false)
        }
        updateModeBtn()
        updateModeUI()
        Log.i("draw&touch", "切换工作模式 -> ${if (m == Protocol.MODE_MOUSE) "触摸板" else "数位板"}")
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
        // 模式切换后按钮显隐会变，顺手把可能残留的悬浮气泡收掉
        if (::hoverTip.isInitialized) hoverTip.hide()
    }

    /** 模式按钮图标 = 当前模式（状态与按钮融合） */
    private fun updateModeBtn() {
        val mouse = workMode == Protocol.MODE_MOUSE
        modeBtn.setImageResource(if (mouse) R.drawable.ic_mouse else R.drawable.ic_pen)
        modeBtn.contentDescription = if (mouse) "当前：触摸板模式（点击切换为数位板）"
                                   else "当前：数位板模式（点击切换为触摸板）"
    }

    /** 问号按钮：**只显示当前模式**的指南（触摸板手势为当前生效配置）；「更多」里才是全部 */
    private fun showGuide() {
        val mouse = workMode == Protocol.MODE_MOUSE
        val html = if (mouse) trackpadGuideHtml() else tabletGuideHtml()
        val tv = guideTextView(html)
        val scroll = android.widget.ScrollView(this).apply { addView(tv) }
        android.app.AlertDialog.Builder(this)
            .setTitle("指南 · ${if (mouse) "触摸板" else "数位板"}模式")
            .setView(scroll)
            // 「更多」= 完整指南（连接说明 + 两种模式 + PC 下载）
            .setNeutralButton("更多指南…") { _, _ -> showFullGuide() }
            .setPositiveButton("知道了", null)
            .show()
    }

    /** 完整指南：连接说明 + 两种模式（当前模式排前）+ 屏幕方向 + PC 下载 */
    private fun showFullGuide() {
        val link = "https://github.com/volcanochen/drawing-phone/releases"
        val mouse = workMode == Protocol.MODE_MOUSE
        val cur = if (mouse) "（当前模式）" else ""
        val trackpad = trackpadGuideHtml().replace("【触摸板模式】", "【触摸板模式$cur】")
        val tablet = tabletGuideHtml().replace("【数位板模式】", "【数位板模式$cur】")
        val tail = """
            <b>【屏幕方向】</b>循环按钮：跟随系统 / 横屏双朝向 / 竖屏双朝向<br><br>
            <b>【PC 端下载】</b>（点击打开）：<br>
            <a href="$link">$link</a>
        """.trimIndent()
        val common = """
            <b>【自动连接（通常什么都不用做）】</b><br>
            · PC 端启动后，打开本 App 就会<b>自动连上</b>（mDNS 自动发现）<br>
            · 左上角 <b><font color="#33DD55">绿点</font></b> = 已连接，<b><font color="#FF4444">红点</font></b> = 未连接，
              <b><font color="#FFC107">黄点</font></b> = PC 端版本不兼容、已停止对接（把两端都更新到同一版本）<br>
            · 点左上角圆点可查看<b>网络拓扑</b>、切换桌面 PC<br><br>
            <b>【需要手动操作的场合（很少）】</b><br>
            · <b>切换到另一台 PC</b>：点绿点 → 「局域网里的桌面」点击切换<br>
            · 局域网里有<b>多台电脑</b>时，首次会弹框让你选；之后默认连你选过的那台<br>
            · <b>自动发现失败</b>（路由器禁用组播等）：点齿轮 → 填 IP → 连接，<br>
              　或用「扫描网段找 PC」；USB 数据线 + adb 转发时填 127.0.0.1<br><br>
        """.trimIndent()
        // 当前模式的章节放最前面：打开就该先看到「我现在怎么用」
        val html = common + (if (mouse) trackpad + tablet else tablet + trackpad) + tail
        val scroll = android.widget.ScrollView(this).apply { addView(guideTextView(html)) }
        android.app.AlertDialog.Builder(this)
            .setTitle("使用指南 · ${if (mouse) "触摸板" else "数位板"}模式")
            .setView(scroll)
            .setPositiveButton("知道了", null)
            .show()
    }

    /** 触摸板模式章节：手势动作读 canvas 上的**生效值**（设置页改完立即同步） */
    private fun trackpadGuideHtml(): String {
        val an = { code: Int -> canvas.actionName(code) }
        return """
            <b>【触摸板模式】（手指 = 触摸板）</b><br>
            · 单指滑动 = <b>移动光标</b>；<b>单击 = 左键；双击 = 双击</b>（不再是右键）<br>
            · <b>双击后不松手再拖</b> = 按住左键拖动 → 拖文件、框选文字（和笔记本触摸板一样）<br>
            · 双指滑动 = <b>${an(canvas.g2Swipe)}</b>（自动锁定滚动方向：横滑不会误滚上下）；双指捏合 = <b>${an(canvas.g2Pinch)}</b><br>
            · 双指点击 = <b>${an(canvas.g2Tap)}</b>；三指点击 = <b>${an(canvas.g3Tap)}</b><br>
            · 三指　右滑=<b>${an(canvas.g3L)}</b>　左滑=<b>${an(canvas.g3R)}</b>　上滑=<b>${an(canvas.g3U)}</b>　下滑=<b>${an(canvas.g3D)}</b><br>
            · 四指　点击=<b>${an(canvas.g4Tap)}</b>　右滑=<b>${an(canvas.g4L)}</b>　左滑=<b>${an(canvas.g4R)}</b>　上滑=<b>${an(canvas.g4U)}</b>　下滑=<b>${an(canvas.g4D)}</b><br>
            · 左右滑动有优先判定：上下要有<b>明显更大</b>的幅度才算上下（横滑带一点漂移不会误触发）<br>
            · 右侧滚轮条滑动 = 滚动，<b>双击滚轮条</b>切换普通滚动 / 无极滚动（动量可在设置里调）<br>
            · 底部<b>可拖动浮动面板</b> = 激光笔 + 左 / 中 / 右键（挡住画面就按住拖走，位置会记住）<br>
            · 点「激光笔」开/关：<b>桌面所有位置都保持激光笔图标</b>（包括输入框），走过的地方留下半透明荧光痕迹；长按换颜色（黄/绿/粉/蓝）<br>
            · 浮动键被手势/手指触发时都会在手机端闪一下，方便看清「打中了哪个键」<br>
            · <b>触摸板模式下 S Pen 不参与</b>：笔碰屏幕 / 悬停都不会动光标，光标只由手指控制<br>
            · 手势动作可在设置里自定义（改完这里同步显示）<br>
        """.trimIndent()
    }

    /** 数位板模式章节 */
    private fun tabletGuideHtml(): String {
        return """
            <b>【数位板模式】（S Pen 作画）</b><br>
            · 笔在橙色框内 = 在 PC 上作画（压感 / 倾斜）<br>
            · <b>默认是「备注」模式</b>：笔迹直接画在桌面的框里，手机与桌面同步<br>
            · 点底部<b>备注</b>按钮切到<b>「画笔」模式</b>：笔迹只画在手机上、定时淡出，桌面不受影响；再点一下切回「备注」<br>
            · 单指滑动 = 移动范围框；双指捏合 = 缩放范围框<br>
            · 框可以拖到任意显示器（含多屏 / 多桌面）<br>
            · 底部浮动圆钮：<b>备注 / 画笔</b>（切模式）/ <b>撤销</b>（点击手机+桌面各撤一笔、长按两边一起清空）/ <b>$shotBtnLabel</b>（预览：桌面框内容设为手机画布背景，可继续在上面画；再点「完成」恢复）/ <b>截屏</b>（桌面+笔迹存成图片，可在设置里选存到电脑/手机相册）<br>
            · 浮动面板 = <b>备注 / 撤销 / 预览</b> + 7 色板，<b>按住可整体拖动</b>（挡住画面就拖走，位置会记住）<br>
            · 换色只影响之后画的笔；<b>笔悬停在按钮上会弹出该按钮的说明</b><br>
        """.trimIndent()
    }

    /** 指南正文的统一样式 */
    private fun guideTextView(html: String): TextView {
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
        return tv
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
        // 协议兼容性检查：PC 报上来的 pv 与本机不一致 → 黄灯警告 + 停止后续对接。
        // 版本对不上的两端硬连，只会出现「手势错乱 / 光标瞬移」这类查不出原因的怪象。
        pcProtoVer = map["pv"]?.toIntOrNull() ?: 0
        if (pcProtoVer != Protocol.PROTO_VER) {
            protoMismatch = "PC 端版本不兼容（协议 v$pcProtoVer ≠ 本机 v${Protocol.PROTO_VER}），已停止对接，请更新另一端"
            if (::net.isInitialized) net.holdReason = protoMismatch
            DebugLog.write("[ver] $protoMismatch")
        } else if (protoMismatch != null) {
            protoMismatch = null
            if (::net.isInitialized) net.holdReason = null
        }
        Log.i("draw&touch", "PC INFO → ver=$pcVersion pv=$pcProtoVer name=$pcName ips=$pcIps")
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
        // 转屏后父容器尺寸变了：把可拖动面板按记住的**比例**重新定位（restore 内部会钳制在可视范围内），
        // 没拖过的面板（gravity 定位）自动适应，不受影响
        if (::trackpadDock.isInitialized) restorePanelPos(trackpadDock, "tpDock")
        if (::tabletToolbar.isInitialized) restorePanelPos(tabletToolbar, "toolbar")
        // 设置页在竖屏是单列、横屏是两列 → 转屏后要重排（设置页开着时才重建）
        if (::settingsPage.isInitialized && settingsPage.visibility == View.VISIBLE) {
            rebuildSettings(land)   // 必须用 newConfig 的方向，不能读 resources.configuration
        }
    }

    /** 方向说明文字（设置页/提示用） */
    private fun orientText(mode: Int) = when (mode) {
        1 -> "屏幕方向：横屏（陀螺仪在两个横屏朝向间自动翻转，点此切换）"
        2 -> "屏幕方向：竖屏（陀螺仪在两个竖屏朝向间自动翻转，点此切换）"
        else -> "屏幕方向：跟随系统（听系统的自动旋转开关，点此切换）"
    }

    private fun applyOrientationMode() {
        requestedOrientation = when (orientMode) {
            // 横屏/竖屏均带 SENSOR：同组两个朝向由陀螺仪自动切换
            1 -> android.content.pm.ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
            2 -> android.content.pm.ActivityInfo.SCREEN_ORIENTATION_SENSOR_PORTRAIT
            // v1.4.9：「跟随系统」改成 FULL_USER（听系统的自动旋转开关）。
            // 原来用 FULL_SENSOR = 无视系统设置、永远跟陀螺仪 —— 手机平放在桌上
            // 会自己横过来，触摸板场景下很烦；而且名不副实（叫「跟随系统」却不跟系统）。
            else -> android.content.pm.ActivityInfo.SCREEN_ORIENTATION_FULL_USER
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
                        Haptics.action()   // 模式切换确认震动
                        DebugLog.write("[wheel] ${if (wheelInertia) "无极滚动" else "普通滚动"}")
                    }
                    wheelLastDownAt = now
                    stopInertia()
                    // 速度追踪：无极滚动以**手指离开当下**的速度为基础继续滚
                    wheelVelocity?.recycle()
                    wheelVelocity = android.view.VelocityTracker.obtain()
                    wheelVelocity?.addMovement(ev)
                    v.tag = ev.y
                    true
                }
                MotionEvent.ACTION_MOVE -> {
                    wheelVelocity?.addMovement(ev)
                    val last = (v.tag as? Float) ?: ev.y
                    val dy = ev.y - last
                    v.tag = ev.y
                    if (canvas.height > 0) {
                        canvas.sendWheel(dy / canvas.height)
                        strip.scrollBy(dy)      // 横纹跟着手指走，像真的滚轮
                        Haptics.scrollTick()     // 与双指滚动同一套细震，手感一致
                    }
                    true
                }
                MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                    // 抬手瞬间用 VelocityTracker 取**最近 100ms** 的滑动速度——
                    // 以前用「最后一次 MOVE 事件的瞬时差」，手指最后一下的位移经常丢，
                    // 甩得快慢和惯性对不上。现在初始速度就是离开当下的真实速度。
                    wheelVelocity?.addMovement(ev)
                    wheelVelocity?.computeCurrentVelocity(1000)   // 单位：px / 秒
                    val vy = wheelVelocity?.yVelocity ?: 0f
                    wheelVelocity?.recycle()
                    wheelVelocity = null
                    if (canvas.height > 0) {
                        // 归一化为「每 16ms 滚过的书写区高度」，并封顶防止暴力一甩滚飞
                        wheelVel = (vy / canvas.height * 0.016f).coerceIn(-0.2f, 0.2f)
                        if (wheelInertia && kotlin.math.abs(wheelVel) > 0.0005f) startInertia()
                    }
                    true
                }
                else -> true
            }
        }
        wheel.inertia = wheelInertia

        // —— 底部浮动操作面板：激光笔 + 左 / 中 / 右 键合并在一起，整体可拖动 ——
        // 挡住画面时按住面板任意位置拖走（位置按父容器比例记住，下次还原），
        // 单指点击（不超 slop）则照常触发对应按钮。
        trackpadDock = DraggablePanel(this, "tpDock").apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            background = android.graphics.drawable.GradientDrawable().apply {
                setColor(Color.parseColor("#99222222"))
                cornerRadius = dp(12).toFloat()
                setStroke(1, Color.parseColor("#66FFFFFF"))
            }
            setPadding(dp(4), dp(4), dp(4), dp(4))
            layoutParams = FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT,
                Gravity.BOTTOM or Gravity.CENTER_HORIZONTAL).apply { bottomMargin = dp(10) }
        }
        // 激光笔按钮：点一下开/关（桌面指针变成激光笔）；长按换颜色（黄 → 绿 → 粉 → 蓝）
        val hl = Button(this).apply {
            text = "激光笔"
            layoutParams = LinearLayout.LayoutParams(dp(72), dp(48))
                .apply { setMargins(dp(4), 0, dp(4), 0) }
            background = floatBtnBg()
            setTextColor(Color.WHITE)
            setOnClickListener { toggleHighlighter(); flashButton(this) }
            setOnLongClickListener { cycleHighlighterColor(); true }
        }
        // 「左」键：按下即「左键按下」，抬起才「左键抬起」——
        // 按住不放再移动手指 = 拖拽（激光笔模式下就是拉着一支激光笔涂）。
        // 拖动超 slop 时面板会拦截、整体移动；此时 ACTION_CANCEL 会抬起左键，不会卡住。
        val leftBtn = Button(this).apply {
            text = "左"
            layoutParams = LinearLayout.LayoutParams(dp(64), dp(64))
                .apply { setMargins(dp(4), 0, dp(4), 0) }
            background = floatBtnBg()
            setTextColor(Color.WHITE)
            setOnTouchListener { _, ev ->
                when (ev.actionMasked) {
                    MotionEvent.ACTION_DOWN -> { flashButton(this); canvas.sendLeftDown(); true }
                    MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> { canvas.sendLeftUp(); true }
                    else -> true
                }
            }
        }
        val midBtn = Button(this).apply {
            text = "中"
            layoutParams = LinearLayout.LayoutParams(dp(64), dp(64))
                .apply { setMargins(dp(4), 0, dp(4), 0) }
            background = floatBtnBg()
            setTextColor(Color.WHITE)
            setOnClickListener { flashButton(this); canvas.sendClick(Protocol.MOUSE_MIDDLE_CLICK) }
        }
        val rightBtn = Button(this).apply {
            text = "右"
            layoutParams = LinearLayout.LayoutParams(dp(64), dp(64))
                .apply { setMargins(dp(4), 0, dp(4), 0) }
            background = floatBtnBg()
            setTextColor(Color.WHITE)
            setOnClickListener { flashButton(this); canvas.sendClick(Protocol.MOUSE_RIGHT_CLICK) }
        }
        hlBtn = hl
        tLeftBtn = leftBtn
        tMidBtn = midBtn
        tRightBtn = rightBtn
        trackpadDock.addView(hl)
        trackpadDock.addView(leftBtn)
        trackpadDock.addView(midBtn)
        trackpadDock.addView(rightBtn)
        // 悬浮说明
        hoverTip.attach(hl) { "激光笔：PC 指针变激光光斑（点击开关，长按换色）" }
        hoverTip.attach(leftBtn) { "左键：按下不放再移动 = 拖拽 / 涂改" }
        hoverTip.attach(midBtn) { "中键：多数程序里点击链接开新标签页" }
        hoverTip.attach(rightBtn) { "右键：打开上下文菜单" }
        hoverTip.attach(wheel) {
            "滚轮条：上下滑 = 滚动；双击切换 普通滚动 / 无极滚动"
        }

        trackpadOverlay.addView(wheel)
        trackpadOverlay.addView(trackpadDock)

        root.addView(trackpadOverlay, FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
        restorePanelPos(trackpadDock, "tpDock")
        updateHighlighterBtn()
    }

    // ================= 激光笔（触摸板模式）=================
    /** 激光笔是否开启 */
    private var highlighterOn = false
    /** 激光笔颜色索引（0 黄 / 1 绿 / 2 粉 / 3 蓝） */
    private var highlighterColor = 0
    /** 激光笔颜色（与 PC 端 HighlighterOverlay.Presets 一致） */
    private val hlColors = intArrayOf(
        0xFFEBEB3C.toInt(),  // 黄
        0xFF50F078.toInt(),  // 绿
        0xFFFF6EBE.toInt(),  // 粉
        0xFF5ABEFF.toInt()   // 蓝
    )

    /** 开关激光笔（桌面指针变成激光笔笔迹） */
    private fun toggleHighlighter() {
        highlighterOn = !highlighterOn
        prefs.edit().putBoolean("hlOn", highlighterOn).apply()
        sendHighlighter()
        updateHighlighterBtn()
    }

    /** 长按：换激光笔颜色 */
    private fun cycleHighlighterColor() {
        highlighterColor = (highlighterColor + 1) % hlColors.size
        prefs.edit().putInt("hlColor", highlighterColor).apply()
        sendHighlighter()
        updateHighlighterBtn()
    }

    private fun sendHighlighter() {
        if (::net.isInitialized) net.send(Protocol.buildHighlighter(highlighterOn, highlighterColor))
        DebugLog.write("[hl] 激光笔 ${if (highlighterOn) "开" else "关"}｜色 #$highlighterColor")
    }

    /** 按钮外观：开启时染成当前激光笔颜色，一眼能看出开没开 */
    private fun updateHighlighterBtn() {
        if (!::hlBtn.isInitialized) return
        if (highlighterOn) {
            hlBtn.text = "荧光✓"
            hlBtn.background = android.graphics.drawable.GradientDrawable().apply {
                setColor(hlColors[highlighterColor])
                cornerRadius = dp(32).toFloat()
                setStroke(2, Color.WHITE)
            }
            hlBtn.setTextColor(Color.BLACK)
        } else {
            hlBtn.text = "激光笔"
            hlBtn.background = floatBtnBg()
            hlBtn.setTextColor(Color.WHITE)
        }
    }

    // ---- 无极滚动（惯性）----
    private var wheelLastDownAt = 0L
    /** 滚轮条的速度追踪器：抬手瞬间取最近 100ms 的真实滑动速度，作为惯性初速 */
    private var wheelVelocity: android.view.VelocityTracker? = null
    /** 抬手瞬间的滚动速度（归一化/16ms） */
    private var wheelVel = 0f
    private val inertiaHandler = android.os.Handler(android.os.Looper.getMainLooper())
    /** 惯性开始时刻 */
    private var inertiaStartAt = 0L
    /** 本轮惯性的起始速度（按缓出曲线衰减到 0） */
    private var inertiaBaseVel = 0f
    /** 本轮惯性的总时长(ms)：默认 1.0 倍动量 = 3 秒 */
    private var inertiaDurationMs = 3000L

    private val inertiaTask = object : Runnable {
        override fun run() {
            if (!wheelInertia) { stopInertia(); return }
            val t = android.os.SystemClock.uptimeMillis() - inertiaStartAt
            if (t >= inertiaDurationMs) { wheelVel = 0f; return }   // 到点精确停下
            // 缓出曲线 (1-t/T)^2：先快后慢，T 时刻速度正好为 0 —— 抬手后 3 秒稳稳停下
            val remain = 1f - t.toFloat() / inertiaDurationMs
            canvas.sendWheel(inertiaBaseVel * remain * remain)
            Haptics.scrollTick()      // 惯性滚动期间同样有细震（跟手到停）
            inertiaHandler.postDelayed(this, inertiaTickMs)
        }
    }

    private fun startInertia() {
        // 动量倍率同时决定惯性时长：1.0 → 抬手 3 秒滑停（0.2 → 0.6s，3.0 → 9s）
        inertiaDurationMs = (inertiaBaseMs * wheelMomentum.coerceIn(0.2f, 3.0f)).toLong()
        inertiaBaseVel = wheelVel
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

    /** 闪烁反馈用的高亮底（覆盖原背景一小会儿，让人看清「这个键被按了 / 打中了」） */
    private fun flashBg() = android.graphics.drawable.GradientDrawable().apply {
        setColor(Color.parseColor("#FFCC33"))
        cornerRadius = dp(32).toFloat()
        setStroke(2, Color.WHITE)
    }

    /**
     * 在浮动按钮上做一下闪烁高亮（约 180ms），用于「点击 / 手势触发」的可见反馈。
     * [flashBase] 记下按钮的「真实底色」：同一按钮在 180ms 内被连续触发时不会把闪烁底
     * 当成底色，因此快速连点也不会卡在亮色；还原时始终回到真实底色。
     */
    private val flashBase = java.util.WeakHashMap<View, android.graphics.drawable.Drawable>()

    private fun flashButton(v: View) {
        // 真的按下了 → 悬浮气泡立刻收掉（笔尖触屏时部分机型不发 HOVER_EXIT）
        if (::hoverTip.isInitialized) hoverTip.hide()
        val base = flashBase[v] ?: (v.background as android.graphics.drawable.Drawable).also { flashBase[v] = it }
        val flash = flashBg()
        v.background = flash
        v.postDelayed({
            if (v.background === flash) {
                v.background = base
                flashBase.remove(v)
            }
        }, 180)
    }

    /**
     * 手势触发的鼠标动作 → 在手机端对应的浮动键上闪一下，让人看清「这次手势打中了哪个键」。
     * 仅鼠标按钮类动作（左/中/右）有对应浮动键；音量/任务等无对应键则不打闪。
     */
    private fun flashActionVisual(code: Int) {
        val btn = when (code) {
            Protocol.MOUSE_LEFT_CLICK, Protocol.MOUSE_LEFT_DOUBLE -> tLeftBtn
            Protocol.MOUSE_MIDDLE_CLICK -> tMidBtn
            Protocol.MOUSE_RIGHT_CLICK -> tRightBtn
            else -> null
        }
        btn?.let { flashButton(it) }
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
        // 备注按钮 + 撤销/完成 + 7 色板合并成**一个**浮动面板，整体可拖动，
        // 挡住画笔区域时直接拖走（位置会记住）
        tabletToolbar = DraggablePanel(this).apply {
            orientation = LinearLayout.VERTICAL
            background = android.graphics.drawable.GradientDrawable().apply {
                setColor(Color.parseColor("#B0222222"))
                cornerRadius = dp(14).toFloat()
                setStroke(1, Color.parseColor("#66FFFFFF"))
            }
            setPadding(dp(6), dp(6), dp(6), dp(6))
            layoutParams = FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT,
                Gravity.BOTTOM or Gravity.CENTER_HORIZONTAL).apply { bottomMargin = dp(10) }
        }
        // —— 第一行：备注 / 撤销 / 预览 ——
        val btnRow = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        fun toolBtn(label: String): Button = Button(this).apply {
            text = label
            layoutParams = LinearLayout.LayoutParams(dp(66), dp(56))
                .apply { setMargins(dp(4), 0, dp(4), 0) }
            background = floatBtnBg()
            setTextColor(Color.WHITE)
        }
        val nb = toolBtn("备注").apply {
            setOnClickListener { flashButton(this); toggleNote(!noteOn) }
        }
        noteBtn = nb
        undoBtn = toolBtn("撤销").apply {
            setOnClickListener {
                flashButton(this)
                canvas.undoStroke()                              // 手机屏同步撤一笔
                net.send(Protocol.buildNote(Protocol.NOTE_UNDO)) // PC 端桌面撤一笔
            }
            setOnLongClickListener {
                canvas.clear()                                    // 手机屏同步清空
                net.send(Protocol.buildNote(Protocol.NOTE_CLEAR)) // PC 端桌面清空
                true
            }
        }
        shotBtn = toolBtn("预览").apply {
            setOnClickListener { flashButton(this); onShotButton() }
        }
        saveBtn = toolBtn("截屏").apply {
            setOnClickListener { flashButton(this); onSaveButton() }
        }
        btnRow.addView(noteBtn)
        btnRow.addView(undoBtn)
        btnRow.addView(shotBtn)
        btnRow.addView(saveBtn)
        // 悬浮说明（文案随当前状态现取：备注/画笔、预览/完成）
        hoverTip.attach(noteBtn) {
            if (noteOn) "「备注」模式：笔迹直接画在 PC 桌面上。点击切到「画笔」"
            else "「画笔」模式：笔迹只画在手机上。点击切到「备注」"
        }
        hoverTip.attach(undoBtn) { "撤销一笔（长按 = 清空手机与桌面全部笔迹）" }
        hoverTip.attach(shotBtn) {
            if (shotCaptured) "「完成」：退出预览，清掉背景并解除范围框锁定"
            else "「预览」：把桌面范围框的内容设为手机画布背景"
        }
        hoverTip.attach(saveBtn) { saveBtnTip() }
        // 启动时按当前状态定文案（默认数位板会开备注，但可能是切的「画笔」）
        updateNoteBtn()
        updateInkModeButtons()
        // —— 第二行：7 个笔迹颜色块 ——
        val colorRow = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER
        }
        noteColorRow = colorRow
        noteColorIdx = prefs.getInt("noteColor", 0).coerceIn(0, noteColors.lastIndex)
        // 手机屏上画的笔迹也用同一个颜色（落笔时锁定，之后换色不影响已画的笔）
        canvas.strokeColor = noteColors[noteColorIdx]
        repeat(noteColors.size) { i ->
            val sw = View(this).apply {
                layoutParams = LinearLayout.LayoutParams(dp(26), dp(26)).apply {
                    setMargins(dp(3), dp(3), dp(3), dp(2))
                }
                background = swatchBg(i)
                setOnClickListener { flashButton(this); selectNoteColor(i) }
            }
            hoverTip.attach(sw) { "笔迹颜色 ${colorName(i)}（只影响之后画的笔）" }
            colorRow.addView(sw)
        }
        tabletToolbar.addView(btnRow)
        tabletToolbar.addView(colorRow)
        tabletToolbar.visibility = View.GONE
        root.addView(tabletToolbar)
        restorePanelPos(tabletToolbar, "toolbar")
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

    /** 色块名字（悬浮提示里用，顺序与 [noteColors] 一一对应） */
    private val noteColorNames = arrayOf("红", "橙", "黄", "绿", "蓝", "紫", "白")
    private fun colorName(idx: Int): String =
        noteColorNames.getOrElse(idx) { "#$idx" }

    /** 色块外观：圆角方块，选中的加白描边 */
    private fun swatchBg(idx: Int) = android.graphics.drawable.GradientDrawable().apply {
        setColor(noteColors[idx])
        cornerRadius = dp(5).toFloat()
        setStroke(if (idx == noteColorIdx) dp(3) else 1,
            if (idx == noteColorIdx) Color.WHITE else Color.parseColor("#99777777"))
    }

    /** 恢复上次拖动到的面板位置（按父容器比例存，转屏后按新尺寸换算） */
    private fun restorePanelPos(view: View, prefKey: String) {
        val xf = prefs.getFloat("${prefKey}XF", -1f)
        val yf = prefs.getFloat("${prefKey}YF", -1f)
        if (xf < 0f || yf < 0f) return
        view.post {
            val p = view.parent as? ViewGroup ?: return@post
            if (p.width <= 0 || p.height <= 0 || view.width <= 0) return@post
            val lp = view.layoutParams as FrameLayout.LayoutParams
            lp.gravity = Gravity.NO_GRAVITY
            lp.leftMargin = (xf * p.width).toInt().coerceIn(0, (p.width - view.width).coerceAtLeast(0))
            lp.topMargin = (yf * p.height).toInt().coerceIn(0, (p.height - view.height).coerceAtLeast(0))
            view.layoutParams = lp
        }
    }

    /** 选中一个笔迹颜色：高亮该色块，并把颜色索引发给 PC */
    private fun selectNoteColor(idx: Int) {
        noteColorIdx = idx
        noteColorRow?.let { row ->
            for (i in 0 until row.childCount) row.getChildAt(i).background = swatchBg(i)
        }
        prefs.edit().putInt("noteColor", idx).apply()
        // 手机端本地笔迹同步换色（只影响之后落的新笔），并下发索引给 PC 端叠加层
        canvas.strokeColor = noteColors[idx]
        if (::net.isInitialized) net.send(Protocol.buildNote(Protocol.NOTE_COLOR, idx.toFloat()))
        DebugLog.write("[note] 笔迹颜色 → 预设 #$idx")
    }

    /**
     * 可拖动的浮动面板：整体按住拖走（超过触摸阈值才判定为拖动，否则按钮照常点击）。
     * 拖动结束后把位置按父容器比例存进 prefs，下次启动还原。
     * [prefKey] 区分不同面板（数位板工具条 = "toolbar"，激光笔按钮 = "hl"）。
     */
    private inner class DraggablePanel(
        context: android.content.Context,
        private val prefKey: String = "toolbar"
    ) : LinearLayout(context) {
        private var downRawX = 0f
        private var downRawY = 0f
        private var startX = 0
        private var startY = 0
        private var dragging = false
        private val slop = android.view.ViewConfiguration.get(context).scaledTouchSlop

        override fun onInterceptTouchEvent(ev: MotionEvent): Boolean {
            when (ev.actionMasked) {
                MotionEvent.ACTION_DOWN -> {
                    downRawX = ev.rawX; downRawY = ev.rawY
                    startX = left; startY = top      // 相对父容器的当前位置（拖动基准）
                    dragging = false
                }
                MotionEvent.ACTION_MOVE -> {
                    if (!dragging && (kotlin.math.abs(ev.rawX - downRawX) > slop ||
                                kotlin.math.abs(ev.rawY - downRawY) > slop)) dragging = true
                }
            }
            return dragging
        }

        override fun onTouchEvent(ev: MotionEvent): Boolean {
            when (ev.actionMasked) {
                MotionEvent.ACTION_MOVE -> if (dragging) moveTo(ev.rawX, ev.rawY)
                MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                    if (dragging) { dragging = false; savePos() }
                }
            }
            return dragging || super.onTouchEvent(ev)
        }

        private fun moveTo(rawX: Float, rawY: Float) {
            val p = parent as? ViewGroup ?: return
            val lp = layoutParams as FrameLayout.LayoutParams
            lp.gravity = Gravity.NO_GRAVITY
            lp.leftMargin = (startX + (rawX - downRawX).toInt()).coerceIn(0, (p.width - width).coerceAtLeast(0))
            lp.topMargin = (startY + (rawY - downRawY).toInt()).coerceIn(0, (p.height - height).coerceAtLeast(0))
            layoutParams = lp
        }

        private fun savePos() {
            val p = parent as? ViewGroup ?: return
            if (p.width <= 0 || p.height <= 0) return
            prefs.edit()
                .putFloat("${prefKey}XF", left.toFloat() / p.width)
                .putFloat("${prefKey}YF", top.toFloat() / p.height)
                .apply()
        }

        /**
         * 每次 layout 后把自己钳回父容器可视范围内。
         * 转屏时 Activity 不重建（manifest 配了 configChanges），之前拖到旧屏幕边缘的
         * left/top 像素值在新方向上可能完全出界 —— 在这里兜底钳制，保证面板永远可见。
         * 只有确实超界才改布局，避免反复触发 layout。
         */
        override fun onLayout(changed: Boolean, l: Int, t: Int, r: Int, b: Int) {
            super.onLayout(changed, l, t, r, b)
            clampToParent()
        }

        /** 钳回父容器可视范围（已拖动过 = gravity 为 NO_GRAVITY 的才需要；gravity 定位的自动适应） */
        fun clampToParent() {
            val p = parent as? ViewGroup ?: return
            if (p.width <= 0 || p.height <= 0 || width <= 0 || height <= 0) return
            val lp = layoutParams as? FrameLayout.LayoutParams ?: return
            if (lp.gravity != Gravity.NO_GRAVITY) return
            val nl = left.coerceIn(0, (p.width - width).coerceAtLeast(0))
            val nt = top.coerceIn(0, (p.height - height).coerceAtLeast(0))
            if (nl != left || nt != top) {
                lp.leftMargin = nl
                lp.topMargin = nt
                layoutParams = lp
            }
        }
    }

    private lateinit var noteBtn: Button
    private lateinit var shotBtn: Button
    private lateinit var undoBtn: Button
    private lateinit var saveBtn: Button
    /** 截图当前处于「已预览」状态（按钮变成「完成」） */
    private var shotCaptured = false

    /** 桌面备注开关：开启后 S Pen 直接在 PC 桌面固定位置画笔迹（不随范围框移动） */
    private fun toggleNote(on: Boolean) {
        noteOn = on
        prefs.edit().putBoolean("noteOn", on).apply()   // 记住选择，切模式/重启后沿用
        updateNoteBtn()          // 未连接时也要让按钮文案跟上，否则界面和实际状态对不上
        // 画笔模式 = 笔迹只画在手机上且会自己淡掉；两种模式用的图层不同，
        // 切换时必须清干净，否则会同时出现"永久笔迹"和"淡出笔迹"两份。
        canvas.localInk = !on
        canvas.clear()
        updateInkModeButtons()
        if (!::net.isInitialized) return
        net.send(Protocol.buildNote(if (on) Protocol.NOTE_ON else Protocol.NOTE_OFF))
        if (on) net.send(Protocol.buildNote(Protocol.NOTE_COLOR, noteColorIdx.toFloat()))
        DebugLog.write("[note] ${if (on) "备注模式" else "画笔模式"}")
    }

    /**
     * 备注按钮的文案 = **当前模式名**（不再用 ✓ 后缀）：
     *   备注 → 笔迹直接画在 PC 桌面上（默认）
     *   画笔 → 原来的非备注模式，笔迹只画在手机上，桌面不受影响
     * 点一下就在两个模式之间切换。
     */
    private fun updateNoteBtn() {
        if (!::noteBtn.isInitialized) return
        noteBtn.text = if (noteOn) "备注" else "画笔"
    }

    /** 截图按钮状态机：预览 → 完成 → 预览…（预览期间锁定范围框） */
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

    /** 清除截图背景（按钮变回「预览」，同时解除范围框锁定） */
    private fun closeShot() {
        canvas.shotBackground = null
        if (shotCaptured) {
            canvas.frameLocked = false
            shotCaptured = false
            shotBtn.text = "预览"
        }
    }

    /** 指南/其它文案里描述预览按钮的当前名称（「捕获」改叫「预览」，状态机照旧） */
    private val shotBtnLabel: String get() = if (shotCaptured) "完成" else "预览"

    private fun toast(msg: String) =
        android.widget.Toast.makeText(this, msg, android.widget.Toast.LENGTH_SHORT).show()

    // ================= 截屏（存到电脑 / 手机相册）=================
    /** 截屏范围：false=桌面范围框那块，true=整块虚拟屏幕 */
    private var saveFull = false
    /** 截屏是否存到 PC（目录在 PC 端配置里） */
    private var saveToPc = true
    /** 截屏是否传回手机存进相册 */
    private var saveToPhone = true

    private fun saveBtnTip(): String {
        val where = mutableListOf<String>()
        if (saveToPc) where.add("电脑")
        if (saveToPhone) where.add("手机相册")
        val scope = if (saveFull) "整块屏幕" else "桌面范围框"
        return if (where.isEmpty()) "「截屏」：当前没选任何保存位置（去设置里开一个）"
        else "「截屏」：抓 $scope + 桌面笔迹 → 存到 ${where.joinToString(" 和 ")}"
    }

    private fun onSaveButton() {
        if (!::net.isInitialized) { toast("还没连上电脑"); return }
        if (!saveToPc && !saveToPhone) { toast("设置里至少要选一个保存位置"); return }
        net.send(Protocol.buildSaveReq(saveFull, saveToPc, saveToPhone))
        DebugLog.write("[save] 请求截屏 full=$saveFull pc=$saveToPc phone=$saveToPhone")
        toast("已请求截屏…")
    }

    /** PC 合成好的 PNG → 写进系统相册（Pictures/draw&touch），在图库里能直接看到 */
    private fun saveShotToGallery(png: ByteArray) {
        Thread {
            try {
                val name = "DrawTouch_" + java.text.SimpleDateFormat(
                    "yyyyMMdd_HHmmss", java.util.Locale.US).format(java.util.Date()) + ".png"
                val resolver = contentResolver
                val values = android.content.ContentValues().apply {
                    put(android.provider.MediaStore.Images.Media.DISPLAY_NAME, name)
                    put(android.provider.MediaStore.Images.Media.MIME_TYPE, "image/png")
                    if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.Q) {
                        // Android 10+：分区存储，用 RELATIVE_PATH 指定相册目录
                        put(android.provider.MediaStore.Images.Media.RELATIVE_PATH,
                            android.os.Environment.DIRECTORY_PICTURES + "/draw&touch")
                        put(android.provider.MediaStore.Images.Media.IS_PENDING, 1)
                    }
                }
                val uri = resolver.insert(
                    android.provider.MediaStore.Images.Media.EXTERNAL_CONTENT_URI, values)
                if (uri == null) { toast("保存到相册失败：无法创建文件"); return@Thread }
                resolver.openOutputStream(uri)?.use { it.write(png) }
                if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.Q) {
                    values.clear()
                    values.put(android.provider.MediaStore.Images.Media.IS_PENDING, 0)
                    resolver.update(uri, values, null, null)
                }
                runOnUiThread { toast("已存到相册：$name") }
                DebugLog.write("[save] 已存相册 $name ${png.size}B")
            } catch (e: Exception) {
                DebugLog.write("[save] 存相册失败 ${e.message}")
                runOnUiThread { toast("保存到相册失败：${e.message}") }
            }
        }.start()
    }

    /**
     * 画笔模式（笔迹只画在手机上、会自己淡掉）不需要「撤销」和「预览」：
     * 笔画本来就会消失，撤销没意义；预览是为了对着桌面画，画笔模式不画到桌面。
     */
    private fun updateInkModeButtons() {
        if (!::undoBtn.isInitialized || !::shotBtn.isInitialized) return
        val v = if (noteOn) View.VISIBLE else View.GONE
        undoBtn.visibility = v
        shotBtn.visibility = v
        if (!noteOn && shotCaptured) closeShot()   // 退出画笔模式时别留着锁定的范围框
    }

    // ================= 触摸板配置（速度/滚动/手势/颜色）=================
    /** 无极滚动：抬手后按动量继续滚（双击右侧滚轮条切换） */
    private var wheelInertia = false
    /** 无极滚动的动量倍率（越大惯性滑得越远） */
    private var wheelMomentum = 1.0f
    /** 震动强度百分比（0~200），设置页可调 */
    private var hapticStrengthPct = 100f

    /** 无极滚动的基础时长(ms)：动量倍率 1.0 时抬手后滚 3 秒停下 */
    private val inertiaBaseMs = 3000L
    /** 惯性滑动的调度间隔(ms) */
    private val inertiaTickMs = 16L

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
        // 手势死区：0 = 关闭（v1.4.3 默认 8dp → 3dp，慢速拖动不再「划很久不动」）
        canvas.moveDeadZoneDp = prefs.getFloat("tpMoveDz", 3f)
        // 指针平滑档位（噪声闸门灵敏度）：默认「稳」
        canvas.pointerSmoothProfile = prefs.getString("ptrSmooth", "STABLE") ?: "STABLE"
        // （捏合死区已废弃：2026-10-29 捏合判定改为「两指反向运动」，不再读这两个 prefs）
        // ---- 默认值来源：2026-10-29 从用户手机上读出的实际配置快照 ----
        // 之前这些值是"估"的（三指滑=前进后退、四指=切模式），用户实际配置完全不是那套，
        // 每次重装/换设备都要重新点一遍。现在默认值直接对齐他在用的配置。
        canvas.trackpadColor = prefs.getInt("tpColor", 0xFF20242B.toInt())
        // 手指轨迹：跟手提示，抬手后自动淡出
        canvas.showFingerTrail = prefs.getBoolean("trailOn", true)
        canvas.trailLifeMs = prefs.getInt("trailLife", 800).toLong()
        // 触觉反馈 / 重按（2026-10-30）
        canvas.hapticsEnabled = prefs.getBoolean("hapticsOn", true)
        canvas.pressToDragEnabled = prefs.getBoolean("pressDragOn", true)
        canvas.pressDownThreshold = prefs.getFloat("pressDownTh", 0.55f)
        canvas.pressUpThreshold = prefs.getFloat("pressUpTh", 0.42f)
        // 重按的接触面积阈值（%）：相对**近期静息水平**高出这么多算「重按」
        // （v1.4.6：判「按压过程中的涨幅」，不再是「相对固定历史基线的倍数」）
        canvas.pressAreaRisePct = prefs.getFloat("pressAreaRisePct", 30f)
        // v1.4.8：确认帧数 + 松开滞回（设置页可调，让用户自己定灵敏度/是否粘键）
        canvas.pressConfirmFrames = prefs.getInt("pressConfirmFrames", 3)
        canvas.pressReleaseSlackPct = prefs.getFloat("pressReleaseSlackPct", 5f)
        // 震动强度倍率 + 是否绕过系统触觉强度限制
        hapticStrengthPct = prefs.getInt("hapticStrength", 100).toFloat()
        Haptics.strengthScale = hapticStrengthPct / 100f
        Haptics.ignoreSystemLimit = prefs.getBoolean("hapticForce", false)
        canvas.g2Swipe = prefs.getInt("g2Swipe", 1)
        canvas.g2Pinch = prefs.getInt("g2Pinch", 2)
        canvas.g2Tap = prefs.getInt("g2Tap", 4)
        canvas.g3Tap = prefs.getInt("g3Tap", 5)
        // 三指：右滑=下一任务 / 左滑=上一任务 / 上滑=音量+ / 下滑=音量-
        canvas.g3L = prefs.getInt("g3L", 13)
        canvas.g3R = prefs.getInt("g3R", 14)
        canvas.g3U = prefs.getInt("g3U", 10)
        canvas.g3D = prefs.getInt("g3D", 11)
        // 四指：全部默认为「无」（用户在用的一套里四指没绑任何动作）
        canvas.g4Tap = prefs.getInt("g4Tap", 0)
        canvas.g4L = prefs.getInt("g4L", 9)
        canvas.g4R = prefs.getInt("g4R", 0)
        canvas.g4U = prefs.getInt("g4U", 0)
        canvas.g4D = prefs.getInt("g4D", 0)
        wheelInertia = prefs.getBoolean("wheelInertia", false)
        wheelMomentum = prefs.getFloat("wheelMomentum", 3.0f)
        saveFull = prefs.getBoolean("saveFull", false)
        saveToPc = prefs.getBoolean("saveToPc", true)
        saveToPhone = prefs.getBoolean("saveToPhone", true)
        highlighterOn = prefs.getBoolean("hlOn", false)
        highlighterColor = prefs.getInt("hlColor", 0).coerceIn(0, hlColors.size - 1)
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
                        if (manual) ipText.text = "没发现 PC。\n请确认 PC 上 DrawTouchHost 已启动，且手机与 PC 在同一网络（热点/同一 WiFi）。"
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
                        // 局域网里有**多台**电脑时，不能默默连第一台 ——
                        // 连错了就表现为「控制不了」「笔落在看不见的框框外面」。
                        // 优先连上次用过的那台；没有记录就弹框让用户选。
                        val lastPc = prefs.getString("lastPc", null)
                        val picked = list.firstOrNull { it.serviceName == lastPc }
                            ?: if (list.size == 1) list[0] else null
                        if (picked != null) {
                            connectToPc(picked, "mDNS 自动发现")
                        } else {
                            DebugLog.write("[mdns] 发现 ${list.size} 台桌面，请用户选择")
                            showPcChooser(list)
                        }
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
        // 用户手动选了 PC：清除上一台的「版本不兼容」阻止（新一台要重新做兼容性检查）
        protoMismatch = null
        if (::net.isInitialized) net.holdReason = null
        // 记住这台：下次自动发现时优先连它（局域网里有多台时尤其重要）
        prefs.edit().putString("lastPc", f.serviceName).apply()
        val candidates = f.candidates.ifEmpty { listOf(f.host) }
        Thread {
            var picked: String? = null
            for (c in candidates) {
                if (probeTcp(c, f.port)) {
                    picked = c
                    break
                }
                Log.i("draw&touch", "$tag 候选 $c:${f.port} 连不上，换下一个")
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
                Log.i("draw&touch", "$tag → ${f.serviceName} $target:${f.port}")
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

    /**
     * 创建 NetClient —— **全项目唯一**的连接创建点。
     *
     * 以前有两处（onCreate 和 restartNet），restartNet 那份漏挂了 `onShot`，
     * 于是 mDNS 发现 / 手动重连之后「捕获」就静默失效：PC 端明明把 PNG 发过来了，
     * 手机这边回调是 null，既没背景也不报错（查了很久才发现）。
     * 所以：**下行回调和重连后状态补发，只允许写在这个函数里。**
     */
    private fun newNetClient(): NetClient = NetClient(host, port) {
        if (::net.isInitialized) {
            // 备注状态**每次连接都完整同步一遍**（手机才是信息源）。
            // 以前只在 `!noteOn` 时才补发 NOTE_ON，可是 PC 重启后 _noteOn 是 false，
            // 手机这边如果本来就已经是"开"，就永远不会再发 —— 两边状态错位，
            // 表现为「笔迹只画在手机上，桌面什么都不出」（捕获正常，但备注像消失了一样）。
            // 注意这里**不**强行打开备注：用户选了「画笔」，重连后也要保持画笔。
            if (workMode != Protocol.MODE_TABLET) noteOn = false  // 触摸板与备注互斥
            net.send(Protocol.buildNote(
                if (noteOn) Protocol.NOTE_ON else Protocol.NOTE_OFF))
            if (noteOn) net.send(Protocol.buildNote(Protocol.NOTE_COLOR, noteColorIdx.toFloat()))
            updateNoteBtn()
            // 激光笔状态也要补回去（PC 端同样不记状态）
            if (highlighterOn) net.send(Protocol.buildHighlighter(true, highlighterColor))
        }
        helloFrame()
    }.also { n ->
        // 接收 PC 下发的 INFO（版本号 / 计算机名 / 候选 IP）
        n.onInfo = { text -> parsePcInfo(text) }
        // 接收 PC 下发的截图（PNG）→ 设为书写区背景
        n.onShot = { png -> runOnUiThread { showShot(png) } }
        n.onSave = { png -> runOnUiThread { saveShotToGallery(png) } }
    }

    private fun restartNet() {
        if (::net.isInitialized) net.stop()
        net = newNetClient()
        net.start()
        Log.i("draw&touch", "重连 $host:$port")
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
