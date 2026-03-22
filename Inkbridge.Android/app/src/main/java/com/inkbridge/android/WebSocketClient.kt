package com.inkbridge.android

import android.util.Log
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import okhttp3.*
import okio.ByteString
import okio.ByteString.Companion.toByteString
import java.util.concurrent.TimeUnit

enum class ConnectionState { Disconnected, Connecting, Connected, Failed }

class InkbridgeWebSocketClient {
    private val client = OkHttpClient.Builder()
        .pingInterval(15, TimeUnit.SECONDS)
        .build()
    private var webSocket: WebSocket? = null
    private var lastUrl: String? = null

    private val _connectionState = MutableStateFlow(ConnectionState.Disconnected)
    val connectionState: StateFlow<ConnectionState> = _connectionState

    private val _messages = MutableSharedFlow<String>(extraBufferCapacity = 64)
    val messages: SharedFlow<String> = _messages

    private val _frames = MutableSharedFlow<ByteArray>(extraBufferCapacity = 64)
    val frames: SharedFlow<ByteArray> = _frames

    var onWhiteboardMessage: ((String) -> Unit)? = null

    fun connect(url: String) {
        lastUrl = url
        _connectionState.value = ConnectionState.Connecting
        val request = Request.Builder().url(url).build()
        webSocket = client.newWebSocket(request, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                Log.d("Inkbridge", "WebSocket connected")
                _connectionState.value = ConnectionState.Connected
            }

            override fun onMessage(webSocket: WebSocket, text: String) {
                if (text.contains("\"wb-")) {
                    onWhiteboardMessage?.invoke(text)
                }
                _messages.tryEmit(text)
            }

            override fun onMessage(webSocket: WebSocket, bytes: ByteString) {
                _frames.tryEmit(bytes.toByteArray())
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                Log.e("Inkbridge", "WebSocket Error: ${t.message}")
                _connectionState.value = ConnectionState.Failed
            }

            override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                _connectionState.value = ConnectionState.Disconnected
            }

            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                _connectionState.value = ConnectionState.Disconnected
            }
        })
    }

    fun reconnect() {
        val url = lastUrl ?: return
        disconnect()
        connect(url)
    }

    fun sendText(json: String) {
        webSocket?.send(json)
    }

    fun sendBinary(bytes: ByteArray) {
        webSocket?.send(bytes.toByteString())
    }

    fun disconnect() {
        try { webSocket?.close(1000, "User requested") } catch (_: Exception) {}
        webSocket = null
        _connectionState.value = ConnectionState.Disconnected
    }
}
