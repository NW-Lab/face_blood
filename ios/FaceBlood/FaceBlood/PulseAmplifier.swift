//
//  PulseAmplifier.swift
//  FaceBlood
//
//  GPU-accelerated colour amplification for the three pulse modes.
//
//  - SUBTLE  : soft radial tint inside the ROI (CIRadialGradient + screen blend)
//  - VIVID   : larger radial tint + soft-light wash over the whole frame
//  - EXTREME : per-pixel skin shift via a CIColorKernel — pixels detected as
//              skin are mixed dramatically toward the target colour and have
//              their saturation boosted, then composited with "lighter" blend.
//
//  The kernel implements the same heuristic as the Web build:
//    isSkin = brightness in [55, 245] and R > G > B*0.85 and R > B and R > G*1.04
//

import CoreImage
import CoreImage.CIFilterBuiltins
import UIKit
import simd

final class PulseAmplifier {

    // Reused across calls.
    private let ciContext: CIContext
    private let extremeKernel: CIColorKernel?

    init() {
        // Prefer Metal, fall back to whatever is available.
        if let device = MTLCreateSystemDefaultDevice() {
            self.ciContext = CIContext(mtlDevice: device,
                                       options: [.cacheIntermediates: false])
        } else {
            self.ciContext = CIContext(options: [.cacheIntermediates: false])
        }
        self.extremeKernel = PulseAmplifier.compileExtremeKernel()
    }

    var context: CIContext { ciContext }

    // MARK: - Public entry point
    /// - Parameters:
    ///   - input: BGRA CIImage straight from the camera.
    ///   - mode:  Selected visualization mode.
    ///   - pulse: rPPG instantaneous waveform value (-1..1 or so).
    ///   - amp:   User AMP slider value (1..10).
    ///   - confidence: 0..1, used to fade the effect on/off smoothly.
    /// - Returns: A new CIImage with the colour amplification applied.
    func process(input: CIImage,
                 mode: PulseMode,
                 pulse: Float,
                 amp: Float,
                 confidence: Float) -> CIImage {

        // Effective intensity, mirroring the JS computation.
        let intensity = max(-1.5, min(1.5, pulse * (amp / 3) * mode.ampScale))
        let sign: Float = intensity >= 0 ? 1 : -1
        let absI = min(Float(1.2), abs(intensity))
        let target = sign > 0 ? mode.systoleRGB : mode.diastoleRGB

        let effectAlpha = max(0, min(1, (confidence - 0.1) / 0.4))
        if effectAlpha <= 0.001 {
            return input
        }

        let extent = input.extent
        let centre = CGPoint(x: extent.midX, y: extent.midY)
        let shorter = min(extent.width, extent.height)
        let roi = shorter * 0.55

        switch mode.id {
        case .subtle:
            return subtleOverlay(on: input,
                                 centre: centre,
                                 roi: roi,
                                 colour: target,
                                 absI: absI * effectAlpha)
        case .vivid:
            return vividOverlay(on: input,
                                centre: centre,
                                roi: roi,
                                colour: target,
                                absI: absI * effectAlpha)
        case .extreme:
            return extremeOverlay(on: input,
                                  colour: target,
                                  absI: absI * effectAlpha)
        }
    }

    // MARK: - SUBTLE
    private func subtleOverlay(on input: CIImage,
                               centre: CGPoint,
                               roi: CGFloat,
                               colour: SIMD3<Float>,
                               absI: Float) -> CIImage {
        let alpha = CGFloat(min(0.7, absI * 0.5))
        guard let radial = CIFilter(name: "CIRadialGradient",
                                    parameters: [
                                        "inputCenter": CIVector(cgPoint: centre),
                                        "inputRadius0": 0,
                                        "inputRadius1": roi * 0.6,
                                        "inputColor0": CIColor(
                                            red: CGFloat(colour.x),
                                            green: CGFloat(colour.y),
                                            blue: CGFloat(colour.z),
                                            alpha: alpha),
                                        "inputColor1": CIColor(
                                            red: CGFloat(colour.x),
                                            green: CGFloat(colour.y),
                                            blue: CGFloat(colour.z),
                                            alpha: 0),
                                    ])?.outputImage?.cropped(to: input.extent)
        else { return input }
        return screenBlend(top: radial, base: input)
    }

