package com.volcano.spentablet

import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import android.os.Build
import android.os.Handler
import android.util.Log
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL

/**
 * 检查更新：直接比对 GitHub Release 的 latest 版本。
 * 发现更新 → 从 Release 下载 APK → 用 PackageInstaller 提交安装（无需 FileProvider）。
 * 安装确认由系统弹窗完成；[UpdateReceiver] 负责在「需用户确认」时拉起系统安装界面。
 */
object UpdateChecker {
    private const val TAG = "SpenTablet.Update"
    private const val REPO = "volcanochen/drawing-phone"
    private const val RELEASE_API = "https://api.github.com/repos/$REPO/releases/latest"

    /** 当前已安装的版本名（如 "1.3.0"），供比对用 */
    fun currentVersion(ctx: Context): String = try {
        ctx.packageManager.getPackageInfo(ctx.packageName, 0).versionName ?: ""
    } catch (e: Throwable) { "" }

    data class UpdateInfo(val version: String, val apkUrl: String, val notes: String)

    /**
     * 检查 GitHub Release 最新版本（网络在子线程，结果回主线程）。
     * 有更新 → 回调 [UpdateInfo]；已是最新 / 出错 / 无 APK 资源 → 回调 null。
     */
    fun check(ctx: Context, onResult: (UpdateInfo?) -> Unit) {
        Thread {
            try {
                val conn = URL(RELEASE_API).openConnection() as HttpURLConnection
                conn.connectTimeout = 8000
                conn.readTimeout = 8000
                conn.setRequestProperty("Accept", "application/vnd.github+json")
                conn.setRequestProperty("User-Agent", "SpenTablet")
                val code = conn.responseCode
                if (code != 200) { conn.disconnect(); runOnUi(ctx) { onResult(null) }; return@Thread }
                val body = conn.inputStream.bufferedReader().use { it.readText() }
                conn.disconnect()
                val json = JSONObject(body)
                val tag = json.optString("tag_name", "")
                val notes = json.optString("body", "")
                // 在 assets 里找第一个 .apk 下载地址
                val assets = json.optJSONArray("assets")
                var apkUrl: String? = null
                if (assets != null) {
                    for (i in 0 until assets.length()) {
                        val a = assets.getJSONObject(i)
                        val name = a.optString("name", "")
                        if (name.endsWith(".apk", ignoreCase = true)) {
                            apkUrl = a.optString("browser_download_url", "")
                            break
                        }
                    }
                }
                if (tag.isBlank() || apkUrl.isNullOrBlank()) {
                    runOnUi(ctx) { onResult(null) }; return@Thread
                }
                val latest = tag.removePrefix("v").trim()
                val cur = currentVersion(ctx)
                if (isNewer(latest, cur)) {
                    runOnUi(ctx) { onResult(UpdateInfo(latest, apkUrl!!, notes)) }
                } else {
                    runOnUi(ctx) { onResult(null) }
                }
            } catch (e: Throwable) {
                Log.w(TAG, "检查更新失败: $e")
                runOnUi(ctx) { onResult(null) }
            }
        }.start()
    }

    /**
     * 下载 APK 并用 PackageInstaller 安装。进度/结果经 [onStatus] 回主线程。
     * 真正的安装确认由系统界面完成（[UpdateReceiver] 拉起）。
     */
    fun downloadAndInstall(ctx: Context, info: UpdateInfo, onStatus: (String) -> Unit) {
        Thread {
            try {
                onStatus("正在下载 v${info.version}…")
                val conn = URL(info.apkUrl).openConnection() as HttpURLConnection
                conn.connectTimeout = 10000
                conn.readTimeout = 30000
                conn.setRequestProperty("User-Agent", "SpenTablet")
                val code = conn.responseCode
                if (code != 200) { conn.disconnect(); runOnUi(ctx) { onStatus("下载失败（HTTP $code）") }; return@Thread }
                val total = conn.contentLength.toLong()
                val `in` = conn.inputStream

                val pm = ctx.packageManager
                val installer = pm.packageInstaller
                val params = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL)
                if (Build.VERSION.SDK_INT >= 24) params.setAppPackageName(ctx.packageName)
                val sessionId = installer.createSession(params)
                val session = installer.openSession(sessionId)
                // size < 0 表示未知长度，允许流式写入任意大小
                val size = if (total > 0) total else -1L
                val out = session.openWrite("spen-update", 0, size)
                val buf = ByteArray(8192)
                var read: Int
                var sofar = 0L
                var lastPct = -1
                while (`in`.read(buf).also { read = it } >= 0) {
                    out.write(buf, 0, read)
                    sofar += read
                    if (total > 0) {
                        val pct = ((sofar * 100) / total).toInt()
                        if (pct != lastPct) { lastPct = pct; runOnUi(ctx) { onStatus("下载中 $pct%") } }
                    }
                }
                `in`.close()
                session.fsync(out)
                out.close()
                conn.disconnect()

                // 提交安装：结果用 PendingIntent（广播）回 [UpdateReceiver]
                val intent = Intent(ctx, UpdateReceiver::class.java).apply {
                    action = "com.volcano.spentablet.UPDATE_INSTALL"
                }
                val flags = if (Build.VERSION.SDK_INT >= 23)
                    PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
                else PendingIntent.FLAG_UPDATE_CURRENT
                val pi = PendingIntent.getBroadcast(ctx, 0, intent, flags)
                session.commit(pi.intentSender)
                session.close()
                runOnUi(ctx) { onStatus("已提交安装，请在系统中确认") }
            } catch (e: Throwable) {
                Log.w(TAG, "下载/安装失败: $e")
                runOnUi(ctx) { onStatus("更新失败：${e.message}") }
            }
        }.start()
    }

    /** 语义化版本比较：a 是否比 b 新（逐段比较 major.minor.patch） */
    private fun isNewer(a: String, b: String): Boolean {
        if (b.isBlank()) return true
        val va = parseVer(a)
        val vb = parseVer(b)
        for (i in 0..2) {
            if (va[i] != vb[i]) return va[i] > vb[i]
        }
        return false
    }

    private fun parseVer(s: String): IntArray {
        val parts = s.split('.', '-')
        val nums = IntArray(3)
        for (i in 0 until kotlin.math.min(3, parts.size)) {
            nums[i] = parts[i].filter { it.isDigit() }.toIntOrNull() ?: 0
        }
        return nums
    }

    private fun runOnUi(ctx: Context, block: () -> Unit) {
        Handler(ctx.mainLooper).post(block)
    }
}
