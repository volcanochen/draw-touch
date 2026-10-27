package com.volcano.spentablet

import android.app.Activity
import android.content.SharedPreferences
import android.graphics.Color
import android.os.Build
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
import android.widget.ImageButton
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

    /** 主界面顶部左侧的连接状态点（绿=已连 红=未连） */
    private lateinit var statusDot: View
    /** 方向切换图标按钮（主界面独立按钮） */
    private lateinit var rotateBtn: ImageButton
    /** 连接状态（拓扑图）按钮 */
    private lateinit var topologyBtn: ImageButton
    /** 设置（齿轮）按钮 */
    private lateinit var settingsBtn: ImageButton

    /** 独立设置页（全屏覆盖层，默认隐藏；返回键 / 完成按钮关闭） */
    private lateinit var settingsPage: LinearLayout
    /** 设置页里的可滚动正文（所有设置控件都在这里） */
    private lateinit var details: LinearLayout

    /** 当前是否横屏（决定控制栏的紧凑度） */
    private var landscape = false

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
    private var workMode = Protocol.MODE_TABLET

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
        workMode = prefs.getInt("mode", Protocol.MODE_TABLET)
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
            layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
        }
        rotateBtn = ImageButton(this).apply {
            setImageResource(R.drawable.ic_rotate)
            background = null
            setPadding(8, 8, 8, 8)
            layoutParams = LinearLayout.LayoutParams(dp(40), dp(40))
            contentDescription = "屏幕方向"
            setOnClickListener { cycleOrientation() }
        }
        val rotateCol = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER_HORIZONTAL
            addView(rotateBtn)
        }
        topologyBtn = ImageButton(this).apply {
            setImageResource(R.drawable.ic_topology)
            background = null
            setPadding(8, 8, 8, 8)
            layoutParams = LinearLayout.LayoutParams(dp(40), dp(40))
            contentDescription = "连接状态 / 网络拓扑"
            setOnClickListener { openTopology() }
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
        topRow.addView(rotateCol)
        topRow.addView(topologyBtn)
        topRow.addView(settingsBtn)
        // 固定满宽：状态文字(weight=1)占中间，三个图标恒定右对齐
        topRow.layoutParams = LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT
        )

        // 顶部栏只有一行：状态点 + 图标按钮。原来的书写区信息行 / 底部调试栏已移除，
        // 这些调试信息改为后台定量循环写入文件（见 DebugLog）。
        topBar.addView(topRow)
        content.addView(
            topBar, 0,
            LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT)
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

        status.setOnClickListener { openSettings() }
        statusDot.setOnClickListener { openSettings() } // 绿点也可点开设置
        doneBtn.setOnClickListener { closeSettings() }

        setContentView(root)
        applyOrientationMode()

        net = NetClient(host, port) { helloFrame() }
        // 接收 PC 下发的 INFO（版本号 / 计算机名 / 候选 IP）
        net.onInfo = { text -> parsePcInfo(text) }
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
                val modeName = if (workMode == Protocol.MODE_MOUSE) "鼠标" else "数位板"
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
            text = "数位板（默认）：显示范围框，S Pen 在框内作画，手指滑动=移动范围框"
            setTextColor(Color.WHITE)
        }
        val radioMouse = RadioButton(this).apply {
            id = View.generateViewId()
            text = "鼠标：取消范围框，手指滑动=移动光标，单击=左键，双击=右键"
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
            if (m == workMode) return@setOnCheckedChangeListener
            workMode = m
            canvas.workMode = m
            prefs.edit().putInt("mode", m).apply()
            net.send(Protocol.buildMode(m))
            Log.i("SpenTablet", "切换工作模式 -> ${if (m == Protocol.MODE_MOUSE) "鼠标" else "数位板"}")
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
    private fun openSettings() {
        settingsPage.visibility = View.VISIBLE
    }

    private fun closeSettings() {
        settingsPage.visibility = View.GONE
    }

    override fun onBackPressed() {
        if (settingsPage.visibility == View.VISIBLE) {
            closeSettings()
            return
        }
        super.onBackPressed()
    }

    // ---------- 屏幕方向（主界面图标按钮）----------
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
