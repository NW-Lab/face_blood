package com.nwlab.faceblood.service

import com.nwlab.faceblood.model.RgbSample
import com.nwlab.faceblood.model.RppgResult
import kotlin.math.*

/**
 * rPPG signal processing — Kotlin port of the POS-based pipeline shared across
 * the Web, iOS, and Windows builds.
 *
 * Pipeline:
 *  1. Maintain a rolling ring buffer of (timestamp, R, G, B) face-ROI samples.
 *  2. Resample to a uniform grid via linear interpolation.
 *  3. Normalise each channel by its mean.
 *  4. Project onto a POS-like chrominance plane (X = G-B, Y = G+B-2R).
 *  5. Detrend with a moving-average window (~0.7 s).
 *  6. Apply a Hann window.
 *  7. Run a radix-2 FFT.
 *  8. Find the dominant peak in the 0.7–3.5 Hz band (≈ 42–210 BPM).
 *  9. Parabolic interpolation for sub-bin accuracy.
 * 10. Compute SNR and confidence.
 * 11. Inverse-FFT a band-limited spectrum to produce a clean waveform.
 *
 * @param windowSeconds  Duration of the rolling sample window (default 12 s).
 */
class RppgProcessor(private val windowSeconds: Double = 12.0) {

    private val minHz = 0.7f   // 42 BPM
    private val maxHz = 3.5f   // 210 BPM

    private val samples = ArrayDeque<RgbSample>(1024)

    val count: Int get() = samples.size

    val spanSeconds: Double
        get() {
            if (samples.size < 2) return 0.0
            return samples.last().t - samples.first().t
        }

    fun reset() { samples.clear() }

    fun push(s: RgbSample) {
        samples.addLast(s)
        val cutoff = s.t - windowSeconds
        while (samples.isNotEmpty() && samples.first().t < cutoff) {
            samples.removeFirst()
        }
    }

    // -------------------------------------------------------------------------
    // Main analysis
    // -------------------------------------------------------------------------

