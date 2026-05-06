package com.nwlab.faceblood.ui.theme

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

val CyanHud = Color(0x3DFAFFFF)   // rgba(61,250,255,0.24)
val RedPulse = Color(0xFFFF2E4D)
val GreenConf = Color(0xFF4FF18C)
val YellowConf = Color(0xFFC7C745)

private val DarkColors = darkColorScheme(
    primary = RedPulse,
    secondary = CyanHud,
    background = Color.Black,
    surface = Color.Black,
    onBackground = Color.White,
    onSurface = Color.White,
)

@Composable
fun FaceBloodTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = DarkColors,
        content = content,
    )
}
