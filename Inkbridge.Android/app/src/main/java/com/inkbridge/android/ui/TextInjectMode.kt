package com.inkbridge.android.ui

import android.view.MotionEvent
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.ExperimentalComposeUiApi
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.input.pointer.pointerInteropFilter
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.google.mlkit.vision.digitalink.*
import com.inkbridge.android.InkbridgeWebSocketClient
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import org.json.JSONObject

@OptIn(ExperimentalComposeUiApi::class)
@Composable
fun TextInjectMode(
    wsClient: InkbridgeWebSocketClient,
    focusedApp: String,
    focusedTitle: String,
    injectMethod: String
) {
    var sentLog by remember { mutableStateOf("") }
    val strokePoints = remember { mutableStateListOf<Offset>() }
    val committedPaths = remember { mutableStateListOf<Path>() }
    var inkBuilder by remember { mutableStateOf(Ink.builder()) }
    var strokeBuilder by remember { mutableStateOf(Ink.Stroke.builder()) }
    var recognizeJob by remember { mutableStateOf<Job?>(null) }
    val scope = rememberCoroutineScope()

    // Initialize ML Kit recognizer (English)
    val recognizer = remember {
        val modelId = DigitalInkRecognitionModelIdentifier.fromLanguageTag("en")!!
        val model = DigitalInkRecognitionModel.builder(modelId).build()
        val remoteModelManager = com.google.mlkit.common.model.RemoteModelManager.getInstance()
        remoteModelManager.download(model, com.google.mlkit.common.model.DownloadConditions.Builder().build())
        DigitalInkRecognition.getClient(DigitalInkRecognizerOptions.builder(model).build())
    }

    // Auto-recognize and send 2s after last stroke
    fun autoRecognizeAndSend() {
        recognizeJob?.cancel()
        recognizeJob = scope.launch {
            delay(2000)
            val ink = inkBuilder.build()
            if (ink.strokes.isEmpty()) return@launch
            recognizer.recognize(ink).addOnSuccessListener { result ->
                if (result.candidates.isNotEmpty()) {
                    val text = result.candidates[0].text
                    sentLog = if (sentLog.isEmpty()) text else "$sentLog $text"
                    val json = JSONObject()
                    json.put("type", "inject")
                    json.put("text", text)
                    wsClient.sendText(json.toString())
                }
                committedPaths.clear()
                strokePoints.clear()
                inkBuilder = Ink.builder()
            }
        }
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
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text(
                text = "${focusedApp} · ${focusedTitle} — ${injectMethod}",
                style = MaterialTheme.typography.labelMedium
            )
        }

        Divider(color = Color.DarkGray, thickness = 0.5.dp)

        // Sent text log
        if (sentLog.isNotEmpty()) {
            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .heightIn(max = 100.dp)
                    .background(Color(0xFF111111))
                    .padding(12.dp)
                    .verticalScroll(rememberScrollState())
            ) {
                Text(
                    text = sentLog + " |",
                    style = TextStyle(color = Color(0xFF4CAF50), fontSize = 16.sp)
                )
            }
            Divider(color = Color.DarkGray, thickness = 0.5.dp)
        }

        // Handwriting Canvas
        Box(
            modifier = Modifier
                .weight(1f)
                .fillMaxWidth()
                .pointerInteropFilter { event ->
                    when (event.actionMasked) {
                        MotionEvent.ACTION_DOWN -> {
                            recognizeJob?.cancel()
                            strokeBuilder = Ink.Stroke.builder()
                            strokePoints.clear()
                            val x = event.x
                            val y = event.y
                            strokePoints.add(Offset(x, y))
                            strokeBuilder.addPoint(Ink.Point.create(x, y, event.eventTime))
                            true
                        }
                        MotionEvent.ACTION_MOVE -> {
                            for (i in 0 until event.historySize) {
                                val hx = event.getHistoricalX(i)
                                val hy = event.getHistoricalY(i)
                                strokePoints.add(Offset(hx, hy))
                                strokeBuilder.addPoint(Ink.Point.create(hx, hy, event.getHistoricalEventTime(i)))
                            }
                            val x = event.x
                            val y = event.y
                            strokePoints.add(Offset(x, y))
                            strokeBuilder.addPoint(Ink.Point.create(x, y, event.eventTime))
                            true
                        }
                        MotionEvent.ACTION_UP -> {
                            val x = event.x
                            val y = event.y
                            strokePoints.add(Offset(x, y))
                            strokeBuilder.addPoint(Ink.Point.create(x, y, event.eventTime))
                            // Commit stroke visually
                            val path = Path()
                            strokePoints.forEachIndexed { i, pt ->
                                if (i == 0) path.moveTo(pt.x, pt.y) else path.lineTo(pt.x, pt.y)
                            }
                            committedPaths.add(path)
                            strokePoints.clear()
                            inkBuilder.addStroke(strokeBuilder.build())
                            // Auto-recognize and send after 2s of inactivity
                            autoRecognizeAndSend()
                            true
                        }
                        else -> false
                    }
                }
        ) {
            if (sentLog.isEmpty() && committedPaths.isEmpty() && strokePoints.isEmpty()) {
                Text(
                    text = "Write with pen — auto-sends to PC",
                    style = TextStyle(color = Color.DarkGray, fontSize = 20.sp),
                    modifier = Modifier.padding(16.dp)
                )
            }
            Canvas(modifier = Modifier.fillMaxSize()) {
                val style = Stroke(width = 4f, cap = StrokeCap.Round, join = StrokeJoin.Round)
                committedPaths.forEach { path ->
                    drawPath(path, Color.White, style = style)
                }
                if (strokePoints.size > 1) {
                    val currentPath = Path()
                    strokePoints.forEachIndexed { i, pt ->
                        if (i == 0) currentPath.moveTo(pt.x, pt.y) else currentPath.lineTo(pt.x, pt.y)
                    }
                    drawPath(currentPath, Color.White, style = style)
                }
            }
        }

        Divider(color = Color.DarkGray, thickness = 0.5.dp)

        // Bottom Bar
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(12.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.End
        ) {
            TextButton(onClick = {
                sentLog = ""
                committedPaths.clear()
                strokePoints.clear()
                inkBuilder = Ink.builder()
                recognizeJob?.cancel()
            }) {
                Text("Clear", color = Color.Gray)
            }
        }
    }
}
