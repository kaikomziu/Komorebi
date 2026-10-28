using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Komorebi;

internal class Tab
{
    public int Id;
    public string Ws;
    public string Url;
    public string Title = "新しいタブ";
    public string Favicon;
    public WebView2 View;                 // null なら休止中
    public CoreWebView2 Core => View?.CoreWebView2;
    public bool Pinned, Muted, Audible, Loading, CanGoBack, CanGoForward, Mobile, Reader, DarkApplied;
    public bool ApplyingZoom;
    public bool Hidden;                   // ダウンロード完了待ちの空タブ。一覧・セッションには出さない
    public int Blocked;
    public long LastActive;
    public string LastNavUri;             // 直近にナビゲーションを始めたURL（失敗時のHTTPS→HTTP戻しに使う）
    public TaskCompletionSource<bool> Ready = new();
}

internal class SplitInfo
{
    public int A, B;
}

internal class TabOpts
{
    public string Ws, Title, Favicon;
    public bool Pinned, Sleeping, Popup;
    public bool Activate = true;
    public int After;
}

public partial class MainForm
{
    internal readonly List<Tab> tabs = new();
    internal int activeId;
    int tabSeq;
    internal SplitInfo split;
    internal string currentWs = "w1";
    readonly List<(string url, string title, string ws, bool pinned)> closedStack = new();
    Tab fullscreenTab;

    internal long focusUntil;
    System.Windows.Forms.Timer focusTimer;

    internal Tab GetTab(int id) => tabs.FirstOrDefault(t => t.Id == id);
    internal Tab ActiveTab => GetTab(activeId);

    // 現在のワークスペースのタブ（ピン留めを先頭に並べた表示順）
    internal List<Tab> WsOrdered(string ws = null)
    {
        ws ??= currentWs;
        var l = tabs.Where(t => t.Ws == ws && !t.Hidden).ToList();
        return l.Where(t => t.Pinned).Concat(l.Where(t => !t.Pinned)).ToList();
    }

    string ValidWs(string id)
        => S.Workspaces.Any(w => w.Id == id) ? id : S.Workspaces[0].Id;

    // ------------------------------------------------------------------
    // タブの作成・破棄
    // ------------------------------------------------------------------
    internal Tab CreateTab(string url, TabOpts o = null)
    {
        o ??= new TabOpts();
        var t = new Tab
        {
            Id = ++tabSeq,
            Ws = ValidWs(o.Ws ?? currentWs),
            Url = url ?? NewtabUrl,
            Title = o.Title ?? "新しいタブ",
            Favicon = o.Favicon,
            Pinned = o.Pinned,
            LastActive = Util.Now(),
        };
        var afterIdx = o.After != 0 ? tabs.FindIndex(x => x.Id == o.After) : -1;
        if (afterIdx >= 0) tabs.Insert(afterIdx + 1, t);
        else tabs.Add(t);

        if (!o.Sleeping) Wake(t, o.Popup);
        if (o.Activate) ActivateTab(t.Id);
        ScheduleState();
        SaveSessionSoon();
        return t;
    }

    // 休止中のタブに WebView2 を与えて読み込む
    internal void Wake(Tab t, bool popup = false)
    {
        if (t.View != null) return;
        var v = NewHost();
        v.Visible = false;
        t.View = v;
        t.Ready = new TaskCompletionSource<bool>();
        Controls.Add(v);
        v.BringToFront();
        overlayView?.BringToFront();
        _ = InitTabAsync(t, popup);
    }

    async Task InitTabAsync(Tab t, bool popup)
    {
        var v = t.View;
        try
        {
            await v.EnsureCoreWebView2Async(env);
            if (t.View != v) return; // 初期化中に破棄された
            await SetupTabCore(t);
            t.Ready.TrySetResult(true);
            if (!popup) t.Core.Navigate(t.Url);
            Relayout();
            // 準備できた時点でこのタブがアクティブなら、そのまま入力できるようフォーカスを移す
            // （ウィンドウ内の移動だけなので、ウィンドウが前面でなくても問題ない）
            if (t.Id == activeId) FocusActive();
        }
        catch (Exception ex)
        {
            t.Ready.TrySetException(ex);
            Console.Error.WriteLine("タブの初期化に失敗: " + ex.Message);
        }
    }

