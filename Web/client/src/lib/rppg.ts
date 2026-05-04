/*
 * rPPG signal processing — Bio-Lab Noir build
 *
 * - Maintains a ring buffer of (timestamp, R, G, B) samples taken from a face ROI.
 * - Detrends, band-pass filters around 0.7–3.5 Hz (≈ 42–210 BPM), and runs a
 *   short-time DFT to estimate dominant heart-rate frequency.
 * - Uses a simplified POS-like channel combination (X = G - B, Y = G + B - 2R)
 *   that is robust to illumination changes on smartphone cameras.
 * - Exposes an `analyze` method that returns BPM, SNR, and the most recent
 *   filtered waveform suitable for visualization.
 */

export interface RgbSample {
  t: number; // ms
  r: number;
  g: number;
  b: number;
}

export interface RppgResult {
  bpm: number;
  snr: number; // dB-ish, higher is better
  confidence: number; // 0..1
  fps: number;
  waveform: number[]; // filtered waveform, normalized to ~[-1, 1]
  freqHz: number;
  phase: number; // 0..2π, instantaneous phase of dominant component
}

const MIN_HZ = 0.7; // 42 BPM
const MAX_HZ = 3.5; // 210 BPM

export class RppgProcessor {
  private samples: RgbSample[] = [];
  private windowMs: number;

  constructor(windowSeconds = 10) {
    this.windowMs = windowSeconds * 1000;
  }

  push(sample: RgbSample) {
    this.samples.push(sample);
    const cutoff = sample.t - this.windowMs;
    // drop old
    while (this.samples.length > 0 && this.samples[0].t < cutoff) {
      this.samples.shift();
    }
  }

  reset() {
    this.samples = [];
  }

  get count() {
    return this.samples.length;
  }

  get spanSeconds() {
    if (this.samples.length < 2) return 0;
    return (
      (this.samples[this.samples.length - 1].t - this.samples[0].t) / 1000
    );
  }

  analyze(): RppgResult | null {
    const N = this.samples.length;
    if (N < 64) return null;

    const t0 = this.samples[0].t;
    const tN = this.samples[N - 1].t;
    const durationS = (tN - t0) / 1000;
    if (durationS < 3) return null;

    const fps = (N - 1) / durationS;
    const targetFs = Math.min(60, Math.max(15, fps));
    const M = Math.floor(durationS * targetFs);
    if (M < 64) return null;

    // Resample to uniform sampling at targetFs using linear interpolation.
    const rs = new Float32Array(M);
    const gs = new Float32Array(M);
    const bs = new Float32Array(M);
    let j = 0;
    for (let i = 0; i < M; i++) {
      const tt = t0 + (i / (targetFs)) * 1000;
      while (j < N - 2 && this.samples[j + 1].t < tt) j++;
      const a = this.samples[j];
      const b = this.samples[Math.min(j + 1, N - 1)];
      const span = Math.max(1, b.t - a.t);
      const u = Math.min(1, Math.max(0, (tt - a.t) / span));
      rs[i] = a.r * (1 - u) + b.r * u;
      gs[i] = a.g * (1 - u) + b.g * u;
      bs[i] = a.b * (1 - u) + b.b * u;
    }

    // Normalize each channel by its mean.
    const meanR = mean(rs);
    const meanG = mean(gs);
    const meanB = mean(bs);
    const nr = new Float32Array(M);
    const ng = new Float32Array(M);
    const nb = new Float32Array(M);
    for (let i = 0; i < M; i++) {
      nr[i] = meanR > 0 ? rs[i] / meanR - 1 : 0;
      ng[i] = meanG > 0 ? gs[i] / meanG - 1 : 0;
      nb[i] = meanB > 0 ? bs[i] / meanB - 1 : 0;
    }

    // POS-like projection.
    // X = G - B  (chrominance roughly aligned with pulsatile blood absorption)
    // Y = G + B - 2R
    const X = new Float32Array(M);
    const Y = new Float32Array(M);
    for (let i = 0; i < M; i++) {
      X[i] = ng[i] - nb[i];
      Y[i] = ng[i] + nb[i] - 2 * nr[i];
    }
    const sX = std(X);
    const sY = std(Y);
    const alpha = sY > 1e-9 ? sX / sY : 1;
    const S = new Float32Array(M);
    for (let i = 0; i < M; i++) S[i] = X[i] + alpha * Y[i];

    // Detrend (subtract moving average ≈ 0.7s window) and bandpass via FFT.
    detrendInPlace(S, Math.max(3, Math.floor(targetFs * 0.7)));
    // Hann window.
    const W = new Float32Array(M);
    for (let i = 0; i < M; i++) {
      W[i] = S[i] * 0.5 * (1 - Math.cos((2 * Math.PI * i) / (M - 1)));
    }

    // DFT (bounded resolution → use radix-friendly size).
    const NFFT = nextPow2(Math.max(256, M));
    const re = new Float32Array(NFFT);
    const im = new Float32Array(NFFT);
    for (let i = 0; i < M; i++) re[i] = W[i];
    fft(re, im);

    const binHz = targetFs / NFFT;
    const minBin = Math.max(1, Math.floor(MIN_HZ / binHz));
    const maxBin = Math.min(NFFT / 2 - 1, Math.ceil(MAX_HZ / binHz));

    let peakBin = minBin;
    let peakMag = 0;
    let totalMag = 0;
    let inBandMag = 0;
    for (let k = 1; k < NFFT / 2; k++) {
      const m = Math.hypot(re[k], im[k]);
      totalMag += m;
      if (k >= minBin && k <= maxBin) {
        inBandMag += m;
        if (m > peakMag) {
          peakMag = m;
          peakBin = k;
        }
      }
    }

    // Parabolic interpolation around peak for sub-bin resolution.
    let kHat = peakBin;
    if (peakBin > 1 && peakBin < NFFT / 2 - 1) {
      const ym1 = Math.hypot(re[peakBin - 1], im[peakBin - 1]);
      const y0 = peakMag;
      const yp1 = Math.hypot(re[peakBin + 1], im[peakBin + 1]);
      const denom = ym1 - 2 * y0 + yp1;
      if (Math.abs(denom) > 1e-9) {
        const delta = (0.5 * (ym1 - yp1)) / denom;
        kHat = peakBin + delta;
      }
    }
    const freqHz = kHat * binHz;
    const bpm = freqHz * 60;

    // SNR-ish: peak vs in-band noise floor.
    const noiseFloor = (inBandMag - peakMag) / Math.max(1, maxBin - minBin);
    const snr = noiseFloor > 0 ? 20 * Math.log10(peakMag / noiseFloor) : 0;
    const confidence = Math.max(
      0,
      Math.min(1, (snr - 2) / 10) *
        Math.min(1, durationS / 6) *
        (peakMag / Math.max(1e-9, totalMag / (NFFT / 4))),
    );

    // Build a clean filtered waveform by zeroing bins outside band, plus
    // narrow ±0.4 Hz around peak emphasized for visualization.
    const reF = new Float32Array(NFFT);
    const imF = new Float32Array(NFFT);
    const peakHalfBins = Math.max(2, Math.floor(0.4 / binHz));
    for (let k = 0; k < NFFT; k++) {
      const kk = k <= NFFT / 2 ? k : NFFT - k;
      if (kk >= minBin && kk <= maxBin) {
        let gain = 1;
        if (Math.abs(kk - peakBin) <= peakHalfBins) gain = 1.6;
        reF[k] = re[k] * gain;
        imF[k] = im[k] * gain;
      }
    }
    ifft(reF, imF);
    const waveform: number[] = new Array(M);
    let wMax = 1e-9;
    for (let i = 0; i < M; i++) {
      waveform[i] = reF[i];
      const a = Math.abs(reF[i]);
      if (a > wMax) wMax = a;
    }
    for (let i = 0; i < M; i++) waveform[i] /= wMax;

    // Instantaneous phase via the latest sample's analytic signal value.
    const lastReal = reF[M - 1];
    const lastImag = imF[M - 1];
    const phase = Math.atan2(lastImag, lastReal);

    return {
      bpm,
      snr,
      confidence,
      fps: targetFs,
      waveform,
      freqHz,
      phase: phase < 0 ? phase + 2 * Math.PI : phase,
    };
  }
}

