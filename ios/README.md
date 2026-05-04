# Face Blood — iOS Native (SwiftUI)

Web版（`/web/client`）と同じアルゴリズムを **Swift / SwiftUI / AVFoundation / Core Image / Accelerate** でネイティブ実装したものです。Web版より高フレームレートで安定動作し、GPU フィルタによる肌色シフトもより滑らかに表現できます。

## ディレクトリ構成

```
ios/FaceBlood/
├── FaceBlood.xcodeproj/        ← Xcode プロジェクト
└── FaceBlood/
    ├── FaceBloodApp.swift      ← @main エントリポイント
    ├── ContentView.swift       ← Bio-Lab Noir HUD（SwiftUI）
    ├── PulseEngine.swift       ← カメラ→rPPG→アンプ→出力 を束ねる ObservableObject
    ├── CameraController.swift  ← AVFoundation キャプチャ + ROI 平均色計算
    ├── RppgProcessor.swift     ← POS 法 + Accelerate vDSP FFT による信号処理
    ├── PulseAmplifier.swift    ← Core Image / Metal の色増幅フィルタ（3モード）
    ├── PulseMode.swift         ← SUBTLE / VIVID / EXTREME 定義
    ├── WaveformView.swift      ← rPPG 波形描画（Canvas）
    ├── Info.plist              ← カメラ使用許可・縦向き固定
    └── Assets.xcassets/
```

## 必要環境

| 項目 | バージョン |
|---|---|
| Xcode | 15.0 以上 |
| iOS デプロイメントターゲット | 16.0 以上 |
| 対応デバイス | iPhone（フロントカメラ必須） |
| Swift | 5.9 以上 |
| 外部依存 | **なし**（標準フレームワークのみ） |

## セットアップ

```bash
# リポジトリを取得
git clone https://github.com/NW-Lab/face_blood.git
cd face_blood/ios/FaceBlood

# Xcode で開く
open FaceBlood.xcodeproj
```

1. Xcode で `FaceBlood.xcodeproj` を開く
2. ターゲット `FaceBlood` を選択 → **Signing & Capabilities** タブで自分の **Team** を選択
3. 必要に応じて Bundle ID を `im.nwlab.faceblood` から自分のものに変更
4. iPhone を USB 接続し、デバイスを選択
5. **⌘R** で実行

> 初回起動時、iPhone 側で「設定 > 一般 > VPN とデバイス管理」から開発者プロファイルを信頼する必要があります。

## アーキテクチャ

```
┌──────────────────────┐    BGRA frame + RGB mean
│  CameraController    │─────────────────────────────────┐
│  (AVFoundation)      │                                 │
└──────────────────────┘                                 ▼
                                            ┌────────────────────────┐
                                            │     PulseEngine        │
                                            │  @MainActor            │
                                            │                        │
                                            │  • RppgProcessor.push  │
                                            │  • RppgProcessor.analyze
                                            │     ↓ BPM, SNR, phase  │
                                            │  • EMA smoothing       │
                                            │  • PulseAmplifier      │
                                            │     ↓ CIImage (GPU)    │
                                            │  • CIContext renders   │
                                            │     to CGImage         │
                                            └────────────┬───────────┘
                                                         │ @Published
                                                         ▼
                                            ┌────────────────────────┐
                                            │     ContentView        │
                                            │   SwiftUI HUD          │
                                            │  - BPM / SNR / Conf    │
                                            │  - Mode tabs           │
                                            │  - AMP slider          │
                                            │  - Waveform Canvas     │
                                            │  - Vignette pulse      │
                                            └────────────────────────┘
```

### rPPG 信号処理 (`RppgProcessor.swift`)

Web 版と同一のパイプライン。

