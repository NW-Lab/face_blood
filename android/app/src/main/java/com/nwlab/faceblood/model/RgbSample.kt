package com.nwlab.faceblood.model

/**
 * A single RGB colour sample captured from the face ROI at a given timestamp.
 *
 * @param t  Timestamp in seconds (e.g. System.nanoTime() / 1e9).
 * @param r  Mean red channel value (0..255 or normalised 0..1 — must be consistent).
 * @param g  Mean green channel value.
 * @param b  Mean blue channel value.
 */
data class RgbSample(
    val t: Double,
    val r: Float,
    val g: Float,
    val b: Float,
)
