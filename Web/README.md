## 開発環境のセットアップ

```bash
cd Web

# 依存インストール
pnpm install

# 開発時: 開発サーバー起動（HTTPS が必要な場合は ngrok 等でトンネル）
pnpm dev

# リリース前確認: 本番ビルド
pnpm build
```

## GitHub 上で Web 版を試す

このリポジトリには GitHub Pages へ自動デプロイする Workflow を追加しています。

1. GitHub のリポジトリ画面で Settings > Pages を開く
2. Build and deployment の Source を GitHub Actions に設定する
3. main ブランチへ push する（または Actions から Deploy Web App to GitHub Pages を手動実行）
4. デプロイ完了後、以下の URL で確認する

https://<ユーザー名>.github.io/<リポジトリ名>/

注意:

- iPhone/Safari でカメラを使う場合は HTTPS が必要です（GitHub Pages は HTTPS 配信）
- 初回デプロイは数分かかることがあります
