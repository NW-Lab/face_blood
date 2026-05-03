//
//  ContentView.swift
//  FaceBlood
//
//  Bio-Lab Noir HUD: full-screen camera with floating panels.
//

import SwiftUI

struct ContentView: View {
    @StateObject private var engine = PulseEngine()

    var body: some View {
        GeometryReader { geo in
            ZStack {
                Color.black.ignoresSafeArea()

                // Camera frame.
                if let cg = engine.processedImage {
                    Image(decorative: cg, scale: 1.0, orientation: .up)
                        .resizable()
                        .scaledToFill()
                        .frame(width: geo.size.width, height: geo.size.height)
                        .clipped()
                        .ignoresSafeArea()
                        .overlay(scanlines.allowsHitTesting(false))
                }

                // ROI guide.
                if engine.phase == .calibrating || engine.phase == .measuring {
                    roiGuide(in: geo.size)
                }

                // Vignette pulse.
                if engine.beatPulse {
                    RadialGradient(
                        colors: [.clear, Color(red: 1.0, green: 0.12, blue: 0.24).opacity(0.32)],
                        center: .center,
                        startRadius: min(geo.size.width, geo.size.height) * 0.25,
                        endRadius: max(geo.size.width, geo.size.height) * 0.7
                    )
                    .ignoresSafeArea()
                    .allowsHitTesting(false)
                    .transition(.opacity)
                }

                // Idle / error overlay.
                if engine.phase == .idle || engine.phase == .error {
                    idleOverlay
                }

                // HUD panels.
                if engine.phase != .idle && engine.phase != .error {
                    VStack(spacing: 0) {
                        topBar
                        HStack(alignment: .top) {
                            bpmPanel
                            Spacer()
                            vitalsPanel
                        }
                        .padding(.horizontal, 12)
                        .padding(.top, 8)

                        Spacer()

                        bottomPanel
                    }
                }
            }
            .preferredColorScheme(.dark)
            .animation(.easeInOut(duration: 0.2), value: engine.beatPulse)
        }
    }

    // MARK: - Subviews
    private var idleOverlay: some View {
        ZStack {
            Color.black.opacity(0.92).ignoresSafeArea()
            VStack(spacing: 18) {
                Text("FACE BLOOD")
                    .font(.system(size: 11, weight: .bold, design: .monospaced))
                    .tracking(4)
                    .foregroundStyle(Color(red: 0.24, green: 0.98, blue: 1.0))
                Text("rPPG Pulse Visualizer")
                    .font(.system(size: 28, weight: .bold, design: .monospaced))
                    .foregroundStyle(Color(red: 1.0, green: 0.18, blue: 0.30))
                    .shadow(color: Color(red: 1, green: 0.2, blue: 0.3).opacity(0.6),
                            radius: 12, x: 0, y: 0)
                Text("顔をカメラに向けて枠内に収めてください。\n脈拍に合わせて顔が赤と青にダイナミックに脈動します。")
                    .multilineTextAlignment(.center)
                    .font(.system(size: 13))
                    .foregroundStyle(Color.white.opacity(0.7))
                    .padding(.horizontal, 28)
                if engine.phase == .error {
                    Text(engine.errorMessage)
                        .font(.system(size: 12))
                        .foregroundStyle(.red)
                        .multilineTextAlignment(.center)
                        .padding(.horizontal, 28)
                }
                Button(action: { engine.start() }) {
                    Text("計測開始")
                        .font(.system(size: 13, weight: .heavy, design: .monospaced))
                        .tracking(3)
                        .foregroundStyle(.white)
                        .padding(.horizontal, 32)
                        .padding(.vertical, 14)
                        .background(
                            Color(red: 1.0, green: 0.2, blue: 0.32)
                                .shadow(color: Color(red: 1, green: 0.2, blue: 0.32)
                                            .opacity(0.6),
                                        radius: 16, x: 0, y: 0)
                        )
                        .clipShape(RoundedRectangle(cornerRadius: 6))
                }
                .padding(.top, 6)
            }
        }
    }

