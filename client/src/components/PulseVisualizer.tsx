/*
 * PulseVisualizer — Bio-Lab Noir design
 *
 * Architecture:
 *   <video> (hidden, camera feed)
 *     → requestAnimationFrame loop
 *       → drawImage to offscreen canvas → read ROI pixels → RppgProcessor.push()
 *       → RppgProcessor.analyze() → result
 *       → draw amplified overlay on visible <canvas>
 *       → update waveform + BPM state
 *
 * Eulerian-style color amplification:
 *   The rPPG signal's instantaneous value is used to drive a per-pixel
 *   red-channel boost over the face ROI, rendered as a semi-transparent
 *   overlay with mix-blend-mode "screen" on a second canvas layer.
 *   The boost oscillates with the heartbeat phase, making the face visibly
 *   "pulse" in synchrony with the detected heart rate.
 */

import { useCallback, useEffect, useRef, useState } from "react";
import { RppgProcessor, type RppgResult } from "@/lib/rppg";

// ── constants ──────────────────────────────────────────────────────────────
const ROI_FRACTION = 0.55; // fraction of the shorter video dimension
const WAVEFORM_POINTS = 200;
const AMPLIFICATION_BASE = 4.0; // colour amplification factor (user-adjustable)
const TARGET_FPS = 30;

// ── types ──────────────────────────────────────────────────────────────────
type Phase = "idle" | "calibrating" | "measuring" | "error";

interface PulseVisualizerProps {
  amplification?: number; // 1–10, default 4
}

