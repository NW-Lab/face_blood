//
//  PulseEngine.swift
//  FaceBlood
//
//  Glues the camera, the rPPG processor, and the amplifier together.
//  Publishes the most recent processed frame (as CGImage) along with
//  vitals so that SwiftUI can render the HUD reactively.
//

import Combine
import CoreImage
import CoreVideo
import SwiftUI

@MainActor
final class PulseEngine: ObservableObject {

    enum Phase: String { case idle, calibrating, measuring, error }

    // MARK: - Published HUD state
    @Published private(set) var phase: Phase = .idle
    @Published private(set) var bpm: Float = 0
    @Published private(set) var snr: Float = 0
    @Published private(set) var confidence: Float = 0
    @Published private(set) var fps: Float = 0
    @Published private(set) var sampleCount: Int = 0
    @Published private(set) var waveform: [Float] = []
    @Published private(set) var processedImage: CGImage?
    @Published private(set) var beatPulse: Bool = false
    @Published private(set) var errorMessage: String = ""

    // MARK: - User-controlled
    @Published var modeID: PulseModeID = .vivid
    @Published var amp: Float = 5.0

    // MARK: - Internals
    private let camera = CameraController()
    private let processor = RppgProcessor(windowSeconds: 12)
    private let amplifier = PulseAmplifier()
    private var lastBeatPhase: Float = 0
    private var smoothPulse: Float = 0
    private var lastFrameAt: TimeInterval = 0
    private var beatResetWork: DispatchWorkItem?
    private let renderQueue = DispatchQueue(label: "faceblood.render",
                                            qos: .userInitiated)

    init() {
        camera.delegate = self
    }

    // MARK: - Lifecycle
    func start() {
        guard phase == .idle || phase == .error else { return }
        CameraController.requestPermission { [weak self] granted in
            guard let self else { return }
            guard granted else {
                self.phase = .error
                self.errorMessage = "カメラへのアクセスが許可されていません。設定アプリから許可してください。"
                return
            }
            do {
                try self.camera.configure()
                self.processor.reset()
                self.smoothPulse = 0
                self.lastBeatPhase = 0
                self.phase = .calibrating
                self.errorMessage = ""
                self.camera.start()
            } catch {
                self.phase = .error
                self.errorMessage = "カメラを起動できませんでした: \(error.localizedDescription)"
            }
        }
    }

    func stop() {
        camera.stop()
        processor.reset()
        phase = .idle
        bpm = 0; snr = 0; confidence = 0; fps = 0; sampleCount = 0
        waveform = []
        processedImage = nil
        smoothPulse = 0
    }

    // MARK: - Frame processing
    nonisolated private func handleFrame(image: CIImage,
                                         sample: RgbSample,
                                         timestamp: TimeInterval) {
        Task { @MainActor [weak self] in
            guard let self else { return }
            self.processor.push(sample)
            self.sampleCount = self.processor.count

            let res = self.processor.analyze()

            // Update vitals.
            var pulseRaw: Float = 0
            if let r = res {
                self.bpm = r.bpm
                self.snr = r.snr
                self.confidence = r.confidence
                self.fps = r.fps
                if let last = r.waveform.last { pulseRaw = last }

                // Down-sample waveform to ~200 points for the chart.
                let stride = max(1, r.waveform.count / 200)
                var pts: [Float] = []
                pts.reserveCapacity(r.waveform.count / stride + 1)
                var i = 0
                while i < r.waveform.count {
                    pts.append(r.waveform[i])
                    i += stride
                }
                self.waveform = pts

                // Beat detection — fire vignette when phase wraps.
                let ph = r.phase
                let prev = self.lastBeatPhase
                let wrapped = ph < prev - 1.0 || (prev > 5 && ph < 1)
                if wrapped, r.confidence > 0.2, !self.beatPulse {
                    self.beatPulse = true
                    let work = DispatchWorkItem { [weak self] in
                        Task { @MainActor in self?.beatPulse = false }
                    }
                    self.beatResetWork = work
                    DispatchQueue.main
                        .asyncAfter(deadline: .now() + 0.32, execute: work)
                }
                self.lastBeatPhase = ph

                if self.phase != .measuring {
                    self.phase = .measuring
                }
            }

            // Smooth the pulse value with an EMA so the colour eases between
            // analyser updates (which only run every few frames worth of data).
            let now = timestamp
            let dt = self.lastFrameAt > 0
                ? Float(now - self.lastFrameAt)
                : Float(1.0 / 30.0)
            self.lastFrameAt = now
            let tau: Float = 0.06
            let alpha = 1 - expf(-dt / tau)
            self.smoothPulse += (pulseRaw - self.smoothPulse) * alpha

            // GPU colour amplification.
            let mode = PulseMode.by(self.modeID)
            let amped = self.amplifier.process(
                input: image,
                mode: mode,
                pulse: self.smoothPulse,
                amp: self.amp,
                confidence: self.confidence
            )
            let context = self.amplifier.context
            self.renderQueue.async { [weak self] in
                guard let self else { return }
                let extent = amped.extent.integral
                guard extent.width > 0, extent.height > 0,
                      let cg = context.createCGImage(amped, from: extent)
                else { return }
                Task { @MainActor in
                    self.processedImage = cg
                }
            }
        }
    }
}

// MARK: - CameraControllerDelegate
extension PulseEngine: CameraControllerDelegate {
    nonisolated func cameraController(_ controller: CameraController,
                                      didCapture image: CIImage,
                                      rgbSample: RgbSample,
                                      timestamp: TimeInterval) {
        handleFrame(image: image, sample: rgbSample, timestamp: timestamp)
    }
}
