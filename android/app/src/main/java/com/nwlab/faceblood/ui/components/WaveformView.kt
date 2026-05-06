package com.nwlab.faceblood.ui.components

import androidx.compose.foundation.Canvas
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.drawscope.Stroke
import com.nwlab.faceblood.model.PulseMode
import com.nwlab.faceblood.ui.theme.CyanHud

/**
 * Waveform canvas that mirrors [WaveformView.swift] on iOS.
 *
 * Draws five faint horizontal grid lines and a polyline from the [data] array,
 * coloured with a left-to-right gradient from the mode's systole colour to the
 * diastole colour (or a default cyan when inactive).
 */
@Composable
fun WaveformView(
    data: List<Float>,
    mode: PulseMode,
    active: Boolean,
    modifier: Modifier = Modifier,
) {
    Canvas(modifier = modifier) {
        val w = size.width
        val h = size.height
        val mid = h / 2f

        // Grid lines
        val gridPaint = androidx.compose.ui.graphics.drawscope.DrawScope::drawLine
        for (row in 0..4) {
            val y = h * row / 4f
            drawLine(
                color = Color.White.copy(alpha = 0.08f),
                start = Offset(0f, y),
                end = Offset(w, y),
                strokeWidth = 1f,
            )
        }

        if (data.size < 2) return@Canvas

        // Build path
        val path = Path()
        data.forEachIndexed { i, v ->
            val x = w * i / (data.size - 1).toFloat()
            val y = mid - v * h * 0.42f
            if (i == 0) path.moveTo(x, y) else path.lineTo(x, y)
        }

        val startColor = if (active) mode.systoleColor else CyanHud
        val endColor = if (active) mode.diastoleColor.copy(alpha = 0f) else CyanHud.copy(alpha = 0f)

        drawPath(
            path = path,
            brush = Brush.horizontalGradient(listOf(startColor, endColor)),
            style = Stroke(width = 2f, cap = StrokeCap.Round, join = StrokeJoin.Round),
        )
    }
}