// ── component ──────────────────────────────────────────────────────────────
export default function PulseVisualizer({
  amplification = AMPLIFICATION_BASE,
}: PulseVisualizerProps) {
  const videoRef = useRef<HTMLVideoElement>(null);
  const displayCanvasRef = useRef<HTMLCanvasElement>(null);
  const waveCanvasRef = useRef<HTMLCanvasElement>(null);
  const offscreenRef = useRef<HTMLCanvasElement | null>(null);
  const processorRef = useRef(new RppgProcessor(12));
  const rafRef = useRef<number>(0);
  const lastFrameRef = useRef<number>(0);
  const vignetteTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const prevBpmRef = useRef<number>(0);

  const [phase, setPhase] = useState<Phase>("idle");
  const [result, setResult] = useState<RppgResult | null>(null);
  const [sampleCount, setSampleCount] = useState(0);
  const [vignetteActive, setVignetteActive] = useState(false);
  const [errorMsg, setErrorMsg] = useState("");
  const [waveformData, setWaveformData] = useState<number[]>([]);
  const [ampLevel, setAmpLevel] = useState(amplification);

  // ── camera start ──────────────────────────────────────────────────────────
  const startCamera = useCallback(async () => {
    setPhase("calibrating");
    setErrorMsg("");
    processorRef.current.reset();
    try {
      const stream = await navigator.mediaDevices.getUserMedia({
        video: {
          facingMode: "user",
          width: { ideal: 640 },
          height: { ideal: 480 },
          frameRate: { ideal: TARGET_FPS, max: 60 },
        },
        audio: false,
      });
      const video = videoRef.current!;
      video.srcObject = stream;
      await video.play();
    } catch (e: unknown) {
      const msg = e instanceof Error ? e.message : String(e);
      setErrorMsg(`カメラを起動できませんでした: ${msg}`);
      setPhase("error");
    }
  }, []);

  // ── camera stop ───────────────────────────────────────────────────────────
  const stopCamera = useCallback(() => {
    cancelAnimationFrame(rafRef.current);
    const video = videoRef.current;
    if (video?.srcObject) {
      (video.srcObject as MediaStream)
        .getTracks()
        .forEach((t) => t.stop());
      video.srcObject = null;
    }
    processorRef.current.reset();
    setPhase("idle");
    setResult(null);
    setSampleCount(0);
    setWaveformData([]);
  }, []);

  // ── main render loop ──────────────────────────────────────────────────────
  useEffect(() => {
    const video = videoRef.current;
    const displayCanvas = displayCanvasRef.current;
    const waveCanvas = waveCanvasRef.current;
    if (!video || !displayCanvas || !waveCanvas) return;
    if (phase === "idle" || phase === "error") {
      cancelAnimationFrame(rafRef.current);
      return;
    }

    const loop = (now: number) => {
      rafRef.current = requestAnimationFrame(loop);
      if (now - lastFrameRef.current < 1000 / TARGET_FPS) return;
      lastFrameRef.current = now;

      if (video.readyState < 2) return;

      const vw = video.videoWidth;
      const vh = video.videoHeight;
      if (!vw || !vh) return;

      // Ensure offscreen canvas matches video dimensions.
      if (
        !offscreenRef.current ||
        offscreenRef.current.width !== vw ||
        offscreenRef.current.height !== vh
      ) {
        const c = document.createElement("canvas");
        c.width = vw;
        c.height = vh;
        offscreenRef.current = c;
      }

      // Sync display canvas to container size.
      const rect = displayCanvas.getBoundingClientRect();
      if (
        displayCanvas.width !== Math.floor(rect.width) ||
        displayCanvas.height !== Math.floor(rect.height)
      ) {
        displayCanvas.width = Math.floor(rect.width) || vw;
        displayCanvas.height = Math.floor(rect.height) || vh;
      }

      const dw = displayCanvas.width;
      const dh = displayCanvas.height;
      const dCtx = displayCanvas.getContext("2d")!;

      // Mirror the video (selfie mode).
      dCtx.save();
      dCtx.translate(dw, 0);
      dCtx.scale(-1, 1);
      dCtx.drawImage(video, 0, 0, dw, dh);
      dCtx.restore();

      // ── ROI ──────────────────────────────────────────────────────────────
      const shorter = Math.min(vw, vh);
      const roiSize = Math.floor(shorter * ROI_FRACTION);
      const rx = Math.floor((vw - roiSize) / 2);
      const ry = Math.floor((vh - roiSize) / 2);

      // Read ROI pixels from offscreen.
      const off = offscreenRef.current;
      const offCtx = off.getContext("2d", { willReadFrequently: true })!;
      offCtx.drawImage(video, 0, 0, vw, vh);
      const imgData = offCtx.getImageData(rx, ry, roiSize, roiSize);
      const pixels = imgData.data;

      let sumR = 0, sumG = 0, sumB = 0;
      const step = 4 * 4; // sample every 4th pixel for speed
      let count = 0;
      for (let i = 0; i < pixels.length; i += step) {
        sumR += pixels[i];
        sumG += pixels[i + 1];
        sumB += pixels[i + 2];
        count++;
      }
      if (count > 0) {
        processorRef.current.push({
          t: now,
          r: sumR / count,
          g: sumG / count,
          b: sumB / count,
        });
      }

      const n = processorRef.current.count;
      setSampleCount(n);

      // ── Analyse ──────────────────────────────────────────────────────────
      const res = processorRef.current.analyze();
      if (res) {
        setResult(res);
        if (phase !== "measuring") setPhase("measuring");

        // Trigger vignette pulse on each new beat.
        const bpmRounded = Math.round(res.bpm);
        if (Math.abs(bpmRounded - prevBpmRef.current) > 0 || !vignetteTimerRef.current) {
          prevBpmRef.current = bpmRounded;
        }
        // Beat vignette: fire when phase crosses 0 (rising edge).
        // We approximate by checking if phase < π/4 (just after 0).
        if (res.phase < 0.4 && res.confidence > 0.25) {
          if (!vignetteTimerRef.current) {
            setVignetteActive(true);
            vignetteTimerRef.current = setTimeout(() => {
              setVignetteActive(false);
              vignetteTimerRef.current = null;
            }, 350);
          }
        }

        // Update waveform state (throttled to avoid excessive re-renders).
        const wf = res.waveform;
        const stride = Math.max(1, Math.floor(wf.length / WAVEFORM_POINTS));
        const pts: number[] = [];
        for (let i = 0; i < wf.length; i += stride) pts.push(wf[i]);
        setWaveformData(pts.slice(-WAVEFORM_POINTS));
      }

      // ── Colour amplification overlay ─────────────────────────────────────
      if (res && res.confidence > 0.15) {
        // Map rPPG waveform's last value to an amplification intensity.
        const wf = res.waveform;
        const lastVal = wf.length > 0 ? wf[wf.length - 1] : 0;
        // Clamp to [-1, 1] and scale by user amplification.
        const intensity = Math.max(-1, Math.min(1, lastVal)) * ampLevel;

        // Compute ROI position in display canvas coordinates.
        const scaleX = dw / vw;
        const scaleY = dh / vh;
        // Mirror: ROI x is mirrored.
        const dRoiX = dw - (rx + roiSize) * scaleX;
        const dRoiY = ry * scaleY;
        const dRoiW = roiSize * scaleX;
        const dRoiH = roiSize * scaleY;

        // Draw a colour-amplified overlay using screen blend mode.
        dCtx.save();
        dCtx.globalCompositeOperation = "screen";

        // Positive phase: red boost; negative phase: cyan (complement).
        const absI = Math.abs(intensity);
        if (intensity > 0) {
          // arterial red glow
          const alpha = Math.min(0.55, absI * 0.12);
          dCtx.fillStyle = `rgba(255,46,77,${alpha.toFixed(3)})`;
        } else {
          // venous / deoxygenated — cyan-magenta
          const alpha = Math.min(0.4, absI * 0.09);
          dCtx.fillStyle = `rgba(61,250,255,${alpha.toFixed(3)})`;
        }
        // Soft elliptical ROI mask via radial gradient.
        const grad = dCtx.createRadialGradient(
          dRoiX + dRoiW / 2,
          dRoiY + dRoiH / 2,
          0,
          dRoiX + dRoiW / 2,
          dRoiY + dRoiH / 2,
          Math.max(dRoiW, dRoiH) * 0.55,
        );
        const baseAlpha = intensity > 0
          ? Math.min(0.65, absI * 0.14)
          : Math.min(0.45, absI * 0.10);
        const colorCore = intensity > 0
          ? `rgba(255,46,77,${baseAlpha.toFixed(3)})`
          : `rgba(61,250,255,${(baseAlpha * 0.8).toFixed(3)})`;
        grad.addColorStop(0, colorCore);
        grad.addColorStop(1, "rgba(0,0,0,0)");
        dCtx.fillStyle = grad;
        dCtx.fillRect(dRoiX, dRoiY, dRoiW, dRoiH);
        dCtx.restore();

        // ROI border — pulsing brightness.
        dCtx.save();
        const borderAlpha = 0.3 + absI * 0.3;
        const borderColor = intensity > 0
          ? `rgba(255,46,77,${borderAlpha.toFixed(2)})`
          : `rgba(61,250,255,${borderAlpha.toFixed(2)})`;
        dCtx.strokeStyle = borderColor;
        dCtx.lineWidth = 1.5;
        dCtx.setLineDash([6, 4]);
        dCtx.strokeRect(dRoiX, dRoiY, dRoiW, dRoiH);
        dCtx.restore();
      } else {
        // ROI guide — static dashed box.
        const scaleX = dw / vw;
        const scaleY = dh / vh;
        const dRoiX = dw - (rx + roiSize) * scaleX;
        const dRoiY = ry * scaleY;
        const dRoiW = roiSize * scaleX;
        const dRoiH = roiSize * scaleY;
        dCtx.save();
        dCtx.strokeStyle = "rgba(61,250,255,0.35)";
        dCtx.lineWidth = 1.5;
        dCtx.setLineDash([6, 4]);
        dCtx.strokeRect(dRoiX, dRoiY, dRoiW, dRoiH);
        dCtx.restore();
      }

      // ── Waveform canvas ───────────────────────────────────────────────────
      drawWaveform(waveCanvas, waveformData, res);
    };

    rafRef.current = requestAnimationFrame(loop);
    return () => cancelAnimationFrame(rafRef.current);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [phase, ampLevel]);

  // ── waveform draw helper ──────────────────────────────────────────────────
  function drawWaveform(
    canvas: HTMLCanvasElement,
    data: number[],
    res: RppgResult | null,
  ) {
    const w = canvas.width;
    const h = canvas.height;
    const ctx = canvas.getContext("2d")!;
    ctx.clearRect(0, 0, w, h);

    // Grid lines.
    ctx.save();
    ctx.strokeStyle = "rgba(61,250,255,0.08)";
    ctx.lineWidth = 1;
    for (let y = 0; y <= 4; y++) {
      const yy = (y / 4) * h;
      ctx.beginPath();
      ctx.moveTo(0, yy);
      ctx.lineTo(w, yy);
      ctx.stroke();
    }
    ctx.restore();

    if (data.length < 2) return;

    // Waveform line.
    ctx.save();
    const grad = ctx.createLinearGradient(0, 0, w, 0);
    const hasSignal = res && res.confidence > 0.2;
    if (hasSignal) {
      grad.addColorStop(0, "rgba(255,46,77,0.0)");
      grad.addColorStop(0.3, "rgba(255,46,77,0.8)");
      grad.addColorStop(0.7, "rgba(255,61,240,0.9)");
      grad.addColorStop(1, "rgba(61,250,255,0.9)");
    } else {
      grad.addColorStop(0, "rgba(61,250,255,0.0)");
      grad.addColorStop(1, "rgba(61,250,255,0.4)");
    }
    ctx.strokeStyle = grad;
    ctx.lineWidth = 1.5;
    ctx.shadowColor = hasSignal ? "rgba(255,46,77,0.6)" : "rgba(61,250,255,0.4)";
    ctx.shadowBlur = 6;
    ctx.beginPath();
    const step = w / (data.length - 1);
    data.forEach((v, i) => {
      const x = i * step;
      const y = h / 2 - (v * h * 0.42);
      if (i === 0) ctx.moveTo(x, y);
      else ctx.lineTo(x, y);
    });
    ctx.stroke();
    ctx.restore();
  }

  // ── confidence colour ─────────────────────────────────────────────────────
  const confColor = (c: number) => {
    if (c > 0.6) return "text-emerald-400";
    if (c > 0.3) return "text-amber-400";
    return "text-red-400";
  };

  const bpmDisplay = result ? Math.round(result.bpm) : "--";
  const snrDisplay = result ? result.snr.toFixed(1) : "--";
  const confDisplay = result ? (result.confidence * 100).toFixed(0) : "--";
  const fpsDisplay = result ? result.fps.toFixed(0) : "--";

  // Progress bar (0..100) for calibration.
  const calibProgress = Math.min(100, (sampleCount / 64) * 100);

  return (
    <div className="relative w-full h-full overflow-hidden bg-black">
      {/* ── Hidden video element ── */}
      <video
        ref={videoRef}
        className="hidden"
        playsInline
        muted
        autoPlay
        onLoadedMetadata={() => {
          if (phase === "calibrating") setPhase("calibrating");
        }}
      />

      {/* ── Main camera canvas ── */}
      <canvas
        ref={displayCanvasRef}
        className="absolute inset-0 w-full h-full object-cover scanlines"
        style={{ imageRendering: "auto" }}
      />

      {/* ── Vignette pulse overlay ── */}
      <div
        className={`absolute inset-0 pointer-events-none transition-opacity duration-300 ${
          vignetteActive ? "opacity-100 pulse-vignette" : "opacity-0"
        }`}
        style={{
          background:
            "radial-gradient(ellipse at center, transparent 40%, rgba(255,46,77,0.18) 100%)",
        }}
      />

      {/* ── Scan line (calibrating) ── */}
      {phase === "calibrating" && <div className="scan-line" />}

      {/* ── Idle / error overlay ── */}
      {(phase === "idle" || phase === "error") && (
        <div className="absolute inset-0 flex flex-col items-center justify-center gap-6 bg-background/90 backdrop-blur-sm">
          <div className="text-center space-y-2">
            <p className="hud-label tracking-widest text-accent">
              FACE BLOOD
            </p>
            <h1
              className="text-3xl font-bold hud-numeric glow-red"
              style={{ color: "oklch(0.65 0.27 18)" }}
            >
              rPPG Pulse Visualizer
            </h1>
            <p className="text-sm text-muted-foreground max-w-xs leading-relaxed">
              顔をカメラに向けて枠内に収めてください。
              <br />
              Eulerian映像増幅で脈拍の色変化を可視化します。
            </p>
          </div>
          {phase === "error" && (
            <p className="text-destructive text-sm px-4 text-center">
              {errorMsg}
            </p>
          )}
          <button
            onClick={startCamera}
            className="px-8 py-3 rounded font-bold text-sm tracking-widest uppercase
              bg-primary text-primary-foreground
              hover:brightness-110 active:scale-95 transition-all
              shadow-[0_0_20px_rgba(255,46,77,0.4)]"
          >
            計測開始
          </button>
        </div>
      )}

      {/* ── HUD — top status bar ── */}
      {(phase === "calibrating" || phase === "measuring") && (
        <div
          className="absolute top-0 left-0 right-0 flex items-center justify-between
            px-4 py-2 hud-panel"
          style={{ paddingTop: "max(env(safe-area-inset-top), 0.5rem)" }}
        >
          <span className="hud-label text-accent">
            {phase === "calibrating" ? "CALIBRATING" : "MEASURING"}
          </span>
          <span className="hud-label">
            {phase === "calibrating"
              ? `${Math.round(calibProgress)}%`
              : `FPS ${fpsDisplay}`}
          </span>
          <button
            onClick={stopCamera}
            className="hud-label text-destructive hover:brightness-125 transition-colors"
          >
            STOP
          </button>
        </div>
      )}

      {/* ── HUD — BPM (left) ── */}
      {phase === "measuring" && (
        <div
          className="absolute left-3 hud-panel rounded-lg px-4 py-3 min-w-[110px]"
          style={{ top: "calc(max(env(safe-area-inset-top), 0.5rem) + 3rem)" }}
        >
          <p className="hud-label mb-1">HEART RATE</p>
          <p
            className={`hud-numeric text-5xl font-bold leading-none glow-red ${
              result && result.confidence > 0.3
                ? "text-primary"
                : "text-muted-foreground"
            }`}
          >
            {bpmDisplay}
          </p>
          <p className="hud-label mt-1">BPM</p>
        </div>
      )}

      {/* ── HUD — vitals (right) ── */}
      {phase === "measuring" && (
        <div
          className="absolute right-3 hud-panel rounded-lg px-3 py-3 min-w-[90px]"
          style={{ top: "calc(max(env(safe-area-inset-top), 0.5rem) + 3rem)" }}
        >
          <div className="space-y-2">
            <div>
              <p className="hud-label">SNR</p>
              <p className="hud-numeric text-lg font-bold text-accent glow-cyan">
                {snrDisplay}
                <span className="text-xs font-normal ml-1 text-muted-foreground">
                  dB
                </span>
              </p>
            </div>
            <div>
              <p className="hud-label">CONF</p>
              <p
                className={`hud-numeric text-lg font-bold ${confColor(
                  result?.confidence ?? 0,
                )}`}
              >
                {confDisplay}
                <span className="text-xs font-normal ml-0.5">%</span>
              </p>
            </div>
            <div>
              <p className="hud-label">SAMPLES</p>
              <p className="hud-numeric text-sm text-muted-foreground">
                {sampleCount}
              </p>
            </div>
          </div>
        </div>
      )}

      {/* ── Waveform panel (bottom) ── */}
      {(phase === "calibrating" || phase === "measuring") && (
        <div
          className="absolute left-0 right-0 hud-panel"
          style={{
            bottom: 0,
            paddingBottom: "max(env(safe-area-inset-bottom), 0.5rem)",
          }}
        >
          {/* Amplification slider */}
          <div className="flex items-center gap-3 px-4 pt-2 pb-1">
            <span className="hud-label shrink-0">AMP</span>
            <input
              type="range"
              min={1}
              max={10}
              step={0.5}
              value={ampLevel}
              onChange={(e) => setAmpLevel(Number(e.target.value))}
              className="flex-1 accent-primary h-1"
            />
            <span className="hud-numeric text-xs text-accent w-6 text-right">
              {ampLevel.toFixed(1)}
            </span>
          </div>

          {/* Waveform canvas */}
          <canvas
            ref={waveCanvasRef}
            width={600}
            height={72}
            className="w-full"
            style={{ height: 72, display: "block" }}
          />

          {/* Frequency label */}
          {result && (
            <p className="hud-label text-center pb-1">
              {result.freqHz.toFixed(2)} Hz &nbsp;·&nbsp; {Math.round(result.bpm)} BPM
            </p>
          )}
        </div>
      )}

      {/* ── Calibration progress bar ── */}
      {phase === "calibrating" && (
        <div className="absolute left-0 right-0 bottom-[140px] px-4">
          <div className="w-full h-1 bg-border rounded-full overflow-hidden">
            <div
              className="h-full bg-accent transition-all duration-300"
              style={{ width: `${calibProgress}%` }}
            />
          </div>
          <p className="hud-label text-center mt-1 text-accent">
            SIGNAL ACQUISITION {Math.round(calibProgress)}%
          </p>
        </div>
      )}
    </div>
  );
}
