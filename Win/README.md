# Face Blood Windows (WinUI 3)

`FaceBlood.WinUI3` は Web 版の UI/信号処理を参考にした Windows 向け実装です。

## できること

- カメラ映像の中央 ROI から平均 RGB を取得
- POS 投影 + FFT で BPM/SNR/CONF を推定
- SUBTLE / VIVID / EXTREME の 3 モード
- AMP スライダと波形表示（HUD）

## ビルドと実行

```bash
# x64 でビルド
dotnet build ./FaceBlood.WinUI3/FaceBlood.WinUI3.csproj -p:Platform=x64

# x64 で実行
dotnet run --project ./FaceBlood.WinUI3/FaceBlood.WinUI3.csproj -p:Platform=x64
```

## 注意

- 初回起動時にカメラ許可が必要です。
- `Package.appxmanifest` に `webcam` capability を設定済みです。
- 現状は nullability 警告が残っていますが、動作には影響しません。