    fun analyze(): RppgResult? {
        val n = samples.size
        if (n < 64) return null
        val t0 = samples.first().t
        val tN = samples.last().t
        val durationS = (tN - t0).toFloat()
        if (durationS < 3f) return null

        val fps = (n - 1).toFloat() / durationS
        val targetFs = fps.coerceIn(15f, 60f)
        val m = (durationS * targetFs).toInt()
        if (m < 64) return null

        // ---- 1. Resample to uniform grid via linear interpolation ----
        val rs = FloatArray(m)
        val gs = FloatArray(m)
        val bs = FloatArray(m)
        var j = 0
        for (i in 0 until m) {
            val tt = t0 + i.toDouble() / targetFs
            while (j < n - 2 && samples[j + 1].t < tt) j++
            val a = samples[j]
            val b = samples[minOf(j + 1, n - 1)]
            val span = maxOf(1e-6, b.t - a.t).toFloat()
            val u = ((tt - a.t).toFloat() / span).coerceIn(0f, 1f)
            rs[i] = a.r * (1f - u) + b.r * u
            gs[i] = a.g * (1f - u) + b.g * u
            bs[i] = a.b * (1f - u) + b.b * u
        }

        // ---- 2. Normalise each channel by its mean ----
        val meanR = rs.mean()
        val meanG = gs.mean()
        val meanB = bs.mean()
        val nr = FloatArray(m) { i -> if (meanR > 0f) rs[i] / meanR - 1f else 0f }
        val ng = FloatArray(m) { i -> if (meanG > 0f) gs[i] / meanG - 1f else 0f }
        val nb = FloatArray(m) { i -> if (meanB > 0f) bs[i] / meanB - 1f else 0f }

        // ---- 3. POS-like projection ----
        // X = G - B,  Y = G + B - 2R
        val X = FloatArray(m) { i -> ng[i] - nb[i] }
        val Y = FloatArray(m) { i -> ng[i] + nb[i] - 2f * nr[i] }
        val sX = X.std()
        val sY = Y.std()
        val alpha = if (sY > 1e-9f) sX / sY else 1f
        val S = FloatArray(m) { i -> X[i] + alpha * Y[i] }

        // ---- 4. Detrend (subtract moving average ~0.7 s) ----
        val win = maxOf(3, (targetFs * 0.7f).toInt())
        detrendInPlace(S, win)

        // ---- 5. Hann window ----
        val W = FloatArray(m) { i ->
            val w = 0.5f * (1f - cos(2f * PI.toFloat() * i / (m - 1).toFloat()))
            S[i] * w
        }

        // ---- 6. FFT ----
        val nfft = nextPow2(maxOf(256, m))
        val re = FloatArray(nfft).also { for (i in 0 until m) it[i] = W[i] }
        val im = FloatArray(nfft)
        fftInPlace(re, im, inverse = false)

        val binHz = targetFs / nfft.toFloat()
        val minBin = maxOf(1, (minHz / binHz).toInt())
        val maxBin = minOf(nfft / 2 - 1, ceil(maxHz / binHz).toInt())
        if (maxBin <= minBin) return null

        // ---- 7. Find peak in band ----
        var peakBin = minBin
        var peakMag = 0f
        var inBandMag = 0f
        var totalMag = 0f
        for (k in 1 until nfft / 2) {
            val mag = sqrt(re[k] * re[k] + im[k] * im[k])
            totalMag += mag
            if (k in minBin..maxBin) {
                inBandMag += mag
                if (mag > peakMag) { peakMag = mag; peakBin = k }
            }
        }

        // ---- 8. Parabolic interpolation ----
        var kHat = peakBin.toFloat()
        if (peakBin > 1 && peakBin < nfft / 2 - 1) {
            val ym1 = sqrt(re[peakBin - 1].pow(2) + im[peakBin - 1].pow(2))
            val y0  = peakMag
            val yp1 = sqrt(re[peakBin + 1].pow(2) + im[peakBin + 1].pow(2))
            val denom = ym1 - 2f * y0 + yp1
            if (abs(denom) > 1e-9f) {
                kHat = peakBin + 0.5f * (ym1 - yp1) / denom
            }
        }
        val freqHz = kHat * binHz
        val bpm = freqHz * 60f

        // ---- 9. SNR & confidence ----
        val noiseFloor = (inBandMag - peakMag) / maxOf(1, maxBin - minBin).toFloat()
        val snr = if (noiseFloor > 0f) 20f * log10(peakMag / noiseFloor) else 0f
        val confidence = maxOf(0f, minOf(1f,
            ((snr - 2f) / 10f)
                * minOf(1f, durationS / 6f)
                * (peakMag / maxOf(1e-9f, totalMag / (nfft / 4).toFloat()))
        ))

        // ---- 10. Build filtered waveform via inverse FFT ----
        val reF = FloatArray(nfft)
        val imF = FloatArray(nfft)
        val peakHalfBins = maxOf(2, (0.4f / binHz).toInt())
        for (k in 0 until nfft) {
            val kk = if (k <= nfft / 2) k else nfft - k
            if (kk in minBin..maxBin) {
                val gain = if (abs(kk - peakBin) <= peakHalfBins) 1.6f else 1f
                reF[k] = re[k] * gain
                imF[k] = im[k] * gain
            }
        }
        fftInPlace(reF, imF, inverse = true)
        val scale = 1f / nfft
        for (i in 0 until nfft) { reF[i] *= scale; imF[i] *= scale }

        val waveform = FloatArray(m)
        var wMax = 1e-9f
        for (i in 0 until m) {
            waveform[i] = reF[i]
            val a = abs(reF[i])
            if (a > wMax) wMax = a
        }
        for (i in 0 until m) waveform[i] /= wMax

        val lastReal = reF[m - 1]
        val lastImag = imF[m - 1]
        var phase = atan2(lastImag, lastReal)
        if (phase < 0f) phase += 2f * PI.toFloat()

        return RppgResult(
            bpm = bpm,
            snr = snr,
            confidence = confidence,
            fps = targetFs,
            waveform = waveform,
            freqHz = freqHz,
            phase = phase,
        )
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private fun FloatArray.mean(): Float {
        if (isEmpty()) return 0f
        return sum() / size
    }

    private fun FloatArray.std(): Float {
        val m = mean()
        var s = 0f
        for (v in this) s += (v - m) * (v - m)
        return sqrt(s / maxOf(1, size).toFloat())
    }

    private fun detrendInPlace(x: FloatArray, window: Int) {
        val n = x.size
        if (n == 0) return
        val w = window.coerceIn(1, n)
        val cum = DoubleArray(n + 1)
        for (i in 0 until n) cum[i + 1] = cum[i] + x[i]
        for (i in 0 until n) {
            val a = maxOf(0, i - w / 2)
            val b = minOf(n, i + w / 2 + 1)
            val mean = ((cum[b] - cum[a]) / (b - a)).toFloat()
            x[i] -= mean
        }
    }

    private fun nextPow2(n: Int): Int {
        var p = 1
        while (p < n) p = p shl 1
        return p
    }

    /**
     * In-place radix-2 Cooley-Tukey FFT.
     * Both [re] and [im] must have the same power-of-2 length.
     *
     * @param inverse  If true, performs the inverse DFT (without the 1/N scaling).
     */
    private fun fftInPlace(re: FloatArray, im: FloatArray, inverse: Boolean) {
        val n = re.size
        require(n == im.size && n > 0 && n and (n - 1) == 0) {
            "FFT size must be a power of 2"
        }
        // Bit-reversal permutation
        var j = 0
        for (i in 1 until n) {
            var bit = n shr 1
            while (j and bit != 0) { j = j xor bit; bit = bit shr 1 }
            j = j xor bit
            if (i < j) { re[i] = re[j].also { re[j] = re[i] }; im[i] = im[j].also { im[j] = im[i] } }
        }
        // Butterfly passes
        var len = 2
        while (len <= n) {
            val ang = 2.0 * PI / len * (if (inverse) -1 else 1)
            val wRe = cos(ang).toFloat()
            val wIm = sin(ang).toFloat()
            var i = 0
            while (i < n) {
                var curRe = 1f
                var curIm = 0f
                for (k in 0 until len / 2) {
                    val uRe = re[i + k]
                    val uIm = im[i + k]
                    val vRe = re[i + k + len / 2] * curRe - im[i + k + len / 2] * curIm
                    val vIm = re[i + k + len / 2] * curIm + im[i + k + len / 2] * curRe
                    re[i + k] = uRe + vRe
                    im[i + k] = uIm + vIm
                    re[i + k + len / 2] = uRe - vRe
                    im[i + k + len / 2] = uIm - vIm
                    val newCurRe = curRe * wRe - curIm * wIm
                    curIm = curRe * wIm + curIm * wRe
                    curRe = newCurRe
                }
                i += len
            }
            len = len shl 1
        }
    }
}
