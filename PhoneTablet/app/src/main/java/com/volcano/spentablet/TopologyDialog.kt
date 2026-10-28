package com.volcano.spentablet

import android.app.AlertDialog
import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.RectF
import android.view.View
import android.view.ViewGroup
import android.widget.Button
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView

/**
 * 连接状态 / 网络拓扑弹窗。
 *
 * 顶部画一张「手机 ↔ PC」的拓扑示意图（连线中间标注连接方式），
 * 下面列出关键连接信息（PC IP、客户端版本号、连接方式、手机 IP / 手机名），
 * 最后把局域网里发现的其它桌面列出来，点一下即可切换连接。
 */
object TopologyDialog {

    /** 拓扑图需要展示的数据。 */
    data class TopologyData(
        val pcIp: String,
        val pcName: String,
        val pcVersion: String,
        val method: String,
        val phoneIp: String,
        val phoneName: String,
        val otherPcs: List<MdnsDiscovery.Found>
    )

    fun show(ctx: Context, data: TopologyData, onSwitch: (MdnsDiscovery.Found) -> Unit) {
        val pad = (16 * ctx.resources.displayMetrics.density).toInt()
        val content = LinearLayout(ctx).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(pad, pad, pad, pad)
        }

        val title = TextView(ctx).apply {
            text = "连接状态 / 网络拓扑"
            textSize = 18f
            setTextColor(Color.WHITE)
            setPadding(0, 0, 0, pad / 2)
        }
        content.addView(title)

        // ---- 拓扑示意图 ----
        val diagram = TopologyView(ctx, data)
        content.addView(diagram, LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, (160 * ctx.resources.displayMetrics.density).toInt()))

        // ---- 信息行 ----
        content.addView(infoRow(ctx, "桌面 PC IP", data.pcIp))
        content.addView(infoRow(ctx, "客户端版本号", data.pcVersion))
        content.addView(infoRow(ctx, "网络连接方式", data.method))
        content.addView(infoRow(ctx, "手机 IP", data.phoneIp))
        content.addView(infoRow(ctx, "手机名", data.phoneName))

        // ---- 局域网里发现的其它桌面 ----
        val otherTitle = TextView(ctx).apply {
            text = "局域网里的桌面（点击切换连接）"
            textSize = 14f
            setTextColor(Color.parseColor("#FFBBBBBB"))
            setPadding(0, pad, 0, 4)
        }
        content.addView(otherTitle)

        val list = LinearLayout(ctx).apply { orientation = LinearLayout.VERTICAL }
        if (data.otherPcs.isEmpty()) {
            list.addView(TextView(ctx).apply {
                text = "（未发现其它桌面。确认 PC 端 SpenTabletHost 已启动且在同一网络。）"
                textSize = 12f
                setTextColor(Color.parseColor("#FF999999"))
                setPadding(0, 4, 0, 4)
            })
        } else {
            for (pc in data.otherPcs) {
                val b = Button(ctx).apply {
                    text = "${pc.serviceName}  ·  ${pc.host}:${pc.port}"
                    setOnClickListener { onSwitch(pc) }
                }
                list.addView(b)
            }
        }
        content.addView(list)

        // 整页包一层 ScrollView：屏幕小、发现的主机多时也能滚动看完，不再被截断
        val scroller = ScrollView(ctx).apply {
            addView(content)
        }

        AlertDialog.Builder(ctx)
            .setView(scroller)
            .setPositiveButton("关闭", null)
            .show()
    }

    private fun infoRow(ctx: Context, label: String, value: String): View {
        val row = LinearLayout(ctx).apply {
            orientation = LinearLayout.HORIZONTAL
            setPadding(0, 4, 0, 4)
        }
        val l = TextView(ctx).apply {
            text = label
            textSize = 13f
            setTextColor(Color.parseColor("#FFBBBBBB"))
            layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
        }
        val v = TextView(ctx).apply {
            text = if (value.isBlank()) "—" else value
            textSize = 13f
            setTextColor(Color.WHITE)
            layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 2f)
        }
        row.addView(l)
        row.addView(v)
        return row
    }

    /** 用 Canvas 画「手机 ↔ PC」拓扑示意图。 */
    private class TopologyView(c: Context, private val data: TopologyData) : View(c) {
        private val paint = Paint().apply { isAntiAlias = true }

        override fun onDraw(cv: Canvas) {
            super.onDraw(cv)
            val w = width.toFloat()
            val h = height.toFloat()
            val midY = h * 0.42f
            val nodeW = w * 0.30f
            val nodeH = h * 0.40f
            val left = w * 0.06f
            val right = w - nodeW - w * 0.06f

            // 连线
            paint.style = Paint.Style.STROKE
            paint.strokeWidth = 3f
            paint.color = Color.parseColor("#FF5B9BD5")
            cv.drawLine(left + nodeW, midY, right, midY, paint)
            // 连线上的箭头
            paint.color = Color.parseColor("#FF5B9BD5")
            cv.drawLine(right, midY, right - 12f, midY - 7f, paint)
            cv.drawLine(right, midY, right - 12f, midY + 7f, paint)

            // 连接方式标签
            paint.style = Paint.Style.FILL
            paint.color = Color.WHITE
            paint.textSize = 13f
            paint.textAlign = Paint.Align.CENTER
            cv.drawText(data.method, w / 2f, midY - 10f, paint)

            // 手机节点
            drawNode(cv, left, midY - nodeH / 2, nodeW, nodeH, "手机", data.phoneIp, "#FF33DD55")
            // PC 节点
            drawNode(cv, right, midY - nodeH / 2, nodeW, nodeH, "桌面 PC", data.pcIp, "#FF3B8EFF")
        }

        private fun drawNode(cv: Canvas, x: Float, y: Float, nw: Float, nh: Float, title: String, sub: String, color: String) {
            paint.style = Paint.Style.FILL
            paint.color = Color.parseColor("#33FFFFFF")
            cv.drawRoundRect(RectF(x, y, x + nw, y + nh), 12f, 12f, paint)
            paint.style = Paint.Style.STROKE
            paint.strokeWidth = 3f
            paint.color = Color.parseColor(color)
            cv.drawRoundRect(RectF(x, y, x + nw, y + nh), 12f, 12f, paint)

            paint.style = Paint.Style.FILL
            paint.color = Color.WHITE
            paint.textAlign = Paint.Align.CENTER
            paint.textSize = 14f
            cv.drawText(title, x + nw / 2f, y + nh * 0.42f, paint)
            paint.textSize = 13f
            paint.color = Color.parseColor("#FFCCCCCC")
            val shown = if (sub.length > 18) sub.substring(0, 18) + "…" else sub
            cv.drawText(shown, x + nw / 2f, y + nh * 0.72f, paint)
        }
    }
}
