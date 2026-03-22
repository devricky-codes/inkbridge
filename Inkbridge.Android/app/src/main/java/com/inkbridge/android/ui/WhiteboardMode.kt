package com.inkbridge.android.ui

import android.view.MotionEvent
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.ExperimentalComposeUiApi
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.drawscope.withTransform
import androidx.compose.ui.input.pointer.pointerInteropFilter
import androidx.compose.ui.unit.dp
import com.inkbridge.android.InkbridgeWebSocketClient
import org.json.JSONArray
import org.json.JSONObject
import kotlin.math.sqrt

data class WhiteboardStroke(
    val id: String,
    val points: List<Offset>,
    val color: Long = 0xFFFFFFFF,
    val width: Float = 4f
)

@OptIn(ExperimentalComposeUiApi::class)
@Composable
fun WhiteboardMode(webSocketClient: InkbridgeWebSocketClient) {
    // All strokes (local + remote)
    val strokes = remember { mutableStateListOf<WhiteboardStroke>() }
    // Current in-progress stroke
    var currentPoints by remember { mutableStateOf(listOf<Offset>()) }
    var currentStrokeId by remember { mutableStateOf("") }

    // Pan & zoom state
    var panX by remember { mutableFloatStateOf(0f) }
    var panY by remember { mutableFloatStateOf(0f) }
    var zoom by remember { mutableFloatStateOf(1f) }

    // Two-finger gesture tracking
    var prevPinchDist by remember { mutableFloatStateOf(0f) }
    var prevPinchMid by remember { mutableStateOf(Offset.Zero) }
    var isPinching by remember { mutableStateOf(false) }

    // Listen for incoming whiteboard messages from PC
    LaunchedEffect(Unit) {
        webSocketClient.onWhiteboardMessage = { json ->
            try {
                val obj = JSONObject(json)
                val type = obj.optString("type")
                if (type == "wb-stroke") {
                    val id = obj.getString("id")
                    val pointsArr = obj.getJSONArray("points")
                    val pts = mutableListOf<Offset>()
                    for (i in 0 until pointsArr.length()) {
                        val pt = pointsArr.getJSONObject(i)
                        pts.add(Offset(pt.getDouble("x").toFloat(), pt.getDouble("y").toFloat()))
                    }
                    val color = obj.optLong("color", 0xFFFFFFFF)
                    val width = obj.optDouble("width", 4.0).toFloat()
                    strokes.add(WhiteboardStroke(id, pts, color, width))
                } else if (type == "wb-clear") {
                    strokes.clear()
                }
            } catch (_: Exception) {}
        }
    }

    DisposableEffect(Unit) {
        onDispose {
            webSocketClient.onWhiteboardMessage = null
        }
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .background(Color(0xFF0A0A0A))
    ) {
        // Toolbar
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 12.dp, vertical = 6.dp),
            horizontalArrangement = Arrangement.End
        ) {
            TextButton(onClick = {
                strokes.clear()
                currentPoints = emptyList()
                // Notify PC
                val msg = JSONObject().apply { put("type", "wb-clear") }
                webSocketClient.sendText(msg.toString())
            }) {
                Text("Clear", color = Color(0xFF888888))
            }
        }

        // Infinite canvas
        Canvas(
            modifier = Modifier
                .fillMaxSize()
                .pointerInteropFilter { event ->
                    val pointerCount = event.pointerCount

                    when {
                        // Two or more fingers => pan/zoom
                        pointerCount >= 2 -> {
                            val x1 = event.getX(0)
                            val y1 = event.getY(0)
                            val x2 = event.getX(1)
                            val y2 = event.getY(1)
                            val mid = Offset((x1 + x2) / 2f, (y1 + y2) / 2f)
                            val dist = sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1))

                            when (event.actionMasked) {
                                MotionEvent.ACTION_POINTER_DOWN -> {
                                    isPinching = true
                                    prevPinchDist = dist
                                    prevPinchMid = mid
                                    // Cancel any in-progress pen stroke
                                    currentPoints = emptyList()
                                }
                                MotionEvent.ACTION_MOVE -> {
                                    if (isPinching && prevPinchDist > 0f) {
                                        val scale = dist / prevPinchDist
                                        zoom = (zoom * scale).coerceIn(0.1f, 10f)
                                        panX += mid.x - prevPinchMid.x
                                        panY += mid.y - prevPinchMid.y
                                    }
                                    prevPinchDist = dist
                                    prevPinchMid = mid
                                }
                                MotionEvent.ACTION_POINTER_UP, MotionEvent.ACTION_UP -> {
                                    isPinching = false
                                    prevPinchDist = 0f
                                }
                            }
                            true
                        }
                        // Single finger/pen => draw (only for stylus or if not pinching)
                        !isPinching -> {
                            // Convert screen coords to canvas coords
                            val canvasX = (event.x - panX) / zoom
                            val canvasY = (event.y - panY) / zoom

                            when (event.actionMasked) {
                                MotionEvent.ACTION_DOWN -> {
                                    currentStrokeId = "s_${System.currentTimeMillis()}_${(Math.random() * 10000).toInt()}"
                                    currentPoints = listOf(Offset(canvasX, canvasY))
                                }
                                MotionEvent.ACTION_MOVE -> {
                                    currentPoints = currentPoints + Offset(canvasX, canvasY)
                                }
                                MotionEvent.ACTION_UP -> {
                                    if (currentPoints.size >= 2) {
                                        val stroke = WhiteboardStroke(currentStrokeId, currentPoints)
                                        strokes.add(stroke)
                                        // Send to PC
                                        sendStroke(webSocketClient, stroke)
                                    }
                                    currentPoints = emptyList()
                                }
                            }
                            true
                        }
                        else -> true
                    }
                }
        ) {
            withTransform({
                translate(left = panX, top = panY)
                scale(zoom, zoom, Offset.Zero)
            }) {
                // Draw grid dots for spatial reference
                val gridSpacing = 80f
                val visibleLeft = -panX / zoom - gridSpacing
                val visibleTop = -panY / zoom - gridSpacing
                val visibleRight = (size.width - panX) / zoom + gridSpacing
                val visibleBottom = (size.height - panY) / zoom + gridSpacing
                val startX = (visibleLeft / gridSpacing).toInt() * gridSpacing
                val startY = (visibleTop / gridSpacing).toInt() * gridSpacing
                var gx = startX
                while (gx <= visibleRight) {
                    var gy = startY
                    while (gy <= visibleBottom) {
                        drawCircle(Color(0xFF222222), radius = 1.5f, center = Offset(gx, gy))
                        gy += gridSpacing
                    }
                    gx += gridSpacing
                }

                // Draw completed strokes
                for (stroke in strokes) {
                    if (stroke.points.size < 2) continue
                    val path = Path().apply {
                        moveTo(stroke.points[0].x, stroke.points[0].y)
                        for (i in 1 until stroke.points.size) {
                            lineTo(stroke.points[i].x, stroke.points[i].y)
                        }
                    }
                    drawPath(
                        path,
                        color = Color(stroke.color),
                        style = Stroke(
                            width = stroke.width,
                            cap = StrokeCap.Round,
                            join = StrokeJoin.Round
                        )
                    )
                }

                // Draw in-progress stroke
                if (currentPoints.size >= 2) {
                    val path = Path().apply {
                        moveTo(currentPoints[0].x, currentPoints[0].y)
                        for (i in 1 until currentPoints.size) {
                            lineTo(currentPoints[i].x, currentPoints[i].y)
                        }
                    }
                    drawPath(
                        path,
                        color = Color.White,
                        style = Stroke(
                            width = 4f,
                            cap = StrokeCap.Round,
                            join = StrokeJoin.Round
                        )
                    )
                }
            }
        }
    }
}

private fun sendStroke(ws: InkbridgeWebSocketClient, stroke: WhiteboardStroke) {
    try {
        val pointsArr = JSONArray()
        for (pt in stroke.points) {
            pointsArr.put(JSONObject().apply {
                put("x", pt.x.toDouble())
                put("y", pt.y.toDouble())
            })
        }
        val msg = JSONObject().apply {
            put("type", "wb-stroke")
            put("id", stroke.id)
            put("points", pointsArr)
            put("color", stroke.color)
            put("width", stroke.width.toDouble())
        }
        ws.sendText(msg.toString())
    } catch (_: Exception) {}
}
