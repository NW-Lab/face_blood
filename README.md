# Face Blood — rPPG Pulse Visualizer

**Face Blood** は、iPhone のフロントカメラを使い、顔の微細な色変化から心拍数（BPM）をリアルタイムに推定し、**Eulerian Video Magnification（EVM）的な映像増幅**で脈拍の血色変化を派手に可視化するアプリです。

本リポジトリには **2 つの実装** が含まれます。

| 実装 | パス | 技術スタック | 備考 |
|---|---|---|---|
| **Web 版** | [`/client`](./client) | React 19 + TypeScript + Canvas 2D | iPhone Safari など HTTPS ブラウザで動作。`https://faceblood-5daeuetf.manus.space` にデプロイ済み |
| **iOS ネイティブ版** | [`/ios`](./ios) | SwiftUI + AVFoundation + Core Image (Metal) + Accelerate vDSP | Xcode 15 でビルドして実機にインストール。GPU フィルタで高フレームレート |

両実装は同一の rPPG パイプライン（POS 法 + FFT）と 3 段階のビジュアルモード（SUBTLE / VIVID / EXTREME）を共有します。

> "Our goal is to reveal temporal variations in videos that are difficult or impossible to see with the naked eye."
> — Wu et al., *Eulerian Video Magnification*, SIGGRAPH 2012

---

## デモ

| 画面 | 説明 |
|---|---|
| 起動画面 | 「計測開始」をタップしてカメラを起動 |
| キャリブレーション | 顔を枠内に収めて 3〜5 秒待機 |
| 計測中 | 顔の ROI に赤〜シアンの拍動オーバーレイが表示され、左に BPM、下に rPPG 波形が流れる |
| 増幅スライダ | 1〜10 倍で色増幅の強度を調整可能 |

---

## 技術スタック

| 要素 | 内容 |
|---|---|
| フレームワーク | React 19 + TypeScript + Vite |
| スタイリング | Tailwind CSS 4 + shadcn/ui |
| 映像取得 | `getUserMedia` API（iPhone Safari 対応） |
| 信号処理 | 独自実装 rPPG ライブラリ（POS 法 + FFT、純 TypeScript） |
| 映像増幅 | Canvas 2D API による色増幅オーバーレイ（screen blend mode） |
| デプロイ | Manus Web Hosting（HTTPS 必須） |

---

## アーキテクチャ

```
<video> (hidden, getUserMedia)
  └─ requestAnimationFrame ループ（30fps）
       ├─ offscreen <canvas> に drawImage
       ├─ ROI（画面中央 55%）のピクセル平均 R/G/B を取得
       ├─ RppgProcessor.push(sample)
       │    └─ リングバッファ（12秒）に蓄積
       ├─ RppgProcessor.analyze()
       │    ├─ 一様サンプリングに補間
       │    ├─ POS 投影（X = G−B, Y = G+B−2R）
       │    ├─ デトレンド + Hann 窓
       │    ├─ FFT → 0.7〜3.5 Hz バンドパス
       │    ├─ 放物線補間でピーク周波数を推定
       │    └─ BPM / SNR / 信頼度 / 波形 / 瞬時位相を返す
       └─ display <canvas> に映像 + 色増幅オーバーレイを描画
```

### 映像増幅の仕組み

rPPG 信号の瞬時値 `s(t)` を用いて、顔の色をリアルタイムに変調します。3つの**ビジュアルモード**を切り替え可能です。

| モード | 効果 | 実装 |
|---|---|---|
| **SUBTLE** | 控えめモード — ROI内のみ柔らかく増幅 | Canvas `screen` ブレンドの放射状グラデーション |
| **VIVID** | 派手モード — 顔と画面全体が赤⇄青にダイナミックに染まる | 大きな放射グラデーション + `soft-light` 全画面ウォッシュ |
| **EXTREME** | 超派手モード — 顔全体がドカンと真っ赤⇄真っ青に脈動 | **ピクセル単位の肌色シフト** + 彩度ブースト + `lighter` グロー |

**EXTREME モードのパイプライン**:

1. カメラフレームを 240×180 のオフスクリーンキャンバスにダウンサンプリング
2. 各ピクセルが肌色か判定（`R > G > B` + 明度範囲 + R/G比 のヒューリスティック）
3. 肌色ピクセルを `s(t)` の符号と振幅に応じて **動脈赤 / 酸素シアン** へミックス
4. 彩度ブーストを適用して、`globalCompositeOperation = "lighter"` で原映像に合成
5. 全画面に `screen` モードでカラーティントを乗せて、画面全体が脈動するように見せる

増幅倍率は AMP スライダ（1〜10倍）とモードの `ampScale`（SUBTLE: 1.0 / VIVID: 2.5 / EXTREME: 5.0）の積で決定されます。

