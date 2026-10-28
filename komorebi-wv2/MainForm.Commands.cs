using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Komorebi;

internal record CommandDef(string Id, string Label, string Keys, Action Run);

public partial class MainForm
{
    internal readonly List<CommandDef> commands = new();
    readonly Dictionary<string, string> shortcuts = new();

    void Cmd(string id, string label, string keys, Action run) => commands.Add(new CommandDef(id, label, keys, run));

    // 非同期の処理を安全に起動する（例外は握りつぶさずログへ）
    void Fire(Func<Task> f) => _ = SafeRun(f);

    async Task SafeRun(Func<Task> f)
    {
        try { await f(); }
        catch (Exception e) { Console.Error.WriteLine("コマンド実行中のエラー: " + e); }
    }

    Action Toggle(string label, Func<bool> get, Action<bool> set) => () =>
    {
        set(!get());
        settingsStore.Save();
        ApplySettingEffects();
        Toast($"{label}: {(get() ? "ON" : "OFF")}");
        ScheduleState();
    };

    // ------------------------------------------------------------------
    // コマンド一覧（パレット・メニュー・ショートカットの共通定義）
    // ------------------------------------------------------------------
    void BuildCommands()
    {
        // タブ
        Cmd("tab.new", "新しいタブ", "Ctrl+T", () => CreateTab(NewtabUrl, new TabOpts { After = activeId }));
        Cmd("tab.close", "タブを閉じる", "Ctrl+W", () => CloseTab(activeId));
        Cmd("tab.restore", "閉じたタブを開く", "Ctrl+Shift+T", ReopenClosed);
        Cmd("tab.duplicate", "タブを複製", "", () => { var a = ActiveTab; if (a != null) CreateTab(a.Url, new TabOpts { After = a.Id }); });
        Cmd("tab.pin", "タブをピン留め / 解除", "", () => { var a = ActiveTab; if (a != null) { a.Pinned = !a.Pinned; ScheduleState(); SaveSessionSoon(); } });
        Cmd("tab.mute", "タブをミュート / 解除", "Ctrl+Shift+M", () => ToggleMute(ActiveTab));
        Cmd("tab.sleep", "このタブを休止（メモリ解放）", "", () => SleepTab(ActiveTab, true));
        Cmd("tab.sleepOthers", "他のタブをすべて休止", "", () =>
        {
            int n = tabs.ToArray().Count(t => SleepTab(t));
            Toast($"{n}個のタブを休止しました");
        });
        Cmd("tab.closeOthers", "他のタブを閉じる", "", () =>
        {
            foreach (var t in tabs.ToArray())
                if (t.Id != activeId && t.Ws == currentWs && !t.Pinned) CloseTab(t.Id);
        });
        Cmd("tab.next", "次のタブ", "Ctrl+Tab", () => CycleTab(1));
        Cmd("tab.prev", "前のタブ", "Ctrl+Shift+Tab", () => CycleTab(-1));

        // ナビゲーション
        Cmd("nav.back", "戻る", "Alt+←", () => ActiveTab?.Core?.GoBack());
        Cmd("nav.forward", "進む", "Alt+→", () => ActiveTab?.Core?.GoForward());
        Cmd("nav.reload", "再読み込み", "Ctrl+R", () => { var a = ActiveTab; if (a?.Core != null) { a.Reader = false; a.Core.Reload(); } });
        Cmd("nav.hardReload", "キャッシュを無視して再読み込み", "Ctrl+Shift+R", () => Fire(async () =>
        {
            var a = ActiveTab;
            if (a?.Core == null) return;
            a.Reader = false;
            await a.Core.CallDevToolsProtocolMethodAsync("Page.reload", "{\"ignoreCache\":true}");
        }));
        Cmd("nav.stop", "読み込みを停止", "", () => ActiveTab?.Core?.Stop());
        Cmd("nav.home", "ホーム（新しいタブページ）へ", "Alt+Home", () => Navigate(NewtabUrl));
        Cmd("focus.address", "アドレスバーに移動", "Ctrl+L", () => { FocusUI(); ToUI("k:focus-address"); });
        Cmd("bookmark.toggle", "このページをブックマーク / 解除", "Ctrl+D", ToggleBookmark);

        // 表示
        Cmd("view.palette", "コマンドパレットを開く", "Ctrl+K", TogglePalette);
        Cmd("view.find", "ページ内検索", "Ctrl+F", () => { FocusUI(); ToUI("k:find-open"); });
        Cmd("view.split", "分割表示 ON / OFF", "Alt+S", ToggleSplit);
        Cmd("view.layout", "タブの位置を切り替え（上 / 縦）", "Ctrl+Shift+L", () =>
        {
            S.TabLayout = S.TabLayout == "top" ? "side" : "top";
            settingsStore.Save();
            ScheduleState();
        });
        Cmd("view.bookmarksBar", "ブックマークバーの表示切替", "Ctrl+Shift+B", Toggle("ブックマークバー", () => S.ShowBookmarksBar, v => S.ShowBookmarksBar = v));
        Cmd("view.fullscreen", "全画面表示", "F11", ToggleFullscreen);
        Cmd("view.zoomIn", "拡大", "Ctrl+Plus", () => AdjustZoom(0.1));
        Cmd("view.zoomOut", "縮小", "Ctrl+Minus", () => AdjustZoom(-0.1));
        Cmd("view.zoomReset", "ズームをリセット", "Ctrl+0", () => AdjustZoom(0));

        // ツール
        Cmd("tools.reader", "リーダーモード", "F9", () => Fire(ToggleReader));
        Cmd("tools.dark", "このサイトを強制ダークモード", "Ctrl+Shift+D", () => Fire(ToggleDark));
        Cmd("tools.picker", "要素ブロッカー（クリックした要素を消す）", "Ctrl+Shift+X", () => Fire(RunPicker));
        Cmd("tools.unhide", "このサイトの非表示要素をすべて元に戻す", "", UnhideAll);
        Cmd("tools.pip", "ピクチャーインピクチャー", "Ctrl+Shift+P", () => Fire(TogglePip));
        Cmd("tools.shot", "スクリーンショット（表示範囲）", "Ctrl+Shift+S", () => Fire(ShotVisible));
        Cmd("tools.shotFull", "スクリーンショット（ページ全体）", "", () => Fire(ShotFull));
        Cmd("tools.copyClean", "トラッキングを除いたURLをコピー", "Ctrl+Shift+C", CopyCleanUrl);
        Cmd("tools.translate", "このページを日本語に翻訳", "", () => Fire(Translate));
        Cmd("tools.mobile", "モバイル表示 / デスクトップ表示", "", () => Fire(ToggleMobile));
        Cmd("tools.adblockSite", "このサイトの広告ブロックを ON / OFF", "", ToggleSiteAdblock);
        Cmd("tools.devtools", "デベロッパーツール", "F12", () => ActiveTab?.Core?.OpenDevToolsWindow());
        Cmd("tools.external", "このページを既定のブラウザで開く", "", () => { var a = ActiveTab; if (a != null && Util.HostOf(a.Url) != "") OpenExternal(a.Url); });

        // パネル
        Cmd("panel.history", "履歴", "Ctrl+H", () => OpenPanel("history"));
        Cmd("panel.downloads", "ダウンロード", "Ctrl+J", () => OpenPanel("downloads"));
        Cmd("panel.bookmarks", "ブックマーク", "Ctrl+Shift+O", () => OpenPanel("bookmarks"));
        Cmd("panel.notes", "クイックメモ", "Ctrl+Shift+N", () => OpenPanel("notes"));
        Cmd("panel.settings", "設定", "Ctrl+,", () => OpenPanel("settings"));
        Cmd("panel.help", "コマンド・ショートカット一覧", "F1", () => OpenPanel("help"));

        // 集中モード
        Cmd("focus.start25", "集中モード 25分", "", () => StartFocus(25));
        Cmd("focus.start50", "集中モード 50分", "", () => StartFocus(50));
        Cmd("focus.startCustom", "集中モード（時間を指定）", "Ctrl+Shift+F", () => Fire(StartFocusCustom));
        Cmd("focus.stop", "集中モードを終了", "", StopFocus);

        // ワークスペース
        Cmd("ws.new", "ワークスペースを作成", "", () => Fire(NewWs));
        Cmd("ws.rename", "ワークスペース名を変更", "", () => Fire(() => RenameWs()));
        Cmd("ws.delete", "ワークスペースを削除", "", () => Fire(() => DeleteWs()));

        // ウィンドウ
        Cmd("win.top", "ウィンドウを常に最前面にする ON / OFF", "", Toggle("常に最前面", () => S.AlwaysOnTop, v => S.AlwaysOnTop = v));
        foreach (var o in new[] { 100, 80, 60, 40 })
        {
            var op = o;
            Cmd($"win.opacity{op}", $"ウィンドウの透明度 {op}%", "", () => { S.Opacity = op; settingsStore.Save(); ApplyOpacityAndTop(); });
        }

        // 機能のON/OFF
        Cmd("set.adblock", "広告ブロック（全体）ON / OFF", "", Toggle("広告ブロック", () => S.Adblock, v => S.Adblock = v));
        Cmd("set.httpsUpgrade", "HTTPS自動アップグレード ON / OFF", "", Toggle("HTTPS自動アップグレード", () => S.HttpsUpgrade, v => S.HttpsUpgrade = v));
        Cmd("set.forceDark", "全サイト強制ダークモード ON / OFF", "", Toggle("全サイト強制ダーク", () => S.ForceDark, v => S.ForceDark = v));

        // データ・アプリ
        Cmd("data.clear", "閲覧データ（Cookie・キャッシュ）を削除", "", () => Fire(ClearBrowsingData));
        Cmd("data.clearHistory", "履歴をすべて削除", "", () => Fire(async () =>
        {
            if (!await AskConfirm("閲覧履歴をすべて削除します。")) return;
            History.Clear();
            historyStore.Save();
            RefreshPanel("history");
            Toast("履歴を削除しました");
        }));
        Cmd("app.shortcut", "デスクトップとスタートメニューにショートカットを作る", "", () => CreateShortcuts());
        Cmd("app.about", "Komorebi について", "", ShowAbout);
        Cmd("app.quit", "Komorebi を終了", "Ctrl+Shift+Q", Close);

        // ショートカット表を作る（"Ctrl+Shift+T" → "ctrl+shift+t"）
        foreach (var c in commands)
            if (!string.IsNullOrEmpty(c.Keys)) shortcuts[NormKeys(c.Keys)] = c.Id;
        shortcuts["ctrl+\\"] = "view.split";
        shortcuts["ctrl+shift+f"] = "focus.startCustom";
    }

