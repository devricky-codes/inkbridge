package com.inkbridge.android

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.util.Log
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

class NetworkManager(context: Context) {
    private val nsdManager = context.getSystemService(Context.NSD_SERVICE) as NsdManager
    private val serviceType = "_inkbridge._tcp."
    
    private val _discoveredIp = MutableStateFlow<String?>(null)
    val discoveredIp: StateFlow<String?> = _discoveredIp

    private val discoveryListener = object : NsdManager.DiscoveryListener {
        override fun onStartDiscoveryFailed(type: String, errorCode: Int) {
            Log.e("Inkbridge", "Discovery failed: $errorCode")
        }
        override fun onStopDiscoveryFailed(type: String, errorCode: Int) { }
        override fun onDiscoveryStarted(regType: String) { }
        override fun onDiscoveryStopped(type: String) { }

        override fun onServiceFound(serviceInfo: NsdServiceInfo) {
            if (serviceInfo.serviceType == serviceType) {
                nsdManager.resolveService(serviceInfo, object : NsdManager.ResolveListener {
                    override fun onResolveFailed(serviceInfo: NsdServiceInfo, errorCode: Int) { }
                    override fun onServiceResolved(serviceInfo: NsdServiceInfo) {
                        val host = serviceInfo.host.hostAddress
                        val port = serviceInfo.port
                        if (host != null) {
                            _discoveredIp.value = "ws://$host:$port"
                        }
                    }
                })
            }
        }
        override fun onServiceLost(serviceInfo: NsdServiceInfo) { }
    }

    fun startDiscovery() {
        try {
            nsdManager.discoverServices(serviceType, NsdManager.PROTOCOL_DNS_SD, discoveryListener)
        } catch (e: Exception) {
            Log.e("Inkbridge", "Already discovering")
        }
    }

    fun stopDiscovery() {
        try {
            nsdManager.stopServiceDiscovery(discoveryListener)
        } catch (e: Exception) {}
    }
}