- `s(t) > 0`（収縮期・動脈血充満）→ **動脈赤** に強くシフト
- `s(t) < 0`（拡張期・脱酸素）→ **酸素シアン／ブルー** に強くシフト
- 心拍検出時に画面縁が赤く脈打つ「vignette pulse」アニメーション
- 30fps カメラ取得と 60fps 表示を分離し、指数移動平均でなめらかに補間

これは Wu et al. (2012) の**線形色増幅パイプライン**（ラプラシアンピラミッド + 時間バンドパスフィルタ + 増幅）と、Wadhwa et al. (2013) の**位相ベース増幅**の概念を、ブラウザ上で実時間動作するよう簡略化・誇張表現として近似実装したものです。

---

## 使い方（iPhone）

1. Safari で HTTPS の URL を開く
2. 「計測開始」をタップ → カメラ許可を承認
3. 顔を画面の点線枠に合わせ、**静止した状態で 5〜10 秒待つ**
4. キャリブレーション完了後、BPM と波形が表示される
5. 「AMP」スライダで色増幅の強度を調整

> **注意**: 十分な照明環境（室内蛍光灯以上）と静止状態が精度に直結します。激しい動きや逆光では信頼度（CONF）が低下します。

---

## 開発環境のセットアップ

```bash
# 依存インストール
pnpm install

# 開発サーバー起動（HTTPS が必要な場合は ngrok 等でトンネル）
pnpm dev

# ビルド
pnpm build
```

---

## 研究文献・参照論文

本アプリは以下の研究成果に基づいて実装されています。

### Eulerian Video Magnification（EVM）

#### 元論文