    void ShowAbout()
    {
        var ver = typeof(MainForm).Assembly.GetName().Version;
        TaskDialog.ShowDialog(this, new TaskDialogPage
        {
            Caption = "Komorebi",
            Heading = $"Komorebi {ver?.ToString(3)}",
            Text = $"WebView2 (Chromium) {env?.BrowserVersionString}\n.NET {Environment.Version}",
            Buttons = { TaskDialogButton.OK },
        });
    }

    void ApplySettingEffects(string key = null)
    {
        ApplyOpacityAndTop();
        if (key == null || key == "forceDark")
            foreach (var t in tabs) if (t.View != null) _ = ApplyPageStyles(t);
        ScheduleState();
    }

    // 設定パネルからの変更（プロパティ名で反映する）
    void SetSetting(string key, JsonElement val)
    {
        if (key is "workspaces" or "currentWs" or "bounds") return;
        var prop = typeof(Settings).GetProperties()
            .FirstOrDefault(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));
        if (prop == null) return;
        object v = null;
        if (prop.PropertyType == typeof(bool)) v = val.ValueKind == JsonValueKind.True;
        else if (prop.PropertyType == typeof(int)) v = (int)val.GetDouble();
        else if (prop.PropertyType == typeof(string)) v = val.GetString();
        else if (prop.PropertyType == typeof(List<string>)) v = JsonSerializer.Deserialize<List<string>>(val.GetRawText());
        if (v == null) return;
        prop.SetValue(S, v);
        settingsStore.Save();
        ApplySettingEffects(key);
        if (key.Equals("sleepMinutes", StringComparison.OrdinalIgnoreCase))
            Toast((int)v > 0 ? $"{v}分使わないタブを自動で休止します" : "タブの自動休止をオフにしました");
    }

    internal void RunCommand(string id)
    {
        if (id.StartsWith("ws.switch:")) { SwitchWs(id[10..]); return; }
        var c = commands.FirstOrDefault(x => x.Id == id);
        if (c == null) return;
        try { c.Run(); }
        catch (Exception e) { Console.Error.WriteLine($"コマンド {id} でエラー: {e}"); }
    }

    // ------------------------------------------------------------------
    // ショートカット
    // ------------------------------------------------------------------
    static string NormKeys(string str)
    {
        var parts = str.ToLowerInvariant().Split('+').ToList();
        var key = parts[^1];
        parts.RemoveAt(parts.Count - 1);
        key = key switch { "←" => "left", "→" => "right", _ => key };
        var mods = new[] { "ctrl", "alt", "shift", "meta" }.Where(parts.Contains);
        return string.Join("+", mods.Append(key));
    }

    static string KeyName(Keys k)
    {
        if (k >= Keys.A && k <= Keys.Z) return ((char)('a' + (k - Keys.A))).ToString();
        if (k >= Keys.D0 && k <= Keys.D9) return ((char)('0' + (k - Keys.D0))).ToString();
        if (k >= Keys.NumPad0 && k <= Keys.NumPad9) return ((char)('0' + (k - Keys.NumPad0))).ToString();
        if (k >= Keys.F1 && k <= Keys.F24) return "f" + (k - Keys.F1 + 1);
        return k switch
        {
            Keys.Tab => "tab", Keys.Left => "left", Keys.Right => "right", Keys.Up => "up", Keys.Down => "down",
            Keys.Home => "home", Keys.Oemplus => "plus", Keys.Add => "plus", Keys.OemMinus => "minus",
            Keys.Subtract => "minus", Keys.Oemcomma => ",", Keys.Oem5 => "\\", Keys.Oem102 => "\\",
            Keys.Space => "space", _ => null,
        };
    }

    // どのWebView2にフォーカスがあってもショートカットが効くようにする
    void OnAccelerator(object sender, CoreWebView2AcceleratorKeyPressedEventArgs e)
    {
        if (e.KeyEventKind != CoreWebView2KeyEventKind.KeyDown && e.KeyEventKind != CoreWebView2KeyEventKind.SystemKeyDown) return;
        var handled = TryShortcut((Keys)e.VirtualKey, Control.ModifierKeys);
        if (handled) e.Handled = true;
        if (SelfTest && (Control.ModifierKeys & (Keys.Control | Keys.Alt)) != 0)
            testLog?.WriteLine($"  [accel] {(Keys)e.VirtualKey} mods={Control.ModifierKeys} handled={handled} from={(sender == chrome?.Ctl() ? "chrome" : sender == overlayView?.Ctl() ? "overlay" : "tab" + tabs.FirstOrDefault(t => t.View?.Ctl() == sender)?.Id)} active={activeId}");
    }

    // 割り当てがあるか（実行はしない）
    internal bool HasShortcut(Keys vk, Keys mods)
    {
        var key = KeyName(vk);
        if (key == null) return false;
        if (key.Length == 1 && char.IsDigit(key[0]) && key != "0" && (mods == Keys.Control || mods == Keys.Alt)) return true;
        return shortcuts.ContainsKey(ShortcutKey(key, mods));
    }

    static string ShortcutKey(string key, Keys mods)
    {
        var m = new List<string>();
        if (mods.HasFlag(Keys.Control)) m.Add("ctrl");
        if (mods.HasFlag(Keys.Alt)) m.Add("alt");
        if (mods.HasFlag(Keys.Shift) && key != "plus") m.Add("shift");
        return string.Join("+", m.Append(key));
    }

    // キーの組み合わせ → コマンド。割り当てがあれば実行を予約して true を返す（テストからも直接呼べる）
    internal bool TryShortcut(Keys vk, Keys mods)
    {
        var key = KeyName(vk);
        if (key == null) return false;
        var k = ShortcutKey(key, mods);
        var m = k.Split('+').SkipLast(1).ToList();

        if (key.Length == 1 && char.IsDigit(key[0]) && key != "0" && m.Count == 1 && (m[0] == "ctrl" || m[0] == "alt"))
        {
            var n = key[0] - '0';
            var ctrl = m[0] == "ctrl";
            BeginInvoke(() =>
            {
                if (ctrl) JumpTab(n);
                else if (n - 1 < S.Workspaces.Count) SwitchWs(S.Workspaces[n - 1].Id);
            });
            return true;
        }
        if (shortcuts.TryGetValue(k, out var id))
        {
            BeginInvoke(() => RunCommand(id));
            return true;
        }
        return false;
    }

    // ------------------------------------------------------------------
    // アドレスバーの候補
    // ------------------------------------------------------------------
    List<SuggestItem> omniItems = new();
    int omniSel = -1;
    string omniText = "";

    internal List<SuggestItem> BuildSuggestions(string text)
    {
        var q = text.Trim();
        var outList = new List<SuggestItem>();
        var seen = new HashSet<string>();
        void Push(SuggestItem it)
        {
            var key = it.Kind == "tab" ? "tab:" + it.TabId : it.Url;
            if (seen.Add(key)) outList.Add(it);
        }
        var eng = Search.Engine(S.SearchEngine);
        if (Util.LooksLikeUrl(q))
            Push(new SuggestItem { Kind = "url", Title = q, Sub = "このURLを開く", Url = Search.ToUrl(q, S.SearchEngine, NewtabUrl) });
        else
            Push(new SuggestItem
            {
                Kind = "search", Title = q, Url = Search.ToUrl(q, S.SearchEngine, NewtabUrl),
                Sub = Search.TryBang(q, out var bk, out _) ? $"「{bk}」ショートカットで検索" : $"{eng.Name} で検索",
            });

        var scored = new List<(double s, SuggestItem it)>();
        foreach (var t in tabs)
        {
            var s = Util.FuzzyAll(q, $"{t.Title} {t.Url}");
            if (s > 0) scored.Add((s + 50, new SuggestItem { Kind = "tab", Title = t.Title, Sub = "タブに切り替え", Url = t.Url, TabId = t.Id }));
        }
        foreach (var b in Bookmarks)
        {
            var s = Util.FuzzyAll(q, $"{b.Title} {b.Url}");
            if (s > 0) scored.Add((s + 20, new SuggestItem { Kind = "bookmark", Title = b.Title, Sub = b.Url, Url = b.Url }));
        }
        for (int i = History.Count - 1; i >= 0 && i > History.Count - 1500; i--)
        {
            var h = History[i];
            var s = Util.FuzzyAll(q, $"{h.Title} {h.Url}");
            if (s > 0) scored.Add((s, new SuggestItem { Kind = "history", Title = string.IsNullOrEmpty(h.Title) ? h.Url : h.Title, Sub = h.Url, Url = h.Url }));
        }
        foreach (var (_, it) in scored.OrderByDescending(x => x.s))
        {
            if (outList.Count >= 7) break;
            Push(it);
        }
        return outList;
    }

    void UpdateSuggest(string text, JsonElement rect)
    {
        omniText = text;
        if (string.IsNullOrWhiteSpace(text) || (overlayMode != null && overlayMode != "suggest"))
        {
            HideSuggest();
            return;
        }
        omniItems = BuildSuggestions(text);
        omniSel = -1;
        var sc = UiScale;
        suggestRect = new Rectangle(
            (int)Math.Round(rect.Num("left") * sc),
            (int)Math.Round((rect.Num("bottom") + 4) * sc),
            (int)Math.Round(rect.Num("width") * sc),
            (int)Math.Round(Math.Min(omniItems.Count * 46 + 12, 420) * sc));
        ShowOverlay("suggest", new { items = omniItems, sel = -1 }, false);
    }

    void PickSuggest(SuggestItem it)
    {
        HideSuggest();
        if (it.Kind == "tab") ActivateTab(it.TabId);
        else Navigate(it.Url);
        ToUI("k:omni-done");
    }

    // ------------------------------------------------------------------
    // コマンドパレットの検索
    // ------------------------------------------------------------------
    internal List<PaletteItem> PaletteQuery(string raw)
    {
        var q = (raw ?? "").Trim();
        var mode = "all";
        if (q.Length > 0)
        {
            var m = q[0] switch { '>' => "cmd", '@' => "tab", '*' => "bm", '?' => "hist", _ => null };
            if (m != null) { mode = m; q = q[1..].Trim(); }
        }
        bool Want(string x) => mode == "all" || mode == x;
        var items = new List<PaletteItem>();

        if (mode == "all" && Util.TryEval(q, out var calc))
            items.Add(new PaletteItem { S = 5000, Kind = "calc", Title = "= " + calc, Sub = "Enter で結果をコピー", Icon = "🧮", Value = calc });

        if (Want("tab"))
        {
            foreach (var t in tabs)
            {
                var ws = S.Workspaces.FirstOrDefault(w => w.Id == t.Ws);
                var s = q.Length > 0 ? Util.FuzzyAll(q, $"{t.Title} {t.Url}") : 1;
                if (s > 0) items.Add(new PaletteItem { S = s + 60, Kind = "tab", Title = t.Title, Sub = $"タブ · {ws?.Name} · {t.Url}", Icon = "🗂", TabId = t.Id });
            }
        }
        if (Want("cmd"))
        {
            foreach (var c in commands)
            {
                var s = q.Length > 0 ? Util.FuzzyAll(q, $"{c.Label} {c.Id}") : 1;
                if (s > 0) items.Add(new PaletteItem { S = q.Length > 0 ? s + 40 : 0.5, Kind = "cmd", Title = c.Label, Sub = string.IsNullOrEmpty(c.Keys) ? "コマンド" : c.Keys, Icon = "⌘", Id = c.Id });
            }
            foreach (var w in S.Workspaces)
            {
                var label = "ワークスペースを切り替え: " + w.Name;
                var s = q.Length > 0 ? Util.FuzzyAll(q, label) : 0;
                if (s > 0) items.Add(new PaletteItem { S = s + 40, Kind = "cmd", Title = label, Sub = "ワークスペース", Icon = "🎨", Id = "ws.switch:" + w.Id });
            }
        }
        if (q.Length > 0 && Want("bm"))
        {
            foreach (var b in Bookmarks)
            {
                var s = Util.FuzzyAll(q, $"{b.Title} {b.Url}");
                if (s > 0) items.Add(new PaletteItem { S = s + 30, Kind = "url", Title = b.Title, Sub = b.Url, Icon = "★", Url = b.Url });
            }
        }
        if (q.Length > 0 && Want("hist"))
        {
            for (int i = History.Count - 1; i >= 0 && i > History.Count - 2000; i--)
            {
                var h = History[i];
                var s = Util.FuzzyAll(q, $"{h.Title} {h.Url}");
                if (s > 0) items.Add(new PaletteItem { S = s, Kind = "url", Title = string.IsNullOrEmpty(h.Title) ? h.Url : h.Title, Sub = h.Url, Icon = "🕘", Url = h.Url });
            }
        }
        if (q.Length > 0 && mode == "all")
        {
            var isUrl = Util.LooksLikeUrl(q);
            items.Add(new PaletteItem
            {
                S = 1, Kind = "url", Icon = isUrl ? "🌐" : "🔎",
                Title = isUrl ? q : $"{Search.Engine(S.SearchEngine).Name} で「{q}」を検索",
                Sub = isUrl ? "このURLを開く" : "Web検索",
                Url = Search.ToUrl(q, S.SearchEngine, NewtabUrl),
            });
        }

        var seen = new HashSet<string>();
        var result = new List<PaletteItem>();
        foreach (var it in items.OrderByDescending(x => x.S))
        {
            var key = it.Kind == "tab" ? "tab" + it.TabId : it.Kind == "cmd" ? it.Id : it.Kind + (it.Url ?? it.Title);
            if (!seen.Add(key)) continue;
            result.Add(it);
            if (result.Count >= 40) break;
        }
        return result;
    }

    void RunPaletteItem(PaletteItem it, bool ctrl)
    {
        HideOverlay(false);
        switch (it.Kind)
        {
            case "cmd": RunCommand(it.Id); break;
            case "tab": ActivateTab(it.TabId); break;
            case "calc": try { Clipboard.SetText(it.Value); } catch { } Toast("計算結果をコピーしました"); break;
            case "url":
                if (ctrl) CreateTab(it.Url, new TabOpts { After = activeId });
                else Navigate(it.Url);
                break;
        }
        // パレットを閉じたらページへフォーカスを戻す（UIにフォーカスを渡すコマンドは除く）
        var keepUi = it.Kind == "cmd" && (it.Id == "focus.address" || it.Id == "view.find");
        if (overlayMode == null && !keepUi) FocusActive();
    }

    // ------------------------------------------------------------------
    // ネイティブメニュー（ダークテーマ）
    // ------------------------------------------------------------------
    sealed class DarkColors : ProfessionalColorTable
    {
        static readonly Color Back = Color.FromArgb(0x29, 0x2b, 0x2f);
        static readonly Color Hover = Color.FromArgb(0x42, 0x46, 0x4c);
        static readonly Color Line = Color.FromArgb(0x3a, 0x3e, 0x44);
        public override Color ToolStripDropDownBackground => Back;
        public override Color ImageMarginGradientBegin => Back;
        public override Color ImageMarginGradientMiddle => Back;
        public override Color ImageMarginGradientEnd => Back;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Line;
        public override Color CheckBackground => Hover;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Hover;
    }

    sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Color.FromArgb(0xe8, 0xea, 0xed) : Color.FromArgb(0x7f, 0x85, 0x8b);
            base.OnRenderItemText(e);
        }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Color.FromArgb(0xe8, 0xea, 0xed);
            base.OnRenderArrow(e);
        }
    }

    ContextMenuStrip NewMenu()
    {
        var m = new ContextMenuStrip
        {
            Renderer = new DarkRenderer(),
            ShowImageMargin = false,
            ShowCheckMargin = true,
            Font = new Font("Yu Gothic UI", 9.5f),
            BackColor = Color.FromArgb(0x29, 0x2b, 0x2f),
            ForeColor = Color.FromArgb(0xe8, 0xea, 0xed),
        };
        m.Closed += (_, _) => BeginInvoke(() => m.Dispose());
        return m;
    }

    static ToolStripMenuItem MI(string text, Action act, bool enabled = true, bool? check = null, string keys = null)
    {
        var it = new ToolStripMenuItem(text) { Enabled = enabled };
        if (check != null) { it.CheckOnClick = false; it.Checked = check.Value; }
        if (!string.IsNullOrEmpty(keys)) it.ShortcutKeyDisplayString = keys;
        if (act != null) it.Click += (_, _) => act();
        return it;
    }

    ToolStripMenuItem CmdItem(string id)
    {
        var c = commands.First(x => x.Id == id);
        return MI(c.Label, c.Run, true, null, c.Keys);
    }

    void ShowMenu(ContextMenuStrip m) => m.Show(Cursor.Position);

    void ShowMainMenu()
    {
        var m = NewMenu();
        m.Items.Add(CmdItem("tab.new"));
        m.Items.Add(CmdItem("tab.restore"));
        m.Items.Add(CmdItem("view.split"));
        m.Items.Add(new ToolStripSeparator());

        var ws = new ToolStripMenuItem("ワークスペース");
        foreach (var w in S.Workspaces) { var id = w.Id; ws.DropDownItems.Add(MI(w.Name, () => SwitchWs(id), true, id == currentWs)); }
        ws.DropDownItems.Add(new ToolStripSeparator());
        ws.DropDownItems.Add(CmdItem("ws.new"));
        ws.DropDownItems.Add(CmdItem("ws.rename"));
        ws.DropDownItems.Add(CmdItem("ws.delete"));
        m.Items.Add(ws);
        m.Items.Add(new ToolStripSeparator());

        foreach (var id in new[] { "panel.downloads", "panel.history", "panel.bookmarks", "panel.notes" }) m.Items.Add(CmdItem(id));
        m.Items.Add(new ToolStripSeparator());

        ToolStripMenuItem Sub(string title, params string[] ids)
        {
            var s = new ToolStripMenuItem(title);
            foreach (var id in ids)
            {
                if (id == "-") s.DropDownItems.Add(new ToolStripSeparator());
                else s.DropDownItems.Add(CmdItem(id));
            }
            return s;
        }
        m.Items.Add(Sub("ツール", "tools.reader", "tools.dark", "tools.picker", "tools.unhide", "tools.pip", "-",
            "tools.shot", "tools.shotFull", "tools.copyClean", "-", "tools.translate", "tools.mobile", "tools.external", "tools.devtools"));
        m.Items.Add(Sub("ズーム", "view.zoomIn", "view.zoomOut", "view.zoomReset"));
        m.Items.Add(Sub("集中モード", "focus.start25", "focus.start50", "focus.startCustom", "focus.stop"));

        var win = new ToolStripMenuItem("ウィンドウ");
        win.DropDownItems.Add(MI("常に最前面", () => RunCommand("win.top"), true, S.AlwaysOnTop));
        win.DropDownItems.Add(new ToolStripSeparator());
        foreach (var o in new[] { 100, 80, 60, 40 }) { var op = o; win.DropDownItems.Add(MI($"透明度 {op}%", () => RunCommand("win.opacity" + op), true, S.Opacity == op)); }
        win.DropDownItems.Add(new ToolStripSeparator());
        win.DropDownItems.Add(CmdItem("view.fullscreen"));
        m.Items.Add(win);
        m.Items.Add(new ToolStripSeparator());

        foreach (var id in new[] { "view.layout", "view.bookmarksBar", "panel.settings", "panel.help" }) m.Items.Add(CmdItem(id));
        m.Items.Add(new ToolStripSeparator());
        foreach (var id in new[] { "data.clear", "app.shortcut", "app.about", "app.quit" }) m.Items.Add(CmdItem(id));
        ShowMenu(m);
    }

    void ShowTabMenu(int id)
    {
        var t = GetTab(id);
        if (t == null) return;
        var m = NewMenu();
        m.Items.Add(MI("新しいタブ", () => CreateTab(NewtabUrl, new TabOpts { After = t.Id })));
        m.Items.Add(MI("タブを複製", () => CreateTab(t.Url, new TabOpts { After = t.Id })));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(MI(t.Pinned ? "ピン留めを解除" : "ピン留め", () => { t.Pinned = !t.Pinned; ScheduleState(); SaveSessionSoon(); }));
        m.Items.Add(MI(t.Muted ? "ミュート解除" : "ミュート", () => ToggleMute(t), t.View != null));
        m.Items.Add(MI("このタブを休止", () => SleepTab(t, true), t.View != null));
        m.Items.Add(MI("このタブと横に並べる（分割表示）", () => { split = new SplitInfo { A = activeId, B = t.Id }; Wake(t); ActivateTab(activeId); }, t.Id != activeId && ActiveTab != null));

        var others = S.Workspaces.Where(w => w.Id != t.Ws).ToList();
        if (others.Count > 0)
        {
            var mv = new ToolStripMenuItem("ワークスペースへ移動");
            foreach (var w in others)
            {
                var wid = w.Id;
                mv.DropDownItems.Add(MI(w.Name, () =>
                {
                    var wasActive = activeId == t.Id;
                    t.Ws = wid;
                    if (wasActive)
                    {
                        var rest = WsOrdered(currentWs);
                        if (rest.Count > 0) ActivateTab(rest[0].Id);
                        else CreateTab(NewtabUrl);
                    }
                    ScheduleState();
                    SaveSessionSoon();
                }));
            }
            m.Items.Add(mv);
        }
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(MI("他のタブを閉じる", () => { foreach (var x in tabs.ToArray()) if (x.Id != t.Id && x.Ws == t.Ws && !x.Pinned) CloseTab(x.Id); }));
        m.Items.Add(MI("タブを閉じる", () => CloseTab(t.Id)));
        ShowMenu(m);
    }

    void ShowShieldMenu()
    {
        var a = ActiveTab;
        if (a == null) return;
        var h = Util.HostOf(a.Url);
        var hidden = h != "" && Sites.Hidden.TryGetValue(h, out var l) ? l.Count : 0;
        var m = NewMenu();
        m.Items.Add(MI($"このページでブロックした広告・トラッカー: {a.Blocked}件", null, false));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(MI("このサイトで広告ブロック", ToggleSiteAdblock, h != "", !(h != "" && Sites.AdblockOff.Contains(h))));
        m.Items.Add(MI("広告ブロック（全サイト）", () => RunCommand("set.adblock"), true, S.Adblock));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(CmdItem("tools.picker"));
        m.Items.Add(MI($"非表示にした要素を元に戻す（{hidden}件）", UnhideAll, hidden > 0));
        ShowMenu(m);
    }

    void ShowWsMenu(string id)
    {
        var m = NewMenu();
        m.Items.Add(MI("名前を変更", () => Fire(() => RenameWs(id))));
        m.Items.Add(MI("削除", () => Fire(() => DeleteWs(id))));
        ShowMenu(m);
    }

    void ShowBookmarkMenu(string url)
    {
        var m = NewMenu();
        m.Items.Add(MI("新しいタブで開く", () => CreateTab(url, new TabOpts { After = activeId })));
        m.Items.Add(MI("横に並べて開く（分割表示）", () => OpenInSplit(url)));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(MI("ブックマークを削除", () =>
        {
            var i = Bookmarks.FindIndex(b => b.Url == url);
            if (i >= 0) { Bookmarks.RemoveAt(i); bookmarksStore.Save(); RefreshPanel("bookmarks"); ScheduleState(); }
        }));
        ShowMenu(m);
    }
}
