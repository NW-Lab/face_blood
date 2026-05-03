//
//  CameraController.swift
//  FaceBlood
//
//  Captures BGRA frames from the front camera, computes the average
//  R/G/B inside the central ROI, and forwards both the frame (as CIImage)
//  and the colour sample to a delegate. Designed to be driven by the
//  PulseEngine class.
//

import AVFoundation
import CoreImage
import CoreVideo
import UIKit

protocol CameraControllerDelegate: AnyObject {
    /// Called on a background queue for every captured frame.
    func cameraController(_ controller: CameraController,
                          didCapture image: CIImage,
                          rgbSample: RgbSample,
                          timestamp: TimeInterval)
}

final class CameraController: NSObject {
    weak var delegate: CameraControllerDelegate?

    /// Fraction of the shorter image side used as the square ROI (matches Web).
    var roiFraction: CGFloat = 0.55

    private let session = AVCaptureSession()
    private let videoOutput = AVCaptureVideoDataOutput()
    private let sessionQueue = DispatchQueue(label: "faceblood.camera.session")
    private let sampleQueue  = DispatchQueue(label: "faceblood.camera.sample")
    private(set) var isRunning = false

    // MARK: - Permission
    static func requestPermission(_ done: @escaping (Bool) -> Void) {
        switch AVCaptureDevice.authorizationStatus(for: .video) {
        case .authorized:
            done(true)
        case .notDetermined:
            AVCaptureDevice.requestAccess(for: .video) { ok in
                DispatchQueue.main.async { done(ok) }
            }
        default:
            done(false)
        }
    }

    // MARK: - Lifecycle
    func configure() throws {
        sessionQueue.sync {
            session.beginConfiguration()
            session.sessionPreset = .vga640x480

            // Front camera input.
            if let device = AVCaptureDevice.default(.builtInWideAngleCamera,
                                                    for: .video,
                                                    position: .front),
               let input = try? AVCaptureDeviceInput(device: device),
               session.canAddInput(input) {
                session.addInput(input)
                // Lock frame rate to ~30 fps for stable rPPG sampling.
                if let range = device.activeFormat.videoSupportedFrameRateRanges.first {
                    let target = CMTimeMake(value: 1, timescale: 30)
                    if CMTimeCompare(target, range.minFrameDuration) >= 0 &&
                       CMTimeCompare(target, range.maxFrameDuration) <= 0 {
                        do {
                            try device.lockForConfiguration()
                            device.activeVideoMinFrameDuration = target
                            device.activeVideoMaxFrameDuration = target
                            device.unlockForConfiguration()
                        } catch { /* ignore — best effort */ }
                    }
                }
            }

            // Video output (BGRA so we can read pixels easily).
            videoOutput.videoSettings = [
                kCVPixelBufferPixelFormatTypeKey as String:
                    kCVPixelFormatType_32BGRA
            ]
            videoOutput.alwaysDiscardsLateVideoFrames = true
            videoOutput.setSampleBufferDelegate(self, queue: sampleQueue)
            if session.canAddOutput(videoOutput) {
                session.addOutput(videoOutput)
            }
            if let conn = videoOutput.connection(with: .video) {
                if conn.isVideoOrientationSupported {
                    conn.videoOrientation = .portrait
                }
                if conn.isVideoMirroringSupported {
                    conn.isVideoMirrored = true   // selfie mirror
                }
            }
            session.commitConfiguration()
        }
    }

    func start() {
        sessionQueue.async { [weak self] in
            guard let self, !self.session.isRunning else { return }
            self.session.startRunning()
            self.isRunning = self.session.isRunning
        }
    }

    func stop() {
        sessionQueue.async { [weak self] in
            guard let self, self.session.isRunning else { return }
            self.session.stopRunning()
            self.isRunning = false
        }
    }
}

// MARK: - AVCaptureVideoDataOutputSampleBufferDelegate
extension CameraController: AVCaptureVideoDataOutputSampleBufferDelegate {
    func captureOutput(_ output: AVCaptureOutput,
                       didOutput sampleBuffer: CMSampleBuffer,
                       from connection: AVCaptureConnection) {
        guard let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
        let timestamp = CMTimeGetSeconds(CMSampleBufferGetPresentationTimeStamp(sampleBuffer))
        let ci = CIImage(cvPixelBuffer: pixelBuffer)

        // Compute mean RGB over the central square ROI.
        guard let mean = roiMeanRGB(pixelBuffer: pixelBuffer) else { return }
        let sample = RgbSample(t: timestamp, r: mean.r, g: mean.g, b: mean.b)
        delegate?.cameraController(self,
                                   didCapture: ci,
                                   rgbSample: sample,
                                   timestamp: timestamp)
    }

    /// Average BGRA pixel values across the central square ROI.
    /// Sub-samples every Nth pixel for speed (still > 5 000 samples per frame).
    private func roiMeanRGB(pixelBuffer: CVPixelBuffer) -> (r: Float, g: Float, b: Float)? {
        let width = CVPixelBufferGetWidth(pixelBuffer)
        let height = CVPixelBufferGetHeight(pixelBuffer)
        let shorter = min(width, height)
        let roi = Int(CGFloat(shorter) * roiFraction)
        let rx = (width  - roi) / 2
        let ry = (height - roi) / 2

        CVPixelBufferLockBaseAddress(pixelBuffer, .readOnly)
        defer { CVPixelBufferUnlockBaseAddress(pixelBuffer, .readOnly) }
        guard let base = CVPixelBufferGetBaseAddress(pixelBuffer) else { return nil }
        let bpr = CVPixelBufferGetBytesPerRow(pixelBuffer)

        var sumR: UInt64 = 0
        var sumG: UInt64 = 0
        var sumB: UInt64 = 0
        var count: UInt64 = 0
        let stride = 4   // sample every 4th pixel in both dimensions

        for y in Swift.stride(from: ry, to: ry + roi, by: stride) {
            let row = base.advanced(by: y * bpr).assumingMemoryBound(to: UInt8.self)
            var x = rx
            let xEnd = rx + roi
            while x < xEnd {
                let idx = x * 4   // BGRA
                sumB += UInt64(row[idx])
                sumG += UInt64(row[idx + 1])
                sumR += UInt64(row[idx + 2])
                count += 1
                x += stride
            }
        }
        guard count > 0 else { return nil }
        return (
            r: Float(sumR) / Float(count),
            g: Float(sumG) / Float(count),
            b: Float(sumB) / Float(count)
        )
    }
}