// ---------- helpers ----------

function mean(arr: ArrayLike<number>): number {
  let s = 0;
  for (let i = 0; i < arr.length; i++) s += arr[i];
  return arr.length ? s / arr.length : 0;
}

function std(arr: ArrayLike<number>): number {
  const m = mean(arr);
  let s = 0;
  for (let i = 0; i < arr.length; i++) {
    const d = arr[i] - m;
    s += d * d;
  }
  return Math.sqrt(arr.length ? s / arr.length : 0);
}

function detrendInPlace(x: Float32Array, win: number) {
  const N = x.length;
  if (N === 0) return;
  const w = Math.max(1, Math.min(N, win | 0));
  // running sum
  const cum = new Float64Array(N + 1);
  for (let i = 0; i < N; i++) cum[i + 1] = cum[i] + x[i];
  for (let i = 0; i < N; i++) {
    const a = Math.max(0, i - (w >> 1));
    const b = Math.min(N, i + (w >> 1) + 1);
    const m = (cum[b] - cum[a]) / (b - a);
    x[i] = x[i] - m;
  }
}

function nextPow2(n: number): number {
  let p = 1;
  while (p < n) p <<= 1;
  return p;
}

// In-place iterative radix-2 FFT.
function fft(re: Float32Array, im: Float32Array) {
  const n = re.length;
  // bit reversal
  let j = 0;
  for (let i = 1; i < n; i++) {
    let bit = n >> 1;
    for (; j & bit; bit >>= 1) j ^= bit;
    j ^= bit;
    if (i < j) {
      [re[i], re[j]] = [re[j], re[i]];
      [im[i], im[j]] = [im[j], im[i]];
    }
  }
  for (let len = 2; len <= n; len <<= 1) {
    const ang = (-2 * Math.PI) / len;
    const wRe = Math.cos(ang);
    const wIm = Math.sin(ang);
    for (let i = 0; i < n; i += len) {
      let cRe = 1;
      let cIm = 0;
      const half = len >> 1;
      for (let k = 0; k < half; k++) {
        const aRe = re[i + k];
        const aIm = im[i + k];
        const bRe = re[i + k + half] * cRe - im[i + k + half] * cIm;
        const bIm = re[i + k + half] * cIm + im[i + k + half] * cRe;
        re[i + k] = aRe + bRe;
        im[i + k] = aIm + bIm;
        re[i + k + half] = aRe - bRe;
        im[i + k + half] = aIm - bIm;
        const nRe = cRe * wRe - cIm * wIm;
        cIm = cRe * wIm + cIm * wRe;
        cRe = nRe;
      }
    }
  }
}

function ifft(re: Float32Array, im: Float32Array) {
  const n = re.length;
  for (let i = 0; i < n; i++) im[i] = -im[i];
  fft(re, im);
  for (let i = 0; i < n; i++) {
    re[i] = re[i] / n;
    im[i] = -im[i] / n;
  }
}
