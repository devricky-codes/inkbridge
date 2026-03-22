package com.inkbridge.android.ui

import android.graphics.BitmapFactory
import android.view.MotionEvent
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.ExperimentalComposeUiApi
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.input.pointer.pointerInteropFilter
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import com.inkbridge.android.InkbridgeWebSocketClient
import kotlinx.coroutines.flow.launchIn
import kotlinx.coroutines.flow.onEach
import org.json.JSONObject
import java.nio.ByteBuffer
import java.nio.ByteOrder

@OptIn(ExperimentalComposeUiApi::class)
@Composable
fun CanvasMirrorMode(
    wsClient: InkbridgeWebSocketClient,
    focusedApp: String
) {
    var size by remember { mutableStateOf(IntSize.Zero) }
    var currentBitmap by remember { mutableStateOf<androidx.compose.ui.graphics.ImageBitmap?>(null) }
    
    // Listen to frames
    LaunchedEffect(Unit) {
        wsClient.frames.onEach { bytes ->
            try {
                val bmp = BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
                if (bmp != null) currentBitmap = bmp.asImageBitmap()
            } catch (e: Exception) {}
        }.launchIn(this)
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .background(Color(0xFF0A0A0A))
    ) {
        // Top Context Bar
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 16.dp, vertical = 8.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.SpaceBetween
        ) {
            Text(
                text = focusedApp,
                style = MaterialTheme.typography.labelMedium
            )
            
            Row(horizontalArrangement = Arrangement.spacedBy(16.dp), verticalAlignment = Alignment.CenterVertically) {
                // Pen sizes
                listOf(2.dp, 4.dp, 6.dp, 8.dp).forEach { sz ->
                    Box(modifier = Modifier.size(24.dp), contentAlignment = Alignment.Center) {
                        Box(modifier = Modifier.size(sz).background(Color.White, CircleShape))
                    }
                }
                
                Text("|", color = Color.DarkGray)
                
                TextButton(onClick = {
                    val json = JSONObject().apply { put("type", "undo") }
                    wsClient.sendText(json.toString())
                }) {
                    Text("Undo", color = Color.White)
                }
                TextButton(onClick = {
                    val json = JSONObject().apply { put("type", "clear") }
                    wsClient.sendText(json.toString())
                }) {
                    Text("Clear", color = Color.White)
                }
            }
        }

        Divider(color = Color.DarkGray, thickness = 0.5.dp)

        // Surface / Canvas
        Box(
            modifier = Modifier
                .weight(1f)
                .fillMaxWidth()
                .onSizeChanged { size = it }
                .pointerInteropFilter { event ->
                    if (event.getToolType(0) == MotionEvent.TOOL_TYPE_STYLUS || event.getToolType(0) == MotionEvent.TOOL_TYPE_FINGER) {
                        val phase: Byte = when (event.actionMasked) {
                            MotionEvent.ACTION_DOWN -> 0
                            MotionEvent.ACTION_MOVE -> 1
                            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> 2
                            else -> return@pointerInteropFilter false
                        }
                        
                        val w = size.width.coerceAtLeast(1).toFloat()
                        val h = size.height.coerceAtLeast(1).toFloat()
                        
                        for (i in 0 until event.historySize) {
                            val nx = event.getHistoricalX(i) / w
                            val ny = event.getHistoricalY(i) / h
                            val p = event.getHistoricalPressure(i)
                            val ts = event.getHistoricalEventTime(i)
                            sendStroke(wsClient, phase, nx, ny, p, ts)
                        }
                        val nx = event.x / w
                        val ny = event.y / h
                        val p = event.pressure
                        val ts = event.eventTime
                        sendStroke(wsClient, phase, nx, ny, p, ts)
                        
                        return@pointerInteropFilter true
                    }
                    false
                }
        ) {
            Canvas(modifier = Modifier.fillMaxSize()) {
                currentBitmap?.let { bmp ->
                    drawImage(
                        image = bmp,
                        dstSize = androidx.compose.ui.unit.IntSize(size.width, size.height)
                    )
                }
            }
        }
    }
}

private fun sendStroke(ws: InkbridgeWebSocketClient, phase: Byte, x: Float, y: Float, p: Float, ts: Long) {
    val bb = ByteBuffer.allocate(21)
    bb.order(ByteOrder.LITTLE_ENDIAN)
    bb.put(phase)
    bb.putFloat(x)
    bb.putFloat(y)
    bb.putFloat(p)
    bb.putLong(ts)
    ws.sendBinary(bb.array())
}
