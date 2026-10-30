package com.volcano.drawtouch

import kotlin.math.hypot

/**
 * 单指「敲击 / 双击 / 双击拖拽」判定 —— **与 Android 框架无关**，可纯 JVM 单测。
 *
 * 抽出来的原因和 [TrackpadState] 一样：MotionEvent 是 final 且构造要 native 层，
 * 单测里造不出来；而这一段的时序判断恰恰是最容易写错、又最需要回归保护的地方。
 *
 * ── 为什么要重做 ────────────────────────────────────────────────
 * 旧实现是：单击 → 延迟 280ms 发左键；280ms 内再点一下 → 发**右键**。
 * 问题有三个：
 *   ① 双击被占成右键，就没法做笔记本触摸板最常用的「双击不松手拖一下 = 选中」；
 *   ② 右键本来就有「双指点击」和底部浮动面板两个入口，重复了；
 *   ③ 每次单击都被硬性延迟 280ms，点击手感发飘。
 *
 * ── 现在的语义（对齐 Windows 精密触摸板）────────────────────────
 *   单击               → 左键（延迟到双击窗口结束，避免和"双击"抢）
 *   双击（不拖）        → 左键双击（两次点击，等于在资源管理器里双击打开）
 *   双击后按住并移动    → 按下左键 → 拖动/框选 → 松手 ⇒ 打字选词、拖文件都用它
 *   双击后按住但没动    → 按"按住时长"决定：短=双击，长=一次单击
 *
 * ── 关键时序坑 ────────────────────────────────────────────────
 * 第二下按下去时必须**立刻撤掉挂起的单击**（[Act.CANCEL_PENDING]），
 * 否则会先"点一下"（把选区取消掉、或点进别的格子）再开始拖，用户会觉得位置偏了。
 */
class TapDrag(
    private val doubleTapWindowMs: Long = 300L,
    private val tapTimeoutMs: Long = 320L,
    private val doubleTapSlopPx: Float = 48f,
    private val dragSlopPx: Float = 12f,
) {

    /** 判定结果，调用方据此决定发哪个协议帧 */
    enum class Act {
        /** 什么都不用做 */
        NONE,
        /** 这一下可能是单击：延迟 [doubleTapWindowMs] 后没等到第二下就发左键 */
        PENDING_SINGLE,
        /** 这是双击的第二下：撤掉挂起的单击（别再点一下） */
        CANCEL_PENDING,
        /** 双击且没有拖动 → 发左键双击 */
        DOUBLE_CLICK,
        /** 第二下按住太久才松手 → 按一次单击处理 */
        SINGLE_CLICK,
        /** 双击后按住并移动 → 按下左键，开始拖拽 / 选择 */
        DRAG_DOWN,
        /** 拖拽结束 → 松开左键 */
        DRAG_UP,
    }

    /** 上一次单击抬手的时刻（0 = 没有） */
    private var lastTapAt = 0L
    private var lastTapX = 0f
    private var lastTapY = 0f

    /** 双击第二下已按下，还没决定是"双击"还是"拖拽" */
    private var armed = false
    private var armedAt = 0L
    private var armedX = 0f
    private var armedY = 0f

    /** 左键已经被按下（拖拽进行中） */
    var isDragging = false
        private set

    /** 手指落下。[now] 用 SystemClock.uptimeMillis() */
    fun onDown(now: Long, x: Float, y: Float): Act {
        armed = false
        isDragging = false
        if (lastTapAt != 0L && now - lastTapAt <= doubleTapWindowMs &&
            hypot((x - lastTapX).toDouble(), (y - lastTapY).toDouble()) <= doubleTapSlopPx
        ) {
            armed = true
            armedAt = now
            armedX = x
            armedY = y
            lastTapAt = 0L
            return Act.CANCEL_PENDING
        }
        return Act.NONE
    }

    /**
     * 手指在按住状态下移动。
     * 只有"双击第二下"才可能进入拖拽；越过 [dragSlopPx] 就按下左键，且只按一次。
     */
    fun onMove(x: Float, y: Float): Act {
        if (!armed || isDragging) return Act.NONE
        if (hypot((x - armedX).toDouble(), (y - armedY).toDouble()) < dragSlopPx) return Act.NONE
        isDragging = true
        return Act.DRAG_DOWN
    }

    /** 手指抬起 */
    fun onUp(now: Long, x: Float, y: Float): Act {
        if (isDragging) {
            isDragging = false
            armed = false
            return Act.DRAG_UP
        }
        if (armed) {
            armed = false
            // 第二下按了很久才松手（且在原地没动）：当成一次普通单击，别丢用户的输入
            return if (now - armedAt <= tapTimeoutMs) Act.DOUBLE_CLICK else Act.SINGLE_CLICK
        }
        lastTapAt = now
        lastTapX = x
        lastTapY = y
        return Act.PENDING_SINGLE
    }

    /**
     * 手势作废（多指介入 / 切模式 / 兜底复位）。
     * 返回 [Act.DRAG_UP] 表示左键还按着，调用方**必须**补发抬起，否则鼠标会卡在按住状态。
     */
    fun reset(): Act {
        val wasDragging = isDragging
        isDragging = false
        armed = false
        lastTapAt = 0L
        return if (wasDragging) Act.DRAG_UP else Act.NONE
    }
}
