using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Komorebi;

// 動作確認用セルフテスト。  Komorebi.exe --selftest main      （通常テスト）
//                          KMR_KEEP=1 Komorebi.exe --selftest restore （セッション復元テスト）
// 出力先は環境変数 KMR_OUT（結果とスクリーンショット）。一時プロファイルで動くので普段のデータには触れない。
public partial class MainForm
{
    const string Article = "<html><head><title>テスト記事</title></head><body><nav>メニュー<a href=\"#\">リンク</a></nav>" +
        "<div id=\"promo\" style=\"height:60px;background:#fc0;margin:10px\">広告バナー（消す対象）</div>" +
        "<article><h1>木漏れ日のなかで</h1>%P%</article><footer>フッター</footer>" +
        "<img src=\"https://ad.doubleclick.net/pixel.gif\"><img src=\"https://www.google-analytics.com/collect.gif\"></body></html>";

    readonly List<(string name, bool ok)> testResults = new();
    StreamWriter testLog;
    string testOut;

    void Check(string name, bool ok, string detail = "")
    {
        testResults.Add((name, ok));
        var line = $"{(ok ? "PASS" : "FAIL")}  {name}{(string.IsNullOrEmpty(detail) ? "" : "  -> " + detail)}";
        testLog?.WriteLine(line);
        Console.WriteLine(line);
    }