    void DestroyView(Tab t)
    {
        if (t.View == null) return;
        var v = t.View;
        t.View = null;
        t.Loading = false;
        t.Audible = false;
        try
        {
            Controls.Remove(v);
            v.Dispose();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("ビュー破棄に失敗: " + e.Message);
        }
    }

    internal void ActivateTab(int id)
    {
        var t = GetTab(id);
        if (t == null) return;
        if (t.Ws != currentWs)
        {
            currentWs = t.Ws;
            S.CurrentWs = currentWs;
            settingsStore.Save();
        }
        if (split != null && split.A != id && split.B != id) split = null;
        activeId = id;
        t.LastActive = Util.Now();
        Wake(t);
        if (split != null)
        {
            var o = GetTab(split.A == id ? split.B : split.A);
            if (o != null) Wake(o);
        }
        ApplyVisibility();
        Relayout();
        if (overlayMode == null) FocusActive();
        ScheduleState();
        SaveSessionSoon();
    }

    internal void CloseTab(int id, bool noRestore = false)
    {
        var t = GetTab(id);
        if (t == null) return;
        var ordered = WsOrdered(t.Ws);
        var oi = ordered.IndexOf(t);
        if (!noRestore && Util.IsHttp(t.Url))
        {
            closedStack.Add((t.Url, t.Title, t.Ws, t.Pinned));
            if (closedStack.Count > 25) closedStack.RemoveAt(0);
        }
        if (fullscreenTab == t) fullscreenTab = null;
        DestroyView(t);
        tabs.Remove(t);
        if (split != null && (split.A == id || split.B == id)) split = null;

        if (activeId == id)
        {
            var rest = WsOrdered(t.Ws);
            if (rest.Count > 0) ActivateTab(rest[Math.Min(oi, rest.Count - 1)].Id);
            else
            {
                activeId = 0;
                CreateTab(NewtabUrl, new TabOpts { Ws = t.Ws });
            }
        }
        ScheduleState();
        SaveSessionSoon();
    }

    internal void ReopenClosed()
    {
        if (closedStack.Count == 0) { Toast("閉じたタブはありません"); return; }
        var c = closedStack[^1];
        closedStack.RemoveAt(closedStack.Count - 1);
        CreateTab(c.url, new TabOpts { Ws = c.ws, Pinned = c.pinned, Title = c.title, After = activeId });
    }

    internal bool SleepTab(Tab t, bool force = false)
    {
        if (t == null || t.View == null) return false;
        var inSplit = split != null && (split.A == t.Id || split.B == t.Id);
        if (!force && (t.Id == activeId || inSplit)) return false;
        if (t.Id == activeId)
        {
            var o = WsOrdered().FirstOrDefault(x => x.Id != t.Id);
            if (o == null) return false;
            ActivateTab(o.Id);
        }
        DestroyView(t);
        ScheduleState();
        return true;
    }

    internal void CycleTab(int d)
    {
        var l = WsOrdered();
        if (l.Count < 2) return;
        var i = l.FindIndex(t => t.Id == activeId);
        ActivateTab(l[(i + d + l.Count) % l.Count].Id);
    }

    internal void JumpTab(int n)
    {
        var l = WsOrdered();
        if (l.Count == 0) return;
        ActivateTab((n >= 9 ? l[^1] : (n - 1 < l.Count ? l[n - 1] : l[^1])).Id);
    }

    internal void NavigateTab(Tab tab, string url)
    {
        tab.Url = url;
        tab.Reader = false;
        if (tab.View == null) Wake(tab);
        else tab.Core?.Navigate(url); // 初期化前なら InitTabAsync が tab.Url へ移動する
        ScheduleState();
    }

    internal void Navigate(string url)
    {
        var t = ActiveTab;
        if (t != null) NavigateTab(t, url);
        else CreateTab(url);
    }

