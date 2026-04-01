package com.inkbridge.android.ui

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.os.Handler
import android.os.Looper
import android.util.Base64
import android.util.Log
import android.view.MotionEvent
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.ExperimentalComposeUiApi
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.graphics.drawscope.Fill
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.drawscope.withTransform
import androidx.compose.ui.input.pointer.pointerInteropFilter
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.inkbridge.android.InkbridgeWebSocketClient
import org.json.JSONArray
import org.json.JSONObject
import kotlin.math.abs
import kotlin.math.min
import kotlin.math.sqrt

// Shared classes are in WhiteboardMode.kt
private fun argbColor(c: Long): Color = Color(c.toInt())

@OptIn(ExperimentalComposeUiApi::class)
@Composable
fun OverlayMode(webSocketClient: InkbridgeWebSocketClient) {
    val strokes = remember { mutableStateListOf<WbStroke>() }
    val shapes = remember { mutableStateListOf<WbShape>() }
    val images = remember { mutableStateListOf<WbImage>() }

    // Undo history: stores (type, id) of each action for undo
    val undoStack = remember { mutableStateListOf<Pair<String, String>>() } // ("stroke"/"shape", id)

    var currentPoints by remember { mutableStateOf(listOf<Offset>()) }
    var currentStrokeId by remember { mutableStateOf("") }
    val mainHandler = remember { Handler(Looper.getMainLooper()) }

    // Tool state
    var activeTool by remember { mutableStateOf(WbTool.Pen) }
    var penColor by remember { mutableLongStateOf(0xFFFFFFFF) }
    var penWidth by remember { mutableFloatStateOf(4f) }
    var shapeFillColor by remember { mutableLongStateOf(0x00000000) } // transparent
    var showColorPicker by remember { mutableStateOf(false) }
    var showFillPicker by remember { mutableStateOf(false) }

    // Shape drawing anchor
    var shapeAnchor by remember { mutableStateOf<Offset?>(null) }
    var shapeDrag by remember { mutableStateOf<Offset?>(null) }

    // Pan & zoom
    var panX by remember { mutableFloatStateOf(0f) }
    var panY by remember { mutableFloatStateOf(0f) }
    var zoom by remember { mutableFloatStateOf(1f) }
    var prevPinchDist by remember { mutableFloatStateOf(0f) }
    var prevPinchMid by remember { mutableStateOf(Offset.Zero) }
    var isPinching by remember { mutableStateOf(false) }
    var handDragLast by remember { mutableStateOf(Offset.Zero) }
    var zoomEnabled by remember { mutableStateOf(false) }

    // Tablet is busy flag — when true, ignore PC messages
    var tabletBusy by remember { mutableStateOf(false) }

    // Chunked image receiving state
    val imageChunkBuffers = remember { mutableMapOf<String, ImageChunkState>() }
    var imageLoadingText by remember { mutableStateOf<String?>(null) }

    // Overlay streamed background
    var overlayBg by remember { mutableStateOf<Bitmap?>(null) }

    // Listen for incoming messages
    LaunchedEffect(Unit) {
        webSocketClient.onWhiteboardMessage = handler@{ json ->
            // Conflict rule: if tablet is busy, ignore PC-originated updates
            if (tabletBusy) return@handler
            try {
                val obj = JSONObject(json)
                val type = obj.optString("type")
                // Parse on background, update UI on main
                when (type) {
                    "wb-overlay-stroke" -> {
                        val id = obj.getString("id")
                        val pts = parsePoints(obj.getJSONArray("points"))
                        val c = obj.optLong("color", 0xFFFFFFFF)
                        val w = obj.optDouble("width", 4.0).toFloat()
                        mainHandler.post { strokes.add(WbStroke(id, pts, c, w)) }
                    }
                    "wb-overlay-erase" -> {
                        val id = obj.getString("id")
                        mainHandler.post {
                            strokes.removeAll { it.id == id }
                            shapes.removeAll { it.id == id }
                        }
                    }
                    "wb-overlay-shape" -> {
                        val s = parseShape(obj)
                        mainHandler.post { shapes.removeAll { it.id == s.id }; shapes.add(s) }
                    }
                    "wb-image" -> {
                        val id = obj.getString("id")
                        val x = obj.optDouble("x", 0.0).toFloat()
                        val y = obj.optDouble("y", 0.0).toFloat()
                        val w = obj.optDouble("w", 200.0).toFloat()
                        val h = obj.optDouble("h", 200.0).toFloat()
                        val b64 = obj.optString("data", "")
                        if (b64.isNotEmpty()) {
                            val bytes = Base64.decode(b64, Base64.DEFAULT)
                            val bmp = BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
                            if (bmp != null) {
                                mainHandler.post {
                                    images.removeAll { it.id == id }
                                    images.add(WbImage(id, x, y, w, h, bmp))
                                }
                            } else {
                                Log.e("Inkbridge", "wb-image: bitmap decode failed for id=$id, b64 len=${b64.length}")
                            }
                        } else {
                            Log.e("Inkbridge", "wb-image: empty data for id=$id")
                        }
                    }
                    "wb-image-begin" -> {
                        val id = obj.getString("id")
                        val x = obj.optDouble("x", 0.0).toFloat()
                        val y = obj.optDouble("y", 0.0).toFloat()
                        val w = obj.optDouble("w", 200.0).toFloat()
                        val h = obj.optDouble("h", 200.0).toFloat()
                        val totalChunks = obj.getInt("totalChunks")
                        imageChunkBuffers[id] = ImageChunkState(x, y, w, h, totalChunks)
                        mainHandler.post { imageLoadingText = "Receiving image... 0/$totalChunks" }
                    }
                    "wb-image-chunk" -> {
                        val id = obj.getString("id")
                        val index = obj.getInt("index")
                        val data = obj.getString("data")
                        val state = imageChunkBuffers[id]
                        if (state != null) {
                            state.chunks[index] = data
                            val received = state.chunks.size
                            mainHandler.post { imageLoadingText = "Receiving image... $received/${state.totalChunks}" }
                        }
                    }
                    "wb-image-end" -> {
                        val id = obj.getString("id")
                        val state = imageChunkBuffers.remove(id)
                        if (state != null) {
                            val b64 = buildString {
                                for (i in 0 until state.totalChunks) {
                                    append(state.chunks[i] ?: "")
                                }
                            }
                            val bytes = Base64.decode(b64, Base64.DEFAULT)
                            val bmp = BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
                            if (bmp != null) {
                                mainHandler.post {
                                    images.removeAll { it.id == id }
                                    images.add(WbImage(id, state.x, state.y, state.w, state.h, bmp))
                                    imageLoadingText = null
                                }
                            } else {
                                Log.e("Inkbridge", "wb-image-end: decode failed for id=$id, b64 len=${b64.length}")
                                mainHandler.post { imageLoadingText = null }
                            }
                        } else {
                            mainHandler.post { imageLoadingText = null }
                        }
                    }
                    "wb-image-move" -> {
                        val id = obj.getString("id")
                        val x = obj.optDouble("x", 0.0).toFloat()
                        val y = obj.optDouble("y", 0.0).toFloat()
                        val w = obj.optDouble("w", 200.0).toFloat()
                        val h = obj.optDouble("h", 200.0).toFloat()
                        mainHandler.post {
                            val idx = images.indexOfFirst { it.id == id }
                            if (idx >= 0) {
                                images[idx] = images[idx].copy(x = x, y = y, w = w, h = h)
                            }
                        }
                    }
                    "wb-overlay-clear" -> {
                        mainHandler.post { strokes.clear(); shapes.clear(); images.clear(); undoStack.clear() }
                    }
                    "wb-overlay-frame" -> {
                        val b64 = obj.optString("data", "")
                        if (b64.isNotEmpty()) {
                            val bytes = Base64.decode(b64, Base64.DEFAULT)
                            val bmp = BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
                            if (bmp != null) mainHandler.post { overlayBg = bmp }
                        }
                    }
                    "wb-overlay-closed" -> {
                        mainHandler.post { 
                            overlayBg = null
                            strokes.clear(); shapes.clear(); images.clear(); undoStack.clear()
                        }
                    }
                }
            } catch (e: Exception) {
                Log.e("Inkbridge", "wb message error: ${e.message}")
            }
        }
    }

    DisposableEffect(Unit) { onDispose { webSocketClient.onWhiteboardMessage = null } }

    Box(Modifier.fillMaxSize()) {
    Column(Modifier.fillMaxSize().background(Color(0xFF0A0A0A))) {

        // ── Toolbar row 1: Tools ──
        Row(
            Modifier.fillMaxWidth()
                .background(Color(0xFF111111))
                .horizontalScroll(rememberScrollState())
                .padding(horizontal = 8.dp, vertical = 4.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            ToolBtn("✏️", activeTool == WbTool.Pen) { activeTool = WbTool.Pen }
            ToolBtn("🧹", activeTool == WbTool.Eraser) { activeTool = WbTool.Eraser }
            ToolBtn("🤚", activeTool == WbTool.Hand) { activeTool = WbTool.Hand }
            ToolBtn("🔍", zoomEnabled) { zoomEnabled = !zoomEnabled }
            Spacer(Modifier.width(8.dp))
            ToolBtn("▭", activeTool == WbTool.Rect) { activeTool = WbTool.Rect }
            ToolBtn("◯", activeTool == WbTool.Circle) { activeTool = WbTool.Circle }
            ToolBtn("╱", activeTool == WbTool.Line) { activeTool = WbTool.Line }

            Spacer(Modifier.width(12.dp))

            // Color dot
            Box(
                Modifier.size(28.dp).background(Color(penColor), CircleShape)
                    .border(1.dp, Color.Gray, CircleShape)
                    .clickable { showColorPicker = !showColorPicker; showFillPicker = false }
            )
            Spacer(Modifier.width(6.dp))
            // Fill dot (for shapes)
            if (activeTool in listOf(WbTool.Rect, WbTool.Circle)) {
                Box(
                    Modifier.size(28.dp)
                        .background(if (shapeFillColor.toULong() == 0x00000000UL) Color(0xFF0A0A0A) else Color(shapeFillColor), CircleShape)
                        .border(1.dp, Color(0xFF666666), CircleShape)
                        .clickable { showFillPicker = !showFillPicker; showColorPicker = false }
                ) {
                    if (shapeFillColor.toULong() == 0x00000000UL) {
                        Text("∅", color = Color.Gray, fontSize = 12.sp, modifier = Modifier.align(Alignment.Center))
                    }
                }
                Spacer(Modifier.width(6.dp))
            }

            // Thickness slider
            Text("W", color = Color.Gray, fontSize = 12.sp)
            Slider(
                value = penWidth,
                onValueChange = { penWidth = it },
                valueRange = 1f..20f,
                modifier = Modifier.width(100.dp)
            )

            Spacer(Modifier.weight(1f))
            TextButton(onClick = {
                // Undo last action
                if (undoStack.isNotEmpty()) {
                    val (kind, id) = undoStack.removeAt(undoStack.lastIndex)
                    when (kind) {
                        "stroke" -> strokes.removeAll { it.id == id }
                        "shape" -> shapes.removeAll { it.id == id }
                    }
                    sendErase(webSocketClient, id)
                }
            }) {
                Text("↩", color = if (undoStack.isNotEmpty()) Color(0xFFFFAA00) else Color(0xFF444444), fontSize = 18.sp)
            }
            TextButton(onClick = { resync(webSocketClient, strokes, shapes, images) }) {
                Text("Resync", color = Color(0xFF4488FF))
            }
            TextButton(onClick = {
                strokes.clear(); shapes.clear(); images.clear(); currentPoints = emptyList(); undoStack.clear()
                val msg = JSONObject().apply { put("type", "wb-overlay-clear") }
                webSocketClient.sendText(msg.toString())
            }) {
                Text("Clear", color = Color(0xFF888888))
            }
        }

        // ── Color picker row ──
        if (showColorPicker) {
            ColorRow(penColor) { penColor = it; showColorPicker = false }
        }
        if (showFillPicker) {
            FillColorRow(shapeFillColor) { shapeFillColor = it; showFillPicker = false }
        }

        // ── Canvas ──
        Canvas(
            modifier = Modifier.fillMaxSize().clipToBounds().pointerInteropFilter { event ->
                val pointerCount = event.pointerCount

                when {
                    pointerCount >= 2 -> {
                        if (!zoomEnabled) return@pointerInteropFilter true
                        val x1 = event.getX(0); val y1 = event.getY(0)
                        val x2 = event.getX(1); val y2 = event.getY(1)
                        val mid = Offset((x1 + x2) / 2f, (y1 + y2) / 2f)
                        val dist = sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1))
                        when (event.actionMasked) {
                            MotionEvent.ACTION_POINTER_DOWN -> {
                                isPinching = true; prevPinchDist = dist; prevPinchMid = mid
                                currentPoints = emptyList(); shapeAnchor = null
                            }
                            MotionEvent.ACTION_MOVE -> {
                                if (isPinching && prevPinchDist > 0f) {
                                    val s = dist / prevPinchDist
                                    zoom = (zoom * s).coerceIn(0.1f, 10f)
                                    panX += mid.x - prevPinchMid.x
                                    panY += mid.y - prevPinchMid.y
                                }
                                prevPinchDist = dist; prevPinchMid = mid
                            }
                            MotionEvent.ACTION_POINTER_UP, MotionEvent.ACTION_UP -> {
                                isPinching = false; prevPinchDist = 0f
                            }
                        }
                        true
                    }
                    !isPinching -> {
                        val cx = (event.x - panX) / zoom
                        val cy = (event.y - panY) / zoom

                        when (activeTool) {
                            WbTool.Hand -> when (event.actionMasked) {
                                MotionEvent.ACTION_DOWN -> { handDragLast = Offset(event.x, event.y) }
                                MotionEvent.ACTION_MOVE -> {
                                    panX += event.x - handDragLast.x
                                    panY += event.y - handDragLast.y
                                    handDragLast = Offset(event.x, event.y)
                                }
                                else -> {}
                            }

                            WbTool.Pen -> handlePen(event, cx, cy, penColor, penWidth,
                                { currentStrokeId = it }, { currentPoints = it },
                                currentPoints, currentStrokeId,
                                strokes, undoStack, webSocketClient, { tabletBusy = it })

                            WbTool.Eraser -> handleEraser(event, cx, cy, strokes, shapes, webSocketClient)

                            WbTool.Rect, WbTool.Circle, WbTool.Line -> {
                                when (event.actionMasked) {
                                    MotionEvent.ACTION_DOWN -> {
                                        tabletBusy = true
                                        shapeAnchor = Offset(cx, cy); shapeDrag = Offset(cx, cy)
                                    }
                                    MotionEvent.ACTION_MOVE -> { shapeDrag = Offset(cx, cy) }
                                    MotionEvent.ACTION_UP -> {
                                        val a = shapeAnchor; val d = shapeDrag
                                        if (a != null && d != null && (abs(d.x - a.x) > 5 || abs(d.y - a.y) > 5)) {
                                            val kind = when (activeTool) { WbTool.Rect -> "rect"; WbTool.Circle -> "circle"; else -> "line" }
                                            val id = "sh_${System.currentTimeMillis()}_${(Math.random() * 10000).toInt()}"
                                            val shape = WbShape(id, kind, a.x, a.y, d.x, d.y, penColor, shapeFillColor, penWidth)
                                            shapes.add(shape)
                                            undoStack.add("shape" to id)
                                            sendShape(webSocketClient, shape)
                                        }
                                        shapeAnchor = null; shapeDrag = null; tabletBusy = false
                                    }
                                }
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
                drawGrid(panX, panY, zoom, size)

                // Draw overlay background frame
                overlayBg?.let { bg ->
                    drawImage(
                        bg.asImageBitmap(),
                        dstOffset = IntOffset.Zero,
                        dstSize = IntSize(bg.width, bg.height)
                    )
                }

                // Images
                for (img in images) {
                    drawImage(
                        img.bitmap.asImageBitmap(),
                        dstOffset = IntOffset(img.x.toInt(), img.y.toInt()),
                        dstSize = IntSize(img.w.toInt(), img.h.toInt())
                    )
                }

                // Strokes
                for (stroke in strokes) { drawWbStroke(stroke) }

                // Shapes
                for (shape in shapes) { drawWbShape(shape) }

                // In-progress stroke
                if (currentPoints.size >= 2) {
                    val path = Path().apply {
                        moveTo(currentPoints[0].x, currentPoints[0].y)
                        for (i in 1 until currentPoints.size) lineTo(currentPoints[i].x, currentPoints[i].y)
                    }
                    drawPath(path, argbColor(penColor), style = Stroke(penWidth, cap = StrokeCap.Round, join = StrokeJoin.Round))
                }

                // In-progress shape preview
                val a = shapeAnchor; val d = shapeDrag
                if (a != null && d != null) {
                    drawShapePreview(activeTool, a, d, penColor, shapeFillColor, penWidth)
                }
            }
        }
    }
    // Loading overlay for chunked image receiving
    if (imageLoadingText != null) {
        Box(
            Modifier.fillMaxSize().background(Color(0xCC000000)),
            contentAlignment = Alignment.Center
        ) {
            Text(imageLoadingText ?: "", color = Color.White, fontSize = 18.sp)
        }
    }
    } // end Box
}

// ── Tool button ──

@Composable
private fun ToolBtn(label: String, selected: Boolean, onClick: () -> Unit) {
    TextButton(
        onClick = onClick,
        modifier = Modifier.defaultMinSize(minWidth = 40.dp),
        colors = ButtonDefaults.textButtonColors(
            containerColor = if (selected) Color(0xFF333333) else Color.Transparent
        )
    ) {
        Text(label, fontSize = 16.sp, color = if (selected) Color.White else Color.Gray)
    }
}

// ── Color pickers ──

private val PALETTE = listOf(
    0xFFFFFFFF, 0xFFFF4444, 0xFFFF8800, 0xFFFFFF00, 0xFF44FF44,
    0xFF4488FF, 0xFF8844FF, 0xFFFF44FF, 0xFF00DDDD, 0xFF888888
)

@Composable
private fun ColorRow(current: Long, onPick: (Long) -> Unit) {
    var hexText by remember { mutableStateOf("") }
    Column(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 4.dp)) {
        Row {
            PALETTE.forEach { c ->
                val cl = c.toLong()
                Box(
                    Modifier.size(32.dp).padding(2.dp)
                        .background(argbColor(cl), CircleShape)
                        .border(if (cl.toInt() == current.toInt()) 2.dp else 0.dp, Color.White, CircleShape)
                        .clickable { onPick(cl) }
                )
            }
        }
        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(top = 4.dp)) {
            Text("#", color = Color.Gray, fontSize = 14.sp)
            OutlinedTextField(
                value = hexText,
                onValueChange = { hexText = it.take(8) },
                modifier = Modifier.width(140.dp).height(44.dp),
                textStyle = androidx.compose.ui.text.TextStyle(color = Color.White, fontSize = 13.sp),
                singleLine = true,
                placeholder = { Text("RRGGBB or AARRGGBB", color = Color(0xFF555555), fontSize = 11.sp) },
                colors = OutlinedTextFieldDefaults.colors(
                    focusedBorderColor = Color(0xFF4488FF),
                    unfocusedBorderColor = Color(0xFF444444),
                    cursorColor = Color.White
                )
            )
            Spacer(Modifier.width(6.dp))
            TextButton(onClick = {
                val hex = hexText.trim().removePrefix("#")
                val parsed = when (hex.length) {
                    6 -> 0xFF000000L or (hex.toLongOrNull(16) ?: return@TextButton)
                    8 -> hex.toLongOrNull(16) ?: return@TextButton
                    else -> return@TextButton
                }
                onPick(parsed)
            }) {
                Text("Apply", color = Color(0xFF4488FF), fontSize = 13.sp)
            }
            // Preview of current color
            Spacer(Modifier.width(4.dp))
            Box(Modifier.size(24.dp).background(argbColor(current), CircleShape).border(1.dp, Color.Gray, CircleShape))
        }
    }
}

@Composable
private fun FillColorRow(current: Long, onPick: (Long) -> Unit) {
    Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 4.dp), verticalAlignment = Alignment.CenterVertically) {
        // Transparent option
        Box(
            Modifier.size(32.dp).padding(2.dp)
                .background(Color(0xFF0A0A0A), CircleShape)
                .border(if (current.toInt() == 0) 2.dp else 1.dp, Color.Gray, CircleShape)
                .clickable { onPick(0x00000000L) }
        ) { Text("∅", color = Color.Gray, fontSize = 11.sp, modifier = Modifier.align(Alignment.Center)) }
        PALETTE.forEach { c ->
            val cl = c.toLong()
            Box(
                Modifier.size(32.dp).padding(2.dp)
                    .background(argbColor(cl), CircleShape)
                    .border(if (cl.toInt() == current.toInt()) 2.dp else 0.dp, Color.White, CircleShape)
                    .clickable { onPick(cl) }
            )
        }
    }
}

