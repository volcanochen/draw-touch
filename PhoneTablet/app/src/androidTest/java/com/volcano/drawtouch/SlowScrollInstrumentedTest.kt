package com.volcano.drawtouch

import android.os.SystemClock
import android.view.InputDevice
import android.view.MotionEvent
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import kotlin.math.abs

/**
 * 真机上的**慢速双指滚动**端到端测试。
 *
 * ## 为什么要有这个测试
 * 单元测试只能覆盖纯逻辑类（[ScrollEmitter] / [TwoFingerLogic]），
 * 覆盖不到「生产代码里那条逐帧门限」这种**接线**问题。
 *
 * 2026-10-30 用户反馈：「双指滚动内容的时候，好像过程中是有死区的，
 * 也就是**我很缓慢的滑动，累计的移动没有变成滚动**。」
 *
 * 根因就在生产代码里：`abs(dCx) > 0.5f` 这道**逐帧**门限，
 * 小于它的帧被**整帧丢掉**、压根没进死区累加器。
 * 触摸芯片量化步长 0.377 逻辑像素 —— 慢速滑动一帧正好走一个量化步，
 * 于是**每一帧都被丢掉** → 累计位移永远攒不起来。
 *
 * 换算成速度：0.5px/帧 × 60fps = **30 逻辑像素/秒**。
 * 低于这个速度的双指滚动，位移 100% 丢失、一点都滚不动。
 *
 * 本测试直接驱动**真实的 [PenCanvasView]**（不是复刻品），
 * 喂进真实的两指 MotionEvent 序列 —— 修复前这里会得到 **0 帧滚动**。
 */
@RunWith(AndroidJUnit4::class)
class SlowScrollInstrumentedTest {

    private val instrumentation get() = InstrumentationRegistry.getInstrumentation()
    private val context get() = instrumentation.targetContext
    private val dm get() = context.resources.displayMetrics

    private class Recorder {
        var scrollFrames = 0
        var scrollPx = 0f          // 把归一化的滚动量换算回像素

        fun add(dx: Float, dy: Float, h: Int) {
            scrollFrames++
            scrollPx += (abs(dx) + abs(dy)) * h
        }
    }

    /** 造一个多指 MotionEvent（protocol A/B 都支持：显式给 id + 坐标） */
    private fun motion(action: Int, downTime: Long, eventTime: Long, pts: Array<FloatArray>): MotionEvent {
        val props = Array(pts.size) { i ->
            MotionEvent.PointerProperties().apply {
                id = i
                toolType = MotionEvent.TOOL_TYPE_FINGER
            }
        }
        val coords = Array(pts.size) { i ->
            MotionEvent.PointerCoords().apply {
                x = pts[i][0]
                y = pts[i][1]
                pressure = 1f
                size = 1f
            }
        }
        return MotionEvent.obtain(
            downTime, eventTime, action, pts.size, props, coords,
            0, 0, 1f, 1f, 0, 0, InputDevice.SOURCE_TOUCHSCREEN, 0
        )
    }

    private fun dispatch(view: PenCanvasView, e: MotionEvent) {
        instrumentation.runOnMainSync { view.onTouchEvent(e) }
    }

    private fun newView(rec: Recorder): PenCanvasView {
        lateinit var v: PenCanvasView
        instrumentation.runOnMainSync {
            v = PenCanvasView(context)
            v.workMode = Protocol.MODE_MOUSE
            v.layout(0, 0, dm.widthPixels, dm.heightPixels)
            v.onMouse = { kind, dx, dy ->
                if (kind == Protocol.MOUSE_SCROLL) rec.add(dx, dy, dm.heightPixels)
            }
        }
        return v
    }

