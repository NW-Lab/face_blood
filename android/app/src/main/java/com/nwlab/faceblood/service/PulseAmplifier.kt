package com.nwlab.faceblood.service

import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.PorterDuff
import android.graphics.PorterDuffXfermode
import android.graphics.RadialGradient
import android.graphics.Shader
import com.nwlab.faceblood.model.PulseMode
import com.nwlab.faceblood.model.PulseModeId
import kotlin.math.abs
import kotlin.math.min
import kotlin.math.sqrt

/**
 * Software colour amplification for the three pulse modes.
 *
 * Mirrors the iOS [PulseAmplifier] and the Web canvas overlay logic:
 *
 *  - SUBTLE  : soft radial tint inside the ROI (screen blend).
 *  - VIVID   : larger radial tint + soft-light wash over the whole frame.
 *  - EXTREME : per-pixel skin-colour shift with saturation boost.
 *
 * All operations run on a CPU [Bitmap] / [Canvas].  For production use the
 * RenderScript / AGSL path would be preferable, but this keeps the code
 * dependency-free and easy to read in Android Studio.
 */
class PulseAmplifier {

    /**
     * Apply the colour amplification effect to [input] and return a new [Bitmap].
     *
     * @param input      Camera frame (ARGB_8888).
     * @param mode       Selected visualization mode.
     * @param pulse      rPPG instantaneous waveform value (~-1..1).
     * @param amp        User AMP slider value (1..10).
     * @param confidence Confidence score 0..1 — used to fade the effect in/out.
     */
    fun process(
        input: Bitmap,
        mode: PulseMode,
        pulse: Float,
        amp: Float,
        confidence: Float,
    ): Bitmap {
        // Effective intensity — mirrors the JS / Swift computation.
        val intensity = (pulse * (amp / 3f) * mode.ampScale).coerceIn(-1.5f, 1.5f)
        val sign = if (intensity >= 0f) 1f else -1f
        val absI = min(1.2f, abs(intensity))
        val targetR: Float
        val targetG: Float
        val targetB: Float
        if (sign > 0f) {
            targetR = mode.systoleR; targetG = mode.systoleG; targetB = mode.systoleB
        } else {
            targetR = mode.diastoleR; targetG = mode.diastoleG; targetB = mode.diastoleB
        }

        val effectAlpha = ((confidence - 0.1f) / 0.4f).coerceIn(0f, 1f)
        if (effectAlpha <= 0.001f) return input

        val w = input.width
        val h = input.height
        val cx = w / 2f
        val cy = h / 2f
        val shorter = min(w, h).toFloat()
        val roi = shorter * 0.55f

        return when (mode.id) {
            PulseModeId.SUBTLE  -> subtleOverlay(input, cx, cy, roi, targetR, targetG, targetB, absI * effectAlpha)
            PulseModeId.VIVID   -> vividOverlay(input, cx, cy, roi, targetR, targetG, targetB, absI * effectAlpha)
            PulseModeId.EXTREME -> extremeOverlay(input, targetR, targetG, targetB, absI * effectAlpha)
        }
    }

    // -------------------------------------------------------------------------
    // SUBTLE
    // -------------------------------------------------------------------------