// ── Pen handler ──

private fun handlePen(
    event: MotionEvent, cx: Float, cy: Float,
    penColor: Long, penWidth: Float,
    setStrokeId: (String) -> Unit, setPoints: (List<Offset>) -> Unit,
    currentPoints: List<Offset>, currentStrokeId: String,
    strokes: MutableList<WbStroke>, undoStack: MutableList<Pair<String, String>>,
    ws: InkbridgeWebSocketClient,
    setBusy: (Boolean) -> Unit
) {
    when (event.actionMasked) {
        MotionEvent.ACTION_DOWN -> {
            setBusy(true)
            setStrokeId("s_${System.currentTimeMillis()}_${(Math.random() * 10000).toInt()}")
            setPoints(listOf(Offset(cx, cy)))
        }
        MotionEvent.ACTION_MOVE -> { setPoints(currentPoints + Offset(cx, cy)) }
        MotionEvent.ACTION_UP -> {
            val pts = currentPoints + Offset(cx, cy)
            if (pts.size >= 2) {
                val stroke = WbStroke(currentStrokeId, pts, penColor, penWidth)
                strokes.add(stroke)
                undoStack.add("stroke" to currentStrokeId)
                sendStroke(ws, stroke)
            }
            setPoints(emptyList())
            setBusy(false)
        }
    }
}

