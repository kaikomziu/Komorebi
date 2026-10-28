> # ⚠️ このプロジェクトは **Hizashi（日差し）** に移行しました
>
> 同名の有名ソフトとぶつかるため、名前を **Hizashi** に変更し、新しいリポジトリに移しました。
> **最新版・ダウンロード・サポートは、こちらをご利用ください:**
> ### 👉 https://github.com/kaikomziu/hizashi
>
> このリポジトリ（旧名 Komorebi）は更新を終了し、アーカイブ（読み取り専用）にしています。
>
> **This project has moved to [Hizashi](https://github.com/kaikomziu/hizashi)** (renamed to avoid a name clash). This repository is archived and no longer maintained.

---
# Komorebi（木漏れ日）

Windows 用の、自作のデスクトップブラウザです。中身は Chromium なので、たいていのサイトがそのまま表示できます。
コマンドパレット・ワークスペース・縦タブ・分割表示・集中モード・リーダーモード・要素ブロッカーなど、作業しやすさに寄せた機能を入れています。

## ダウンロード

[Releases](../../releases/latest) の `Komorebi-WebView2-win-x64.zip`（約 2.5 MB）を展開して、`Komorebi.exe` を実行します。

必要なもの: Windows 10 / 11、[.NET 9 デスクトップランタイム](https://dotnet.microsoft.com/ja-jp/download/dotnet/9.0)、WebView2 ランタイム（Windows 11 には最初から入っています。入っていない場合は起動時に案内が出ます）。

## 2 つのバージョン

| フォルダ | 作り | 配布サイズ |
|---|---|---|
| [`komorebi-wv2/`](komorebi-wv2) | C#（.NET 9）+ Microsoft Edge WebView2（**配布はこちら**） | 約 2.5 MB |
| [`komorebi/`](komorebi) | Electron（Chromium を同梱）。exe は配布していないので自分でビルド・起動します | 本体だけで約 268 MB |

機能と画面は同じで、エンジンの載せ方だけが違います。詳しい違い・ショートカット・データの保存先・自動テストは、それぞれのフォルダの README を見てください。

## 主な機能

- **コマンドパレット**（`Ctrl+K`）: タブ・ブックマーク・履歴・全コマンドをあいまい検索。計算もできる
- **ワークスペース**（`Alt+1〜9`）: 「仕事」「趣味」など色分けしたタブのグループを切り替え
- **縦タブ** / **分割表示**: 横タブと縦タブの切り替え、2 つのタブを左右に並べる
- **集中モード**: 時間を決めて、気が散るサイトをブロック
- **リーダーモード** / **要素ブロッカー**: 本文だけを表示 / 邪魔な要素をクリックで消してサイトごとに記憶
- **広告・トラッカーのブロック**、HTTPS 自動アップグレード、追跡パラメータ（`utm_*` など）の除去
- **強制ダークモード**、サイトごとのズーム記憶、使っていないタブの自動休止
- スクリーンショット（ページ全体も可）、クイックメモ、アドレスバーの検索ショートカット、新しいタブページ ほか

`Ctrl+K` か `F1` で、すべてのコマンドを確認できます。

## 制限事項

- Chrome ウェブストアの拡張機能は使えません。
- 広告ブロックはドメイン単位です。サイト自身が配信する広告（YouTube の動画広告など）は防げません。
- シークレットウィンドウ、アカウント同期は未実装です。
- Google ログインは未検証です。

## 自分でビルドする

```bat
cd komorebi-wv2
publish.bat
```

`distKomorebiKomorebi.exe` ができます（.NET 9 SDK が必要）。
