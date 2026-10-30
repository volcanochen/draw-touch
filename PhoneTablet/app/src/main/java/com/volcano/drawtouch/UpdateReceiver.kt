package com.volcano.drawtouch

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import android.util.Log

/**
 * 接收 PackageInstaller 的安装结果。
 * 当系统需要用户确认时（STATUS_PENDING_USER_ACTION），拉起系统安装界面完成安装。
 */
class UpdateReceiver : BroadcastReceiver() {
    override fun onReceive(ctx: Context, intent: Intent) {
        val status = intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE)
        when (status) {
            PackageInstaller.STATUS_PENDING_USER_ACTION -> {
                // 系统要求用户确认：intent 里带了一个打开系统安装界面的 Intent
                @Suppress("DEPRECATION")
                val act = intent.getParcelableExtra(Intent.EXTRA_INTENT) as? Intent
                act?.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                try {
                    ctx.startActivity(act)
                } catch (e: Throwable) {
                    Log.w("DrawTouch.Update", "拉起安装界面失败: $e")
                }
            }
            PackageInstaller.STATUS_SUCCESS -> Log.i("DrawTouch.Update", "安装成功")
            else -> {
                val msg = intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE) ?: "安装失败"
                Log.w("DrawTouch.Update", "安装失败：$msg")
            }
        }
    }
}