    private var topBar: some View {
        HStack {
            Text(engine.phase == .calibrating ? "CALIBRATING" : "MEASURING")
                .hudLabel(color: Color(red: 0.24, green: 0.98, blue: 1.0))
            Spacer()
            Text(engine.phase == .calibrating
                 ? "\(min(100, Int(Float(engine.sampleCount) / 64 * 100)))%"
                 : "FPS \(Int(engine.fps))")
                .hudLabel()
            Spacer()
            Button("STOP") { engine.stop() }
                .hudLabel(color: .red)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 8)
        .background(.black.opacity(0.55))
        .background(.ultraThinMaterial.opacity(0.6))
    }

    private var bpmPanel: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("HEART RATE").hudLabel()
            Text(engine.confidence > 0.3 && engine.bpm > 0
                 ? "\(Int(engine.bpm))" : "--")
                .font(.system(size: 56, weight: .heavy, design: .monospaced))
                .foregroundStyle(engine.confidence > 0.3
                                 ? Color(red: 1.0, green: 0.18, blue: 0.30)
                                 : .white.opacity(0.5))
                .shadow(color: Color(red: 1, green: 0.2, blue: 0.3).opacity(0.5),
                        radius: 8)
                .lineLimit(1)
            Text("BPM").hudLabel()
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 12)
        .background(.black.opacity(0.55))
        .clipShape(RoundedRectangle(cornerRadius: 10))
    }

    private var vitalsPanel: some View {
        VStack(alignment: .leading, spacing: 8) {
            VStack(alignment: .leading, spacing: 2) {
                Text("SNR").hudLabel()
                HStack(alignment: .firstTextBaseline, spacing: 2) {
                    Text(engine.snr > 0 ? String(format: "%.1f", engine.snr) : "--")
                        .font(.system(size: 18, weight: .heavy, design: .monospaced))
                        .foregroundStyle(Color(red: 0.24, green: 0.98, blue: 1.0))
                    Text("dB").font(.system(size: 9)).foregroundStyle(.white.opacity(0.5))
                }
            }
            VStack(alignment: .leading, spacing: 2) {
                Text("CONF").hudLabel()
                HStack(alignment: .firstTextBaseline, spacing: 2) {
                    Text("\(Int(engine.confidence * 100))")
                        .font(.system(size: 18, weight: .heavy, design: .monospaced))
                        .foregroundStyle(confColor(engine.confidence))
                    Text("%").font(.system(size: 9)).foregroundStyle(.white.opacity(0.5))
                }
            }
            VStack(alignment: .leading, spacing: 2) {
                Text("SAMPLES").hudLabel()
                Text("\(engine.sampleCount)")
                    .font(.system(size: 13, design: .monospaced))
                    .foregroundStyle(.white.opacity(0.6))
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 12)
        .background(.black.opacity(0.55))
        .clipShape(RoundedRectangle(cornerRadius: 10))
    }

    private var bottomPanel: some View {
        VStack(spacing: 0) {
            // Mode tabs.
            HStack(spacing: 0) {
                ForEach(PulseMode.all) { mode in
                    Button(action: { engine.modeID = mode.id }) {
                        Text(mode.label)
                            .font(.system(size: 11, weight: .bold, design: .monospaced))
                            .tracking(2)
                            .foregroundStyle(engine.modeID == mode.id
                                             ? mode.systoleColor : .white.opacity(0.55))
                            .frame(maxWidth: .infinity)
                            .padding(.vertical, 10)
                            .background(
                                engine.modeID == mode.id
                                    ? Color.white.opacity(0.06)
                                    : Color.clear
                            )
                            .overlay(
                                Rectangle()
                                    .fill(mode.systoleColor)
                                    .frame(height: engine.modeID == mode.id ? 2 : 0),
                                alignment: .bottom
                            )
                    }
                    .buttonStyle(.plain)
                }
            }
            .background(Color.white.opacity(0.04))

            Text(PulseMode.by(engine.modeID).caption)
                .font(.system(size: 10, weight: .medium, design: .monospaced))
                .foregroundStyle(.white.opacity(0.7))
                .multilineTextAlignment(.center)
                .padding(.top, 6)
                .padding(.horizontal, 12)

            // AMP slider.
            HStack(spacing: 10) {
                Text("AMP").hudLabel()
                Slider(value: $engine.amp, in: 1...10, step: 0.5)
                    .tint(Color(red: 1.0, green: 0.2, blue: 0.32))
                Text(String(format: "%.1f", engine.amp))
                    .font(.system(size: 11, design: .monospaced))
                    .foregroundStyle(Color(red: 0.24, green: 0.98, blue: 1.0))
                    .frame(width: 28, alignment: .trailing)
            }
            .padding(.horizontal, 14)
            .padding(.top, 6)

            WaveformView(data: engine.waveform,
                         mode: PulseMode.by(engine.modeID),
                         active: engine.confidence > 0.2)
                .frame(height: 64)
                .padding(.top, 4)

            if engine.bpm > 0 {
                Text(String(format: "%.2f Hz · %d BPM",
                            engine.bpm / 60, Int(engine.bpm)))
                    .font(.system(size: 10, design: .monospaced))
                    .foregroundStyle(.white.opacity(0.5))
                    .padding(.bottom, 4)
            }

            if engine.phase == .calibrating {
                VStack(spacing: 4) {
                    GeometryReader { g in
                        ZStack(alignment: .leading) {
                            Rectangle().fill(Color.white.opacity(0.1))
                            Rectangle()
                                .fill(Color(red: 0.24, green: 0.98, blue: 1.0))
                                .frame(width: g.size.width
                                       * CGFloat(min(1, Float(engine.sampleCount) / 64)))
                        }
                    }
                    .frame(height: 2)
                    Text("SIGNAL ACQUISITION \(min(100, Int(Float(engine.sampleCount) / 64 * 100)))%")
                        .hudLabel(color: Color(red: 0.24, green: 0.98, blue: 1.0))
                }
                .padding(.horizontal, 14)
                .padding(.vertical, 4)
            }
        }
        .background(.black.opacity(0.6))
        .background(.ultraThinMaterial.opacity(0.6))
    }

    private func roiGuide(in size: CGSize) -> some View {
        let shorter = min(size.width, size.height)
        let roi = shorter * 0.55
        let m = PulseMode.by(engine.modeID)
        let active = engine.confidence > 0.1
        let borderColor = active ? m.systoleColor : Color(red: 0.24, green: 0.98, blue: 1.0)
        return RoundedRectangle(cornerRadius: 4)
            .strokeBorder(borderColor.opacity(active ? 0.7 : 0.4),
                          style: StrokeStyle(lineWidth: 2, dash: [8, 6]))
            .frame(width: roi, height: roi)
            .position(x: size.width / 2, y: size.height / 2)
            .allowsHitTesting(false)
    }

    private var scanlines: some View {
        Canvas { ctx, size in
            for y in stride(from: CGFloat(0), to: size.height, by: 3) {
                ctx.fill(Path(CGRect(x: 0, y: y, width: size.width, height: 1)),
                         with: .color(.white.opacity(0.03)))
            }
        }
        .blendMode(.overlay)
    }

    private func confColor(_ c: Float) -> Color {
        if c > 0.6 { return Color(red: 0.31, green: 0.94, blue: 0.55) }
        if c > 0.3 { return Color(red: 1.0, green: 0.78, blue: 0.27) }
        return Color(red: 1.0, green: 0.40, blue: 0.40)
    }
}

// MARK: - Style helpers
private struct HudLabelModifier: ViewModifier {
    let color: Color
    func body(content: Content) -> some View {
        content
            .font(.system(size: 9, weight: .bold, design: .monospaced))
            .tracking(2)
            .foregroundStyle(color)
    }
}
extension View {
    func hudLabel(color: Color = Color.white.opacity(0.55)) -> some View {
        modifier(HudLabelModifier(color: color))
    }
}

#Preview {
    ContentView()
}