    async Task RunSelfTestAsync()
    {
        testOut = Environment.GetEnvironmentVariable("KMR_OUT") ?? Path.Combine(appDir, "selftest-out");
        Directory.CreateDirectory(testOut);
        var which = SelfTestScript ?? "main";
        testLog = new StreamWriter(Path.Combine(testOut, $"selftest-{which}.log"), false, new UTF8Encoding(false)) { AutoFlush = true };
        DownloadsDir = testOut;
        PicturesDir = Path.Combine(testOut, "Komorebi");

        // UIとオーバーレイの準備ができるまで待つ
        for (int i = 0; i < 100 && !(uiReady && overlayReady && tabs.Count > 0); i++) await Task.Delay(100);
        var startupMs = (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds;
        testLog.WriteLine($"  [info] プロセス開始からUI準備完了まで: {startupMs:N0} ms");
        try
        {
            if (which == "restore") await TestRestore();
            else if (which == "mem") await TestMem();
            else await TestMain();
        }
        catch (Exception e)
        {
            Check("テスト中に例外が発生しない", false, e.ToString().Split('\n')[0] + " | " + (e.StackTrace ?? "").Split('\n').FirstOrDefault());
        }
        var failed = testResults.Count(r => !r.ok);
        File.WriteAllText(Path.Combine(testOut, $"results-{which}.json"),
            JsonSerializer.Serialize(new { total = testResults.Count, failed, results = testResults.Select(r => new { r.name, r.ok }) }));
        Check($"== {testResults.Count - failed}/{testResults.Count} PASS ==", failed == 0);
        SaveSession();
        StoreRegistry.FlushAll();
        testLog.Dispose();
        await Task.Delay(300);
        Environment.Exit(failed == 0 ? 0 : 1);
    }

    // ---------------------------------------------------------------- 補助
    async Task Sleep(int ms) => await Task.Delay(ms);

    // 使用中のPCで動かすと、ウィンドウが最小化・最大化されることがある。レイアウト依存のテストの前に標準状態へ戻す
    async Task NormalizeWindow()
    {
        if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
        if (ClientSize.Width != 1360 || ClientSize.Height != 820) ClientSize = new Size(1360, 820);
        Relayout();
        await Sleep(300);
    }

    async Task<bool> WaitLoad(Tab t, int ms = 12000)
    {
        await Sleep(300);
        var t0 = DateTime.UtcNow;
        while ((DateTime.UtcNow - t0).TotalMilliseconds < ms)
        {
            if (t.Core != null && !t.Loading && !string.IsNullOrEmpty(t.Core.Source)) return true;
            await Sleep(150);
        }
        return false;
    }

    async Task<string> JS(Tab t, string expr, bool awaitPromise = false)
    {
        var v = await Eval(t.Core, expr, awaitPromise);
        if (v == null) return null;
        var e = v.Value;
        return e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText();
    }

    async Task<string> UiJs(Microsoft.Web.WebView2.WinForms.WebView2 view, string expr)
    {
        var v = await Eval(view.CoreWebView2, expr);
        if (v == null) return null;
        return v.Value.ValueKind == JsonValueKind.String ? v.Value.GetString() : v.Value.GetRawText();
    }

    async Task Shot(CoreWebView2 core, string name)
    {
        try
        {
            using var fs = File.Create(Path.Combine(testOut, name + ".png"));
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
        }
        catch (Exception e) { testLog.WriteLine("撮影失敗 " + name + ": " + e.Message); }
    }

    // ウィンドウ全体（外枠 + タブ + オーバーレイが重なった状態）
    void WinShot(string name)
    {
        try
        {
            using var bmp = new Bitmap(Width, Height);
            using (var g = Graphics.FromImage(bmp))
            {
                var hdc = g.GetHdc();
                Native.PrintWindow(Handle, hdc, 2);
                g.ReleaseHdc(hdc);
            }
            bmp.Save(Path.Combine(testOut, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (Exception e) { testLog.WriteLine("ウィンドウ撮影失敗 " + name + ": " + e.Message); }
    }

    async Task MouseClick(CoreWebView2 core, int x, int y)
    {
        async Task M(string type) => await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",
            $"{{\"type\":\"{type}\",\"x\":{x},\"y\":{y},\"button\":\"left\",\"clickCount\":1}}");
        await M("mouseMoved");
        await Sleep(200);
        await M("mousePressed");
        await M("mouseReleased");
    }

    HttpListener StartTestServer(out string baseUrl)
    {
        var rnd = new Random();
        for (int i = 0; i < 20; i++)
        {
            var port = rnd.Next(20000, 60000);
            var l = new HttpListener();
            l.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                l.Start();
                baseUrl = $"http://127.0.0.1:{port}";
                _ = Task.Run(async () =>
                {
                    while (l.IsListening)
                    {
                        HttpListenerContext ctx;
                        try { ctx = await l.GetContextAsync(); } catch { return; }
                        var path = ctx.Request.Url.AbsolutePath;
                        byte[] body;
                        if (path.StartsWith("/article"))
                        {
                            ctx.Response.ContentType = "text/html; charset=utf-8";
                            var paras = string.Concat(Enumerable.Range(1, 6).Select(n =>
                                $"<p>これは第{n}段落です。リーダーモードが本文を正しく抽出できるかを確かめるための、十分に長い文章をここに書いています。日本語の文章でも読了時間の計算ができます。</p>"));
                            body = Encoding.UTF8.GetBytes(Article.Replace("%P%", paras));
                        }
                        else if (path.StartsWith("/tall"))
                        {
                            ctx.Response.ContentType = "text/html; charset=utf-8";
                            body = Encoding.UTF8.GetBytes("<html><body style=\"margin:0\"><div style=\"height:3000px;background:linear-gradient(#e33,#33e)\">縦長ページ</div></body></html>");
                        }
                        else if (path.StartsWith("/popup"))
                        {
                            ctx.Response.ContentType = "text/html; charset=utf-8";
                            body = Encoding.UTF8.GetBytes("<html><head><title>ポップアップ</title></head><body>popup</body></html>");
                        }
                        else if (path.StartsWith("/file"))
                        {
                            ctx.Response.ContentType = "application/octet-stream";
                            ctx.Response.AddHeader("Content-Disposition", "attachment; filename=\"kmr-test.bin\"");
                            body = new byte[4096];
                        }
                        else { ctx.Response.StatusCode = 404; body = Encoding.UTF8.GetBytes("not found"); }
                        try { ctx.Response.OutputStream.Write(body); ctx.Response.Close(); } catch { }
                    }
                });
                return l;
            }
            catch { l.Close(); }
        }
        throw new InvalidOperationException("テスト用サーバーを起動できません");
    }

    // ---------------------------------------------------------------- 通常テスト
    async Task TestMain()
    {
        var prevClip = "";
        try { prevClip = Clipboard.GetText(); } catch { }
        var server = StartTestServer(out var bs);
        Activate();

        // --- 単一exe化: UIが exe から展開され、ショートカットを作れる ---
        Check("UIが埋め込みリソースから展開されている", File.Exists(Path.Combine(uiDir, "index.html")) && File.Exists(Path.Combine(uiDir, "bridge.js")) && File.Exists(Path.Combine(uiDir, ".ok")), uiDir);
        var scDesk = Path.Combine(testOut, "sc-desktop");
        var scProg = Path.Combine(testOut, "sc-programs");
        Check("ショートカットを作れる", CreateShortcuts(scDesk, scProg) && File.Exists(Path.Combine(scDesk, "Komorebi.lnk")) && File.Exists(Path.Combine(scProg, "Komorebi.lnk")));
        try
        {
            dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic lnk = sh.CreateShortcut(Path.Combine(scDesk, "Komorebi.lnk"));
            Check("ショートカットのリンク先がこのexe", string.Equals((string)lnk.TargetPath, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase), (string)lnk.TargetPath);
        }
        catch (Exception e) { Check("ショートカットのリンク先がこのexe", false, e.Message); }
        Check("ウィンドウにアイコンが設定されている", Icon != null);

        // --- 起動直後の状態 ---
        await Sleep(1200);
        Check("起動時にタブが1つできる", tabs.Count == 1, $"tabs={tabs.Count}");
        var first = ActiveTab;
        Check("新しいタブページを表示", first != null && first.Url.StartsWith(NewtabUrl), first?.Url);
        Check("外枠UIの高さが報告されている", uiTop > 60 && uiTop < 200, $"top={uiTop}");
        await Sleep(800);
        await Shot(chrome.CoreWebView2, "01-ui-top");
        await Shot(first.Core, "02-newtab");
        WinShot("01-window");

        Check("新しいタブにショートカットが並ぶ", int.Parse(await JS(first, "document.querySelectorAll('#grid a.sc').length")) >= 4);
        Check("新しいタブにだけ専用APIが公開される", await JS(first, "typeof window.komorebiNewtab") == "object");
        await JS(first, "komorebiNewtab.go('example.com'); 1");
        var ok = await WaitLoad(first, 15000);
        Check("新しいタブの検索から遷移できる", ok && first.Core.Source.StartsWith("https://example.com"), first.Core.Source);
        var leak = await JS(first, "typeof window.komorebiNewtab + '/' + typeof window.komorebi + '/' + typeof window.komorebiFocus");
        Check("外部サイトにはAPIが一切公開されない", leak == "undefined/undefined/undefined", leak);

        // --- ブラウザの名乗り（Chrome として振る舞うか）---
        var ua = await JS(first, "navigator.userAgent");
        Check("UAは Chrome（Edg/ を含まない）", ua.Contains("Chrome/") && !ua.Contains("Edg/"), ua);
        var brands = await JS(first, "JSON.stringify(navigator.userAgentData.brands.map(b => b.brand))");
        Check("クライアントヒントのブランドが Google Chrome", brands.Contains("Google Chrome") && !brands.Contains("Edge"), brands);

        // --- URL解釈 ---
        string U(string s) => Search.ToUrl(s, "google", NewtabUrl);
        Check("「yt ねこ」でYouTube検索", U("yt ねこ").StartsWith("https://www.youtube.com/results?search_query="), U("yt ねこ"));
        Check("ドメインはhttpsで開く", U("example.com") == "https://example.com");
        Check("単語はGoogle検索", U("foo bar").StartsWith("https://www.google.com/search?q="));
        Check("localhostはhttp", U("localhost:3000") == "http://localhost:3000");

        // --- パレット ---
        Check("パレットでコマンド検索", PaletteQuery("分割").Any(i => i.Id == "view.split"));
        var calc = PaletteQuery("1+2*3");
        Check("パレットで計算", calc.Count > 0 && calc[0].Kind == "calc" && calc[0].Value == "7", calc.FirstOrDefault()?.Title);
        Check("@でタブだけ検索", PaletteQuery("@example").All(i => i.Kind == "tab"));
        TogglePalette();
        await Sleep(900);
        Check("パレット表示中", overlayMode == "palette");
        await UiJs(overlayView, "(() => { const i = document.getElementById('pal-input'); i.value = 'ダウンロード'; i.dispatchEvent(new Event('input')); return 1; })()");
        await Sleep(700);
        await Shot(overlayView.CoreWebView2, "03-palette");
        Check("パレットの高さが結果に合わせて変わる", paletteHeight < 400, $"h={paletteHeight}");
        await UiJs(overlayView, "document.getElementById('pal-input').dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))");
        await Sleep(900);
        Check("パレットからコマンドを実行できる（ダウンロードパネルが開く）", overlayMode == "panel" && panelName == "downloads");
        HideOverlay(false);

        // --- 実ページ（HTTP）での機能 ---
        var at = CreateTab(bs + "/article");
        await WaitLoad(at);
        await Sleep(1500);
        Check("広告ドメインへの通信をブロック", at.Blocked >= 2, $"blocked={at.Blocked}");

        RunCommand("tools.reader");
        await Sleep(1800);
        var rtxt = await JS(at, "document.body.innerText");
        Check("リーダーモードに切替", at.Reader);
        Check("本文が抽出される（ナビ・フッターは除く）", rtxt.Contains("第3段落") && !rtxt.Contains("メニュー") && !rtxt.Contains("フッター"), rtxt[..Math.Min(40, rtxt.Length)].Replace("\n", " "));
        Check("読了時間が表示される", System.Text.RegularExpressions.Regex.IsMatch(rtxt, @"約\d+分"));
        await Shot(at.Core, "04-reader");
        RunCommand("tools.reader");
        await WaitLoad(at);
        await Sleep(500);
        Check("リーダーモードを解除（元のページに戻る）", !at.Reader && await JS(at, "!!document.getElementById('promo')") == "true");

        // 要素ブロッカー: 実際にクリックして消す
        RunCommand("tools.picker");
        await Sleep(700);
        var rect = JsonDocument.Parse(await JS(at, "JSON.stringify(document.getElementById('promo').getBoundingClientRect())")).RootElement;
        await MouseClick(at.Core, (int)(rect.Num("x") + rect.Num("width") / 2), (int)(rect.Num("y") + rect.Num("height") / 2));
        await Sleep(1000);
        Sites.Hidden.TryGetValue("127.0.0.1", out var rules);
        Check("要素ブロッカーでセレクタが保存される", rules != null && rules.Count == 1 && rules[0].Contains("promo"), rules == null ? "なし" : string.Join(",", rules));
        Check("クリックした要素が消える", await JS(at, "getComputedStyle(document.getElementById('promo')).display") == "none");
        at.Core.Reload();
        await WaitLoad(at);
        await Sleep(900);
        Check("再読み込み後も非表示が維持される", await JS(at, "getComputedStyle(document.getElementById('promo')).display") == "none");
        RunCommand("tools.unhide");
        await WaitLoad(at);
        await Sleep(700);
        Check("非表示を解除できる", await JS(at, "getComputedStyle(document.getElementById('promo')).display") != "none");

        // 強制ダークモード
        RunCommand("tools.dark");
        await Sleep(800);
        Check("サイト別ダークモードが効く", (await JS(at, "getComputedStyle(document.documentElement).filter")).Contains("invert"));
        await Shot(at.Core, "05-dark");
        at.Core.Reload();
        await WaitLoad(at);
        await Sleep(900);
        Check("ダークモードがサイトごとに記憶される", (await JS(at, "getComputedStyle(document.documentElement).filter")).Contains("invert"));
        RunCommand("tools.dark");
        await Sleep(600);
        Check("ダークモードを解除できる", !(await JS(at, "getComputedStyle(document.documentElement).filter")).Contains("invert"));

        // サイトごとのズーム記憶
        RunCommand("view.zoomIn");
        await Sleep(700);
        Sites.Zoom.TryGetValue("127.0.0.1", out var zv);
        Check("ズームがサイトごとに保存される", Math.Abs(zv - 1.1) < 0.001, $"zoom={zv}");
        at.Core.Reload();
        await WaitLoad(at);
        await Sleep(1000);
        Check("再読み込み後もズームが復元される", Math.Abs(at.View.Ctl().ZoomFactor - 1.1) < 0.001, $"zoom={at.View.Ctl().ZoomFactor}");
        RunCommand("view.zoomReset");
        await Sleep(500);

        // ページ内検索（UIの入力 → ホスト → 件数表示まで通す）
        ActivateTab(at.Id);
        await Sleep(400);
        RunCommand("view.find");
        await Sleep(500);
        await UiJs(chrome, "(() => { const i = document.getElementById('find-input'); i.value = '段落'; i.dispatchEvent(new Event('input')); return 1; })()");
        await Sleep(1500);
        var cnt = await UiJs(chrome, "document.getElementById('find-count').textContent");
        Check("ページ内検索で件数が表示される（1 / 6）", System.Text.RegularExpressions.Regex.IsMatch(cnt ?? "", @"1\s*/\s*6"), cnt);
        await UiJs(chrome, "document.getElementById('find-next').click()");
        await Sleep(700);
        cnt = await UiJs(chrome, "document.getElementById('find-count').textContent");
        Check("「次へ」で2件目に進む", System.Text.RegularExpressions.Regex.IsMatch(cnt ?? "", @"2\s*/\s*6"), cnt);
        await Shot(chrome.CoreWebView2, "05b-find");
        await UiJs(chrome, "document.getElementById('find-close').click()");
        await Sleep(300);

        // スクリーンショット（縦長ページで「ページ全体」が本当に全体か）
        await NormalizeWindow();
        var tall = CreateTab(bs + "/tall");
        await WaitLoad(tall);
        await Sleep(500);
        RunCommand("tools.shot");
        await Sleep(1200);
        RunCommand("tools.shotFull");
        await Sleep(2500);
        var shots = Directory.Exists(PicturesDir) ? Directory.GetFiles(PicturesDir, "*.png").OrderBy(x => x).ToArray() : Array.Empty<string>();
        Check("スクリーンショットが2枚保存される", shots.Length >= 2, $"{shots.Length}枚");
        if (shots.Length >= 2)
        {
            using var vis = Image.FromFile(shots[^2]);
            using var full = Image.FromFile(shots[^1]);
            Check("「ページ全体」は表示範囲より縦に長い", full.Height > vis.Height * 1.5, $"表示範囲={vis.Width}x{vis.Height} 全体={full.Width}x{full.Height}");
        }
        CloseTab(tall.Id);

        // ダウンロード（空タブは自動で閉じる）
        var before = tabs.Count;
        CreateTab(bs + "/file");
        await Sleep(3500);
        var dl = downloads.FirstOrDefault();
        Check("ダウンロードが完了して記録される", dl != null && dl.State == "completed" && File.Exists(dl.Path), dl == null ? "なし" : $"{dl.Name} {dl.State}");
        Check("ダウンロード用の空タブが残らない", tabs.Count == before, $"before={before} after={tabs.Count}");

        // window.open（別タブで開き、opener が保たれる）
        ActivateTab(at.Id);
        await Sleep(400);
        var nBefore = tabs.Count;
        await Eval(at.Core, $"window.__w = window.open('{bs}/popup', '_blank'); 1", false, true);
        await Sleep(2500);
        Check("window.open は新しいタブで開く", tabs.Count == nBefore + 1 && ActiveTab.Url.Contains("/popup"), $"tabs {nBefore}->{tabs.Count} url={ActiveTab?.Url}");
        if (tabs.Count == nBefore + 1)
        {
            var op = await JS(ActiveTab, "String(window.opener !== null)");
            Check("ポップアップから window.opener に届く", op == "true", op);
            CloseTab(ActiveTab.Id);
        }

        // モバイル表示 → 元に戻す
        ActivateTab(at.Id);
        await Sleep(300);
        RunCommand("tools.mobile");
        await Sleep(1800);
        Check("モバイル表示でUAが Android になる", (await JS(at, "navigator.userAgent")).Contains("Android"));
        RunCommand("tools.mobile");
        await Sleep(1800);
        Check("デスクトップ表示に戻せる", (await JS(at, "navigator.userAgent")).Contains("Windows"));

        // --- ネットワーク系（外部接続が必要）---
        var nt = CreateTab("https://example.com/?utm_source=news&fbclid=abc&keep=1");
        if (await WaitLoad(nt, 15000))
            Check("追跡パラメータを自動除去（keepは残る）", nt.Core.Source == "https://example.com/?keep=1", nt.Core.Source);
        var ht = CreateTab("http://example.com/");
        if (await WaitLoad(ht, 15000))
            Check("HTTPをHTTPSへ自動アップグレード", ht.Core.Source.StartsWith("https://"), ht.Core.Source);

        // --- 分割表示 ---
        await NormalizeWindow();
        ActivateTab(nt.Id);
        await Sleep(500);
        RunCommand("view.split");
        await Sleep(900);
        Check("分割表示が有効になる", split != null);
        if (split != null)
        {
            var A = GetTab(split.A); var B = GetTab(split.B);
            Check("2つのビューが左右に並ぶ", A.View.Left < B.View.Left && A.View.Width > 100 && B.View.Width > 100 && A.View.Top == B.View.Top,
                $"{A.View.Bounds} | {B.View.Bounds}");
            Check("両方のビューが表示されている", A.View.Visible && B.View.Visible);
        }
        WinShot("06-window-split");
        RunCommand("view.split");
        await Sleep(400);
        Check("分割表示を解除できる", split == null);

        // --- タブ休止 ---
        ActivateTab(at.Id);
        await Sleep(400);
        Check("バックグラウンドのタブを休止できる", SleepTab(nt) && nt.View == null);
        Check("アクティブなタブは休止されない", !SleepTab(ActiveTab));
        ActivateTab(nt.Id);
        await Sleep(1500);
        Check("休止タブを開くと復帰する", nt.View != null && await WaitLoad(nt, 10000));

        // --- ワークスペース ---
        S.Workspaces.Add(new Workspace { Id = "wtest", Name = "仕事", Color = "#f28b82" });
        var nWs = WsOrdered().Count;
        RunCommand("ws.switch:wtest");
        await Sleep(900);
        Check("ワークスペースを切り替えると別のタブ群になる", currentWs == "wtest" && WsOrdered().Count == 1 && nWs > 1, $"ws={currentWs} tabs={WsOrdered().Count}");
        RunCommand("ws.switch:w1");
        await Sleep(700);
        Check("元のワークスペースに戻る", currentWs == "w1" && WsOrdered().Count == nWs);

        // --- 閉じたタブの復元 ---
        var cur = tabs.Count;
        CloseTab(nt.Id);
        await Sleep(300);
        RunCommand("tab.restore");
        await Sleep(700);
        Check("閉じたタブを復元できる", tabs.Count == cur, $"tabs={tabs.Count}");

        // --- キーボードショートカット ---
        ActivateTab(at.Id);
        await Sleep(500);
        var nTabs = tabs.Count;
        Check("Ctrl+T は割り当て済み", TryShortcut(Keys.T, Keys.Control));
        await Sleep(1200);
        Check("Ctrl+T で新しいタブが開く", tabs.Count == nTabs + 1, $"{nTabs}->{tabs.Count}");
        Check("Ctrl+W は割り当て済み", TryShortcut(Keys.W, Keys.Control));
        await Sleep(900);
        Check("Ctrl+W でタブが閉じる", tabs.Count == nTabs, $"tabs={tabs.Count}");
        Check("Ctrl+K は割り当て済み", TryShortcut(Keys.K, Keys.Control));
        await Sleep(900);
        Check("Ctrl+K でパレットが開く", overlayMode == "palette");
        HideOverlay(false);
        Check("Alt+1〜9 / Ctrl+1〜9 の割り当て", HasShortcut(Keys.D1, Keys.Alt) && HasShortcut(Keys.D9, Keys.Control));
        Check("Ctrl+Shift+T / Alt+← / F9 / Ctrl+, / F11 / Ctrl+; も割り当て済み",
            HasShortcut(Keys.T, Keys.Control | Keys.Shift) && HasShortcut(Keys.Left, Keys.Alt) && HasShortcut(Keys.F9, Keys.None) &&
            HasShortcut(Keys.Oemcomma, Keys.Control) && HasShortcut(Keys.F11, Keys.None) && HasShortcut(Keys.Oemplus, Keys.Control | Keys.Shift));
        Check("割り当てのないキーは無視する", !HasShortcut(Keys.Q, Keys.Control) && !HasShortcut(Keys.A, Keys.None));
        ActivateTab(at.Id);
        await Sleep(500);

        await NormalizeWindow();
        // 実際のキー入力（OSのキーボード入力 → WebView2 → AcceleratorKeyPressed）。
        // SendKeys は前面のウィンドウに送られるので、Komorebi が前面にあるときだけ行う（別アプリに誤送信しない）
        Activate();
        Native.ForceForeground(Handle);
        await Sleep(500);
        if (Native.GetForegroundWindow() == Handle)
        {
            ActiveTab.View.Focus();
            await Sleep(400);
            var n2 = tabs.Count;
            SendKeys.SendWait("^t");
            await Sleep(1500);
            var opened = tabs.Count == n2 + 1;
            Check("実キー入力: Ctrl+T で新しいタブが開く", opened, $"{n2}->{tabs.Count}");
            if (opened)
            {
                await Sleep(500);
                testLog.WriteLine($"  [info] Ctrl+W 送信前: active={activeId} fg={Native.GetForegroundWindow() == Handle} focusedTab={tabs.FirstOrDefault(t => t.View != null && t.View.ContainsFocus)?.Id} tabCoreReady={ActiveTab?.Core != null}");
                SendKeys.SendWait("^w");
                await Sleep(1000);
                Check("実キー入力: Ctrl+W でタブが閉じる（新しいタブにもフォーカスが移っている）", tabs.Count == n2, $"tabs={tabs.Count}");
            }
        }
        else
        {
            Check("（スキップ）実キー入力: Komorebi が前面でないため送信しない", true);
        }

        // --- ブックマーク・履歴 ---
        var nb = Bookmarks.Count;
        ActivateTab(ht.Id);
        await Sleep(400);
        RunCommand("bookmark.toggle");
        await Sleep(300);
        Check("ブックマークを追加できる", Bookmarks.Count == nb + 1, $"{nb}->{Bookmarks.Count}");
        Check("履歴が記録される", History.Count >= 3, $"count={History.Count}");

        // --- アドレスバー候補 ---
        var sug = BuildSuggestions("example");
        Check("候補に検索・タブ・履歴が出る", sug.Count >= 2 && sug[0].Kind == "search", string.Join(",", sug.Select(s => s.Kind)));

        // --- パネル ---
        foreach (var (name, file) in new[] { ("settings", "08-panel-settings"), ("history", "09-panel-history"), ("help", "10-panel-help"), ("downloads", "11-panel-downloads"), ("notes", "11b-panel-notes"), ("bookmarks", "11c-panel-bookmarks") })
        {
            OpenPanel(name);
            await Sleep(900);
            Check($"パネル「{name}」が開く", overlayMode == "panel" && panelName == name);
            await Shot(overlayView.CoreWebView2, file);
            if (name == "settings") WinShot("08-window-panel");
            HideOverlay(false);
        }

        // --- 入力ダイアログ ---
        var pr = AskPrompt("ワークスペースを追加", "名前", "勉強", "作成");
        await Sleep(900);
        Check("入力ダイアログが表示される", overlayMode == "prompt");
        await Shot(overlayView.CoreWebView2, "12-prompt");
        await UiJs(overlayView, "document.getElementById('pr-input').value = 'テスト入力'; document.getElementById('pr-ok').click(); 1");
        Check("入力した文字が返る", await pr == "テスト入力");
        pr = AskPrompt("確認", "x", "", "OK", false);
        await Sleep(600);
        await UiJs(overlayView, "document.getElementById('pr-cancel').click(); 1");
        Check("キャンセルするとnullが返る", await pr == null);

        // --- アドレスバー操作（入力 → 候補 → ↓ → Enter → 遷移）---
        var ot = CreateTab("about:blank");
        await Sleep(800);
        await UiJs(chrome, "(() => { const a = document.getElementById('address'); a.focus(); a.value = 'example.com/?omni=1'; a.dispatchEvent(new Event('input')); return 1; })()");
        await Sleep(800);
        Check("入力すると候補が出る", overlayMode == "suggest");
        await Sleep(300);
        WinShot("07-window-suggest");
        await Shot(overlayView.CoreWebView2, "07-suggest");
        await UiJs(chrome, "document.getElementById('address').dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true })); 1");
        await Sleep(500);
        var filled = await UiJs(chrome, "document.getElementById('address').value");
        Check("↓で候補を選ぶとアドレスバーに反映される", filled != null && filled.StartsWith("https://example.com"), filled);
        await UiJs(chrome, "document.getElementById('address').dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })); 1");
        await WaitLoad(ot, 15000);
        Check("Enterで遷移する", ot.Core?.Source == "https://example.com/?omni=1", ot.Core?.Source);
        CloseTab(ot.Id);

        // --- 集中モード ---
        StartFocus(1);
        Check("集中モード中はYouTubeをブロック判定", IsFocusBlocked("https://www.youtube.com/watch?v=1"));
        Check("集中モード中でも他サイトは通る", !IsFocusBlocked("https://example.com/"));
        var ft = CreateTab("https://www.youtube.com/");
        await Sleep(3500);
        Check("集中モード中は遷移が集中画面に差し替わる", (ft.Core?.Source ?? "").Contains("focus.html"), ft.Core?.Source);
        await Shot(ft.Core, "13-focus");
        StopFocus();

        // --- 縦タブレイアウト ---
        await NormalizeWindow();
        ActivateTab(at.Id);
        S.TabLayout = "side";
        ScheduleState();
        await Sleep(1200);
        Check("縦タブでは左にサイドバー分の余白ができる", uiLeft > 150, $"left={uiLeft}");
        WinShot("14-window-side");
        S.SidebarCollapsed = true;
        ScheduleState();
        await Sleep(900);
        Check("サイドバーを折りたためる", uiLeft < 100, $"left={uiLeft}");
        S.TabLayout = "top";
        S.SidebarCollapsed = false;
        ScheduleState();
        await Sleep(800);
        Check("横タブに戻せる", uiLeft == 0);

        // --- 透明度 ---
        S.Opacity = 80;
        ApplyOpacityAndTop();
        await Sleep(500);
        WinShot("15-window-opacity80");
        Check("ウィンドウの透明度を変えられる", Math.Abs(Opacity - 0.8) < 0.01);
        S.Opacity = 100;
        ApplyOpacityAndTop();

        try { server.Stop(); } catch { }
        try { if (!string.IsNullOrEmpty(prevClip)) Clipboard.SetText(prevClip); } catch { }
    }

    // ---------------------------------------------------------------- メモリ計測用（外部から計測する）
    // 新しいタブ + 外部サイト3つを開いて安定させ、準備完了の目印ファイルを置いてしばらく待つ
    async Task TestMem()
    {
        foreach (var u in new[] { "https://example.com/", "https://example.org/", "https://example.net/" })
            CreateTab(u);
        await Sleep(15000);
        File.WriteAllText(Path.Combine(testOut, "mem-ready.txt"), "ready");
        await Sleep(45000);
        Check("メモリ計測の待機が終わった", true);
    }

    // ---------------------------------------------------------------- セッション復元テスト
    async Task TestRestore()
    {
        await Sleep(2000);
        var awake = tabs.Count(t => t.View != null);
        Check("前回のタブが復元される", tabs.Count >= 5, $"tabs={tabs.Count}");
        Check("開いているタブ以外は休止状態で復元される（起動が軽い）", awake == 1, $"awake={awake}");
        Check("アクティブなタブが決まっている", ActiveTab != null && ActiveTab.View != null);
        Check("ワークスペースが復元される", S.Workspaces.Any(w => w.Id == "wtest"));
        Check("ブックマークが保存されている", Bookmarks.Count >= 5, $"count={Bookmarks.Count}");
        Check("履歴が保存されている", History.Count >= 3, $"count={History.Count}");
        Check("サイトごとの設定が保存されている", Sites.Zoom != null && Sites.Dark.Count >= 1);
        var slept = tabs.FirstOrDefault(t => t.View == null && Util.IsHttp(t.Url));
        if (slept != null)
        {
            ActivateTab(slept.Id);
            Check("休止タブを開くと読み込まれる", await WaitLoad(slept, 15000), slept.Url);
        }
    }
}
