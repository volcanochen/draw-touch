package com.volcano.spentablet

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 检查更新的语义化版本比较回归测试（纯 JVM）。
 */
class UpdateCheckerVersionTest {

    @Test
    fun `更新版本大于当前版本`() {
        assertTrue(UpdateChecker.isNewer("1.3.1", "1.3.0"))
        assertTrue(UpdateChecker.isNewer("1.4.0", "1.3.9"))
        assertTrue(UpdateChecker.isNewer("2.0.0", "1.9.9"))
    }

    @Test
    fun `同版本或更旧不更新`() {
        assertFalse(UpdateChecker.isNewer("1.3.0", "1.3.0"))
        assertFalse(UpdateChecker.isNewer("1.2.9", "1.3.0"))
        assertFalse(UpdateChecker.isNewer("0.9.9", "1.0.0"))
    }

    @Test
    fun `逐段比较不按字符串`() {
        // 1.10.0 字符串比较会小于 1.9.0，逐段数值比较必须大于
        assertTrue(UpdateChecker.isNewer("1.10.0", "1.9.0"))
        assertTrue(UpdateChecker.isNewer("1.10.0", "1.3.0"))
        assertFalse(UpdateChecker.isNewer("1.3.0", "1.10.0"))
    }

    @Test
    fun `当前版本为空视为可更新`() {
        assertTrue(UpdateChecker.isNewer("1.3.0", ""))
        assertTrue(UpdateChecker.isNewer("1.3.0", " "))
    }

    @Test
    fun `带 v 前缀与后缀的版本号也能解析`() {
        assertTrue(UpdateChecker.isNewer("v1.3.1", "1.3.0"))
        assertTrue(UpdateChecker.isNewer("1.3.1-beta", "1.3.0"))
        assertFalse(UpdateChecker.isNewer("1.3.0-beta", "1.3.0"))
    }

    @Test
    fun `段数不足补零`() {
        assertTrue(UpdateChecker.isNewer("1.4", "1.3.0"))
        assertFalse(UpdateChecker.isNewer("1.3", "1.3.0"))
    }

    @Test
    fun `parseVer 基础行为`() {
        assertTrue(UpdateChecker.parseVer("1.3.0").contentEquals(intArrayOf(1, 3, 0)))
        assertTrue(UpdateChecker.parseVer("v1.3.1-beta").contentEquals(intArrayOf(1, 3, 1)))
        assertTrue(UpdateChecker.parseVer("垃圾").contentEquals(intArrayOf(0, 0, 0)))
    }
}
