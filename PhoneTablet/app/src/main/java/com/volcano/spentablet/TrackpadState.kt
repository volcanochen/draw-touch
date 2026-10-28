package com.volcano.spentablet

/**
 * 触摸板手势状态里**与 Android 框架无关**的判定逻辑。
 *
 * 单独抽出来的唯一目的：能脱离 MotionEvent 做 JVM 单测。
 * （MotionEvent 是 final 类且构造需要 native 层，单测里造不出来。）
 *
 * 背景 —— 一条真实踩过的坑：
 * 笔按在屏幕上时还在收 STYLUS 事件，`pointerCount` 不为 0；
 * 但触摸板只数**手指**，于是「笔按着 + 最后一根手指抬起」这一帧
 * 在统计完手指数后得到 0，原代码直接 `return`，状态一个都不清：
 *   · 手机端 tpActiveFingers 停在非零 → 之后所有笔事件被"笔让位"分支 continue 掉；
 *   · PC 端 _fingerOnPad 永远为 true → 连笔的接触事件也全丢。
 * 表现：心跳一直正常、手机界面也还有反应，但**光标一动不动**，只能切模式或重启 App。
 */
object TrackpadState {

    // 与 android.view.MotionEvent 的常量值保持一致（纯 JVM 单测里不能引用那个类）
    const val ACTION_DOWN = 0
    const val ACTION_UP = 1
    const val ACTION_MOVE = 2
    const val ACTION_CANCEL = 3
    const val ACTION_POINTER_DOWN = 5
    const val ACTION_POINTER_UP = 6

    /** 是否属于「手势可能结束」的事件类型 */
    fun isEndAction(action: Int): Boolean =
        action == ACTION_UP || action == ACTION_CANCEL || action == ACTION_POINTER_UP

    /**
     * 这一帧是否必须强制复位触摸板状态。
     *
     * 条件：鼠标模式 + 有真实接触 + 一根手指都不剩 + 结束类事件。
     * 只要命中就复位，这是「手机突然怎么动都不动」的兜底。
     */
    fun mustReset(mouseMode: Boolean, contact: Boolean, fingerCount: Int, action: Int): Boolean =
        mouseMode && contact && fingerCount <= 0 && isEndAction(action)

    /**
     * ACTION_POINTER_UP 之后还剩几根手指。
     *
     * [remaining] 是**已经排除抬起那根**之后统计出来的手指数，所以直接用，不能再减 1。
     * 原代码写成 `cnt - 1`，双指抬成单指时会让 tpActiveFingers 掉到 0，
     * 提前给 PC 发"手指全离开"，笔的悬停流立刻回来抢光标（光标往回跳）。
     */
    fun fingersAfterPointerUp(remaining: Int): Int = if (remaining < 0) 0 else remaining
}
