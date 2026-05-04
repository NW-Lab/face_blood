/*
 * PulseVisualizer — Bio-Lab Noir, with bold full-face color amplification.
 *
 * Three visualization modes:
 *   - SUBTLE  : soft radial overlay (original calm look)
 *   - VIVID   : full-frame screen blend with strong red/blue tint synced to heartbeat
 *   - EXTREME : per-pixel color shift on the face — pixels above skin-tone
 *               threshold are pushed dramatically toward arterial red (systole)
 *               or oxy cyan/blue (diastole), making the face visibly throb.
 *
 * EXTREME pipeline (executed on a small offscreen canvas for performance):
 *   1. Down-sample the camera frame to 240×180 → fast pixel access.
 *   2. For every pixel detected as skin (simple R>G>B + R/G ratio rule),
 *      mix its RGB toward the target colour by `intensity` (0..1) computed
 *      from the rPPG instantaneous waveform value × user amplification.
 *   3. Up-scale the result and composite over the mirrored video using
 *      `globalCompositeOperation = "lighter"` for an extra glow punch.
 *
 * The skin-tone heuristic is intentionally simple — for a vivid demo it is
 * better to slightly over-tint than to miss pixels.
 */

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { RppgProcessor, type RppgResult } from "@/lib/rppg";

// ── constants ──────────────────────────────────────────────────────────────
const ROI_FRACTION = 0.55;
const WAVEFORM_POINTS = 200;
const TARGET_FPS = 30;
const FX_W = 240; // offscreen FX canvas width
const FX_H = 180; // offscreen FX canvas height

// ── modes ──────────────────────────────────────────────────────────────────
type ModeId = "subtle" | "vivid" | "extreme";

interface ModeSpec {
  id: ModeId;
  label: string;
  caption: string;
  ampScale: number; // multiplies the amp slider
  // pixel-shift colours (systole = positive phase, diastole = negative phase)
  systoleRgb: [number, number, number];
  diastoleRgb: [number, number, number];
}

const MODES: ModeSpec[] = [
  {
    id: "subtle",
    label: "SUBTLE",
    caption: "控えめモード — ROI内のみ柔らかく増幅",
    ampScale: 1.0,
    systoleRgb: [255, 60, 90],
    diastoleRgb: [80, 200, 255],
  },
  {
    id: "vivid",
    label: "VIVID",
    caption: "派手モード — 画面全体が赤⇄青に染まる",
    ampScale: 2.5,
    systoleRgb: [255, 30, 60],
    diastoleRgb: [40, 120, 255],
  },
  {
    id: "extreme",
    label: "EXTREME",
    caption: "超派手モード — 顔全体がドカンと真っ赤⇄真っ青に",
    ampScale: 5.0,
    systoleRgb: [255, 0, 30],
    diastoleRgb: [0, 80, 255],
  },
];

// ── types ──────────────────────────────────────────────────────────────────
type Phase = "idle" | "calibrating" | "measuring" | "error";
type FacingMode = "user" | "environment";

