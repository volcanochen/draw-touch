package com.volcano.spentablet

import android.graphics.Color
import android.graphics.drawable.GradientDrawable
import android.view.MotionEvent
import android.view.View
import android.view.ViewGroup
import android.widget.FrameLayout
import android.widget.TextView

/**
 * S Pen / 鼠标**悬浮提示**：笔尖悬停在按钮上方（不接触屏幕）就弹一个小气泡说明这个按钮干什么，
 * 移开立刻消失。
 *
 * 为什么自己画而不用系统的 `View.tooltipText`：
 *  - `tooltipText` 要 API 26，本项目 minSdk 21；
 *  - 系统 tooltip 的样式不可控，和浮动面板的深色圆角不统一。
 *
 * 实现要点：
 *  - 气泡是挂在 root（FrameLayout）上的一个 TextView，位置按锚点算：**优先放上方**，
 *    上方放不下就放下方，左右钳制在屏幕内 —— 关键是不能盖住锚点，
 *    否则指针会从按钮"移进"气泡，触发 HOVER_EXIT → 收起 → 又 HOVER_ENTER，来回闪。
 *  - 悬浮事件**不消费**（返回 false），按钮自己的点击逻辑完全不受影响。
 *  - 3.5 秒兜底自动消失：笔抬走后个别机型不发 HOVER_EXIT，避免气泡赖着不走。
 */
class HoverTip(private val root: FrameLayout) {

    private val bubble: TextView = TextView(root.context).apply {
        setTextColor(Color.WHITE)
        textSize = 13f
        setPadding(dp(10), dp(6), dp(10), dp(6))
        background = GradientDrawable().apply {
            cornerRadius = dp(8).toFloat()
            setColor(Color.parseColor("#F0000000"))
        }
        visibility = View.GONE
        isClickable = false
        isFocusable = false
        importantForAccessibility = View.IMPORTANT_FOR_ACCESSIBILITY_NO
        layoutParams = FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT
        )
    }

    private var current: View? = null
    private val hideTask = Runnable { hide() }

    init {
        root.addView(bubble)
    }

    /**
     * 给某个按钮挂上悬浮提示。
     *
     * @param textProvider 每次悬浮时现取文案 —— 文案随状态变化时（备注/画笔、捕获/完成）
     *                     不用重新挂，也不会显示过期说明。
     */
    fun attach(v: View, textProvider: () -> String) {
        v.contentDescription = textProvider()   // 无障碍也能读到同一句说明
        v.setOnHoverListener { view, e ->
            when (e.actionMasked) {
                MotionEvent.ACTION_HOVER_ENTER -> show(view, textProvider())
                MotionEvent.ACTION_HOVER_MOVE -> if (bubble.visibility != View.VISIBLE) {
                    show(view, textProvider())
                }
                MotionEvent.ACTION_HOVER_EXIT -> hide()
            }
            false   // 不消费：按钮的点击/长按照旧
        }
    }

    fun attach(v: View, text: String) = attach(v) { text }

    /** 手指真正按下 / 页面切换时调用，避免气泡残留在屏幕上 */
    fun hide() {
        root.removeCallbacks(hideTask)
        current = null
        if (bubble.visibility != View.GONE) bubble.visibility = View.GONE
    }

    private fun show(anchor: View, text: String) {
        if (anchor.visibility != View.VISIBLE || text.isEmpty()) return
        if (root.width <= 0 || root.height <= 0) return
        current = anchor
        bubble.text = text
        bubble.measure(
            View.MeasureSpec.makeMeasureSpec(root.width, View.MeasureSpec.AT_MOST),
            View.MeasureSpec.UNSPECIFIED
        )
        val bw = bubble.measuredWidth
        val bh = bubble.measuredHeight
        val a = IntArray(2).also { anchor.getLocationOnScreen(it) }
        val r = IntArray(2).also { root.getLocationOnScreen(it) }
        val ax = a[0] - r[0]
        val ay = a[1] - r[1]
        val gap = dp(6)
        val edge = dp(4)

        var y = ay - bh - gap                        // 优先放锚点上方
        if (y < edge) y = ay + anchor.height + gap   // 上方放不下 → 放下方
        var x = ax + (anchor.width - bw) / 2         // 水平居中于锚点
        val maxX = (root.width - bw - edge).coerceAtLeast(edge)
        if (x < edge) x = edge
        if (x > maxX) x = maxX

        bubble.x = x.toFloat()
        bubble.y = y.toFloat()
        // 气泡是最早创建的子 View，后面加的悬浮面板/工具条会盖在它上面 → 每次显示时提到最前
        if (bubble.visibility != View.VISIBLE) {
            bubble.visibility = View.VISIBLE
            bubble.bringToFront()
        }

        // 兜底：个别机型笔抬走后不发 HOVER_EXIT
        root.removeCallbacks(hideTask)
        root.postDelayed(hideTask, 3500)
    }

    private fun dp(v: Int): Int = (v * root.resources.displayMetrics.density).toInt()
}