    private fun subtleOverlay(
        base: Bitmap,
        cx: Float, cy: Float, roi: Float,
        r: Float, g: Float, b: Float,
        absI: Float,
    ): Bitmap {
        val alpha = min(0.7f, absI * 0.5f)
        val out = base.copy(Bitmap.Config.ARGB_8888, true)
        val canvas = Canvas(out)
        val paint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
            shader = RadialGradient(
                cx, cy, roi * 0.6f,
                intArrayOf(
                    argb(alpha, r, g, b),
                    argb(0f, r, g, b),
                ),
                null,
                Shader.TileMode.CLAMP,
            )
            xfermode = PorterDuffXfermode(PorterDuff.Mode.SCREEN)
        }
        canvas.drawRect(0f, 0f, base.width.toFloat(), base.height.toFloat(), paint)
        return out
    }

    // -------------------------------------------------------------------------
    // VIVID
    // -------------------------------------------------------------------------

    private fun vividOverlay(
        base: Bitmap,
        cx: Float, cy: Float, roi: Float,
        r: Float, g: Float, b: Float,
        absI: Float,
    ): Bitmap {
        val a1 = min(0.95f, absI * 0.85f)
        val out = base.copy(Bitmap.Config.ARGB_8888, true)
        val canvas = Canvas(out)

        // Radial gradient (screen blend)
        val radialPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
            shader = RadialGradient(
                cx, cy, roi * 0.95f,
                intArrayOf(
                    argb(a1, r, g, b),
                    argb(0f, r, g, b),
                ),
                floatArrayOf(roi * 0.1f / (roi * 0.95f), 1f),
                Shader.TileMode.CLAMP,
            )
            xfermode = PorterDuffXfermode(PorterDuff.Mode.SCREEN)
        }
        canvas.drawRect(0f, 0f, base.width.toFloat(), base.height.toFloat(), radialPaint)

        // Soft-light wash over the whole frame
        val washAlpha = min(0.35f, absI * 0.35f)
        val washPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
            color = argb(washAlpha, r, g, b)
            xfermode = PorterDuffXfermode(PorterDuff.Mode.SOFT_LIGHT)
        }
        canvas.drawRect(0f, 0f, base.width.toFloat(), base.height.toFloat(), washPaint)
        return out
    }

    // -------------------------------------------------------------------------
    // EXTREME — per-pixel skin shift
    // -------------------------------------------------------------------------

    private fun extremeOverlay(
        base: Bitmap,
        r: Float, g: Float, b: Float,
        absI: Float,
    ): Bitmap {
        val mixBase = min(0.95f, 0.55f + absI * 0.6f)
        val satBoost = 0.4f * absI
        val glowAlpha = min(0.45f, absI * 0.30f)

        // Per-pixel skin shift
        val pixels = IntArray(base.width * base.height)
        base.getPixels(pixels, 0, base.width, 0, 0, base.width, base.height)
        skinShiftPixels(pixels, r, g, b, mixBase, satBoost)

        val out = Bitmap.createBitmap(base.width, base.height, Bitmap.Config.ARGB_8888)
        out.setPixels(pixels, 0, base.width, 0, 0, base.width, base.height)

        // Additive composite (lighter blend)
        val canvas = Canvas(out)
        val addPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
            xfermode = PorterDuffXfermode(PorterDuff.Mode.ADD)
        }
        canvas.drawBitmap(base, 0f, 0f, addPaint)

        // Full-screen colour glow (screen blend)
        val glowPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
            color = argb(glowAlpha, r, g, b)
            xfermode = PorterDuffXfermode(PorterDuff.Mode.SCREEN)
        }
        canvas.drawRect(0f, 0f, base.width.toFloat(), base.height.toFloat(), glowPaint)
        return out
    }

    /**
     * Applies the same skin-detection heuristic as the iOS CIColorKernel and
     * the Web JS EXTREME mode, but running on the CPU per pixel.
     *
     * Skin heuristic (normalised 0..1):
     *   brightness in [55/255, 245/255]  AND  R > G  AND  G > B*0.85  AND  R > B  AND  R > G*1.04
     */
    private fun skinShiftPixels(
        pixels: IntArray,
        tR: Float, tG: Float, tB: Float,
        mixBase: Float, satBoost: Float,
    ) {
        val minB = 55f / 255f
        val maxB = 245f / 255f
        for (i in pixels.indices) {
            val px = pixels[i]
            val pr = ((px shr 16) and 0xFF) / 255f
            val pg = ((px shr 8) and 0xFF) / 255f
            val pb = (px and 0xFF) / 255f
            val pa = (px ushr 24) and 0xFF

            val bright = (pr + pg + pb) / 3f
            val isSkin = bright in minB..maxB &&
                pr > pg &&
                pg > pb * 0.85f &&
                pr > pb &&
                pr > pg * 1.04f

            if (!isSkin) continue

            val weight = ((bright - 50f / 255f) / (180f / 255f)).coerceIn(0f, 1f)
            val k = mixBase * weight
            var mr = min(1f, pr * (1f - k) + tR * k)
            var mg = min(1f, pg * (1f - k) + tG * k)
            var mb = min(1f, pb * (1f - k) + tB * k)

            // Saturation boost
            val avg = (mr + mg + mb) / 3f
            mr = min(1f, mr + (mr - avg) * satBoost)
            mg = min(1f, mg + (mg - avg) * satBoost)
            mb = min(1f, mb + (mb - avg) * satBoost)

            pixels[i] = (pa shl 24) or
                ((mr * 255f).toInt().coerceIn(0, 255) shl 16) or
                ((mg * 255f).toInt().coerceIn(0, 255) shl 8) or
                (mb * 255f).toInt().coerceIn(0, 255)
        }
    }

    // -------------------------------------------------------------------------
    // Colour helpers
    // -------------------------------------------------------------------------

    /** Pack float RGBA (0..1) into an Android ARGB int. */
    private fun argb(a: Float, r: Float, g: Float, b: Float): Int =
        ((a * 255f).toInt().coerceIn(0, 255) shl 24) or
            ((r * 255f).toInt().coerceIn(0, 255) shl 16) or
            ((g * 255f).toInt().coerceIn(0, 255) shl 8) or
            (b * 255f).toInt().coerceIn(0, 255)
}
