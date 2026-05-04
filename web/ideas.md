# Face Blood — Design Brainstorm

iPhone Safari でカメラを使い、顔の rPPG（remote photoplethysmography）から脈拍を推定し、Eulerian Video Magnification 的に顔の血色変化を「派手に」可視化するアプリ。3つのデザインアプローチを検討し、最終的に **A. Bio-Lab Noir** を採用する。

---

<response>
<text>
**A. Bio-Lab Noir（採用）**

- **Design Movement**: Medical/Scientific Instrumentation × Cyberpunk Heads-Up Display。心電計や血液分析装置のような「計測している実感」を、暗室で見るネオン信号の美しさで包む。
- **Core Principles**:
  1. *Diagnostic seriousness* — 数値（BPM, SNR, 信頼度）が常に画面上に並走し、ユーザーに「本物の計測」だと感じさせる。
  2. *Pulsing chromatic energy* — 顔ROIに重ねる色増幅オーバーレイは、心拍の位相と完全同期して赤〜マゼンタ〜シアンへ拍動する。
  3. *Asymmetric HUD* — 中央に映像、右にバイタル数値、左下に波形、上にステータスバーという非対称なコックピット型。
  4. *Honest about uncertainty* — 信号品質が低い場合は赤い警告と "LOW SNR" を出し、嘘をつかない。
- **Color Philosophy**:
  - 背景は深い炭黒（`oklch(0.13 0.02 260)`）。
  - アクセントは **arterial red** `#ff2e4d`、**venous magenta** `#ff3df0`、**oxy cyan** `#3dfaff`、**warning amber** `#ffb020`。
  - ベースの文字は冷たい白 `oklch(0.95 0.01 230)`、サブはグレイ `oklch(0.7 0.02 260)`。
  - 「血液の色」が画面の主役、それ以外は徹底的に静かに。
- **Layout Paradigm**:
  - フルブリードのカメラ映像の上に、左寄せの `BPM` 巨大数値、右寄せに波形＆SNR、上部に細い HUD ステータスバー、下部に開始/停止と感度スライダ。
  - スマホ縦持ちを基準に、`safe-area-inset` を尊重した HUD レイアウト。
- **Signature Elements**:
  1. 顔の検出領域に重なる「拍動する六角形メッシュ」状のヒートマップ。
  2. 画面左に縦に流れるリアルタイム rPPG 波形（オシロスコープ風）。
  3. 心拍ごとに画面の縁が一瞬だけ赤く脈打つ "vignette pulse"。
- **Interaction Philosophy**:
  - タップは最小限。開始/停止と「増幅倍率」スライダだけ。
  - 増幅を上げると顔色変化が誇張され、下げるとリアル寄りに。常に「見えている」ことを優先。
- **Animation**:
  - 60fps を目標に、色増幅のオーバーレイ強度を `sin(2π·f_pulse·t)` で駆動。
  - BPM 数値はスプリングアニメーション、波形は requestAnimationFrame でスクロール。
  - 起動時は HUD 要素が下からフェード＆スライドインし、計測準備中は走査線アニメーション。
- **Typography System**:
  - 計測数値: **JetBrains Mono** 700（数字のリズムが揃う）。
  - 見出し/ラベル: **Space Grotesk** 500/700（モダンな計測器ラベル感）。
  - 本文: Space Grotesk 400。Inter は使わない。
</text>
<probability>0.07</probability>
</response>

<response>
<text>
**B. Anatomical Atlas（不採用）**
- 19世紀の解剖図譜風。セピアの羊皮紙背景に赤インクの血管イラスト、血管走行図に脈動を重ねる。
- 文学的で美しいが、iPhoneで「派手に光る脈」を見せたいユーザー要求からは少し外れる。
</text>
<probability>0.03</probability>
</response>

<response>
<text>
**C. Brutalist Vital Sign（不採用）**
- 巨大な数字、グリッドのみ、白黒＋一色の警告色。スイス国際様式の極北。
- 強いがクールすぎて、「派手に顔の変化を見せる」用途には控えめすぎる。
</text>
<probability>0.02</probability>
</response>

---

## 採用デザインの実装方針

- ThemeProvider は `dark` 既定。`index.css` で OKLCH 変数を Bio-Lab Noir に書き換える。
- ライブラリ追加は最小：`@mediapipe/face_detection` は CDN 経由ではなくローカル npm `@mediapipe/face_detection` + `@mediapipe/camera_utils` を使うが、依存を増やしすぎない場合は **顔検出なしの中央ROI** で MVP を作り、後から MediaPipe を載せる選択も可。今回はバンドル/iOS 互換性とロード時間を優先し、**画面中央に矩形ROIを置く方式（ユーザーが顔を枠に合わせる）** を採用する。
- rPPG: ROI内の RGB 平均を毎フレーム取得 → 30秒のリングバッファ → 緑チャンネルを中心に **POSライク**な簡易処理（detrend + bandpass 0.7–3.5Hz + FFT でピーク周波数）。
- 映像増幅: `<canvas>` 上で、rPPG信号の瞬時位相に合わせて ROI 内の R チャンネルを増幅したレイヤを `mix-blend-mode: screen` で重ねる。
- iPhone対応: `playsInline`, `muted`, `autoplay`, `getUserMedia({video:{facingMode:'user'}})`, HTTPS必須（webdev のドメインで充足）。