// ── Eraser handler ──

private fun handleEraser(
    event: MotionEvent, cx: Float, cy: Float,
    strokes: MutableList<WbStroke>, shapes: MutableList<WbShape>,
    ws: InkbridgeWebSocketClient
) {
    if (event.actionMasked != MotionEvent.ACTION_DOWN && event.actionMasked != MotionEvent.ACTION_MOVE) return
    val hitRadius = 24f
    // Check strokes — remove first one whose any point is within radius
    val hitStroke = strokes.firstOrNull { s ->
        s.points.any { p -> sqrt((p.x - cx) * (p.x - cx) + (p.y - cy) * (p.y - cy)) < hitRadius }
    }
    if (hitStroke != null) {
        strokes.remove(hitStroke)
        sendErase(ws, hitStroke.id)
        return
    }
    // Check shapes
    val hitShape = shapes.firstOrNull { s ->
        val rect = Rect(
            minOf(s.x1, s.x2), minOf(s.y1, s.y2),
            maxOf(s.x1, s.x2), maxOf(s.y1, s.y2)
        )
        rect.contains(Offset(cx, cy)) || sqrt((s.x1 - cx) * (s.x1 - cx) + (s.y1 - cy) * (s.y1 - cy)) < hitRadius
    }
    if (hitShape != null) {
        shapes.remove(hitShape)
        sendErase(ws, hitShape.id)
    }
}

