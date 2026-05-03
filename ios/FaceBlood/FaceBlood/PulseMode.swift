//
//  PulseMode.swift
//  FaceBlood
//
//  Three visualization modes that mirror the Web build.
//

import SwiftUI

enum PulseModeID: String, CaseIterable, Identifiable {
    case subtle, vivid, extreme
    var id: String { rawValue }
}

struct PulseMode: Identifiable {
    let id: PulseModeID
    let label: String
    let caption: String
    /// Multiplier applied on top of the user-controlled AMP slider.
    let ampScale: Float
    /// Colour pushed into skin pixels during systole (positive pulse value).
    let systoleRGB: SIMD3<Float>
    /// Colour pushed into skin pixels during diastole (negative pulse value).
    let diastoleRGB: SIMD3<Float>

    static let all: [PulseMode] = [
        .init(
            id: .subtle,
            label: "SUBTLE",
            caption: "控えめモード — 顔ROI内のみ柔らかく増幅",
            ampScale: 1.0,
            systoleRGB:  SIMD3<Float>(255, 60, 90)  / 255,
            diastoleRGB: SIMD3<Float>(80, 200, 255) / 255
        ),
        .init(
            id: .vivid,
            label: "VIVID",
            caption: "派手モード — 顔と画面全体が赤⇄青にダイナミックに染まる",
            ampScale: 2.5,
            systoleRGB:  SIMD3<Float>(255, 30, 60)  / 255,
            diastoleRGB: SIMD3<Float>(40, 120, 255) / 255
        ),
        .init(
            id: .extreme,
            label: "EXTREME",
            caption: "超派手モード — 顔全体がドカンと真っ赤⇄真っ青に脈動",
            ampScale: 5.0,
            systoleRGB:  SIMD3<Float>(255, 0, 30)   / 255,
            diastoleRGB: SIMD3<Float>(0, 80, 255)   / 255
        ),
    ]

    static func by(_ id: PulseModeID) -> PulseMode {
        all.first { $0.id == id } ?? all[1]
    }

    var systoleColor: Color {
        Color(.sRGB,
              red: Double(systoleRGB.x),
              green: Double(systoleRGB.y),
              blue: Double(systoleRGB.z))
    }
    var diastoleColor: Color {
        Color(.sRGB,
              red: Double(diastoleRGB.x),
              green: Double(diastoleRGB.y),
              blue: Double(diastoleRGB.z))
    }
}
