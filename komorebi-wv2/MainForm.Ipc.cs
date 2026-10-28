using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Komorebi;

// 外枠UI(index.html)とオーバーレイ(overlay.html)からのメッセージ処理。
// チャンネル名は Electron 版と同じ（"k:" で始まる）。UI側のJSは bridge.js 経由でこれを呼ぶ。
public partial class MainForm
{
    async void OnUiMessage(WebView2 view, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // 自分たちの内部ページ以外からのメッセージは受け付けない
        if (!(e.Source ?? "").StartsWith(UiBase)) return;
        JsonElement root;
        try { root = JsonDocument.Parse(e.WebMessageAsJson).RootElement.Clone(); }
        catch { return; }

        var c = root.Str("c");
        if (c == null) return;

        if (root.TryGetProperty("id", out var idEl))
        {
            // 結果を返す呼び出し（invoke）
            var args = root.TryGetProperty("a", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Select(x => x.Clone()).ToArray()
                : Array.Empty<JsonElement>();
            object result = null;
            try { result = HandleInvoke(c, args); }
            catch (Exception ex) { Console.Error.WriteLine($"invoke {c} でエラー: {ex}"); }
            view.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { id = idEl.GetInt32(), r = result }, Util.Json));
            return;
        }

        root.TryGetProperty("p", out var p);
        try { HandleSend(c, p); }
        catch (Exception ex) { Console.Error.WriteLine($"send {c} でエラー: {ex}"); }
        await Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // 結果を返す呼び出し
    // ------------------------------------------------------------------
    object HandleInvoke(string c, JsonElement[] a)
    {
        switch (c)
        {
            case "k:palette-query":
            {
                var items = PaletteQuery(a.Length > 0 ? a[0].GetString() : "");
                if (overlayMode == "palette")
                {
                    // 結果の件数にあわせてパレットの高さを変える
                    paletteHeight = 54 + 12 + Math.Max(1, Math.Min(items.Count, 9)) * 52 + 40 + 2;
                    LayoutOverlay();
                }
                return items;
            }
            case "k:panel-data":
                return PanelData(a.Length > 0 ? a[0].GetString() : "", a.Length > 1 && a[1].ValueKind == JsonValueKind.String ? a[1].GetString() : "");
            case "k:note-new":
            {
                var n = new Note { Id = Util.Now(), Text = "", Ts = Util.Now() };
                Notes.Insert(0, n);
                notesStore.Save();
                return n.Id;
            }
        }
        return null;
    }

    object PanelData(string name, string arg)
    {
        var q = (arg ?? "").ToLowerInvariant();
        switch (name)
        {
            case "history":
                return Enumerable.Reverse(History)
                    .Where(h => q == "" || $"{h.Title} {h.Url}".ToLowerInvariant().Contains(q))
                    .Take(300).ToList();
            case "bookmarks":
                return Bookmarks.Where(b => q == "" || $"{b.Title} {b.Url}".ToLowerInvariant().Contains(q)).ToList();
            case "downloads":
                return downloads.ToList();
            case "notes":
                return Notes;
            case "settings":
                return new
                {
                    adblock = S.Adblock,
                    httpsUpgrade = S.HttpsUpgrade,
                    stripTracking = S.StripTracking,
                    restoreSession = S.RestoreSession,
                    sleepMinutes = S.SleepMinutes,
                    tabLayout = S.TabLayout,
                    searchEngine = S.SearchEngine,
                    forceDark = S.ForceDark,
                    showBookmarksBar = S.ShowBookmarksBar,
                    opacity = S.Opacity,
                    alwaysOnTop = S.AlwaysOnTop,
                    sidebarCollapsed = S.SidebarCollapsed,
                    focusSites = S.FocusSites,
                    engines = Search.Engines.Select(x => new { id = x.Key, name = x.Value.Name }).ToList(),
                    bangs = Search.Bangs.Keys.ToList(),
                };
            case "help":
                return commands.Select(x => new { id = x.Id, label = x.Label, keys = x.Keys }).ToList();
        }
        return null;
    }