    // MARK: - VIVID
    private func vividOverlay(on input: CIImage,
                              centre: CGPoint,
                              roi: CGFloat,
                              colour: SIMD3<Float>,
                              absI: Float) -> CIImage {
        let a1 = CGFloat(min(0.95, absI * 0.85))
        guard let radial = CIFilter(name: "CIRadialGradient",
                                    parameters: [
                                        "inputCenter": CIVector(cgPoint: centre),
                                        "inputRadius0": roi * 0.1,
                                        "inputRadius1": roi * 0.95,
                                        "inputColor0": CIColor(
                                            red: CGFloat(colour.x),
                                            green: CGFloat(colour.y),
                                            blue: CGFloat(colour.z),
                                            alpha: a1),
                                        "inputColor1": CIColor(
                                            red: CGFloat(colour.x),
                                            green: CGFloat(colour.y),
                                            blue: CGFloat(colour.z),
                                            alpha: 0),
                                    ])?.outputImage?.cropped(to: input.extent)
        else { return input }
        let withRadial = screenBlend(top: radial, base: input)

        // Soft-light wash over the whole frame.
        let washAlpha = CGFloat(min(0.35, absI * 0.35))
        let wash = CIImage(color: CIColor(
            red: CGFloat(colour.x),
            green: CGFloat(colour.y),
            blue: CGFloat(colour.z),
            alpha: washAlpha
        )).cropped(to: input.extent)
        return softLightBlend(top: wash, base: withRadial)
    }

    // MARK: - EXTREME
    private func extremeOverlay(on input: CIImage,
                                colour: SIMD3<Float>,
                                absI: Float) -> CIImage {
        guard let kernel = extremeKernel else {
            return input
        }
        let mixBase = min(0.95, 0.55 + absI * 0.6)
        let satBoost = 0.4 * absI
        let glowAlpha = CGFloat(min(0.45, absI * 0.30))

        let args: [Any] = [
            input,
            CIVector(x: CGFloat(colour.x), y: CGFloat(colour.y), z: CGFloat(colour.z)),
            CGFloat(mixBase),
            CGFloat(satBoost),
        ]
        guard let recoloured = kernel.apply(extent: input.extent,
                                            arguments: args) else {
            return input
        }
        // Composite with "lighter" blend (additive clipped).
        let withSkin = additiveBlend(top: recoloured, base: input)

        // Final full-screen colour glow.
        let glow = CIImage(color: CIColor(
            red: CGFloat(colour.x),
            green: CGFloat(colour.y),
            blue: CGFloat(colour.z),
            alpha: glowAlpha
        )).cropped(to: input.extent)
        return screenBlend(top: glow, base: withSkin)
    }

    // MARK: - Blend helpers
    private func screenBlend(top: CIImage, base: CIImage) -> CIImage {
        let f = CIFilter.screenBlendMode()
        f.inputImage = top
        f.backgroundImage = base
        return f.outputImage ?? base
    }
    private func softLightBlend(top: CIImage, base: CIImage) -> CIImage {
        let f = CIFilter.softLightBlendMode()
        f.inputImage = top
        f.backgroundImage = base
        return f.outputImage ?? base
    }
    private func additiveBlend(top: CIImage, base: CIImage) -> CIImage {
        let f = CIFilter.additionCompositing()
        f.inputImage = top
        f.backgroundImage = base
        return f.outputImage ?? base
    }

    // MARK: - Custom kernel
    /// CIKernel source — same skin-shift algorithm as the JS EXTREME mode but
    /// running on the GPU per fragment.
    private static let extremeKernelSource = """
    kernel vec4 skinShift(__sample s, vec3 target, float mixBase, float satBoost) {
        float r = s.r;
        float g = s.g;
        float b = s.b;
        float bright = (r + g + b) / 3.0;
        // Same heuristic as the JS build, normalized to 0..1.
        float minB = 55.0 / 255.0;
        float maxB = 245.0 / 255.0;
        float skin = step(minB, bright)
                   * step(bright, maxB)
                   * step(g, r)
                   * step(b * 0.85, g)
                   * step(b, r)
                   * step(g * 1.04, r * 1.0);
        if (skin < 0.5) {
            return s;
        }
        // Weight: brighter skin gets stronger shift.
        float weight = clamp((bright - 50.0/255.0) / (180.0/255.0), 0.0, 1.0);
        float k = mixBase * weight;
        vec3 mixed;
        mixed.r = min(1.0, r * (1.0 - k) + target.r * k);
        mixed.g = min(1.0, g * (1.0 - k) + target.g * k);
        mixed.b = min(1.0, b * (1.0 - k) + target.b * k);
        // Saturation boost.
        float avg = (mixed.r + mixed.g + mixed.b) / 3.0;
        mixed.r = min(1.0, mixed.r + (mixed.r - avg) * satBoost);
        mixed.g = min(1.0, mixed.g + (mixed.g - avg) * satBoost);
        mixed.b = min(1.0, mixed.b + (mixed.b - avg) * satBoost);
        return vec4(mixed, s.a);
    }
    """

    private static func compileExtremeKernel() -> CIColorKernel? {
        return CIColorKernel(source: extremeKernelSource)
    }
}
