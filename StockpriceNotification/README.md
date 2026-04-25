# 📈 株価モニター — セットアップガイド

## 必要な環境

- **Windows 10 / 11**
- **.NET 10 SDK** → https://dotnet.microsoft.com/download/dotnet/10.0
- インターネット接続（Yahoo Finance API 使用）

---

## ビルド＆起動

```bash
# プロジェクトフォルダに移動
cd StockpriceNotification

# ビルド
dotnet build -c Release

# 起動
dotnet run
```

または Visual Studio 2022 でソリューションを開いて **F5** で実行。

---

## 使い方

### 1. 銘柄を追加
- `銘柄コード` 欄に入力して **「＋ 追加」** ボタンを押す
- 日本株（4桁）は自動的に `.T` を付加（例: `7203` → `7203.T`）
- 米国株はそのまま入力（例: `AAPL`, `TSLA`）

### 2. アラートを設定

| 項目 | 説明 |
|------|------|
| 上限価格 | この価格以上になったら通知 |
| 下限価格 | この価格以下になったら通知 |
| 上昇率 / 下落率 | 前日比の変化率で通知 |
| 急騰 / 急落 | 直近N秒でX%以上変動したら通知 |
| 出来高急増 | 平均出来高のN倍 かつ 最低M万株を超えたら通知 |

### 3. Discord Webhook の取得方法
1. Discord のチャンネル設定 → **連携サービス** → **ウェブフック**
2. **新しいウェブフック** を作成してURLをコピー
3. アプリの「Discord Webhook URL」欄に貼り付け
4. **「テスト」** ボタンで動作確認

### 4. 監視スタート
- **「▶ スタート」** を押すと指定間隔（デフォルト30秒）ごとに自動更新
- 条件達成時、Discord に自動通知（同じ方向への重複通知はスキップ）

---

## 表示色の意味

| 色 | 意味 |
|----|------|
| 🟢 緑 | 前日比プラス / 取引中 |
| 🔴 赤 | 前日比マイナス / エラー |
| 🟡 黄 | Discord 通知送信済み / プレ・アフターマーケット |
| ⚫ グレー | 取引終了 / 待機中 |

---

## データソース

Yahoo Finance の公開エンドポイントを使用（個人利用目的）。

- **基本情報**（価格・高安値・出来高）: `v8/finance/chart`
- **詳細情報**（時価総額・PER・52週高安値）: `v11/finance/quoteSummary`

> **注意**: 市場が閉まっている時間帯（土日・祝日・夜間）は価格が更新されない場合があります。

---

## ファイル構成

```
StockpriceNotification/
├── App.xaml                      # アプリケーション定義
├── App.xaml.cs
├── AssemblyInfo.cs
├── MainWindow.xaml               # UI レイアウト
├── MainWindow.xaml.cs            # ロジック（株価取得・アラート）
└── StockpriceNotification.csproj # プロジェクト設定（net10.0）
```

---

## 変更履歴

### v2.0
- `.csproj` を `net10.0-windows` に統一（旧 `StockMonitor.csproj` 削除）
- `HttpClient` をシングルトン化（接続枯渇の防止）
- `v11/quoteSummary` エンドポイント追加で **時価総額・PER・52週高安値** を正しく取得
- **出来高急増アラート** のロジックを実装（平均出来高比較）
- 未使用の `ParseDetail` メソッドを削除
- 銘柄削除時に関連するアラート状態・履歴をすべてクリア
- `_http.Dispose()` をウィンドウクローズ時から削除（static HttpClient は dispose しない）
