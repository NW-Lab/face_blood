# FaceBlood — Android

rPPG (remote photoplethysmography) を使ってフロントカメラから心拍数をリアルタイム推定し、
脈拍に合わせて顔の色をダイナミックに変化させる Android アプリです。

Web / iOS / Windows 版と同じアルゴリズムを Kotlin で実装しています。

## 動作環境

| 項目 | 要件 |
|------|------|
| Android | 8.0 (API 26) 以上 |
| フロントカメラ | 必須 |
| 言語 | Kotlin |
| UI | Jetpack Compose |
| ビルドツール | Gradle 8.7 + AGP 8.5 |

## ビルド方法

### Android Studio で開く

1. Android Studio (Hedgehog 以降推奨) を起動します。
2. **File → Open** で `android/` フォルダを選択します。
3. Gradle Sync が完了するのを待ちます。
4. 実機またはエミュレータを接続して **Run** します。

### コマンドラインでビルド

```bash
cd android
./gradlew assembleDebug
```

APK は `app/build/outputs/apk/debug/app-debug.apk` に生成されます。

## アーキテクチャ

```
MainActivity
  └── PulseViewModel (AndroidViewModel)
        ├── CameraX (ImageAnalysis) ─→ ROI 平均 RGB サンプリング
        ├── RppgProcessor           ─→ POS + FFT による BPM 推定
        └── PulseAmplifier          ─→ Canvas ベースの色増幅
```

### ファイル構成

| ファイル | 役割 |
|----------|------|
| `model/RgbSample.kt` | タイムスタンプ付き RGB サンプル |
| `model/RppgResult.kt` | 解析結果 (BPM, SNR, 信頼度, 波形, 位相) |
| `model/PulseMode.kt` | SUBTLE / VIVID / EXTREME の定義 |
| `service/RppgProcessor.kt` | rPPG 信号処理 (POS + Hann + FFT) |
| `service/PulseAmplifier.kt` | Canvas による色増幅エフェクト |
| `service/PulseViewModel.kt` | CameraX 統合・状態管理 |
| `ui/MainScreen.kt` | Jetpack Compose HUD |
| `ui/components/WaveformView.kt` | 波形表示 Canvas |
| `ui/theme/Theme.kt` | Material 3 ダークテーマ |
| `MainActivity.kt` | エントリポイント・パーミッション要求 |

## rPPG パイプライン

iOS / Web 版と同一のアルゴリズムを使用しています。

1. **ROI サンプリング** — 画面中央の正方形 (短辺の 55%) の平均 R/G/B を取得
2. **リサンプリング** — 線形補間で均一グリッド (15–60 Hz) に変換
3. **正規化** — 各チャンネルを平均で除算
4. **POS 射影** — `X = G-B`, `Y = G+B-2R` → `S = X + α·Y`
5. **デトレンド** — 移動平均 (~0.7 s) を減算
6. **Hann 窓** — スペクトルリークを低減
7. **FFT** — 基数 2 Cooley-Tukey
8. **ピーク検出** — 0.7–3.5 Hz (42–210 BPM) 帯域内の最大値
9. **放物線補間** — サブビン精度
10. **逆 FFT** — 帯域制限波形の再構成

## 可視化モード

| モード | 説明 | AMP スケール |
|--------|------|-------------|
| SUBTLE | ROI 内のみ柔らかく増幅 | 1.0× |
| VIVID | 顔と画面全体が赤⇄青に染まる | 2.5× |
| EXTREME | 顔全体がドカンと真っ赤⇄真っ青に脈動 | 5.0× |

## 権限

- `android.permission.CAMERA` — フロントカメラへのアクセス

## 既知の制限

- EXTREME モードの色増幅は CPU で処理しているため、高解像度端末では負荷が高くなる場合があります。
  将来的には RenderScript / AGSL への移行を検討しています。
- 顔検出は実装されていません。顔を枠内に手動で合わせてください。
