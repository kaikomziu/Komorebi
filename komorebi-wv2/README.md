# Komorebi（木漏れ日）— WebView2 版

Windows 標準の **Microsoft Edge WebView2**（Chromium）と C#(.NET 9) で作った、デスクトップブラウザです。
Electron 版（`../komorebi`）と**同じ機能・同じ画面**で、エンジンの載せ方だけが違います。

## 起動

```bat
run.bat
```

`dist\Komorebi\Komorebi.exe` があればそれを、なければビルドして起動します。

配布用の exe を作るには:

```bat
publish.bat
```

`dist\Komorebi\` ができます（**約 2.5 MB**）。このフォルダごと配れば動きます（`ui` フォルダが必須）。
.NET 9 デスクトップランタイムが無いPCに渡す場合は、`publish.bat` の最後に書いてある `--self-contained true` 版（約150MB）を使います。

必要なもの: Windows 10/11、.NET 9 デスクトップランタイム、WebView2 ランタイム（Windows 11 には最初から入っています。入っていない場合は起動時に案内が出ます）。

## Electron 版との違い

| | Electron 版 | WebView2 版 |
|---|---|---|
| 配布サイズ | 本体だけで約 268 MB | 約 2.5 MB |
| メモリ（4タブ、実測） | 約 760 MB / プライベート約 460 MB | 約 750 MB / プライベート約 410 MB |
| Chromium の更新 | アプリごとに同梱（自分で更新） | Windows が自動更新（Evergreen） |
| 対応OS | Windows / Mac / Linux にできる | Windows 専用 |

メモリは**ほぼ同じ**です（どちらも Chromium が描画するため）。差が大きいのは配布サイズです。

見た目の細かい違い:
- コマンドパレット・アドレスバー候補・ダイアログは、Electron 版の「全画面の半透明レイヤー」ではなく、必要な大きさだけの**不透明な子ウィンドウ**です（背景が暗くならない）。
- タイトルバーは Windows 標準（ダークカラーで外枠と同色）。Electron 版のようにタブをタイトルバーに入れてはいません。
- ブラウザの名乗り: WebView2 の既定は Edge ですが、UA とクライアントヒントを `Emulation.setUserAgentOverride` で **Google Chrome** に揃えています（Google ログインの「安全でないブラウザ」判定対策）。実際の Google ログインは試していません。

機能の一覧とショートカットは Electron 版の README と同じです（コマンドパレット、ワークスペース、縦タブ、分割表示、集中モード、リーダーモード、要素ブロッカー、広告ブロック、HTTPS自動化、追跡パラメータ除去、強制ダーク、タブの自動休止、サイト別ズーム、スクリーンショット、クイックメモ、検索ショートカット、新しいタブページ ほか）。`Ctrl+K` か `F1` で全コマンドを確認できます。

## データの保存先

- `%APPDATA%\KomorebiWV2\` に `settings.json` `sites.json` `bookmarks.json` `history.json` `notes.json` `session.json`
- `%LOCALAPPDATA%\KomorebiWV2\WebView2\` に Cookie・キャッシュ（ログイン状態）
- 初回起動時に、Electron 版（`%APPDATA%\komorebi`）の設定・ブックマーク・履歴・メモを**コピー**して引き継ぎます（Cookie＝ログイン状態は引き継げません）
- エラーは `%LOCALAPPDATA%\KomorebiWV2\error.log`
- スクリーンショットは `Pictures\Komorebi\`

## 作りの要点

```
Program.cs            エントリ。多重起動の防止（2つ目はURLを渡して終了）
MainForm.cs           ウィンドウ・レイアウト・状態通知・オーバーレイ
MainForm.Tabs.cs      タブ / ワークスペース / 分割表示 / セッション / 集中モード / サイト別設定
MainForm.Network.cs   ナビゲーション制御・広告ブロック・ダウンロード・権限・右クリックメニュー
MainForm.Commands.cs  コマンド一覧・ショートカット・パレット検索・アドレスバー候補・ネイティブメニュー
MainForm.Tools.cs     リーダー・要素ブロッカー・PiP・スクリーンショット・ページ内検索
MainForm.Ipc.cs       外枠UI/オーバーレイとのメッセージ処理（チャンネル名は Electron 版と同じ "k:"）
PageScripts.cs        ページに注入するJS/CSS
Blocklist.cs / Util.cs / Store.cs / Models.cs / Ext.cs / SelfTest.cs
ui/                   外枠・オーバーレイ・新しいタブ・集中モード画面（HTML/CSS/JS）。bridge.js が WebView2 の通信を橋渡し
```

- ウィンドウの中は、奥から **外枠UI（WebView2）→ 各タブ（WebView2）→ オーバーレイ（WebView2）** の順に重ねています。
- UI の HTML は仮想ホスト `https://komorebi.app/` として配信します。内部ページ以外にはブリッジ（新しいタブ・集中モード用API）を公開せず、メッセージの送信元URLもホスト側で検証しています。
- WinForms 版の `WebView2` コントロールは `CoreWebView2Controller` を公開していないため、キー入力の横取りに `Ext.cs` でリフレクションを使っています。**WebView2 パッケージのバージョンを上げるときは、このフィールド名（`_coreWebView2Controller`）が残っているか確認してください**。

## 自動テスト

実際にウィンドウを開いて機能を一通り動かします（一時プロファイルで動くので普段のデータには触れません。クリップボードは一時的に使い、終了時に戻します）。
外部接続（example.com / youtube.com）を使う項目があります。テスト中はウィンドウを最小化しないでください。

```bat
set KMR_OUT=C:\temp\kmr-out
dist\Komorebi\Komorebi.exe --selftest main
set KMR_KEEP=1
dist\Komorebi\Komorebi.exe --selftest restore
```

`main` は 88 項目、`restore`（セッション復元）は 8 項目。結果とスクリーンショットは `KMR_OUT` に出ます。
実キー入力のテストは、Komorebi が前面にあるときだけ送信します（別のアプリに誤送信しないため）。

開発用の環境変数: `KMR_DATA_DIR`（保存先の差し替え）、`KMR_OLD_DIR`（引き継ぎ元の差し替え）。

## 制限事項

- Chrome ウェブストアの拡張機能は使えません。
- 広告ブロックはドメイン単位です。YouTube の動画広告など、サイト自身が配信する広告は防げません。
- シークレットウィンドウ、アカウント同期は未実装です。
- ページ全体のスクリーンショットは縦 16000px までです。
- Google ログインは未検証です。