**[EVM-1]** Wu, H.-Y., Rubinstein, M., Shih, E., Guttag, J. V., Durand, F., & Freeman, W. T. (2012).
**Eulerian Video Magnification for Revealing Subtle Changes in the World.**
*ACM Transactions on Graphics (Proc. SIGGRAPH 2012)*, 31(4).
DOI: [10.1145/2185520.2185561](https://doi.org/10.1145/2185520.2185561)
プロジェクトページ: <https://people.csail.mit.edu/mrub/evm/>

> EVM の原著論文。MIT CSAIL のグループが提案した、ラプラシアンピラミッドによる空間分解 + 時間バンドパスフィルタリング + 線形増幅という枠組みを確立。顔の血流変化（rPPG）と微細な動き（呼吸・振動）の両方を可視化できることを示した。被引用数 2,100 件超（2024年時点）。

**[EVM-2]** Wadhwa, N., Rubinstein, M., Durand, F., & Freeman, W. T. (2013).
**Phase-Based Video Motion Processing.**
*ACM Transactions on Graphics (Proc. SIGGRAPH 2013)*, 32(4).
DOI: [10.1145/2461912.2461966](https://doi.org/10.1145/2461912.2461966)
プロジェクトページ: <https://people.csail.mit.edu/nwadhwa/phase-video/>

> EVM の後継手法。複素ステアラブルピラミッドの位相変化を解析することで、EVM より大きな増幅倍率と低ノイズを実現。動き増幅に特に有効。

**[EVM-3]** Wadhwa, N., Rubinstein, M., Durand, F., & Freeman, W. T. (2014).
**Riesz Pyramids for Fast Phase-Based Video Magnification.**
*IEEE International Conference on Computational Photography (ICCP 2014)*.
プロジェクトページ: <https://people.csail.mit.edu/nwadhwa/riesz-pyramid/>

> Riesz 変換を用いたコンパクトなピラミッド表現により、位相ベース映像増幅をリアルタイム動作可能にした手法。

**[EVM-4]** Wadhwa, N., Rubinstein, M., Durand, F., & Freeman, W. T. (2016).
**Eulerian Video Magnification and Analysis.**
*Communications of the ACM*, 60(1), 87–95.
DOI: [10.1145/3015573](https://doi.org/10.1145/3015573)

> EVM の包括的レビュー論文。色増幅・動き増幅の両パイプラインを統一的に解説。

---

### Remote Photoplethysmography（rPPG）

#### 先駆的研究

**[rPPG-1]** Verkruysse, W., Svaasand, L. O., & Nelson, J. S. (2008).
**Remote Plethysmographic Imaging Using Ambient Light.**
*Optics Express*, 16(26), 21434–21445.
DOI: [10.1364/OE.16.021434](https://doi.org/10.1364/OE.16.021434)

> rPPG の先駆的論文。環境光と民生用デジタルカメラのみで 1m 以上離れた被験者の心拍・呼吸を計測できることを初めて実証。緑チャンネルが最も強い脈拍信号を含むことを示した。被引用数 2,400 件超。

**[rPPG-2]** Poh, M.-Z., McDuff, D. J., & Picard, R. W. (2010).
**Non-Contact, Automated Cardiac Pulse Measurements Using Video Imaging and Blind Source Separation.**
*Optics Express*, 18(10), 10762–10774.
DOI: [10.1364/OE.18.010762](https://doi.org/10.1364/OE.18.010762)

> MIT Media Lab による rPPG の重要論文。顔の自動追跡と独立成分分析（ICA）を組み合わせ、ウェブカメラのみで動き耐性を持つ心拍計測を実現。被引用数 2,400 件超。

#### 信号処理手法

**[rPPG-3]** de Haan, G., & Jeanne, V. (2013).
**Robust Pulse Rate from Chrominance-Based rPPG.**
*IEEE Transactions on Biomedical Engineering*, 60(10), 2878–2886.
DOI: [10.1109/TBME.2013.2266196](https://doi.org/10.1109/TBME.2013.2266196)

> クロミナンス（色差）ベースの rPPG 手法（CHROM 法）を提案。RGB 色空間での盲目的信号分離の限界を分析し、照明変動に対してより頑健な手法を導出。被引用数 1,000 件超。

**[rPPG-4]** Wang, W., den Brinker, A. C., Stuijk, S., & de Haan, G. (2017).
**Algorithmic Principles of Remote-PPG.**
*IEEE Transactions on Biomedical Engineering*, 64(7), 1479–1491.
DOI: [10.1109/TBME.2016.2609282](https://doi.org/10.1109/TBME.2016.2609282)
プレプリント: <https://pure.tue.nl/ws/files/31563684/TBME_00467_2016_R1_preprint.pdf>

> rPPG の数学的モデルを確立し、既存手法（ICA, CHROM, PBV 等）を統一的に説明。**POS（Plane Orthogonal to Skin-tone）法**を提案し、大規模ベンチマークで最高精度を達成。本アプリの信号処理はこの POS 法を参考に実装。被引用数 1,500 件超。

#### 動き・頭部運動ベース手法

**[rPPG-5]** Balakrishnan, G., Durand, F., & Guttag, J. (2013).
**Detecting Pulse from Head Motions in Video.**
*IEEE Conference on Computer Vision and Pattern Recognition (CVPR 2013)*.
DOI: [10.1109/CVPR.2013.440](https://doi.org/10.1109/CVPR.2013.440)

> 心拍による頭部の微細な動き（バリストカルジオグラフィ）を PCA で抽出し、心拍数を推定する手法。EVM の色増幅とは異なるアプローチ。

#### 深層学習ベース手法

**[rPPG-6]** Chen, W., & McDuff, D. (2018).
**DeepPhys: Video-Based Physiological Measurement Using Convolutional Attention Networks.**
*European Conference on Computer Vision (ECCV 2018)*, 349–365.
DOI: [10.1007/978-3-030-01216-8_22](https://doi.org/10.1007/978-3-030-01216-8_22)
arXiv: <https://arxiv.org/abs/1805.07888>

> rPPG に深層学習を適用した先駆的研究。注意機構付き畳み込みネットワークによるエンドツーエンド学習で、従来の信号処理手法を上回る精度を実現。

**[rPPG-7]** Liu, X., Fromm, J., Patel, S., & McDuff, D. (2020).
**Multi-Task Temporal Shift Attention Networks for On-Device Contactless Vitals Measurement.**
*Advances in Neural Information Processing Systems (NeurIPS 2020)*.
arXiv: <https://arxiv.org/abs/2006.03790>

> モバイルデバイス上でリアルタイム動作する rPPG ネットワーク（MTTS-CAN）。ARM CPU で 150fps 以上を達成し、心拍・呼吸の同時計測が可能。

#### サーベイ・レビュー

**[rPPG-8]** Pirzada, P., Wilde, A., Doherty, G. H., & Harris-Birtill, D. (2023).
**Remote Photoplethysmography (rPPG): A State-of-the-Art Review.**
*medRxiv* (preprint).
DOI: [10.1101/2023.10.12.23296882](https://doi.org/10.1101/2023.10.12.23296882)

> rPPG の包括的サーベイ。信号処理ベース・深層学習ベースの両手法を網羅的に整理。

---

## 実装上の注意点・制限

本アプリは研究・教育目的のデモンストレーションです。以下の制限があります。

| 項目 | 内容 |
|---|---|
| 精度 | 医療用途には使用できません。照明・動き・肌色によって誤差が生じます |
| 顔検出 | MVP では中央 ROI 方式（ユーザーが枠に顔を合わせる）を採用。MediaPipe 等による自動顔検出は未実装 |
| 信号処理 | POS 法の簡略実装。完全な EVM（ラプラシアンピラミッド分解）はブラウザのリアルタイム処理負荷の観点から近似実装 |
| 環境依存 | 蛍光灯フリッカー（50/60Hz）が信号に混入する場合があります |

---

## ライセンス

MIT License

---

## 謝辞

本アプリの設計は、MIT CSAIL の Hao-Yu Wu、Michael Rubinstein、Neal Wadhwa、William T. Freeman、Frédo Durand らによる Eulerian Video Magnification の研究、および Wim Verkruysse、Ming-Zher Poh らによる rPPG の先駆的研究に多大な恩恵を受けています。