    // ------------------------------------------------------------------
    // 分割表示
    // ------------------------------------------------------------------
    internal void ToggleSplit()
    {
        if (split != null)
        {
            split = null;
            ApplyVisibility();
            Relayout();
            ScheduleState();
            Toast("分割表示を終了しました");
            return;
        }
        var a = ActiveTab;
        if (a == null) return;
        var other = WsOrdered().Where(t => t.Id != a.Id).OrderByDescending(t => t.LastActive).FirstOrDefault()
                    ?? CreateTab(NewtabUrl, new TabOpts { After = a.Id, Activate = false });
        split = new SplitInfo { A = a.Id, B = other.Id };
        Wake(other);
        ActivateTab(a.Id);
        Toast("分割表示（クリックした側が操作対象になります）");
    }

    internal void OpenInSplit(string url)
    {
        var a = ActiveTab;
        if (a == null) return;
        var t = CreateTab(url, new TabOpts { After = a.Id, Activate = false });
        split = new SplitInfo { A = a.Id, B = t.Id };
        ActivateTab(a.Id);
    }

    // ------------------------------------------------------------------
    // ワークスペース
    // ------------------------------------------------------------------
    internal void SwitchWs(string id)
    {
        if (id == currentWs || !S.Workspaces.Any(w => w.Id == id)) return;
        currentWs = id;
        S.CurrentWs = id;
        settingsStore.Save();
        split = null;
        var cand = tabs.Where(t => t.Ws == id).OrderByDescending(t => t.LastActive).FirstOrDefault();
        if (cand != null) ActivateTab(cand.Id);
        else CreateTab(NewtabUrl);
        Toast("ワークスペース: " + S.Workspaces.First(x => x.Id == id).Name);
    }

    internal async Task NewWs()
    {
        var name = await AskPrompt("新しいワークスペース", "名前（例: 仕事 / 勉強 / 趣味）", "", "作成");
        if (string.IsNullOrWhiteSpace(name)) return;
        var ws = new Workspace
        {
            Id = "w" + Util.Now(),
            Name = name.Trim()[..Math.Min(12, name.Trim().Length)],
            Color = WsColors[S.Workspaces.Count % WsColors.Length],
        };
        S.Workspaces.Add(ws);
        settingsStore.Save();
        SwitchWs(ws.Id);
    }

    internal async Task RenameWs(string id = null)
    {
        id ??= currentWs;
        var w = S.Workspaces.FirstOrDefault(x => x.Id == id);
        if (w == null) return;
        var name = await AskPrompt("ワークスペース名を変更", "新しい名前", w.Name, "変更");
        if (string.IsNullOrWhiteSpace(name)) return;
        w.Name = name.Trim()[..Math.Min(12, name.Trim().Length)];
        settingsStore.Save();
        ScheduleState();
    }

    internal async Task DeleteWs(string id = null)
    {
        id ??= currentWs;
        if (S.Workspaces.Count <= 1) { Toast("最後のワークスペースは削除できません"); return; }
        var w = S.Workspaces.FirstOrDefault(x => x.Id == id);
        if (w == null) return;
        if (!await AskConfirm($"「{w.Name}」を削除します。\nタブは別のワークスペースに移動します。")) return;
        var dest = S.Workspaces.First(x => x.Id != id).Id;
        foreach (var t in tabs.Where(t => t.Ws == id)) t.Ws = dest;
        S.Workspaces.Remove(w);
        settingsStore.Save();
        if (currentWs == id) SwitchWs(dest);
        ScheduleState();
    }

    // ------------------------------------------------------------------
    // 履歴・ブックマーク
    // ------------------------------------------------------------------
    internal void RecordHistory(Tab t)
    {
        if (!Util.IsHttp(t.Url)) return;
        var last = History.Count > 0 ? History[^1] : null;
        if (last != null && last.Url == t.Url) return;
        History.Add(new HistoryItem { Url = t.Url, Title = t.Title, Ts = Util.Now() });
        if (History.Count > 5000) History.RemoveRange(0, History.Count - 5000);
        historyStore.Save();
        RefreshPanel("history");
    }