    // ------------------------------------------------------------------
    // 結果を返さない呼び出し
    // ------------------------------------------------------------------
    void HandleSend(string c, JsonElement p)
    {
        switch (c)
        {
            case "k:ui-ready":
                uiReady = true;
                if (tabs.Count == 0) RestoreOrOpenFirst();
                ApplyOpacityAndTop();
                ScheduleState();
                break;
            case "k:ui-layout":
                uiTop = Math.Round(p.Num("top"));
                uiLeft = Math.Round(p.Num("left"));
                Relayout();
                break;
            case "k:cmd":
                RunCommand(p.GetString());
                break;

            // タブ
            case "k:tab-activate": ActivateTab(p.GetInt32()); break;
            case "k:tab-close": CloseTab(p.GetInt32()); break;
            case "k:tab-new": RunCommand("tab.new"); break;
            case "k:tab-menu": ShowTabMenu(p.GetInt32()); break;
            case "k:tab-mute": ToggleMute(GetTab(p.GetInt32())); break;
            case "k:tab-move":
            {
                var t = GetTab((int)p.Num("id"));
                var beforeId = p.TryGetProperty("beforeId", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt32() : 0;
                if (t == null || t.Id == beforeId) break;
                tabs.Remove(t);
                var bi = beforeId != 0 ? tabs.FindIndex(x => x.Id == beforeId) : -1;
                if (bi >= 0) tabs.Insert(bi, t);
                else tabs.Add(t);
                ScheduleState();
                SaveSessionSoon();
                break;
            }

            // ワークスペース
            case "k:ws-switch": SwitchWs(p.GetString()); break;
            case "k:ws-new": Fire(NewWs); break;
            case "k:ws-menu": ShowWsMenu(p.GetString()); break;

            // ナビゲーション・メニュー
            case "k:nav":
                RunCommand(p.GetString() switch
                {
                    "back" => "nav.back", "forward" => "nav.forward", "reload" => "nav.reload",
                    "stop" => "nav.stop", "home" => "nav.home", _ => "",
                });
                break;
            case "k:menu": ShowMainMenu(); break;
            case "k:shield-menu": ShowShieldMenu(); break;
            case "k:open-url":
                if (p.Bool("newTab")) CreateTab(p.Str("url"), new TabOpts { After = activeId });
                else Navigate(p.Str("url"));
                break;
            case "k:bm-menu": ShowBookmarkMenu(p.GetString()); break;

            // アドレスバー
            case "k:omni-input":
                p.TryGetProperty("rect", out var rect);
                UpdateSuggest(p.Str("text") ?? "", rect);
                break;
            case "k:omni-key":
            {
                if (overlayMode != "suggest" || omniItems.Count == 0) break;
                var n = omniItems.Count;
                var down = p.GetString() == "down";
                omniSel = down ? (omniSel + 1 >= n ? -1 : omniSel + 1) : (omniSel - 1 < -1 ? n - 1 : omniSel - 1);
                ToOverlay("k:ov-sel", omniSel);
                var it = omniSel >= 0 ? omniItems[omniSel] : null;
                ToUI("k:omni-fill", it != null ? (it.Kind is "tab" or "search" ? it.Title : it.Url) : omniText);
                break;
            }
            case "k:omni-enter":
            {
                if (overlayMode == "suggest" && omniSel >= 0 && omniSel < omniItems.Count)
                {
                    PickSuggest(omniItems[omniSel]);
                    break;
                }
                HideSuggest();
                Navigate(Search.ToUrl(p.GetString(), S.SearchEngine, NewtabUrl));
                ToUI("k:omni-done");
                break;
            }
            case "k:omni-close": HideSuggest(); break;
            case "k:suggest-pick":
            {
                var i = p.GetInt32();
                if (i >= 0 && i < omniItems.Count) PickSuggest(omniItems[i]);
                break;
            }

            // ページ内検索
            case "k:find": Fire(() => DoFind(p.Str("text"), !p.TryGetProperty("forward", out var f) || f.ValueKind != JsonValueKind.False, p.Bool("next"))); break;
            case "k:find-stop": StopFind(); break;

            // オーバーレイ
            case "k:ov-ready":
                overlayReady = true;
                foreach (var (ch, pl) in overlayQueue) Post(overlayView, ch, pl);
                overlayQueue.Clear();
                break;
            case "k:ov-close": HideOverlay(); break;
            case "k:palette-run":
            {
                var item = JsonSerializer.Deserialize<PaletteItem>(p.GetProperty("item").GetRawText(), Util.Json);
                var ctrl = p.TryGetProperty("mod", out var mod) && mod.Bool("ctrl");
                RunPaletteItem(item, ctrl);
                break;
            }
            case "k:prompt-result":
            {
                if (pendingPrompt == null || (int)p.Num("id") != promptId) break;
                var tcs = pendingPrompt;
                pendingPrompt = null;
                var val = p.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                HideOverlay();
                tcs.TrySetResult(val);
                break;
            }
            case "k:panel-set": panelName = p.GetString(); break;

            // パネルからの操作
            case "k:history-delete":
            {
                var ts = (long)p.GetDouble();
                var i = History.FindIndex(h => h.Ts == ts);
                if (i >= 0) { History.RemoveAt(i); historyStore.Save(); }
                RefreshPanel("history");
                break;
            }
            case "k:bookmark-remove":
            {
                var url = p.GetString();
                var i = Bookmarks.FindIndex(b => b.Url == url);
                if (i >= 0) { Bookmarks.RemoveAt(i); bookmarksStore.Save(); }
                RefreshPanel("bookmarks");
                ScheduleState();
                break;
            }
            case "k:bookmark-add-current":
            {
                var a = ActiveTab;
                if (a != null && !IsBookmarked(a.Url)) ToggleBookmark();
                break;
            }
            case "k:dl-open": { var d = downloads.FirstOrDefault(x => x.Id == p.GetInt32()); if (d != null) OpenExternal(d.Path); break; }
            case "k:dl-reveal":
            {
                var d = downloads.FirstOrDefault(x => x.Id == p.GetInt32());
                if (d != null) System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{d.Path}\"");
                break;
            }
            case "k:dl-cancel":
            {
                var id = p.GetInt32();
                if (dlOps.TryGetValue(id, out var op)) op.Cancel();
                break;
            }
            case "k:dl-remove":
            {
                var id = p.GetInt32();
                downloads.RemoveAll(x => x.Id == id);
                PushDownloads();
                break;
            }
            case "k:dl-clear":
                downloads.RemoveAll(x => x.State != "progressing");
                PushDownloads();
                break;
            case "k:note-save":
            {
                var id = (long)p.Num("id");
                var n = Notes.FirstOrDefault(x => x.Id == id);
                if (n != null) { n.Text = p.Str("text") ?? ""; n.Ts = Util.Now(); notesStore.Save(); }
                break;
            }
            case "k:note-delete":
            {
                var id = (long)p.GetDouble();
                var i = Notes.FindIndex(x => x.Id == id);
                if (i >= 0) { Notes.RemoveAt(i); notesStore.Save(); }
                break;
            }
            case "k:note-insert-url":
            {
                var a = ActiveTab;
                if (a != null) ToOverlay("k:note-url", $"{a.Title}\n{a.Url}");
                break;
            }
            case "k:setting":
                SetSetting(p.Str("key"), p.GetProperty("value"));
                break;
        }
    }
}