1. **リサンプリング**: 不均一な時刻の `RgbSample` を等間隔（≈ 30 Hz）に線形補間
2. **正規化**: 各チャンネルを平均で割り `r/μr - 1` の形に
3. **POS 投影**: `X = G - B`、`Y = G + B - 2R`、`S = X + (σX/σY)·Y`
4. **デトレンド**: 0.7 秒の移動平均を減算
5. **Hann 窓 + FFT**: `vDSP_fft_zip` で高速フーリエ変換
6. **ピーク検出**: 0.7–3.5 Hz バンド内の最大ビン → 放物線補間で sub-bin 精度
7. **逆 FFT**: ピーク周辺を強調した信号を IFFT で時間領域に戻す → 波形と瞬時位相

### 色増幅 (`PulseAmplifier.swift`)

`CIColorKernel` (CIKL) で書いた GPU シェーダで肌色判定 + 色シフトを行います。Web 版の Canvas 2D ピクセルループ相当を Metal 上で実行するため、フル解像度フレームでもフレームレートを落としません。

| モード | 効果 | 実装 |
|---|---|---|
| **SUBTLE** | ROI 内のみ柔らかく増幅 | `CIRadialGradient` + screen blend |
| **VIVID** | 顔と画面全体が赤⇄青にダイナミックに染まる | 大きな放射グラデ + `CISoftLightBlendMode` ウォッシュ |
| **EXTREME** | 顔全体がドカンと真っ赤⇄真っ青に脈動 | **CIColorKernel で per-pixel 肌色シフト** + 彩度ブースト + `CIAdditionCompositing` + screen グロー |

CIColorKernel のソースは `PulseAmplifier.extremeKernelSource` に直接記述されており、肌色判定ロジックは Web 版（`R > G > B*0.85` + 明度範囲）と同じです。

### カメラキャプチャ (`CameraController.swift`)

- フロントカメラ（`builtInWideAngleCamera` / `.front`）を `AVCaptureSession` で起動
- プリセット: `.vga640x480`、フレームレートを **30 fps に固定**（rPPG の安定性のため）
- 出力: `kCVPixelFormatType_32BGRA`
- ROI: 中央の正方形（短辺の 55%）
- ピクセル平均: `CVPixelBufferLockBaseAddress` で生バイトに直接アクセスし、4 ピクセルおきにサブサンプリング（1 フレームあたり ≈ 5 000 サンプル）

## 操作

1. アプリ起動 → 「**計測開始**」をタップ → カメラ許可
2. 顔を画面中央の点線枠に合わせて **5〜10 秒静止**
3. BPM / SNR / 信頼度が表示され、選択モードに応じて顔が脈動
4. 画面下部の **SUBTLE / VIVID / EXTREME** タブでモード切替
5. **AMP** スライダ（1.0 〜 10.0）で増幅倍率を調整
6. 「**STOP**」で計測停止

## Web 版との対応関係

| Web 版 (`/web/client`) | iOS 版 (`/ios`) |
|---|---|
| `web/client/src/lib/rppg.ts` | `RppgProcessor.swift` |
| `web/client/src/components/PulseVisualizer.tsx`（カメラ部） | `CameraController.swift` |
| `web/client/src/components/PulseVisualizer.tsx`（描画部） | `PulseAmplifier.swift` + `ContentView.swift` |
| `useState<ModeID>` | `@Published var modeID` |
| Canvas 2D `getImageData` ループ | `CIColorKernel` GPU シェーダ |
| `requestAnimationFrame` | `AVCaptureVideoDataOutputSampleBufferDelegate` |

## 制限事項と今後の改善

- **顔検出は未実装**（中央 ROI 方式）。`Vision` フレームワークの `VNDetectFaceLandmarksRequest` を使えば顔の自動追跡が可能
- 肌色判定はヒューリスティック。`Vision` の `VNGeneratePersonSegmentationRequest` を組み合わせると背景の誤検出を排除できる
- **ProMotion (120 Hz)** を活かすには `preferredFramesPerSecond` をデバイス能力に合わせて引き上げる
- 計測結果の **HealthKit 書き込み** や **CSV エクスポート** は未対応

## 研究文献

ルートの [`/README.md`](../README.md) を参照してください。EVM（MIT CSAIL）と rPPG の主要文献（POS 法、CHROM 法、ICA 法）を網羅しています。

## ライセンス

MIT License（ルートの `LICENSE` を参照）