    internal bool IsBookmarked(string url) => Bookmarks.Any(b => b.Url == url);

    internal void ToggleBookmark()
    {
        var a = ActiveTab;
        if (a == null || !Util.IsHttp(a.Url)) { Toast("このページはブックマークできません"); return; }
        var i = Bookmarks.FindIndex(b => b.Url == a.Url);
        if (i >= 0) { Bookmarks.RemoveAt(i); Toast("ブックマークを解除しました"); }
        else
        {
            Bookmarks.Insert(0, new Bookmark { Url = a.Url, Title = string.IsNullOrEmpty(a.Title) ? a.Url : a.Title, Favicon = a.Favicon });
            Toast("ブックマークに追加しました");
        }
        bookmarksStore.Save();
        RefreshPanel("bookmarks");
        ScheduleState();
    }

    // ------------------------------------------------------------------
    // セッション保存・復元
    // ------------------------------------------------------------------
    System.Windows.Forms.Timer sessTimer;

    internal void SaveSessionSoon()
    {
        if (sessionStore == null) return;
        sessTimer ??= new System.Windows.Forms.Timer { Interval = 1500 };
        sessTimer.Stop();
        sessTimer.Tick -= SessTick;
        sessTimer.Tick += SessTick;
        sessTimer.Start();
    }

    void SessTick(object s, EventArgs e)
    {
        sessTimer.Stop();
        SaveSession();
    }

    internal void SaveSession()
    {
        if (sessionStore == null) return;
        var saved = tabs.Where(t => !t.Hidden).ToList();
        sessionStore.Data.Tabs = saved.Select(t => new SessionTab
        {
            Url = t.Url, Title = t.Title, Favicon = t.Favicon, Ws = t.Ws, Pinned = t.Pinned,
        }).ToList();
        sessionStore.Data.Active = saved.FindIndex(t => t.Id == activeId);
        sessionStore.Save();
    }

    void RestoreOrOpenFirst()
    {
        var s = sessionStore.Data;
        if (S.RestoreSession && s.Tabs != null && s.Tabs.Count > 0)
        {
            foreach (var t in s.Tabs)
                CreateTab(t.Url, new TabOpts { Ws = t.Ws, Title = t.Title, Favicon = t.Favicon, Pinned = t.Pinned, Sleeping = true, Activate = false });
            var idx = s.Active >= 0 && s.Active < tabs.Count ? s.Active : 0;
            var want = tabs[idx];
            // 前回のワークスペースを優先（なければ復元したタブのワークスペース）
            var inCur = tabs.FirstOrDefault(t => t.Ws == currentWs);
            ActivateTab((inCur != null && want.Ws != currentWs ? inCur : want).Id);
        }
        else
        {
            CreateTab(NewtabUrl);
        }
    }

    // ------------------------------------------------------------------
    // サイトごとの見た目（ズーム・ダーク・非表示要素）
    // ------------------------------------------------------------------
    internal bool IsDark(Tab t)
    {
        var h = Util.HostOf(t.Url);
        if (h == "") return false;
        return Sites.Dark.TryGetValue(h, out var d) ? d : S.ForceDark;
    }

    internal async Task ApplyPageStyles(Tab t)
    {
        var core = t.Core;
        var ctl = t.View?.Ctl();
        var host = Util.HostOf(t.Url);
        if (core == null || ctl == null || host == "") return;
        try
        {
            t.ApplyingZoom = true;
            ctl.ZoomFactor = Sites.Zoom.TryGetValue(host, out var z) ? z : 1.0;
            t.DarkApplied = false; // ページ遷移でCSSは消えているので再適用する
            if (!t.Reader && IsDark(t))
            {
                await Eval(core, PageScripts.InsertCss(PageScripts.DarkCss, "dark"));
                t.DarkApplied = true;
            }
            if (Sites.Hidden.TryGetValue(host, out var rules) && rules.Count > 0)
                await Eval(core, PageScripts.InsertCss(string.Join("\n", rules.Select(s => s + "{display:none!important}")), "hidden"));
        }
        catch { /* ページ遷移中などは無視 */ }
        finally { t.ApplyingZoom = false; }
    }

