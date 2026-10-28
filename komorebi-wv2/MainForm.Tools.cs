using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Komorebi;

public partial class MainForm
{
    // DevTools の Runtime.evaluate でスクリプトを実行する（ページのCSPの影響を受けず、Promiseの完了も待てる）
    internal static async Task<JsonElement?> Eval(CoreWebView2 core, string expr, bool awaitPromise = false, bool userGesture = false)
    {
        var json = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
            JsonSerializer.Serialize(new { expression = expr, awaitPromise, returnByValue = true, userGesture }));
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("exceptionDetails", out var ex))
            throw new InvalidOperationException(ex.ToString());
        return doc.RootElement.GetProperty("result").TryGetProperty("value", out var v) ? v.Clone() : null;
    }

    // ------------------------------------------------------------------
    // リーダーモード
    // ------------------------------------------------------------------
    internal async Task ToggleReader()
    {
        var a = ActiveTab;
        var core = a?.Core;
        if (core == null || Util.HostOf(a.Url) == "") { Toast("このページではリーダーモードを使えません"); return; }
        if (a.Reader)
        {
            a.Reader = false;
            core.Reload();
            ScheduleState();
            return;
        }
        try
        {
            if (a.DarkApplied)
            {
                await Eval(core, PageScripts.RemoveCss("dark"));
                a.DarkApplied = false;
            }
            await Eval(core, PageScripts.InsertCss(PageScripts.ReaderCss, "reader"));
            var r = (await Eval(core, PageScripts.ReaderJs))?.GetString();
            if (r == "fail") { Toast("本文を検出できませんでした"); return; }
            a.Reader = true;
            Toast("リーダーモード（もう一度 F9 で元に戻ります）");
        }
        catch
        {
            Toast("リーダーモードに失敗しました");
        }
        ScheduleState();
    }

    // ------------------------------------------------------------------
    // 要素ブロッカー
    // ------------------------------------------------------------------
    internal async Task RunPicker()
    {
        var a = ActiveTab;
        var core = a?.Core;
        var h = a != null ? Util.HostOf(a.Url) : "";
        if (core == null || h == "") { Toast("このページでは使えません"); return; }
        Toast("消したい要素をクリックしてください（Escでキャンセル）");
        a.View.Focus();
        string sel;
        try
        {
            sel = (await Eval(core, PageScripts.PickerJs, true, true))?.GetString();
        }
        catch
        {
            Toast("要素の選択に失敗しました");
            return;
        }
        if (string.IsNullOrEmpty(sel)) { Toast("キャンセルしました"); return; }
        if (!Sites.Hidden.TryGetValue(h, out var list)) Sites.Hidden[h] = list = new List<string>();
        list.Add(sel);
        sitesStore.Save();
        try { await Eval(core, PageScripts.InsertCss(sel + "{display:none!important}", "hidden-" + list.Count)); } catch { /* 無視 */ }
        Toast("要素を非表示にしました（次回以降もこのサイトで有効）");
        ScheduleState();
    }

    internal void UnhideAll()
    {
        var a = ActiveTab;
        var h = a != null ? Util.HostOf(a.Url) : "";
        if (h == "" || !Sites.Hidden.TryGetValue(h, out var l) || l.Count == 0)
        {
            Toast("このサイトで非表示にした要素はありません");
            return;
        }
        Sites.Hidden.Remove(h);
        sitesStore.Save();
        a.Core?.Reload();
        Toast("非表示にした要素を元に戻しました");
        ScheduleState();
    }

    // ------------------------------------------------------------------
    // ピクチャーインピクチャー
    // ------------------------------------------------------------------
    internal async Task TogglePip()
    {
        var core = ActiveTab?.Core;
        if (core == null) return;
        try
        {
            var r = (await Eval(core, PageScripts.PipJs, true, true))?.GetString();
            if (r == "none") Toast("このページに再生できる動画がありません");
            else Toast(r == "exit" ? "ピクチャーインピクチャーを終了しました" : "ピクチャーインピクチャー");
        }
        catch
        {
            Toast("この動画はピクチャーインピクチャーに対応していません");
        }
    }

    // ------------------------------------------------------------------
    // スクリーンショット
    // ------------------------------------------------------------------
    static string Stamp() => DateTime.Now.ToString("yyyyMMdd-HHmmss");

    string SaveShot(byte[] png, string label)
    {
        Directory.CreateDirectory(PicturesDir);
        var file = Path.Combine(PicturesDir, $"komorebi-{Stamp()}.png");
        File.WriteAllBytes(file, png);
        try
        {
            using var img = Image.FromStream(new MemoryStream(png));
            Clipboard.SetImage(img);
        }
        catch { /* クリップボードが使えなくても保存はできている */ }
        Toast($"{label}: 保存してクリップボードにコピーしました");
        return file;
    }

    internal async Task ShotVisible()
    {
        var core = ActiveTab?.Core;
        if (core == null) return;
        try
        {
            using var ms = new MemoryStream();
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
            SaveShot(ms.ToArray(), "スクリーンショット");
        }
        catch
        {
            Toast("スクリーンショットに失敗しました（ウィンドウが表示されていません）");
        }
    }

    // ページ全体（縦に長いページも1枚）を DevTools で撮影する
    internal async Task ShotFull()
    {
        var core = ActiveTab?.Core;
        if (core == null) return;
        try
        {
            var m = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Page.getLayoutMetrics", "{}")).RootElement;
            var cs = m.TryGetProperty("cssContentSize", out var c) ? c : m.GetProperty("contentSize");
            var w = (int)Math.Ceiling(cs.GetProperty("width").GetDouble());
            var h = Math.Min((int)Math.Ceiling(cs.GetProperty("height").GetDouble()), 16000);
            var json = await core.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", JsonSerializer.Serialize(new
            {
                format = "png",
                captureBeyondViewport = true,
                clip = new { x = 0, y = 0, width = w, height = h, scale = 1 },
            }));
            var data = JsonDocument.Parse(json).RootElement.GetProperty("data").GetString();
            SaveShot(Convert.FromBase64String(data), "ページ全体");
        }
        catch
        {
            Toast("ページ全体の撮影に失敗しました");
        }
    }

    // ------------------------------------------------------------------
    // そのほか
    // ------------------------------------------------------------------
    internal void CopyCleanUrl()
    {
        var a = ActiveTab;
        if (a == null) return;
        var clean = Blocklist.CleanTrackingUrl(a.Url);
        try { Clipboard.SetText(clean); } catch { return; }
        Toast(clean == a.Url ? "URLをコピーしました" : "トラッキング情報を除いたURLをコピーしました");
    }

    internal async Task ToggleMobile()
    {
        var a = ActiveTab;
        var core = a?.Core;
        if (core == null) return;
        a.Mobile = !a.Mobile;
        await ApplyIdentity(core, a.Mobile);
        core.Reload();
        Toast(a.Mobile ? "モバイル表示に切り替えました" : "デスクトップ表示に戻しました");
        ScheduleState();
    }

    internal void ToggleMute(Tab t)
    {
        var core = t?.Core;
        if (core == null) return;
        core.IsMuted = !core.IsMuted;
        t.Muted = core.IsMuted;
        ScheduleState();
    }

    internal void ToggleSiteAdblock()
    {
        var a = ActiveTab;
        var h = a != null ? Util.HostOf(a.Url) : "";
        if (h == "") { Toast("このページでは使えません"); return; }
        var on = Sites.AdblockOff.Contains(h);
        if (on) Sites.AdblockOff.Remove(h);
        else Sites.AdblockOff.Add(h);
        sitesStore.Save();
        Toast($"{h}: 広告ブロック {(on ? "ON" : "OFF")}（再読み込みで反映）");
        ScheduleState();
    }

    internal async Task ClearBrowsingData()
    {
        if (!await AskConfirm("Cookie・キャッシュなどの閲覧データを削除します。\n（ログイン状態も解除されます）")) return;
        var core = chrome.CoreWebView2;
        await core.Profile.ClearBrowsingDataAsync();
        Toast("閲覧データを削除しました");
        ActiveTab?.Core?.Reload();
    }

    internal void AddSelectionToNote(string text, Tab tab)
    {
        Notes.Insert(0, new Note { Id = Util.Now(), Text = $"{text}\n\n— {tab.Title}\n{tab.Url}", Ts = Util.Now() });
        notesStore.Save();
        RefreshPanel("notes");
        Toast("選択範囲をメモに追加しました");
    }

    // デスクトップとスタートメニューにショートカットを作る（フォルダはテスト用に差し替えられる）
    internal bool CreateShortcuts(string desktopDir = null, string programsDir = null)
    {
        var exe = Environment.ProcessPath;
        desktopDir ??= Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        programsDir ??= Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        try
        {
            dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            foreach (var dir in new[] { desktopDir, programsDir })
            {
                Directory.CreateDirectory(dir);
                dynamic lnk = sh.CreateShortcut(Path.Combine(dir, "Komorebi.lnk"));
                lnk.TargetPath = exe;
                lnk.WorkingDirectory = Path.GetDirectoryName(exe);
                lnk.IconLocation = exe + ",0";
                lnk.Description = "Komorebi";
                lnk.Save();
            }
            Toast("デスクトップとスタートメニューにショートカットを作りました");
            return true;
        }
        catch (Exception e)
        {
            Program.LogError("ショートカット作成", e);
            Toast("ショートカットを作れませんでした");
            return false;
        }
    }

    internal void OpenExternal(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { /* 開けなければ無視 */ }
    }

    internal async Task Translate()
    {
        var a = ActiveTab;
        if (a == null || Util.HostOf(a.Url) == "") return;
        CreateTab($"https://translate.google.com/translate?sl=auto&tl=ja&u={Uri.EscapeDataString(a.Url)}", new TabOpts { After = a.Id });
        await Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // ページ内検索（WebView2 の Find API）
    // ------------------------------------------------------------------
    string findTerm = "";
    readonly HashSet<int> findHooked = new();

    void HookFind(Tab t)
    {
        if (!findHooked.Add(t.Id)) return;
        var f = t.Core.Find;
        void Report()
        {
            if (t.Id != activeId) return;
            // ActiveMatchIndex は 1 始まり（0件のときは 0）
            ToUI("k:find-result", new { active = f.MatchCount > 0 ? f.ActiveMatchIndex : 0, total = f.MatchCount });
        }
        f.MatchCountChanged += (_, _) => Report();
        f.ActiveMatchIndexChanged += (_, _) => Report();
    }

    internal async Task DoFind(string text, bool forward, bool next)
    {
        var a = ActiveTab;
        var core = a?.Core;
        if (core == null) return;
        HookFind(a);
        if (string.IsNullOrEmpty(text))
        {
            core.Find.Stop();
            findTerm = "";
            ToUI("k:find-result", new { active = 0, total = 0 });
            return;
        }
        if (next && text == findTerm)
        {
            if (forward) core.Find.FindNext();
            else core.Find.FindPrevious();
            return;
        }
        findTerm = text;
        var opt = env.CreateFindOptions();
        opt.FindTerm = text;
        opt.ShouldHighlightAllMatches = true;
        opt.SuppressDefaultFindDialog = true;
        await core.Find.StartAsync(opt);
    }

    internal void StopFind()
    {
        try { ActiveTab?.Core?.Find.Stop(); } catch { /* 無視 */ }
        findTerm = "";
        FocusActive();
    }
}
