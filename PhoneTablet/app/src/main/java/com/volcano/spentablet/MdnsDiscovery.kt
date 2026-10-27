package com.volcano.spentablet

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.os.Handler
import android.os.Looper
import android.util.Log

/**
 * mDNS（DNS-SD）自动发现 PC。
 *
 * PC 端 SpenTabletHost 启动后会在局域网广播 `_spentablet._tcp` 服务，
 * 手机端用 Android 原生 NsdManager 搜到后直接拿到「主机名 + IP + 端口」，
 * 用户不需要再手填 IP —— 这就是 Weylus / ZeroLink 等成熟方案用的同一套机制。
 *
 * 注意事项：
 *  1. Android 的 NsdManager **同一时刻只允许一个 resolveService 在飞**，
 *     这里用队列串行化，否则会抛 "already in progress" 异常、解析结果丢失。
 *  2. 回调可能来自系统线程，统一切回主线程再通知 UI。
 */
object MdnsDiscovery {

    private const val TAG = "SpenTablet"

    /** 与 PC 端 MdnsAdvertiser.ServiceType 保持一致 */
    const val SERVICE_TYPE = "_spentablet._tcp"

    /** 发现到的一台 PC */
    data class Found(
        val serviceName: String,
        /** TXT 里广告的主地址（解析出的 SRV/A 记录） */
        val host: String,
        val port: Int,
        /** PC 广告的全部候选地址（来自 TXT 的 ips 属性），按优先级排序 */
        val candidates: List<String> = listOf(host)
    ) {
        val label: String get() = "$serviceName（$host:$port）"
    }

    interface Listener {
        /** 发现结果有变化（新增 / 移除），list 为当前全部已知 PC */
        fun onServicesChanged(list: List<Found>)
        fun onError(message: String)
    }

    private val main = Handler(Looper.getMainLooper())
    private val found = LinkedHashMap<String, Found>()   // serviceName -> Found
    private val resolving = ArrayDeque<NsdServiceInfo>()  // 待解析队列
    private var resolvingNow = false

    private var nsdManager: NsdManager? = null
    private var discoveryListener: NsdManager.DiscoveryListener? = null
    private var listener: Listener? = null
    private var started = false

    /** 开始搜索。重复调用会先停止上一次。 */
    fun start(context: Context, listener: Listener) {
        stop()
        this.listener = listener
        found.clear()
        resolving.clear()
        resolvingNow = false

        val nm = context.applicationContext.getSystemService(Context.NSD_SERVICE) as? NsdManager
        if (nm == null) {
            listener.onError("系统没有 NSD 服务，mDNS 自动发现不可用")
            return
        }
        nsdManager = nm

        val dl = object : NsdManager.DiscoveryListener {
            override fun onStartDiscoveryFailed(type: String, errorCode: Int) {
                Log.w(TAG, "mDNS 发现启动失败 type=$type err=$errorCode")
                main.post { listener.onError("mDNS 发现启动失败（错误码 $errorCode）") }
            }

            override fun onStopDiscoveryFailed(type: String, errorCode: Int) {
                Log.w(TAG, "mDNS 发现停止失败 type=$type err=$errorCode")
            }

            override fun onDiscoveryStarted(type: String) {
                Log.i(TAG, "mDNS 开始搜索 $type")
            }

            override fun onDiscoveryStopped(type: String) {
                Log.i(TAG, "mDNS 停止搜索")
            }

            override fun onServiceFound(service: NsdServiceInfo) {
                Log.i(TAG, "mDNS 发现服务 ${service.serviceName}")
                main.post { enqueueResolve(service) }
            }

            override fun onServiceLost(service: NsdServiceInfo) {
                val key = service.serviceName ?: return
                main.post {
                    if (found.remove(key) != null) {
                        Log.i(TAG, "mDNS 服务丢失 $key")
                        listener?.onServicesChanged(found.values.toList())
                    }
                }
            }
        }
        discoveryListener = dl
        started = true
        try {
            nm.discoverServices(SERVICE_TYPE, NsdManager.PROTOCOL_DNS_SD, dl)
        } catch (e: Throwable) {
            Log.w(TAG, "mDNS discoverServices 异常: ${e.message}")
            main.post { listener.onError("启动 mDNS 搜索失败：${e.message}") }
        }
    }

    /** 停止搜索并清空缓存。 */
    fun stop() {
        if (!started) return
        started = false
        try {
            nsdManager?.stopServiceDiscovery(discoveryListener)
        } catch (e: Throwable) {
            Log.w(TAG, "停止 mDNS 搜索异常: ${e.message}")
        }
        discoveryListener = null
        nsdManager = null
        listener = null
        found.clear()
        resolving.clear()
        resolvingNow = false
    }

    /** 串行解析：Android 不允许多个 resolve 同时进行 */
    private fun enqueueResolve(service: NsdServiceInfo) {
        resolving.addLast(service)
        drainResolveQueue()
    }

    private fun drainResolveQueue() {
        if (resolvingNow) return
        val nm = nsdManager ?: return
        val service = resolving.removeFirstOrNull() ?: return
        resolvingNow = true

        val rl = object : NsdManager.ResolveListener {
            override fun onResolveFailed(info: NsdServiceInfo, errorCode: Int) {
                Log.w(TAG, "mDNS 解析失败 ${info.serviceName} err=$errorCode")
                resolvingNow = false
                drainResolveQueue()
            }

            override fun onServiceResolved(info: NsdServiceInfo) {
                val host = info.host?.hostAddress
                val name = info.serviceName ?: "PC"
                if (host != null) {
                    // TXT 里带的是 PC 的全部候选 IP（USB 共享 / WiFi / 有线），
                    // 手机逐个试，第一个能连上的就是真正走通的那条路。
                    val candidates = parseIpsAttribute(info) ?: listOf(host)
                    found[name] = Found(name, host, info.port, candidates)
                    Log.i(TAG, "mDNS 解析成功 $name → $host:${info.port} 候选=$candidates")
                    main.post { listener?.onServicesChanged(found.values.toList()) }
                } else {
                    Log.w(TAG, "mDNS 解析结果没有地址: $name")
                }
                resolvingNow = false
                drainResolveQueue()
            }
        }
        try {
            @Suppress("DEPRECATION")
            nm.resolveService(service, rl)
        } catch (e: Throwable) {
            Log.w(TAG, "resolveService 异常: ${e.message}")
            resolvingNow = false
            drainResolveQueue()
        }
    }

    /** 当前已知的 PC 列表（供 UI 直接展示） */
    fun current(): List<Found> = found.values.toList()

    /** 从 TXT 属性里读 PC 广告的候选 IP 列表（ips=a,b,c） */
    private fun parseIpsAttribute(info: NsdServiceInfo): List<String>? {
        return try {
            val attrs = info.attributes ?: return null
            val raw = attrs["ips"] ?: return null
            val text = String(raw, Charsets.UTF_8)
            val list = text.split(",").map { it.trim() }.filter { it.isNotEmpty() }
            if (list.isEmpty()) null else list
        } catch (e: Throwable) {
            Log.w(TAG, "解析 TXT ips 属性失败: ${e.message}")
            null
        }
    }
}
