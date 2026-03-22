package com.inkbridge.android

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.inkbridge.android.ui.TextInjectMode
import com.inkbridge.android.ui.WhiteboardMode
import com.inkbridge.android.ui.theme.InkbridgeTheme
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.launchIn
import kotlinx.coroutines.flow.onEach
import org.json.JSONObject

class MainActivity : ComponentActivity() {
    private lateinit var networkManager: NetworkManager
    private lateinit var wsClient: InkbridgeWebSocketClient

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        networkManager = NetworkManager(this)
        wsClient = InkbridgeWebSocketClient()

        setContent {
            InkbridgeTheme {
                MainScreen(networkManager, wsClient)
            }
        }

        networkManager.startDiscovery()
    }

    override fun onDestroy() {
        super.onDestroy()
        networkManager.stopDiscovery()
        wsClient.disconnect()
    }
}

@Composable
fun MainScreen(networkManager: NetworkManager, wsClient: InkbridgeWebSocketClient) {
    val autoIp by networkManager.discoveredIp.collectAsState()
    var manualIp by remember { mutableStateOf("") }
    var targetUrl by remember { mutableStateOf<String?>(null) }
    val connectionState by wsClient.connectionState.collectAsState()
    var mode by remember { mutableStateOf("text") }
    var focusedApp by remember { mutableStateOf("Desktop") }
    var focusedTitle by remember { mutableStateOf("Waiting for connection...") }
    var injectMethod by remember { mutableStateOf("-") }

    // Auto-connect when mDNS finds an address
    LaunchedEffect(autoIp) {
        autoIp?.let { url ->
            if (targetUrl != url) {
                targetUrl = url
                wsClient.disconnect()
                wsClient.connect(url)
            }
        }
    }

    // Auto-reconnect on failure/disconnect (retry every 3s)
    LaunchedEffect(connectionState) {
        if (connectionState == ConnectionState.Failed || connectionState == ConnectionState.Disconnected) {
            if (targetUrl != null) {
                delay(3000)
                wsClient.reconnect()
            }
        }
    }

    // Listen for focus messages
    LaunchedEffect(Unit) {
        wsClient.messages.onEach { msg ->
            try {
                val json = JSONObject(msg)
                if (json.optString("type") == "focus") {
                    focusedApp = json.optString("app", "Unknown")
                    focusedTitle = json.optString("window", "Unknown")
                    injectMethod = json.optString("method", "-")
                }
            } catch (_: Exception) {}
        }.launchIn(this)
    }

    Column(modifier = Modifier.fillMaxSize().background(Color(0xFF0A0A0A))) {

        // ── Connection bar ──────────────────────────────────────────────
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .background(Color(0xFF111111))
                .padding(horizontal = 16.dp, vertical = 8.dp)
        ) {
            when (connectionState) {
                ConnectionState.Connected -> Text("● Connected to $targetUrl", color = Color(0xFF4CAF50), fontSize = 13.sp)
                ConnectionState.Connecting -> Text("Connecting to $targetUrl…", color = Color(0xFFFFAA00), fontSize = 13.sp)
                ConnectionState.Failed -> Text("● Reconnecting…", color = Color(0xFFFF5252), fontSize = 13.sp)
                ConnectionState.Disconnected -> Text(
                    if (targetUrl != null) "● Reconnecting…"
                    else if (autoIp == null) "Scanning for Inkbridge PC…"
                    else "Found PC – tap Connect",
                    color = Color(0xFFFFAA00), fontSize = 13.sp
                )
            }

            Spacer(modifier = Modifier.height(6.dp))

            // Manual IP row
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                BasicTextField(
                    value = manualIp,
                    onValueChange = { manualIp = it },
                    singleLine = true,
                    textStyle = TextStyle(color = Color.White, fontSize = 14.sp),
                    cursorBrush = SolidColor(Color.White),
                    keyboardOptions = KeyboardOptions(
                        keyboardType = KeyboardType.Uri,
                        imeAction = ImeAction.Done
                    ),
                    keyboardActions = KeyboardActions(onDone = {
                        val url = buildUrl(manualIp)
                        targetUrl = url
                        wsClient.disconnect()
                        wsClient.connect(url)
                    }),
                    decorationBox = { inner ->
                        Box(
                            modifier = Modifier
                                .weight(1f)
                                .background(Color(0xFF1E1E1E), shape = androidx.compose.foundation.shape.RoundedCornerShape(6.dp))
                                .padding(horizontal = 12.dp, vertical = 8.dp)
                        ) {
                            if (manualIp.isEmpty()) Text("PC IP  e.g. 192.168.29.50", color = Color(0xFF555555), fontSize = 14.sp)
                            inner()
                        }
                    },
                    modifier = Modifier.weight(1f)
                )
                Button(
                    onClick = {
                        val url = buildUrl(manualIp)
                        targetUrl = url
                        wsClient.disconnect()
                        wsClient.connect(url)
                    },
                    colors = ButtonDefaults.buttonColors(containerColor = Color.White),
                    modifier = Modifier.height(36.dp)
                ) {
                    Text("Connect", color = Color.Black, fontSize = 13.sp)
                }
            }
        }

        Divider(color = Color(0xFF222222), thickness = 0.5.dp)

        // ── Mode tabs ───────────────────────────────────────────────────
        Row(
            modifier = Modifier.fillMaxWidth().height(44.dp),
            horizontalArrangement = Arrangement.Center,
            verticalAlignment = Alignment.CenterVertically
        ) {
            TextButton(onClick = { mode = "text" }) {
                Text("inkbridge text", color = if (mode == "text") Color.White else Color.DarkGray)
            }
            Text("|", color = Color.DarkGray, modifier = Modifier.padding(horizontal = 8.dp))
            TextButton(onClick = { mode = "whiteboard" }) {
                Text("whiteboard", color = if (mode == "whiteboard") Color.White else Color.DarkGray)
            }
        }

        Divider(color = Color(0xFF222222), thickness = 0.5.dp)

        when (mode) {
            "text" -> TextInjectMode(wsClient, focusedApp, focusedTitle, injectMethod)
            "whiteboard" -> WhiteboardMode(wsClient)
        }
    }
}

private fun buildUrl(input: String): String {
    val trimmed = input.trim()
    return if (trimmed.startsWith("ws://") || trimmed.startsWith("wss://")) trimmed
    else "ws://$trimmed:8765"
}
