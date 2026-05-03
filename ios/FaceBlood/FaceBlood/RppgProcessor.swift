//
//  RppgProcessor.swift
//  FaceBlood
//
//  rPPG signal processing — port of the Web build's POS-based pipeline.
//  Uses Accelerate's vDSP for the FFT and bandpass extraction.
//
//  - Maintains a ring buffer of (timestamp, R, G, B) face-ROI samples.
//  - Resamples to a uniform grid, projects onto a POS-like chrominance plane,
//    detrends, applies a Hann window, runs a real FFT, and finds the peak
//    inside the 0.7–3.5 Hz band (≈ 42–210 BPM).
//  - Returns BPM, SNR, confidence, the filtered waveform, and the
//    instantaneous phase of the dominant component for visual sync.
//

import Foundation
import Accelerate

struct RgbSample {
    let t: TimeInterval   // seconds
    let r: Float
    let g: Float
    let b: Float
}

struct RppgResult {
    let bpm: Float
    let snr: Float          // dB-ish
    let confidence: Float   // 0..1
    let fps: Float
    let waveform: [Float]   // normalized to ~[-1, 1]
    let freqHz: Float
    let phase: Float        // 0..2π, instantaneous phase
}

final class RppgProcessor {
    // MARK: - Tunables
    private let minHz: Float = 0.7   // 42 BPM
    private let maxHz: Float = 3.5   // 210 BPM
    private let windowSeconds: TimeInterval

    // MARK: - State
    private var samples: [RgbSample] = []

    init(windowSeconds: TimeInterval = 12) {
        self.windowSeconds = windowSeconds
        samples.reserveCapacity(1024)
    }

    var count: Int { samples.count }

    var spanSeconds: TimeInterval {
        guard let first = samples.first, let last = samples.last else { return 0 }
        return last.t - first.t
    }

    func reset() {
        samples.removeAll(keepingCapacity: true)
    }

    func push(_ s: RgbSample) {
        samples.append(s)
        let cutoff = s.t - windowSeconds
        // Drop old samples (cheap because we only ever push newer ones).
        var dropTo = 0
        while dropTo < samples.count, samples[dropTo].t < cutoff { dropTo += 1 }
        if dropTo > 0 { samples.removeFirst(dropTo) }
    }