// ── Draw helpers ──

private fun DrawScope.drawGrid(panX: Float, panY: Float, zoom: Float, sz: Size) {
    val gridSpacing = 80f
    val vl = -panX / zoom - gridSpacing; val vt = -panY / zoom - gridSpacing
    val vr = (sz.width - panX) / zoom + gridSpacing; val vb = (sz.height - panY) / zoom + gridSpacing
    var gx = (vl / gridSpacing).toInt() * gridSpacing
    while (gx <= vr) {
        var gy = (vt / gridSpacing).toInt() * gridSpacing
        while (gy <= vb) {
            drawCircle(Color(0xFF222222), 1.5f, Offset(gx, gy))
            gy += gridSpacing
        }
        gx += gridSpacing
    }
}

private fun DrawScope.drawWbStroke(s: WbStroke) {
    if (s.points.size < 2) return
    val path = Path().apply {
        moveTo(s.points[0].x, s.points[0].y)
        for (i in 1 until s.points.size) lineTo(s.points[i].x, s.points[i].y)
    }
    drawPath(path, argbColor(s.color), style = Stroke(s.width, cap = StrokeCap.Round, join = StrokeJoin.Round))
}

private fun DrawScope.drawWbShape(s: WbShape) {
    val fill = if (s.fillColor.toInt() != 0) argbColor(s.fillColor) else null
    val stroke = Stroke(s.strokeWidth, cap = StrokeCap.Round, join = StrokeJoin.Round)
    when (s.kind) {
        "rect" -> {
            val r = Rect(minOf(s.x1, s.x2), minOf(s.y1, s.y2), maxOf(s.x1, s.x2), maxOf(s.y1, s.y2))
            if (fill != null) drawRect(fill, topLeft = r.topLeft, size = r.size, style = Fill)
            drawRect(argbColor(s.strokeColor), topLeft = r.topLeft, size = r.size, style = stroke)
        }
        "circle" -> {
            val center = Offset((s.x1 + s.x2) / 2f, (s.y1 + s.y2) / 2f)
            val rx = abs(s.x2 - s.x1) / 2f; val ry = abs(s.y2 - s.y1) / 2f
            val radius = min(rx, ry)
            if (fill != null) drawCircle(fill, radius, center, style = Fill)
            drawCircle(argbColor(s.strokeColor), radius, center, style = stroke)
        }
        "line" -> {
            drawLine(argbColor(s.strokeColor), Offset(s.x1, s.y1), Offset(s.x2, s.y2), s.strokeWidth, StrokeCap.Round)
        }
    }
}