// ── component ──────────────────────────────────────────────────────────────
export default function PulseVisualizer() {
  const videoRef = useRef<HTMLVideoElement>(null);
  const displayCanvasRef = useRef<HTMLCanvasElement>(null);
  const waveCanvasRef = useRef<HTMLCanvasElement>(null);
  // offscreen for ROI sampling
  const sampleCanvasRef = useRef<HTMLCanvasElement | null>(null);
  // offscreen for pixel-shift FX (small for performance)
  const fxCanvasRef = useRef<HTMLCanvasElement | null>(null);
  const processorRef = useRef(new RppgProcessor(12));
  const rafRef = useRef<number>(0);
  const lastFrameRef = useRef<number>(0);
  const vignetteTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  // smoothed pulse value for buttery animation between rPPG updates
  const smoothPulseRef = useRef<number>(0);
  // beat detection state
  const lastBeatPhaseRef = useRef<number>(0);

  const [phase, setPhase] = useState<Phase>("idle");
  const [result, setResult] = useState<RppgResult | null>(null);
  const [sampleCount, setSampleCount] = useState(0);
  const [vignetteActive, setVignetteActive] = useState(false);
  const [errorMsg, setErrorMsg] = useState("");
  const [waveformData, setWaveformData] = useState<number[]>([]);
  const [ampLevel, setAmpLevel] = useState(5);
  const [modeId, setModeId] = useState<ModeId>("vivid");
  const [cameraDevices, setCameraDevices] = useState<MediaDeviceInfo[]>([]);
  const [selectedDeviceId, setSelectedDeviceId] = useState<string | null>(null);
  const [preferredFacingMode, setPreferredFacingMode] = useState<FacingMode>("user");
  const [isSwitchingCamera, setIsSwitchingCamera] = useState(false);

  const mode = useMemo(
    () => MODES.find((m) => m.id === modeId) ?? MODES[1],
    [modeId],
  );

  const stopStreamTracks = useCallback(() => {
    const video = videoRef.current;
    if (video?.srcObject) {
      (video.srcObject as MediaStream).getTracks().forEach((t) => t.stop());
      video.srcObject = null;
    }
  }, []);

  const refreshCameraDevices = useCallback(async (currentDeviceId?: string) => {
    if (!navigator.mediaDevices?.enumerateDevices) return;
    const devices = await navigator.mediaDevices.enumerateDevices();
    const inputs = devices.filter((d) => d.kind === "videoinput");
    setCameraDevices(inputs);
    if (currentDeviceId) {
      setSelectedDeviceId(currentDeviceId);
      return;
    }
    if (!selectedDeviceId && inputs[0]) {
      setSelectedDeviceId(inputs[0].deviceId);
    }
  }, [selectedDeviceId]);

  // ── camera start ──────────────────────────────────────────────────────────
  const startCamera = useCallback(async (opts?: { deviceId?: string; facingMode?: FacingMode }) => {
    setPhase("calibrating");
    setErrorMsg("");
    processorRef.current.reset();
    try {
      stopStreamTracks();
      const videoConstraints: MediaTrackConstraints = {
        width: { ideal: 640 },
        height: { ideal: 480 },
        frameRate: { ideal: TARGET_FPS, max: 60 },
      };
      if (opts?.deviceId) {
        videoConstraints.deviceId = { exact: opts.deviceId };
      } else {
        videoConstraints.facingMode = opts?.facingMode ?? preferredFacingMode;
      }

      const stream = await navigator.mediaDevices.getUserMedia({
        video: videoConstraints,
        audio: false,
      });
      const video = videoRef.current!;
      video.srcObject = stream;
      await video.play();

      const track = stream.getVideoTracks()[0];
      const settings = track?.getSettings();
      const activeDeviceId =
        typeof settings?.deviceId === "string" ? settings.deviceId : undefined;
      if (settings?.facingMode === "user" || settings?.facingMode === "environment") {
        setPreferredFacingMode(settings.facingMode);
      }
      await refreshCameraDevices(activeDeviceId);
    } catch (e: unknown) {
      const msg = e instanceof Error ? e.message : String(e);
      setErrorMsg(`カメラを起動できませんでした: ${msg}`);
      setPhase("error");
    }
  }, [preferredFacingMode, refreshCameraDevices, stopStreamTracks]);

  // ── camera stop ───────────────────────────────────────────────────────────
  const stopCamera = useCallback(() => {
    cancelAnimationFrame(rafRef.current);
    stopStreamTracks();
    processorRef.current.reset();
    setPhase("idle");
    setResult(null);
    setSampleCount(0);
    setWaveformData([]);
    smoothPulseRef.current = 0;
  }, [stopStreamTracks]);

  const switchCamera = useCallback(async () => {
    if (isSwitchingCamera) return;
    setIsSwitchingCamera(true);
    setErrorMsg("");
    try {
      if (cameraDevices.length > 1) {
        const currentIndex = cameraDevices.findIndex((d) => d.deviceId === selectedDeviceId);
        const nextIndex = currentIndex >= 0
          ? (currentIndex + 1) % cameraDevices.length
          : 0;
        const nextDevice = cameraDevices[nextIndex];
        await startCamera({ deviceId: nextDevice.deviceId });
        return;
      }

      // Fallback for browsers that do not expose multiple device IDs.
      const nextFacingMode: FacingMode =
        preferredFacingMode === "user" ? "environment" : "user";
      setPreferredFacingMode(nextFacingMode);
      await startCamera({ facingMode: nextFacingMode });
    } finally {
      setIsSwitchingCamera(false);
    }
  }, [cameraDevices, isSwitchingCamera, preferredFacingMode, selectedDeviceId, startCamera]);

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
      const dt = (now - lastFrameRef.current) / 1000;
      lastFrameRef.current = now;

      if (video.readyState < 2) return;
      const vw = video.videoWidth;
      const vh = video.videoHeight;
      if (!vw || !vh) return;

      // Ensure offscreen sample canvas matches video.
      if (
        !sampleCanvasRef.current ||
        sampleCanvasRef.current.width !== vw ||
        sampleCanvasRef.current.height !== vh
      ) {
        const c = document.createElement("canvas");
        c.width = vw;
        c.height = vh;
        sampleCanvasRef.current = c;
      }
      // Ensure FX canvas exists.
      if (!fxCanvasRef.current) {
        const c = document.createElement("canvas");
        c.width = FX_W;
        c.height = FX_H;
        fxCanvasRef.current = c;
      }

      // Sync display canvas to its container size.
      const rect = displayCanvas.getBoundingClientRect();
      const targetW = Math.floor(rect.width) || vw;
      const targetH = Math.floor(rect.height) || vh;
      if (
        displayCanvas.width !== targetW ||
        displayCanvas.height !== targetH
      ) {
        displayCanvas.width = targetW;
        displayCanvas.height = targetH;
      }

      const dw = displayCanvas.width;
      const dh = displayCanvas.height;
      const dCtx = displayCanvas.getContext("2d")!;

      // Mirror the video for selfie experience.
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

      // Read ROI pixels from sample canvas.
      const sample = sampleCanvasRef.current;
      const sCtx = sample.getContext("2d", { willReadFrequently: true })!;
      sCtx.drawImage(video, 0, 0, vw, vh);
      const imgData = sCtx.getImageData(rx, ry, roiSize, roiSize);
      const pix = imgData.data;
      let sumR = 0, sumG = 0, sumB = 0;
      const step = 4 * 4; // sample every 4th pixel
      let cnt = 0;
      for (let i = 0; i < pix.length; i += step) {
        sumR += pix[i];
        sumG += pix[i + 1];
        sumB += pix[i + 2];
        cnt++;
      }
      if (cnt > 0) {
        processorRef.current.push({
          t: now,
          r: sumR / cnt,
          g: sumG / cnt,
          b: sumB / cnt,
        });
      }
      const n = processorRef.current.count;
      setSampleCount(n);

      // ── Analyse ──────────────────────────────────────────────────────────
      const res = processorRef.current.analyze();
      let pulseRaw = 0; // -1..1 instantaneous waveform value
      if (res) {
        setResult(res);
        if (phase !== "measuring") setPhase("measuring");

        const wf = res.waveform;
        if (wf.length > 0) pulseRaw = wf[wf.length - 1];

        // Update waveform state (downsampled).
        const stride = Math.max(1, Math.floor(wf.length / WAVEFORM_POINTS));
        const pts: number[] = [];
        for (let i = 0; i < wf.length; i += stride) pts.push(wf[i]);
        setWaveformData(pts.slice(-WAVEFORM_POINTS));

        // Beat detection: phase wrap-around → fire vignette.
        const ph = res.phase;
        const prev = lastBeatPhaseRef.current;
        const wrapped = ph < prev - 1.0 || (prev > 5 && ph < 1);
        if (wrapped && res.confidence > 0.2 && !vignetteTimerRef.current) {
          setVignetteActive(true);
          vignetteTimerRef.current = setTimeout(() => {
            setVignetteActive(false);
            vignetteTimerRef.current = null;
          }, 320);
        }
        lastBeatPhaseRef.current = ph;
      }

      // Smoothly track the rPPG value for buttery 60fps animation.
      // Exponential moving average toward pulseRaw.
      const tau = 0.06; // seconds
      const alphaSmooth = 1 - Math.exp(-dt / tau);
      smoothPulseRef.current +=
        (pulseRaw - smoothPulseRef.current) * alphaSmooth;

      const intensity = Math.max(
        -1.5,
        Math.min(1.5, smoothPulseRef.current * (ampLevel / 3) * mode.ampScale),
      );
      const sign = intensity >= 0 ? 1 : -1;
      const absI = Math.min(1.2, Math.abs(intensity));

      // Compute mirrored ROI rectangle in display coords.
      const scaleX = dw / vw;
      const scaleY = dh / vh;
      const dRoiX = dw - (rx + roiSize) * scaleX;
      const dRoiY = ry * scaleY;
      const dRoiW = roiSize * scaleX;
      const dRoiH = roiSize * scaleY;

      const showFx = res !== null && res.confidence > 0.1;

      if (showFx) {
        if (mode.id === "subtle") {
          drawSubtleOverlay(dCtx, dRoiX, dRoiY, dRoiW, dRoiH, sign, absI, mode);
        } else if (mode.id === "vivid") {
          drawVividOverlay(dCtx, dw, dh, dRoiX, dRoiY, dRoiW, dRoiH, sign, absI, mode);
        } else {
          drawExtremeOverlay(
            dCtx,
            video,
            fxCanvasRef.current!,
            dw,
            dh,
            sign,
            absI,
            mode,
          );
        }

        // ROI border — pulsing.
        dCtx.save();
        const borderAlpha = 0.35 + absI * 0.4;
        const [tr, tg, tb] =
          sign > 0 ? mode.systoleRgb : mode.diastoleRgb;
        dCtx.strokeStyle = `rgba(${tr},${tg},${tb},${borderAlpha.toFixed(2)})`;
        dCtx.lineWidth = 2;
        dCtx.setLineDash([8, 6]);
        dCtx.strokeRect(dRoiX, dRoiY, dRoiW, dRoiH);
        dCtx.restore();
      } else {
        // Static ROI guide.
        dCtx.save();
        dCtx.strokeStyle = "rgba(61,250,255,0.4)";
        dCtx.lineWidth = 2;
        dCtx.setLineDash([8, 6]);
        dCtx.strokeRect(dRoiX, dRoiY, dRoiW, dRoiH);
        dCtx.restore();
      }

      // Waveform.
      drawWaveform(waveCanvas, waveformData, res, mode);
    };

    rafRef.current = requestAnimationFrame(loop);
    return () => cancelAnimationFrame(rafRef.current);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [phase, ampLevel, mode]);

  // ── overlay renderers ─────────────────────────────────────────────────────
  function drawSubtleOverlay(
    ctx: CanvasRenderingContext2D,
    x: number, y: number, w: number, h: number,
    sign: number, absI: number, m: ModeSpec,
  ) {
    const [tr, tg, tb] = sign > 0 ? m.systoleRgb : m.diastoleRgb;
    ctx.save();
    ctx.globalCompositeOperation = "screen";
    const grad = ctx.createRadialGradient(
      x + w / 2, y + h / 2, 0,
      x + w / 2, y + h / 2, Math.max(w, h) * 0.6,
    );
    const a = Math.min(0.7, absI * 0.4);
    grad.addColorStop(0, `rgba(${tr},${tg},${tb},${a.toFixed(3)})`);
    grad.addColorStop(1, "rgba(0,0,0,0)");
    ctx.fillStyle = grad;
    ctx.fillRect(x, y, w, h);
    ctx.restore();
  }

  function drawVividOverlay(
    ctx: CanvasRenderingContext2D,
    dw: number, dh: number,
    x: number, y: number, w: number, h: number,
    sign: number, absI: number, m: ModeSpec,
  ) {
    const [tr, tg, tb] = sign > 0 ? m.systoleRgb : m.diastoleRgb;
    // Strong radial tint over the face.
    ctx.save();
    ctx.globalCompositeOperation = "screen";
    const grad = ctx.createRadialGradient(
      x + w / 2, y + h / 2, w * 0.1,
      x + w / 2, y + h / 2, Math.max(w, h) * 0.85,
    );
    const a1 = Math.min(0.95, absI * 0.85);
    const a2 = Math.min(0.5, absI * 0.45);
    grad.addColorStop(0, `rgba(${tr},${tg},${tb},${a1.toFixed(3)})`);
    grad.addColorStop(0.55, `rgba(${tr},${tg},${tb},${a2.toFixed(3)})`);
    grad.addColorStop(1, "rgba(0,0,0,0)");
    ctx.fillStyle = grad;
    ctx.fillRect(0, 0, dw, dh);
    ctx.restore();

    // Full-screen subtle wash too.
    ctx.save();
    ctx.globalCompositeOperation = "soft-light";
    ctx.fillStyle = `rgba(${tr},${tg},${tb},${(absI * 0.35).toFixed(3)})`;
    ctx.fillRect(0, 0, dw, dh);
    ctx.restore();
  }

  function drawExtremeOverlay(
    ctx: CanvasRenderingContext2D,
    video: HTMLVideoElement,
    fxCanvas: HTMLCanvasElement,
    dw: number, dh: number,
    sign: number, absI: number, m: ModeSpec,
  ) {
    // 1) Render mirrored small frame to fxCanvas.
    const fxCtx = fxCanvas.getContext("2d", { willReadFrequently: true })!;
    fxCtx.save();
    fxCtx.translate(FX_W, 0);
    fxCtx.scale(-1, 1);
    fxCtx.drawImage(video, 0, 0, FX_W, FX_H);
    fxCtx.restore();

    // 2) Per-pixel skin shift.
    const img = fxCtx.getImageData(0, 0, FX_W, FX_H);
    const data = img.data;
    const [tr, tg, tb] = sign > 0 ? m.systoleRgb : m.diastoleRgb;
    // mix factor — the further |intensity| from 0, the more dramatic.
    const mixBase = Math.min(0.95, 0.55 + absI * 0.6);

    for (let i = 0; i < data.length; i += 4) {
      const r = data[i];
      const g = data[i + 1];
      const b = data[i + 2];
      // Simple skin detection: brightness between 60 and 240, R > G > B,
      // and R/G ratio > 1.05. Cheap and good enough for a face-centred frame.
      const bright = (r + g + b) / 3;
      const isSkin =
        bright > 55 &&
        bright < 245 &&
        r > g &&
        g > b * 0.85 &&
        r > b &&
        r * 1.0 > g * 1.04;
      if (!isSkin) continue;

      // Extra weight for warm/light pixels, less for shadows.
      const weight = Math.min(1, (bright - 50) / 180);
      const k = mixBase * weight;
      data[i] = Math.min(255, r * (1 - k) + tr * k);
      data[i + 1] = Math.min(255, g * (1 - k) + tg * k);
      data[i + 2] = Math.min(255, b * (1 - k) + tb * k);
      // Saturation boost for the dramatic effect.
      const avg = (data[i] + data[i + 1] + data[i + 2]) / 3;
      const satBoost = 0.4 * absI;
      data[i] = Math.min(255, data[i] + (data[i] - avg) * satBoost);
      data[i + 1] = Math.min(255, data[i + 1] + (data[i + 1] - avg) * satBoost);
      data[i + 2] = Math.min(255, data[i + 2] + (data[i + 2] - avg) * satBoost);
    }
    fxCtx.putImageData(img, 0, 0);

    // 3) Composite the recoloured face on top of the video at full size.
    ctx.save();
    ctx.globalCompositeOperation = "lighter";
    ctx.globalAlpha = Math.min(1, 0.55 + absI * 0.45);
    ctx.imageSmoothingEnabled = true;
    ctx.imageSmoothingQuality = "high";
    ctx.drawImage(fxCanvas, 0, 0, dw, dh);
    ctx.restore();

    // 4) Punchy full-screen glow tint.
    ctx.save();
    ctx.globalCompositeOperation = "screen";
    ctx.fillStyle = `rgba(${tr},${tg},${tb},${(absI * 0.25).toFixed(3)})`;
    ctx.fillRect(0, 0, dw, dh);
    ctx.restore();
  }

  function drawWaveform(
    canvas: HTMLCanvasElement,
    data: number[],
    res: RppgResult | null,
    m: ModeSpec,
  ) {
    const w = canvas.width;
    const h = canvas.height;
    const ctx = canvas.getContext("2d")!;
    ctx.clearRect(0, 0, w, h);

    ctx.save();
    ctx.strokeStyle = "rgba(120,200,255,0.08)";
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

    ctx.save();
    const grad = ctx.createLinearGradient(0, 0, w, 0);
    const hasSignal = res && res.confidence > 0.2;
    const [sr, sg, sb] = m.systoleRgb;
    const [dr, dg, db] = m.diastoleRgb;
    if (hasSignal) {
      grad.addColorStop(0, `rgba(${dr},${dg},${db},0.0)`);
      grad.addColorStop(0.3, `rgba(${dr},${dg},${db},0.85)`);
      grad.addColorStop(0.7, `rgba(${sr},${sg},${sb},0.95)`);
      grad.addColorStop(1, `rgba(${sr},${sg},${sb},0.95)`);
    } else {
      grad.addColorStop(0, "rgba(120,200,255,0.0)");
      grad.addColorStop(1, "rgba(120,200,255,0.5)");
    }
    ctx.strokeStyle = grad;
    ctx.lineWidth = 1.8;
    ctx.shadowColor = hasSignal
      ? `rgba(${sr},${sg},${sb},0.6)`
      : "rgba(120,200,255,0.4)";
    ctx.shadowBlur = 8;
    ctx.beginPath();
    const step = w / (data.length - 1);
    data.forEach((v, i) => {
      const x = i * step;
      const y = h / 2 - v * h * 0.42;
      if (i === 0) ctx.moveTo(x, y);
      else ctx.lineTo(x, y);
    });
    ctx.stroke();
    ctx.restore();
  }

  // ── derived display values ────────────────────────────────────────────────
  const confColor = (c: number) =>
    c > 0.6 ? "text-emerald-400" : c > 0.3 ? "text-amber-400" : "text-red-400";
  const bpmDisplay = result ? Math.round(result.bpm) : "--";
  const snrDisplay = result ? result.snr.toFixed(1) : "--";
  const confDisplay = result ? (result.confidence * 100).toFixed(0) : "--";
  const fpsDisplay = result ? result.fps.toFixed(0) : "--";
  const calibProgress = Math.min(100, (sampleCount / 64) * 100);

  return (
    <div className="relative w-full h-full overflow-hidden bg-black">
      <video
        ref={videoRef}
        className="hidden"
        playsInline
        muted
        autoPlay
      />
      <canvas
        ref={displayCanvasRef}
        className="absolute inset-0 w-full h-full object-cover scanlines"
      />

      {/* Vignette pulse */}
      <div
        className={`absolute inset-0 pointer-events-none transition-opacity duration-300 ${
          vignetteActive ? "opacity-100 pulse-vignette" : "opacity-0"
        }`}
        style={{
          background:
            "radial-gradient(ellipse at center, transparent 35%, rgba(255,30,60,0.28) 100%)",
        }}
      />

      {phase === "calibrating" && <div className="scan-line" />}

      {/* Idle / error overlay */}
      {(phase === "idle" || phase === "error") && (
        <div className="absolute inset-0 flex flex-col items-center justify-center gap-6 bg-background/90 backdrop-blur-sm">
          <div className="text-center space-y-2 px-6">
            <p className="hud-label tracking-widest text-accent">FACE BLOOD</p>
            <h1
              className="text-3xl font-bold hud-numeric glow-red"
              style={{ color: "oklch(0.65 0.27 18)" }}
            >
              rPPG Pulse Visualizer
            </h1>
            <p className="text-sm text-muted-foreground max-w-xs leading-relaxed mx-auto">
              顔をカメラに向けて枠内に収めてください。
              <br />
              脈拍に合わせて顔が赤と青にダイナミックに脈動します。
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
              shadow-[0_0_20px_rgba(255,46,77,0.5)]"
          >
            計測開始
          </button>
        </div>
      )}

      {/* Top status bar */}
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
          <div className="flex items-center gap-3">
            <button
              onClick={switchCamera}
              disabled={isSwitchingCamera}
              className="hud-label text-accent hover:brightness-125 transition-colors disabled:opacity-50"
            >
              {isSwitchingCamera ? "CAM..." : "CAM"}
            </button>
            <button
              onClick={stopCamera}
              className="hud-label text-destructive hover:brightness-125 transition-colors"
            >
              STOP
            </button>
          </div>
        </div>
      )}

      {/* BPM (left) */}
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

      {/* Vitals (right) */}
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
                <span className="text-xs font-normal ml-1 text-muted-foreground">dB</span>
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

      {/* Bottom panel: mode tabs + amp slider + waveform */}
      {(phase === "calibrating" || phase === "measuring") && (
        <div
          className="absolute left-0 right-0 hud-panel"
          style={{
            bottom: 0,
            paddingBottom: "max(env(safe-area-inset-bottom), 0.5rem)",
          }}
        >
          {/* Mode tabs */}
          <div className="flex items-stretch border-b border-border/50">
            {MODES.map((m) => {
              const active = m.id === modeId;
              const [r, g, b] = m.systoleRgb;
              return (
                <button
                  key={m.id}
                  onClick={() => setModeId(m.id)}
                  className={`flex-1 py-2 px-2 text-center transition-all relative ${
                    active
                      ? "bg-card/60"
                      : "opacity-55 hover:opacity-90"
                  }`}
                  style={{
                    boxShadow: active
                      ? `inset 0 -2px 0 0 rgba(${r},${g},${b},0.95)`
                      : "none",
                  }}
                >
                  <span
                    className="hud-label block leading-tight"
                    style={{ color: active ? `rgb(${r},${g},${b})` : undefined }}
                  >
                    {m.label}
                  </span>
                </button>
              );
            })}
          </div>

          {/* Mode caption */}
          <p className="hud-label text-center pt-1.5 px-3 opacity-80">
            {mode.caption}
          </p>

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

          <canvas
            ref={waveCanvasRef}
            width={600}
            height={64}
            className="w-full"
            style={{ height: 64, display: "block" }}
          />

          {result && (
            <p className="hud-label text-center pb-1">
              {result.freqHz.toFixed(2)} Hz · {Math.round(result.bpm)} BPM
            </p>
          )}
        </div>
      )}

      {/* Calibration progress */}
      {phase === "calibrating" && (
        <div className="absolute left-0 right-0 bottom-[180px] px-4">
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