    // MARK: - Main analysis
    func analyze() -> RppgResult? {
        let N = samples.count
        guard N >= 64 else { return nil }
        let t0 = samples[0].t
        let tN = samples[N - 1].t
        let durationS = Float(tN - t0)
        guard durationS >= 3 else { return nil }

        let fps = Float(N - 1) / durationS
        let targetFs = min(Float(60), max(Float(15), fps))
        let M = Int(durationS * targetFs)
        guard M >= 64 else { return nil }

        // -------- 1. Resample to uniform grid via linear interpolation --------
        var rs = [Float](repeating: 0, count: M)
        var gs = [Float](repeating: 0, count: M)
        var bs = [Float](repeating: 0, count: M)
        var j = 0
        for i in 0..<M {
            let tt = t0 + Double(Float(i) / targetFs)
            while j < N - 2, samples[j + 1].t < tt { j += 1 }
            let a = samples[j]
            let b = samples[min(j + 1, N - 1)]
            let span = max(1e-6, Float(b.t - a.t))
            let u = max(0, min(1, Float(tt - a.t) / span))
            rs[i] = a.r * (1 - u) + b.r * u
            gs[i] = a.g * (1 - u) + b.g * u
            bs[i] = a.b * (1 - u) + b.b * u
        }

        // -------- 2. Normalize each channel by its mean --------
        let meanR = mean(rs)
        let meanG = mean(gs)
        let meanB = mean(bs)
        var nr = [Float](repeating: 0, count: M)
        var ng = [Float](repeating: 0, count: M)
        var nb = [Float](repeating: 0, count: M)
        for i in 0..<M {
            nr[i] = meanR > 0 ? rs[i] / meanR - 1 : 0
            ng[i] = meanG > 0 ? gs[i] / meanG - 1 : 0
            nb[i] = meanB > 0 ? bs[i] / meanB - 1 : 0
        }

        // -------- 3. POS-like projection --------
        // X = G - B, Y = G + B - 2R
        var X = [Float](repeating: 0, count: M)
        var Y = [Float](repeating: 0, count: M)
        for i in 0..<M {
            X[i] = ng[i] - nb[i]
            Y[i] = ng[i] + nb[i] - 2 * nr[i]
        }
        let sX = std(X)
        let sY = std(Y)
        let alpha = sY > 1e-9 ? sX / sY : 1
        var S = [Float](repeating: 0, count: M)
        for i in 0..<M { S[i] = X[i] + alpha * Y[i] }

        // -------- 4. Detrend (subtract moving average ~0.7s) --------
        let win = max(3, Int(targetFs * 0.7))
        detrendInPlace(&S, window: win)

        // -------- 5. Hann window --------
        var W = [Float](repeating: 0, count: M)
        for i in 0..<M {
            let w = 0.5 * (1 - cosf(2 * .pi * Float(i) / Float(M - 1)))
            W[i] = S[i] * w
        }

        // -------- 6. FFT (radix-2) --------
        let nfft = nextPow2(max(256, M))
        let log2n = vDSP_Length(log2(Float(nfft)))
        guard let setup = vDSP_create_fftsetup(log2n, FFTRadix(kFFTRadix2)) else {
            return nil
        }
        defer { vDSP_destroy_fftsetup(setup) }

        var realIn = [Float](repeating: 0, count: nfft)
        for i in 0..<M { realIn[i] = W[i] }
        var imagIn = [Float](repeating: 0, count: nfft)

        var (re, im) = (realIn, imagIn)
        re.withUnsafeMutableBufferPointer { rePtr in
            im.withUnsafeMutableBufferPointer { imPtr in
                var split = DSPSplitComplex(realp: rePtr.baseAddress!,
                                            imagp: imPtr.baseAddress!)
                vDSP_fft_zip(setup, &split, 1, log2n, FFTDirection(FFT_FORWARD))
            }
        }

        let binHz = targetFs / Float(nfft)
        let minBin = max(1, Int(minHz / binHz))
        let maxBin = min(nfft / 2 - 1, Int(ceilf(maxHz / binHz)))
        if maxBin <= minBin { return nil }

        // Magnitudes (full half spectrum used for SNR; in-band searched for peak).
        var peakBin = minBin
        var peakMag: Float = 0
        var inBandMag: Float = 0
        var totalMag: Float = 0
        for k in 1..<(nfft / 2) {
            let m = sqrtf(re[k] * re[k] + im[k] * im[k])
            totalMag += m
            if k >= minBin && k <= maxBin {
                inBandMag += m
                if m > peakMag {
                    peakMag = m
                    peakBin = k
                }
            }
        }

        // Parabolic interpolation around the peak for sub-bin accuracy.
        var kHat = Float(peakBin)
        if peakBin > 1 && peakBin < nfft / 2 - 1 {
            let ym1 = sqrtf(re[peakBin - 1] * re[peakBin - 1]
                            + im[peakBin - 1] * im[peakBin - 1])
            let y0 = peakMag
            let yp1 = sqrtf(re[peakBin + 1] * re[peakBin + 1]
                            + im[peakBin + 1] * im[peakBin + 1])
            let denom = ym1 - 2 * y0 + yp1
            if abs(denom) > 1e-9 {
                let delta = 0.5 * (ym1 - yp1) / denom
                kHat = Float(peakBin) + delta
            }
        }
        let freqHz = kHat * binHz
        let bpm = freqHz * 60

        // SNR-ish: peak vs average in-band noise floor.
        let noiseFloor = (inBandMag - peakMag) / Float(max(1, maxBin - minBin))
        let snr: Float = noiseFloor > 0 ? 20 * log10f(peakMag / noiseFloor) : 0
        let confidence: Float = max(0, min(1,
            ((snr - 2) / 10)
            * min(1, durationS / 6)
            * (peakMag / max(1e-9, totalMag / Float(nfft / 4)))
        ))

        // -------- 7. Build a clean filtered waveform --------
        // Zero out bins outside [minBin, maxBin] and emphasize the ±0.4 Hz
        // neighbourhood of the peak, then inverse FFT.
        var reF = [Float](repeating: 0, count: nfft)
        var imF = [Float](repeating: 0, count: nfft)
        let peakHalfBins = max(2, Int(0.4 / binHz))
        for k in 0..<nfft {
            let kk = k <= nfft / 2 ? k : nfft - k
            if kk >= minBin && kk <= maxBin {
                var gain: Float = 1
                if abs(kk - peakBin) <= peakHalfBins { gain = 1.6 }
                reF[k] = re[k] * gain
                imF[k] = im[k] * gain
            }
        }
        reF.withUnsafeMutableBufferPointer { rePtr in
            imF.withUnsafeMutableBufferPointer { imPtr in
                var split = DSPSplitComplex(realp: rePtr.baseAddress!,
                                            imagp: imPtr.baseAddress!)
                vDSP_fft_zip(setup, &split, 1, log2n, FFTDirection(FFT_INVERSE))
            }
        }
        // vDSP forward+inverse pair scales by N → divide.
        let scale = 1 / Float(nfft)
        for i in 0..<nfft { reF[i] *= scale; imF[i] *= scale }

        var waveform = [Float](repeating: 0, count: M)
        var wMax: Float = 1e-9
        for i in 0..<M {
            waveform[i] = reF[i]
            let a = abs(reF[i])
            if a > wMax { wMax = a }
        }
        for i in 0..<M { waveform[i] /= wMax }

        let lastReal = reF[M - 1]
        let lastImag = imF[M - 1]
        var phase = atan2f(lastImag, lastReal)
        if phase < 0 { phase += 2 * .pi }

        return RppgResult(
            bpm: bpm,
            snr: snr,
            confidence: confidence,
            fps: targetFs,
            waveform: waveform,
            freqHz: freqHz,
            phase: phase
        )
    }
}

// MARK: - helpers
private func mean(_ a: [Float]) -> Float {
    guard !a.isEmpty else { return 0 }
    var m: Float = 0
    vDSP_meanv(a, 1, &m, vDSP_Length(a.count))
    return m
}

private func std(_ a: [Float]) -> Float {
    let m = mean(a)
    var s: Float = 0
    for v in a { s += (v - m) * (v - m) }
    return sqrtf(s / Float(max(1, a.count)))
}

private func detrendInPlace(_ x: inout [Float], window: Int) {
    let N = x.count
    if N == 0 { return }
    let w = max(1, min(N, window))
    var cum = [Double](repeating: 0, count: N + 1)
    for i in 0..<N { cum[i + 1] = cum[i] + Double(x[i]) }
    for i in 0..<N {
        let a = max(0, i - w / 2)
        let b = min(N, i + w / 2 + 1)
        let m = Float((cum[b] - cum[a]) / Double(b - a))
        x[i] = x[i] - m
    }
}

private func nextPow2(_ n: Int) -> Int {
    var p = 1
    while p < n { p <<= 1 }
    return p
}
