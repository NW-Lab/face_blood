//
//  WaveformView.swift
//  FaceBlood
//
//  Lightweight rPPG waveform plotter.
//

import SwiftUI

struct WaveformView: View {
    let data: [Float]
    let mode: PulseMode
    let active: Bool

    var body: some View {
        Canvas { ctx, size in
            // Grid lines.
            let grid = Path { p in
                for i in 0...4 {
                    let y = CGFloat(i) / 4 * size.height
                    p.move(to: CGPoint(x: 0, y: y))
                    p.addLine(to: CGPoint(x: size.width, y: y))
                }
            }
            ctx.stroke(grid,
                       with: .color(.white.opacity(0.06)),
                       lineWidth: 1)

            guard data.count >= 2 else { return }
            var path = Path()
            let step = size.width / CGFloat(data.count - 1)
            for (i, v) in data.enumerated() {
                let x = CGFloat(i) * step
                let y = size.height / 2 - CGFloat(v) * size.height * 0.42
                if i == 0 { path.move(to: CGPoint(x: x, y: y)) }
                else { path.addLine(to: CGPoint(x: x, y: y)) }
            }

            let leadColor = active ? mode.systoleColor : Color(red: 0.47, green: 0.78, blue: 1)
            let trailColor = active ? mode.diastoleColor.opacity(0) : Color(red: 0.47, green: 0.78, blue: 1).opacity(0)

            ctx.stroke(
                path,
                with: .linearGradient(
                    Gradient(colors: [trailColor, leadColor]),
                    startPoint: CGPoint(x: 0, y: 0),
                    endPoint: CGPoint(x: size.width, y: 0)
                ),
                lineWidth: 1.8
            )
        }
    }
}
