package com.nwlab.faceblood.ui

import android.graphics.Bitmap
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.PathEffect
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.nwlab.faceblood.model.PulseMode
import com.nwlab.faceblood.model.PulseModeId
import com.nwlab.faceblood.service.MeasurePhase
import com.nwlab.faceblood.ui.components.WaveformView
import com.nwlab.faceblood.ui.theme.GreenConf
import com.nwlab.faceblood.ui.theme.YellowConf
import kotlin.math.min

// ─────────────────────────────────────────────────────────────────────────────
// Colour constants
// ─────────────────────────────────────────────────────────────────────────────

private val Cyan = Color(0xFF3DFAFF)
private val RedBright = Color(0xFFFF2E4D)

// ─────────────────────────────────────────────────────────────────────────────
// Root screen
// ─────────────────────────────────────────────────────────────────────────────

@Composable
fun MainScreen(
    phase: MeasurePhase,
    bpm: Float,
    snr: Float,
    confidence: Float,
    fps: Float,
    sampleCount: Int,
    waveform: List<Float>,
    processedBitmap: Bitmap?,
    beatPulse: Boolean,
    errorMessage: String,
    modeId: PulseModeId,
    amp: Float,
    onStart: () -> Unit,
    onStop: () -> Unit,
    onModeChange: (PulseModeId) -> Unit,
    onAmpChange: (Float) -> Unit,
    modifier: Modifier = Modifier,
) {
    Box(
        modifier = modifier
            .fillMaxSize()
            .background(Color.Black),
    ) {
        // ── Camera frame ──────────────────────────────────────────────────────
        processedBitmap?.let { bmp ->
            androidx.compose.foundation.layout.Box(modifier = Modifier.fillMaxSize()) {
                androidx.compose.foundation.Image(
                    bitmap = bmp.asImageBitmap(),
                    contentDescription = null,
                    contentScale = ContentScale.Crop,
                    modifier = Modifier.fillMaxSize(),
                )
            }
        }

        // ── ROI guide ─────────────────────────────────────────────────────────
        if (phase == MeasurePhase.CALIBRATING || phase == MeasurePhase.MEASURING) {
            RoiGuide(
                modeId = modeId,
                confidence = confidence,
                modifier = Modifier.fillMaxSize(),
            )
        }

        // ── Beat vignette ─────────────────────────────────────────────────────
        AnimatedVisibility(
            visible = beatPulse,
            enter = fadeIn(tween(80)),
            exit = fadeOut(tween(240)),
            modifier = Modifier.fillMaxSize(),
        ) {
            Box(
                modifier = Modifier
                    .fillMaxSize()
                    .background(
                        Brush.radialGradient(
                            colors = listOf(
                                Color.Transparent,
                                Color(0xFF1F0008).copy(alpha = 0.32f),
                            ),
                        ),
                    ),
            )
        }

        // ── Idle / error overlay ──────────────────────────────────────────────
        if (phase == MeasurePhase.IDLE || phase == MeasurePhase.ERROR) {
            IdleOverlay(
                phase = phase,
                errorMessage = errorMessage,
                onStart = onStart,
            )
        }

        // ── HUD ───────────────────────────────────────────────────────────────
        if (phase != MeasurePhase.IDLE && phase != MeasurePhase.ERROR) {
            Column(modifier = Modifier.fillMaxSize()) {
                TopBar(phase = phase, sampleCount = sampleCount, fps = fps, onStop = onStop)

                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 12.dp, vertical = 8.dp),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.Top,
                ) {
                    BpmPanel(bpm = bpm, confidence = confidence)
                    VitalsPanel(snr = snr, confidence = confidence, sampleCount = sampleCount)
                }

                Spacer(modifier = Modifier.weight(1f))

                BottomPanel(
                    modeId = modeId,
                    amp = amp,
                    waveform = waveform,
                    bpm = bpm,
                    confidence = confidence,
                    sampleCount = sampleCount,
                    phase = phase,
                    onModeChange = onModeChange,
                    onAmpChange = onAmpChange,
                )
            }
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Idle overlay
// ─────────────────────────────────────────────────────────────────────────────

@Composable
private fun IdleOverlay(
    phase: MeasurePhase,
    errorMessage: String,
    onStart: () -> Unit,
) {
    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(Color.Black.copy(alpha = 0.92f)),
        contentAlignment = Alignment.Center,
    ) {
        Column(
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(18.dp),
        ) {
            Text(
                text = "FACE BLOOD",
                fontFamily = FontFamily.Monospace,
                fontWeight = FontWeight.Bold,
                fontSize = 11.sp,
                letterSpacing = 4.sp,
                color = Cyan,
            )
            Text(
                text = "rPPG Pulse Visualizer",
                fontFamily = FontFamily.Monospace,
                fontWeight = FontWeight.Bold,
                fontSize = 28.sp,
                color = RedBright,
            )
            Text(
                text = "顔をカメラに向けて枠内に収めてください。\n脈拍に合わせて顔が赤と青にダイナミックに脈動します。",
                textAlign = TextAlign.Center,
                fontSize = 13.sp,
                color = Color.White.copy(alpha = 0.7f),
                modifier = Modifier.padding(horizontal = 28.dp),
            )
            if (phase == MeasurePhase.ERROR) {
                Text(
                    text = errorMessage,
                    fontSize = 12.sp,
                    color = Color.Red,
                    textAlign = TextAlign.Center,
                    modifier = Modifier.padding(horizontal = 28.dp),
                )
            }
            Button(
                onClick = onStart,
                colors = ButtonDefaults.buttonColors(containerColor = RedBright),
                shape = RoundedCornerShape(6.dp),
                modifier = Modifier.padding(top = 6.dp),
            ) {
                Text(
                    text = "計測開始",
                    fontFamily = FontFamily.Monospace,
                    fontWeight = FontWeight.ExtraBold,
                    letterSpacing = 3.sp,
                    fontSize = 13.sp,
                    color = Color.White,
                    modifier = Modifier.padding(horizontal = 32.dp, vertical = 6.dp),
                )
            }
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Top bar
// ─────────────────────────────────────────────────────────────────────────────

@Composable
private fun TopBar(
    phase: MeasurePhase,
    sampleCount: Int,
    fps: Float,
    onStop: () -> Unit,
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .background(Color.Black.copy(alpha = 0.55f))
            .padding(horizontal = 14.dp, vertical = 8.dp),
        horizontalArrangement = Arrangement.SpaceBetween,
        verticalAlignment = Alignment.CenterVertically,
    ) {
        HudLabel(
            text = if (phase == MeasurePhase.CALIBRATING) "CALIBRATING" else "MEASURING",
            color = Cyan,
        )
        HudLabel(
            text = if (phase == MeasurePhase.CALIBRATING)
                "${min(100, (sampleCount.toFloat() / 64f * 100).toInt())}%"
            else "FPS ${fps.toInt()}",
        )
        TextButton(onClick = onStop) {
            HudLabel(text = "STOP", color = Color.Red)
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// BPM panel
// ─────────────────────────────────────────────────────────────────────────────

@Composable
private fun BpmPanel(bpm: Float, confidence: Float) {
    Column(
        modifier = Modifier
            .background(Color.Black.copy(alpha = 0.55f), RoundedCornerShape(10.dp))
            .padding(horizontal = 14.dp, vertical = 12.dp),
        verticalArrangement = Arrangement.spacedBy(4.dp),
    ) {
        HudLabel("HEART RATE")
        Text(
            text = if (confidence > 0.3f && bpm > 0f) bpm.toInt().toString() else "--",
            fontFamily = FontFamily.Monospace,
            fontWeight = FontWeight.ExtraBold,
            fontSize = 56.sp,
            color = if (confidence > 0.3f) RedBright else Color.White.copy(alpha = 0.5f),
        )
        HudLabel("BPM")
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Vitals panel
// ─────────────────────────────────────────────────────────────────────────────

@Composable
private fun VitalsPanel(snr: Float, confidence: Float, sampleCount: Int) {
    Column(
        modifier = Modifier
            .background(Color.Black.copy(alpha = 0.55f), RoundedCornerShape(10.dp))
            .padding(horizontal = 12.dp, vertical = 12.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
            HudLabel("SNR")
            Row(verticalAlignment = Alignment.Bottom) {
                Text(
                    text = if (snr > 0f) "%.1f".format(snr) else "--",
                    fontFamily = FontFamily.Monospace,
                    fontWeight = FontWeight.ExtraBold,
                    fontSize = 18.sp,
                    color = Cyan,
                )
                Text(
                    text = " dB",
                    fontSize = 9.sp,
                    color = Color.White.copy(alpha = 0.5f),
                )
            }
        }
        Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
            HudLabel("CONF")
            Row(verticalAlignment = Alignment.Bottom) {
                Text(
                    text = "${(confidence * 100).toInt()}",
                    fontFamily = FontFamily.Monospace,
                    fontWeight = FontWeight.ExtraBold,
                    fontSize = 18.sp,
                    color = confColor(confidence),
                )
                Text(
                    text = " %",
                    fontSize = 9.sp,
                    color = Color.White.copy(alpha = 0.5f),
                )
            }
        }
        Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
            HudLabel("SAMPLES")
            Text(
                text = "$sampleCount",
                fontFamily = FontFamily.Monospace,
                fontSize = 13.sp,
                color = Color.White.copy(alpha = 0.6f),
            )
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Bottom panel
// ─────────────────────────────────────────────────────────────────────────────

@Composable
private fun BottomPanel(
    modeId: PulseModeId,
    amp: Float,
    waveform: List<Float>,
    bpm: Float,
    confidence: Float,
    sampleCount: Int,
    phase: MeasurePhase,
    onModeChange: (PulseModeId) -> Unit,
    onAmpChange: (Float) -> Unit,
) {
    val currentMode = PulseMode.by(modeId)

    Column(
        modifier = Modifier
            .fillMaxWidth()
            .background(Color.Black.copy(alpha = 0.6f)),
    ) {
        // Mode tabs
        Row(modifier = Modifier.fillMaxWidth()) {
            PulseMode.ALL.forEach { mode ->
                val selected = mode.id == modeId
                TextButton(
                    onClick = { onModeChange(mode.id) },
                    modifier = Modifier.weight(1f),
                ) {
                    Column(horizontalAlignment = Alignment.CenterHorizontally) {
                        Text(
                            text = mode.label,
                            fontFamily = FontFamily.Monospace,
                            fontWeight = FontWeight.Bold,
                            fontSize = 11.sp,
                            letterSpacing = 2.sp,
                            color = if (selected) mode.systoleColor else Color.White.copy(alpha = 0.55f),
                        )
                        if (selected) {
                            HorizontalDivider(
                                color = mode.systoleColor,
                                thickness = 2.dp,
                                modifier = Modifier.fillMaxWidth(),
                            )
                        }
                    }
                }
            }
        }

        // Mode caption
        Text(
            text = currentMode.caption,
            fontFamily = FontFamily.Monospace,
            fontSize = 10.sp,
            color = Color.White.copy(alpha = 0.7f),
            textAlign = TextAlign.Center,
            modifier = Modifier
                .fillMaxWidth()
                .padding(top = 6.dp, start = 12.dp, end = 12.dp),
        )

        // AMP slider
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 14.dp, vertical = 6.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            HudLabel("AMP")
            Slider(
                value = amp,
                onValueChange = onAmpChange,
                valueRange = 1f..10f,
                steps = 17,
                colors = SliderDefaults.colors(thumbColor = RedBright, activeTrackColor = RedBright),
                modifier = Modifier.weight(1f),
            )
            Text(
                text = "%.1f".format(amp),
                fontFamily = FontFamily.Monospace,
                fontSize = 11.sp,
                color = Cyan,
                modifier = Modifier.width(28.dp),
                textAlign = TextAlign.End,
            )
        }

        // Waveform
        WaveformView(
            data = waveform,
            mode = currentMode,
            active = confidence > 0.2f,
            modifier = Modifier
                .fillMaxWidth()
                .height(64.dp)
                .padding(top = 4.dp),
        )

        // Frequency / BPM
        if (bpm > 0f) {
            Text(
                text = "%.2f Hz · %d BPM".format(bpm / 60f, bpm.toInt()),
                fontFamily = FontFamily.Monospace,
                fontSize = 10.sp,
                color = Color.White.copy(alpha = 0.5f),
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(bottom = 4.dp),
                textAlign = TextAlign.Center,
            )
        }

        // Calibration progress bar
        if (phase == MeasurePhase.CALIBRATING) {
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 14.dp, vertical = 4.dp),
                verticalArrangement = Arrangement.spacedBy(4.dp),
            ) {
                LinearProgressIndicator(
                    progress = { min(1f, sampleCount.toFloat() / 64f) },
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(2.dp),
                    color = Cyan,
                    trackColor = Color.White.copy(alpha = 0.1f),
                )
                HudLabel(
                    text = "SIGNAL ACQUISITION ${min(100, (sampleCount.toFloat() / 64f * 100).toInt())}%",
                    color = Cyan,
                )
            }
        }

        // Navigation bar padding
        Spacer(modifier = Modifier.windowInsetsBottomHeight(WindowInsets.navigationBars))
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ROI guide overlay
// ─────────────────────────────────────────────────────────────────────────────

@Composable
private fun RoiGuide(
    modeId: PulseModeId,
    confidence: Float,
    modifier: Modifier = Modifier,
) {
    val mode = PulseMode.by(modeId)
    val active = confidence > 0.1f
    val borderColor = if (active) mode.systoleColor else Cyan
    val alpha = if (active) 0.7f else 0.4f

    Canvas(modifier = modifier) {
        val shorter = min(size.width, size.height)
        val roi = shorter * 0.55f
        val left = (size.width - roi) / 2f
        val top = (size.height - roi) / 2f

        drawRoundRect(
            color = borderColor.copy(alpha = alpha),
            topLeft = Offset(left, top),
            size = Size(roi, roi),
            cornerRadius = CornerRadius(8f, 8f),
            style = Stroke(
                width = 2f,
                pathEffect = PathEffect.dashPathEffect(floatArrayOf(8f, 6f)),
            ),
        )
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Small helpers
// ─────────────────────────────────────────────────────────────────────────────

@Composable
private fun HudLabel(text: String, color: Color = Color.White.copy(alpha = 0.55f)) {
    Text(
        text = text,
        fontFamily = FontFamily.Monospace,
        fontWeight = FontWeight.Bold,
        fontSize = 9.sp,
        letterSpacing = 2.sp,
        color = color,
    )
}

private fun confColor(c: Float): Color = when {
    c > 0.6f -> GreenConf
    c > 0.3f -> YellowConf
    else -> Color(0xFFFF6666)
}