    internal async Task ToggleDark()
    {
        var a = ActiveTab;
        var core = a?.Core;
        var h = a != null ? Util.HostOf(a.Url) : "";
        if (core == null || h == "") { Toast("このページでは使えません"); return; }
        var cur = IsDark(a);
        Sites.Dark[h] = !cur;
        sitesStore.Save();
        if (cur)
        {
            await Eval(core, PageScripts.RemoveCss("dark"));
            a.DarkApplied = false;
        }
        else
        {
            await Eval(core, PageScripts.InsertCss(PageScripts.DarkCss, "dark"));
            a.DarkApplied = true;
        }
        Toast($"{h}: ダークモード {(cur ? "OFF" : "ON")}");
        ScheduleState();
    }

    internal void AdjustZoom(double delta)
    {
        var a = ActiveTab;
        var ctl = a?.View?.Ctl();
        if (ctl == null) return;
        var z = delta == 0 ? 1.0 : Math.Round(Math.Clamp(ctl.ZoomFactor + delta, 0.25, 5), 2);
        ctl.ZoomFactor = z;
        PersistZoom(a, z); // コマンド操作はここで確実に保存する（Ctrl+ホイールは ZoomFactorChanged で拾う）
        var h = Util.HostOf(a.Url);
        Toast($"ズーム {Math.Round(z * 100)}%{(h != "" ? "（サイトごとに記憶）" : "")}");
    }

    void PersistZoom(Tab t, double z)
    {
        var host = Util.HostOf(t.Url);
        if (host == "") return;
        z = Math.Round(z, 2);
        if (Math.Abs(z - 1) < 0.001) Sites.Zoom.Remove(host);
        else Sites.Zoom[host] = z;
        sitesStore.Save();
    }

    void OnZoomChanged(Tab t)
    {
        if (SelfTest) testLog?.WriteLine($"  [zoom event] {t.Id} ApplyingZoom={t.ApplyingZoom}");
        if (t.ApplyingZoom || t.View == null) return;
        PersistZoom(t, t.View.Ctl().ZoomFactor);
    }

    // ------------------------------------------------------------------
    // 集中モード
    // ------------------------------------------------------------------
    internal bool IsFocusBlocked(string url)
    {
        if (focusUntil == 0 || Util.Now() > focusUntil) return false;
        var h = Util.HostOf(url);
        return h != "" && S.FocusSites.Any(s => h == s || h.EndsWith("." + s));
    }

    string FocusPageUrl(string url) => $"{FocusUrl}?host={Uri.EscapeDataString(Util.HostOf(url))}&until={focusUntil}";

    internal void StartFocus(int min)
    {
        focusUntil = Util.Now() + min * 60000L;
        focusTimer?.Stop();
        focusTimer = new System.Windows.Forms.Timer { Interval = Math.Max(1000, min * 60000) };
        focusTimer.Tick += (_, _) => EndFocus();
        focusTimer.Start();
        Toast($"集中モード開始（{min}分）: 気が散るサイトをブロックします");
        ScheduleState();
    }

    internal void StopFocus()
    {
        if (focusUntil == 0) return;
        focusUntil = 0;
        focusTimer?.Stop();
        Toast("集中モードを終了しました");
        ScheduleState();
    }

    void EndFocus()
    {
        focusUntil = 0;
        focusTimer?.Stop();
        System.Media.SystemSounds.Asterisk.Play();
        Toast("集中モードが終わりました。おつかれさまでした！");
        ScheduleState();
    }

    internal async Task StartFocusCustom()
    {
        var v = await AskPrompt("集中モード", "何分間？（1〜480）", "25", "開始");
        if (int.TryParse(v, out var n) && n >= 1 && n <= 480) StartFocus(n);
    }
}