private fun DrawScope.drawShapePreview(tool: WbTool, a: Offset, d: Offset, color: Long, fill: Long, width: Float) {
    val stroke = Stroke(width, cap = StrokeCap.Round, join = StrokeJoin.Round)
    val fillC = if (fill.toInt() != 0) argbColor(fill) else null
    when (tool) {
        WbTool.Rect -> {
            val r = Rect(minOf(a.x, d.x), minOf(a.y, d.y), maxOf(a.x, d.x), maxOf(a.y, d.y))
            if (fillC != null) drawRect(fillC, r.topLeft, r.size, style = Fill)
            drawRect(argbColor(color), r.topLeft, r.size, style = stroke)
        }
        WbTool.Circle -> {
            val center = Offset((a.x + d.x) / 2f, (a.y + d.y) / 2f)
            val radius = min(abs(d.x - a.x), abs(d.y - a.y)) / 2f
            if (fillC != null) drawCircle(fillC, radius, center, style = Fill)
            drawCircle(argbColor(color), radius, center, style = stroke)
        }
        WbTool.Line -> drawLine(argbColor(color), a, d, width, StrokeCap.Round)
        else -> {}
    }
}

// ── Network helpers ──

private fun sendStroke(ws: InkbridgeWebSocketClient, s: WbStroke) {
    try {
        val pts = JSONArray()
        for (p in s.points) pts.put(JSONObject().apply { put("x", p.x.toDouble()); put("y", p.y.toDouble()) })
        ws.sendText(JSONObject().apply {
            put("type", "wb-overlay-stroke"); put("id", s.id); put("points", pts)
            put("color", s.color); put("width", s.width.toDouble())
        }.toString())
    } catch (_: Exception) {}
}

