package com.nwlab.faceblood.model

import androidx.compose.ui.graphics.Color

/**
 * Visualization modes that mirror the Web / iOS builds.
 *
 * Each mode carries display metadata and the two target RGB colours used by
 * [com.nwlab.faceblood.service.PulseAmplifier] to tint the camera frame.
 */
enum class PulseModeId { SUBTLE, VIVID, EXTREME }

data class PulseMode(
    val id: PulseModeId,
    val label: String,
    val caption: String,
    /** Multiplier applied on top of the user AMP slider value. */
    val ampScale: Float,
    /** Target colour during systole (positive pulse value). Components in 0..1. */
    val systoleR: Float,
    val systoleG: Float,
    val systoleB: Float,
    /** Target colour during diastole (negative pulse value). Components in 0..1. */
    val diastoleR: Float,
    val diastoleG: Float,
    val diastoleB: Float,
) {
    val systoleColor: Color get() = Color(systoleR, systoleG, systoleB)
    val diastoleColor: Color get() = Color(diastoleR, diastoleG, diastoleB)

    companion object {
        val ALL: List<PulseMode> = listOf(
            PulseMode(
                id = PulseModeId.SUBTLE,
                label = "SUBTLE",
                caption = "控えめモード — 顔ROI内のみ柔らかく増幅",
                ampScale = 1.0f,
                systoleR = 255f / 255f, systoleG = 60f / 255f, systoleB = 90f / 255f,
                diastoleR = 80f / 255f, diastoleG = 200f / 255f, diastoleB = 255f / 255f,
            ),
            PulseMode(
                id = PulseModeId.VIVID,
                label = "VIVID",
                caption = "派手モード — 顔と画面全体が赤⇄青にダイナミックに染まる",
                ampScale = 2.5f,
                systoleR = 255f / 255f, systoleG = 30f / 255f, systoleB = 60f / 255f,
                diastoleR = 40f / 255f, diastoleG = 120f / 255f, diastoleB = 255f / 255f,
            ),
            PulseMode(
                id = PulseModeId.EXTREME,
                label = "EXTREME",
                caption = "超派手モード — 顔全体がドカンと真っ赤⇄真っ青に脈動",
                ampScale = 5.0f,
                systoleR = 255f / 255f, systoleG = 0f / 255f, systoleB = 30f / 255f,
                diastoleR = 0f / 255f, diastoleG = 80f / 255f, diastoleB = 255f / 255f,
            ),
        )

        fun by(id: PulseModeId): PulseMode = ALL.firstOrNull { it.id == id } ?: ALL[1]
    }
}