    /**
     * 【核心回归】慢速双指滑动必须真的滚起来。
     *
     * 每帧 0.3px ≈ 18 逻辑像素/秒，**远低于**旧门限折算出的 30px/s。
     * 修复前：每一帧都被逐帧门限丢掉 → 死区累加器永远攒不够 → **0 帧滚动**。
     */
    @Test
    fun 慢速双指滑动必须真的滚起来() {
        val rec = Recorder()
        val view = newView(rec)

        val w = dm.widthPixels
        val h = dm.heightPixels
        val ax = w * 0.40f
        val bx = w * 0.60f
        val ay = h * 0.55f
        val step = 0.3f          // ← 慢速：每帧 0.3 逻辑像素
        val frames = 240

        val downTime = SystemClock.uptimeMillis()
        var t = downTime

        dispatch(view, motion(MotionEvent.ACTION_DOWN, downTime, t, arrayOf(floatArrayOf(ax, ay))))
        t += 16
        dispatch(
            view,
            motion(
                MotionEvent.ACTION_POINTER_DOWN or (1 shl MotionEvent.ACTION_POINTER_INDEX_SHIFT),
                downTime, t, arrayOf(floatArrayOf(ax, ay), floatArrayOf(bx, ay))
            )
        )
        // 必须等过双指手势的 150ms 启动窗口（判定用真实时钟，不是事件时间）
        Thread.sleep(220)

        for (i in 1..frames) {
            val dy = -step * i          // 两指同向缓慢上移 → 纵向滚动
            t += 16
            dispatch(
                view,
                motion(
                    MotionEvent.ACTION_MOVE, downTime, t,
                    arrayOf(floatArrayOf(ax, ay + dy), floatArrayOf(bx, ay + dy))
                )
            )
        }

        val totalTravel = step * frames
        assertTrue(
            "慢速双指滑动必须产生滚动帧（修复前是 0 帧：每帧位移被逐帧门限丢掉）",
            rec.scrollFrames > 0
        )
        assertTrue(
            "慢速滚动的累计量应与行程同量级（行程=${"%.1f".format(totalTravel)}px，" +
                "实得=${"%.1f".format(rec.scrollPx)}px）",
            rec.scrollPx > totalTravel * 0.3f
        )
        assertTrue(
            "累计滚动量不应超过总行程（不得凭空放大）：行程=${"%.1f".format(totalTravel)}px，" +
                "实得=${"%.1f".format(rec.scrollPx)}px",
            rec.scrollPx <= totalTravel * 1.05f
        )
    }

    /**
     * 对照组：快速双指滑动同样要滚，且累计量应接近「行程 − 起手死区」。
     */
    @Test
    fun 快速双指滑动累计量接近行程() {
        val rec = Recorder()
        val view = newView(rec)

        val w = dm.widthPixels
        val h = dm.heightPixels
        val ax = w * 0.40f
        val bx = w * 0.60f
        val ay = h * 0.55f
        val step = 20f
        val frames = 60

        val downTime = SystemClock.uptimeMillis()
        var t = downTime
        dispatch(view, motion(MotionEvent.ACTION_DOWN, downTime, t, arrayOf(floatArrayOf(ax, ay))))
        t += 16
        dispatch(
            view,
            motion(
                MotionEvent.ACTION_POINTER_DOWN or (1 shl MotionEvent.ACTION_POINTER_INDEX_SHIFT),
                downTime, t, arrayOf(floatArrayOf(ax, ay), floatArrayOf(bx, ay))
            )
        )
        Thread.sleep(220)

        for (i in 1..frames) {
            val dy = -step * i
            t += 16
            dispatch(
                view,
                motion(
                    MotionEvent.ACTION_MOVE, downTime, t,
                    arrayOf(floatArrayOf(ax, ay + dy), floatArrayOf(bx, ay + dy))
                )
            )
        }

        val totalTravel = step * frames
        val deadZone = 3f * dm.density      // 拖框死区默认 3dp
        assertTrue("快速双指滑动应产生滚动帧", rec.scrollFrames > 0)
        assertEquals(
            "快速滚动的累计量应 ≈ 行程 − 起手死区（位移一分不丢）",
            totalTravel - deadZone, rec.scrollPx, totalTravel * 0.12f
        )
    }

    /**
     * 噪声对照：正负交替的抖动**互相抵消**，不该滚起来。
     * 这条保证「不丢位移」没有变成「噪声也放行」。
     */
    @Test
    fun 交替抖动不该触发滚动() {
        val rec = Recorder()
        val view = newView(rec)

        val w = dm.widthPixels
        val h = dm.heightPixels
        val ax = w * 0.40f
        val bx = w * 0.60f
        val ay = h * 0.55f

        val downTime = SystemClock.uptimeMillis()
        var t = downTime
        dispatch(view, motion(MotionEvent.ACTION_DOWN, downTime, t, arrayOf(floatArrayOf(ax, ay))))
        t += 16
        dispatch(
            view,
            motion(
                MotionEvent.ACTION_POINTER_DOWN or (1 shl MotionEvent.ACTION_POINTER_INDEX_SHIFT),
                downTime, t, arrayOf(floatArrayOf(ax, ay), floatArrayOf(bx, ay))
            )
        )
        Thread.sleep(220)

        // 量化步长 0.377px，正负交替 → 净位移为 0
        for (i in 1..240) {
            val d = if (i % 2 == 0) 0.377f else -0.377f
            t += 16
            dispatch(
                view,
                motion(
                    MotionEvent.ACTION_MOVE, downTime, t,
                    arrayOf(floatArrayOf(ax, ay + d), floatArrayOf(bx, ay + d))
                )
            )
        }

        assertEquals("交替抖动的净位移为 0，不该产生任何滚动", 0, rec.scrollFrames)
    }
}