private fun sendShape(ws: InkbridgeWebSocketClient, s: WbShape) {
    try {
        ws.sendText(JSONObject().apply {
            put("type", "wb-overlay-shape"); put("id", s.id); put("kind", s.kind)
            put("x1", s.x1.toDouble()); put("y1", s.y1.toDouble())
            put("x2", s.x2.toDouble()); put("y2", s.y2.toDouble())
            put("strokeColor", s.strokeColor); put("fillColor", s.fillColor)
            put("strokeWidth", s.strokeWidth.toDouble())
        }.toString())
    } catch (_: Exception) {}
}

private fun sendErase(ws: InkbridgeWebSocketClient, id: String) {
    try {
        ws.sendText(JSONObject().apply { put("type", "wb-overlay-erase"); put("id", id) }.toString())
    } catch (_: Exception) {}
}

private fun resync(ws: InkbridgeWebSocketClient, strokes: List<WbStroke>, shapes: List<WbShape>, images: List<WbImage>) {
    try {
        // Send clear then re-send everything
        ws.sendText(JSONObject().apply { put("type", "wb-resync-begin") }.toString())
        for (s in strokes) sendStroke(ws, s)
        for (s in shapes) sendShape(ws, s)
        for (img in images) {
            try {
                val stream = java.io.ByteArrayOutputStream()
                img.bitmap.compress(android.graphics.Bitmap.CompressFormat.PNG, 90, stream)
                val b64 = Base64.encodeToString(stream.toByteArray(), Base64.NO_WRAP)
                ws.sendText(JSONObject().apply {
                    put("type", "wb-image"); put("id", img.id)
                    put("x", img.x.toDouble()); put("y", img.y.toDouble())
                    put("w", img.w.toDouble()); put("h", img.h.toDouble())
                    put("data", b64)
                }.toString())
            } catch (_: Exception) {}
        }
        ws.sendText(JSONObject().apply { put("type", "wb-resync-end") }.toString())
    } catch (_: Exception) {}
}

// ── JSON parsing helpers ──

private fun parsePoints(arr: JSONArray): List<Offset> {
    val pts = mutableListOf<Offset>()
    for (i in 0 until arr.length()) {
        val o = arr.getJSONObject(i)
        pts.add(Offset(o.getDouble("x").toFloat(), o.getDouble("y").toFloat()))
    }
    return pts
}

private fun parseShape(obj: JSONObject): WbShape = WbShape(
    id = obj.getString("id"),
    kind = obj.getString("kind"),
    x1 = obj.optDouble("x1").toFloat(),
    y1 = obj.optDouble("y1").toFloat(),
    x2 = obj.optDouble("x2").toFloat(),
    y2 = obj.optDouble("y2").toFloat(),
    strokeColor = obj.optLong("strokeColor", 0xFFFFFFFF),
    fillColor = obj.optLong("fillColor", 0x00000000),
    strokeWidth = obj.optDouble("strokeWidth", 4.0).toFloat()
)
