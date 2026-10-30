package com.volcano.drawtouch

import android.util.Log
import java.net.Inet4Address
import java.net.InetSocketAddress
import java.net.NetworkInterface
import java.net.Socket
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

/**
 * 「自动查找 PC」：USB 网络共享时用户最大的痛点是不确定该填哪个 IP。
 *
 * 原理：USB 网络共享(RNDIS)时，手机自己在那张网卡上有个地址（常见 192.168.42.129），
 * PC 就在同一个 /24 里。所以只要扫本机各网卡的同网段、找开放着 PC 端口的主机即可。
 *
 * 只在用户明确点击「自动查找」时才扫描，不做后台自动扫描。
 */
object NetworkScan {

    private const val TAG = "draw&touch"

    /** 本机所有 IPv4 地址（排除回环），附带网卡名 */
    private fun localIpv4(): List<Pair<String, String>> = try {
        val out = mutableListOf<Pair<String, String>>()
        val ifs = NetworkInterface.getNetworkInterfaces()
        while (ifs.hasMoreElements()) {
            val ni = ifs.nextElement()
            val addrs = ni.inetAddresses
            while (addrs.hasMoreElements()) {
                val a = addrs.nextElement()
                if (a is Inet4Address && !a.isLoopbackAddress && !a.isLinkLocalAddress) {
                    a.hostAddress?.let { out.add(it to (ni.name ?: ni.displayName ?: "")) }
                }
            }
        }
        out
    } catch (e: Throwable) {
        Log.w(TAG, "枚举本机 IP 失败: ${e.message}")
        emptyList()
    }

    /**
     * 找出值得扫描的网段。
     *
     * 重点：不能只认 192.168.42.x / 192.168.43.x —— 各家手机的 USB 共享网段不一样
     * （本机三星 RNDIS 实测是 10.136.80.x），所以这里扫描**本机所有非链路本地网卡的 /24**，
     * 并优先扫名字像 USB/RNDIS 的网卡。
     */
    private fun candidateSubnets(): List<String> {
        val all = localIpv4()
            .filterNot { it.first.startsWith("169.254.") }   // 链路本地地址，没有意义
        val byName = all.filter {
            val n = it.second.lowercase()
            n.contains("rndis") || n.contains("usb") || n.contains("tether") ||
                    n.contains("ap") || n.contains("wifi")
        }.map { it.first }
        val rest = all.map { it.first }.filterNot { byName.contains(it) }
        return (byName + rest).distinct()
    }

    /**
     * 扫描候选网段里开着 [port] 的主机。
     * 回调 onFound(找到的 IP 或 null)。在后台线程执行。
     */
    fun findHost(port: Int, timeoutMs: Int = 350, onFound: (String?) -> Unit) {
        Thread {
            var found: String? = null
            try {
                // 关键：**排除本机自己的 IP**。
                // 手机自己的 RNDIS 地址（比如 10.136.80.31）和 PC 是同一网段，
                // 不排除的话会先「找到」自己 → 连自己 → Connection refused，白折腾。
                val self = localIpv4().map { it.first }.toSet()
                for (base in candidateSubnets()) {
                    val prefix = base.substringBeforeLast('.')
                    val pool = (1..254)
                        .map { "$prefix.$it" }
                        .filterNot { self.contains(it) }
                    if (pool.isEmpty()) continue
                    found = scanPool(pool, port, timeoutMs)
                    if (found != null) break
                }
            } catch (e: Throwable) {
                Log.w(TAG, "扫描失败: ${e.message}")
            }
            onFound(found)
        }.apply { isDaemon = true }.start()
    }

    /** 并发扫一个网段（每批 32 个），返回第一个开放的 IP */
    private fun scanPool(pool: List<String>, port: Int, timeoutMs: Int): String? {
        val hit = AtomicBoolean(false)
        var result: String? = null
        var idx = 0
        while (idx < pool.size) {
            val batch = pool.subList(idx, minOf(idx + 32, pool.size))
            val ex = Executors.newFixedThreadPool(batch.size)
            val latch = CountDownLatch(batch.size)
            for (ip in batch) {
                ex.execute {
                    try {
                        if (probe(ip, port, timeoutMs) && hit.compareAndSet(false, true)) {
                            synchronized(hit) { result = ip }
                        }
                    } catch (_: Throwable) {
                    } finally {
                        latch.countDown()
                    }
                }
            }
            latch.await(timeoutMs.toLong() * 3 + 500, TimeUnit.MILLISECONDS)
            ex.shutdownNow()
            if (hit.get()) return result
            idx += 32
        }
        return null
    }

    private fun probe(ip: String, port: Int, timeoutMs: Int): Boolean = try {
        Socket().use { s ->
            s.connect(InetSocketAddress(ip, port), timeoutMs)
            true
        }
    } catch (_: Throwable) {
        false
    }

    /** 供界面显示：当前手机各网卡 IP */
    fun describeLocalIps(): String {
        val ips = localIpv4()
        if (ips.isEmpty()) return "（未发现可用网卡）"
        return ips.joinToString("、") { (ip, name) -> "$ip($name)" }
    }

    /** 手机主 IP（拓扑图展示用）。优先 WiFi / USB 共享网卡。 */
    fun primaryLocalIp(): String {
        val all = localIpv4().filterNot { it.first.startsWith("169.254.") }
        if (all.isEmpty()) return "（无）"
        val prefer = all.firstOrNull { it.second.lowercase().let { n -> n.contains("wlan") || n.contains("wifi") } }
            ?: all.firstOrNull { it.second.lowercase().let { n -> n.contains("rndis") || n.contains("usb") || n.contains("tether") } }
            ?: all.firstOrNull()
        return prefer?.first ?: all.first().first
    }

    /**
     * 推断手机到 PC 的「网络连接方式」，供拓扑图展示。
     * 127.0.0.1 → USB + adb 转发；否则看 PC 的 IP 落在手机哪张网卡的同网段，
     * 据此判断是 USB 网络共享 / WiFi 局域网 / 其它局域网。
     */
    fun networkMethod(host: String): String {
        val h = host.trim()
        if (h == "127.0.0.1" || h == "localhost") return "USB + adb 转发"
        val prefix = h.substringBeforeLast('.')
        for ((ip, name) in localIpv4()) {
            if (ip.substringBeforeLast('.') == prefix) {
                val n = name.lowercase()
                return when {
                    n.contains("rndis") || n.contains("usb") || n.contains("tether") -> "USB 网络共享"
                    n.contains("wlan") || n.contains("wifi") -> "WiFi 局域网"
                    else -> "局域网"
                }
            }
        }
        return "局域网（跨网段）"
    }
}
