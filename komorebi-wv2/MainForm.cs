using System.Drawing.Drawing2D;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Komorebi;

// ブラウザ本体のウィンドウ。
// 構造（奥 → 手前）:  外枠UI(WebView2) → 各タブ(WebView2) → オーバーレイ(WebView2)
// WinForms の子HWNDは重ね順どおりに描画されるので、外枠の上にタブを載せ、その上に候補やパレットを出せる。
public partial class MainForm : Form
{
    public const string UiHost = "komorebi.app";
    public const string UiBase = "https://komorebi.app/";
    public static readonly string NewtabUrl = UiBase + "newtab.html";
    public static readonly string FocusUrl = UiBase + "focus.html";

    static readonly Color Bg = Color.FromArgb(0x20, 0x21, 0x24);
    static readonly string[] WsColors = { "#8ab4f8", "#f28b82", "#81c995", "#fdd663", "#c58af9", "#ff8bcb", "#78d9ec", "#fcad70" };

    internal readonly bool SelfTest;
    internal readonly string SelfTestScript;
    internal readonly bool DevMode;
    readonly string appDir, dataDir, wvDir, uiRoot;

    // ---- 保存データ ----
    internal Store<Settings> settingsStore;
    internal Store<SiteData> sitesStore;
    internal Store<List<Bookmark>> bookmarksStore;
    internal Store<List<HistoryItem>> historyStore;
    internal Store<List<Note>> notesStore;
    internal Store<SessionData> sessionStore;
    internal Settings S => settingsStore.Data;
    internal SiteData Sites => sitesStore.Data;
    internal List<Bookmark> Bookmarks => bookmarksStore.Data;
    internal List<HistoryItem> History => historyStore.Data;
    internal List<Note> Notes => notesStore.Data;

    // ---- WebView2 ----
    internal CoreWebView2Environment env;
    internal WebView2 chrome;       // 外枠UI（タブバー・ツールバー等）
    internal WebView2 overlayView;  // コマンドパレット・候補・パネル・ダイアログ
    bool uiReady, overlayReady;
    readonly List<(string c, object p)> overlayQueue = new();

    // ---- レイアウト（外枠UIが占める領域。CSS px）----
    double uiTop = 84, uiLeft = 0;
    internal double UiScale => DeviceDpi / 96.0;

    // ---- オーバーレイ ----
    internal string overlayMode; // null | palette | panel | suggest | prompt
    string panelName = "history";
    Rectangle suggestRect;
    int paletteHeight = 120;
    DateTime overlayShownAt = DateTime.MinValue;
    TaskCompletionSource<string> pendingPrompt;
    int promptId, promptSeq;

    public MainForm(bool selfTest, string selfTestScript, bool dev)
    {
        SelfTest = selfTest;
        SelfTestScript = selfTestScript;
        DevMode = dev;
        appDir = AppContext.BaseDirectory;
        if (selfTest)
        {
            // セルフテストは使い捨ての一時プロファイルで動かし、普段のデータには触れない
            var tmp = Path.Combine(Path.GetTempPath(), "komorebi-wv2-selftest");
            if (Environment.GetEnvironmentVariable("KMR_KEEP") != "1" && Directory.Exists(tmp)) TryDelete(tmp);
            dataDir = Path.Combine(tmp, "data");
            wvDir = Path.Combine(tmp, "webview2");
            uiRoot = Path.Combine(tmp, "ui");
        }
        else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KMR_DATA_DIR")))
        {
            // 開発用: 保存先を差し替える（通常起動の経路を、普段のデータに触れずに確かめるため）
            var root = Environment.GetEnvironmentVariable("KMR_DATA_DIR");
            dataDir = Path.Combine(root, "data");
            wvDir = Path.Combine(root, "webview2");
            uiRoot = Path.Combine(root, "ui");
        }
        else
        {
            var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KomorebiWV2");
            dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KomorebiWV2");
            wvDir = Path.Combine(local, "WebView2");
            uiRoot = Path.Combine(local, "ui");
        }

        Text = "Komorebi";
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath); } catch { /* アイコンが取れなくても起動は続ける */ }
        BackColor = Bg;
        ClientSize = new Size(1360, 820);
        MinimumSize = new Size(640, 440);
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;

        BuildCommands();
        Load += async (_, _) => await InitAsync();
        FormClosing += OnFormClosing;
        SizeChanged += (_, _) => { Relayout(); SaveBoundsSoon(); };
        LocationChanged += (_, _) => SaveBoundsSoon();
        DpiChanged += (_, _) => BeginInvoke(Relayout);
        Activated += (_, _) => { if (overlayMode == null) FocusActive(); };
    }

    static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, true); } catch { /* 使用中なら残す */ }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.DarkTitleBar(Handle, Bg);
    }

    // ------------------------------------------------------------------
    // 起動
    // ------------------------------------------------------------------
    async Task InitAsync()
    {
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(uiRoot);
        uiDir = EnsureUiExtracted();
        MigrateOldData();
        settingsStore = new Store<Settings>(Path.Combine(dataDir, "settings.json"), () => new Settings());
        sitesStore = new Store<SiteData>(Path.Combine(dataDir, "sites.json"), () => new SiteData());
        bookmarksStore = new Store<List<Bookmark>>(Path.Combine(dataDir, "bookmarks.json"), () => new List<Bookmark>
        {
            new() { Url = "https://www.google.com", Title = "Google" },
            new() { Url = "https://github.com", Title = "GitHub" },
            new() { Url = "https://www.youtube.com", Title = "YouTube" },
            new() { Url = "https://ja.wikipedia.org", Title = "Wikipedia" },
        });
        historyStore = new Store<List<HistoryItem>>(Path.Combine(dataDir, "history.json"), () => new List<HistoryItem>());
        notesStore = new Store<List<Note>>(Path.Combine(dataDir, "notes.json"), () => new List<Note>());
        sessionStore = new Store<SessionData>(Path.Combine(dataDir, "session.json"), () => new SessionData());
        if (S.Workspaces == null || S.Workspaces.Count == 0)
            S.Workspaces = new List<Workspace> { new() { Id = "w1", Name = "メイン", Color = WsColors[0] } };
        currentWs = ValidWs(S.CurrentWs);
        ApplySavedBounds();

        try
        {
            env = await CoreWebView2Environment.CreateAsync(null, wvDir, new CoreWebView2EnvironmentOptions { Language = "ja" });
        }
        catch (WebView2RuntimeNotFoundException)
        {
            // WebView2 ランタイム（Edge の Chromium）が無い環境。Windows 11 には最初から入っている
            MessageBox.Show(this,
                "Komorebi の動作には「Microsoft Edge WebView2 ランタイム」が必要です。\n\n" +
                "次のページからインストールしてください:\nhttps://developer.microsoft.com/microsoft-edge/webview2/",
                "Komorebi", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
            return;
        }
        SetupBrowserIdentity();

        chrome = NewHost();
        overlayView = NewHost();
        Controls.Add(chrome);
        Controls.Add(overlayView);
        chrome.SendToBack();
        overlayView.Visible = false;

        await chrome.EnsureCoreWebView2Async(env);
        SetupUiCore(chrome, false);
        chrome.CoreWebView2.Navigate(UiBase + "index.html");

        await overlayView.EnsureCoreWebView2Async(env);
        SetupUiCore(overlayView, true);
        overlayView.CoreWebView2.Navigate(UiBase + "overlay.html");

        ApplyOpacityAndTop();
        Relayout();

        // 休止タイマー: 一定時間使っていないタブのメモリを解放する
        var sleepTimer = new System.Windows.Forms.Timer { Interval = 30000 };
        sleepTimer.Tick += (_, _) =>
        {
            var min = S.SleepMinutes;
            if (min <= 0) return;
            var limit = Util.Now() - min * 60000L;
            foreach (var t in tabs.ToArray())
                if (t.View != null && t.LastActive < limit && !t.Audible && !t.Loading) SleepTab(t);
        };
        sleepTimer.Start();

        StartPipeServer();
        if (SelfTest) _ = RunSelfTestAsync();
    }

    // 旧Electron版のデータ（設定・ブックマーク・履歴・メモ）を、初回だけ引き継ぐ
    void MigrateOldData()
    {
        if (SelfTest) return;
        try
        {
            var old = Environment.GetEnvironmentVariable("KMR_OLD_DIR")
                      ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "komorebi");
            if (!Directory.Exists(old) || File.Exists(Path.Combine(dataDir, "settings.json"))) return;
            foreach (var f in new[] { "settings.json", "sites.json", "bookmarks.json", "history.json", "notes.json" })
            {
                var src = Path.Combine(old, f);
                if (File.Exists(src)) File.Copy(src, Path.Combine(dataDir, f), false);
            }
        }
        catch { /* 引き継げなくても起動は続ける */ }
    }

    WebView2 NewHost() => new WebView2 { DefaultBackgroundColor = Bg, TabStop = false };

    // 外枠UI・オーバーレイ共通の設定
    void SetupUiCore(WebView2 view, bool isOverlay)
    {
        var core = view.CoreWebView2;
        var st = core.Settings;
        st.AreDefaultContextMenusEnabled = false;
        st.AreDevToolsEnabled = DevMode;
        st.IsZoomControlEnabled = false;
        st.AreBrowserAcceleratorKeysEnabled = false;
        st.IsStatusBarEnabled = false;
        st.IsSwipeNavigationEnabled = false;
        st.IsPinchZoomEnabled = false;
        st.IsGeneralAutofillEnabled = false;
        MapUiHost(core);
        core.WebMessageReceived += (_, e) => OnUiMessage(view, e);
        core.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith(UiBase)) e.Cancel = true; };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        view.Ctl().AcceleratorKeyPressed += OnAccelerator;
        if (isOverlay) view.Ctl().LostFocus += (_, _) => OnOverlayLostFocus();
        if (DevMode && !isOverlay) core.OpenDevToolsWindow();
    }

    // https://komorebi.app/ → 展開した ui フォルダ（外枠・新しいタブ・集中モード画面を配信する）
    void MapUiHost(CoreWebView2 core)
        => core.SetVirtualHostNameToFolderMapping(UiHost, uiDir, CoreWebView2HostResourceAccessKind.Allow);

    string uiDir;

    // exe に埋め込んだ UI ファイルを、ビルドごとのフォルダに展開する（展開済みなら何もしない）。
    // 古いビルドの展開先は掃除する（他のインスタンスが使用中なら消せないので無視）。
    string EnsureUiExtracted()
    {
        var asm = typeof(MainForm).Assembly;
        var stamp = asm.ManifestModule.ModuleVersionId.ToString("N")[..12];
        var dir = Path.Combine(uiRoot, stamp);
        if (!File.Exists(Path.Combine(dir, ".ok")))
        {
            Directory.CreateDirectory(dir);
            foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("ui/")))
            {
                using var src = asm.GetManifestResourceStream(name)!;
                using var dst = File.Create(Path.Combine(dir, name[3..]));
                src.CopyTo(dst);
            }
            File.WriteAllText(Path.Combine(dir, ".ok"), "");
            foreach (var old in Directory.GetDirectories(uiRoot))
                if (old != dir) TryDelete(old);
        }
        return dir;
    }

    // ------------------------------------------------------------------
    // ウィンドウの状態保存
    // ------------------------------------------------------------------
    System.Windows.Forms.Timer boundsTimer;

    void ApplySavedBounds()
    {
        var b = S.Bounds;
        if (b == null || b.Width < 400 || b.Height < 300) return;
        var r = new Rectangle(b.X, b.Y, b.Width, b.Height);
        // 画面外に出ていたら無視する
        if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(r))) return;
        StartPosition = FormStartPosition.Manual;
        Bounds = r;
        if (b.Maximized) WindowState = FormWindowState.Maximized;
    }

    void SaveBoundsSoon()
    {
        if (settingsStore == null || SelfTest) return;
        boundsTimer ??= new System.Windows.Forms.Timer { Interval = 700 };
        boundsTimer.Stop();
        boundsTimer.Tick -= SaveBoundsNow;
        boundsTimer.Tick += SaveBoundsNow;
        boundsTimer.Start();
    }

    void SaveBoundsNow(object s, EventArgs e)
    {
        boundsTimer?.Stop();
        if (settingsStore == null || SelfTest || WindowState == FormWindowState.Minimized) return;
        var maxed = WindowState == FormWindowState.Maximized;
        var r = maxed ? RestoreBounds : Bounds;
        S.Bounds = new WinBounds { X = r.X, Y = r.Y, Width = r.Width, Height = r.Height, Maximized = maxed };
        settingsStore.Save();
    }

    void OnFormClosing(object s, FormClosingEventArgs e)
    {
        SaveBoundsNow(null, null); // 直前に動かした位置・大きさも残す
        SaveSession();
        StoreRegistry.FlushAll();
    }

    internal void ApplyOpacityAndTop()
    {
        TopMost = S.AlwaysOnTop;
        // 透明度。WebView2 を載せたウィンドウでも描画できることを確認済み
        Opacity = Math.Max(0.3, S.Opacity / 100.0);
    }

    // ------------------------------------------------------------------
    // レイアウト
    // ------------------------------------------------------------------
    internal void Relayout()
    {
        if (chrome == null || WindowState == FormWindowState.Minimized) return;
        var cs = ClientSize;
        // 最小化・復元の途中などでサイズが小さくなることがある。そのときはビューを潰さない
        if (cs.Width < 100 || cs.Height < 100) return;
        chrome.SetBounds(0, 0, cs.Width, cs.Height);

        var sc = UiScale;
        int x0 = (int)Math.Round(uiLeft * sc);
        int y0 = (int)Math.Round(uiTop * sc);
        int w = Math.Max(0, cs.Width - x0);
        int h = Math.Max(0, cs.Height - y0);

        if (fullscreenTab != null && fullscreenTab.View != null)
        {
            fullscreenTab.View.SetBounds(0, 0, cs.Width, cs.Height);
        }
        else if (split != null)
        {
            const int gap = 3;
            int half = (w - gap) / 2;
            var A = GetTab(split.A);
            var B = GetTab(split.B);
            A?.View?.SetBounds(x0, y0, half, h);
            B?.View?.SetBounds(x0 + half + gap, y0, w - half - gap, h);
        }
        else
        {
            ActiveTab?.View?.SetBounds(x0, y0, w, h);
        }
        LayoutOverlay();
    }

    void ApplyVisibility()
    {
        foreach (var t in tabs)
        {
            if (t.View == null) continue;
            bool shown = t.Ws == currentWs &&
                         (t.Id == activeId || (split != null && (t.Id == split.A || t.Id == split.B)));
            if (fullscreenTab != null) shown = t == fullscreenTab;
            if (t.View.Visible != shown) t.View.Visible = shown;
        }
    }

    internal void FocusActive()
    {
        if (overlayMode != null) return;
        var v = ActiveTab?.View;
        if (v != null && v.CoreWebView2 != null) v.Focus();
    }

    internal void FocusUI() => chrome?.Focus();

    // ------------------------------------------------------------------
    // WebView2 ⇄ 外枠UI/オーバーレイのメッセージ送信
    // ------------------------------------------------------------------
    internal void Post(WebView2 view, string channel, object payload)
    {
        var core = view?.CoreWebView2;
        if (core == null) return;
        core.PostWebMessageAsJson(JsonSerializer.Serialize(new { c = channel, p = payload }, Util.Json));
    }

    internal void ToUI(string channel, object payload = null)
    {
        if (chrome?.CoreWebView2 != null) Post(chrome, channel, payload);
    }

    internal void ToOverlay(string channel, object payload = null)
    {
        if (!overlayReady) { overlayQueue.Add((channel, payload)); return; }
        Post(overlayView, channel, payload);
    }

    // 外枠に一時メッセージ（トースト）を出す
    internal void Toast(string msg) => ToUI("k:toast", msg);

    // ------------------------------------------------------------------
    // 状態をUIへ送る
    // ------------------------------------------------------------------
    bool stateQueued;
    internal void ScheduleState()
    {
        if (stateQueued || !IsHandleCreated) return;
        stateQueued = true;
        BeginInvoke(async () =>
        {
            await Task.Delay(16);
            stateQueued = false;
            SendState();
        });
    }

    object PubTab(Tab t) => new
    {
        id = t.Id,
        title = t.Title,
        url = t.Url,
        favicon = t.Favicon,
        loading = t.Loading,
        sleeping = t.View == null,
        pinned = t.Pinned,
        muted = t.Muted,
        audible = t.Audible,
        inSplit = split != null && (split.A == t.Id || split.B == t.Id),
    };

    void SendState()
    {
        if (!uiReady || chrome?.CoreWebView2 == null) return;
        var a = ActiveTab;
        var host = a != null ? Util.HostOf(a.Url) : "";
        object info = null;
        if (a != null)
        {
            info = new
            {
                url = a.Url,
                host,
                blocked = a.Blocked,
                bookmarked = IsBookmarked(a.Url),
                dark = IsDark(a),
                mobile = a.Mobile,
                reader = a.Reader,
                loading = a.Loading,
                canGoBack = a.CanGoBack,
                canGoForward = a.CanGoForward,
                adblockOff = host != "" && Sites.AdblockOff.Contains(host),
                hiddenCount = host != "" && Sites.Hidden.TryGetValue(host, out var hl) ? hl.Count : 0,
            };
        }
        ToUI("k:state", new
        {
            tabs = WsOrdered().Select(PubTab).ToList(),
            activeId,
            workspaces = S.Workspaces,
            currentWs,
            split = split != null,
            bookmarks = S.ShowBookmarksBar ? Bookmarks : new List<Bookmark>(),
            settings = new
            {
                tabLayout = S.TabLayout,
                showBookmarksBar = S.ShowBookmarksBar,
                sidebarCollapsed = S.SidebarCollapsed,
                adblock = S.Adblock,
                alwaysOnTop = S.AlwaysOnTop,
            },
            info,
            downloadsActive = downloads.Count(d => d.State == "progressing"),
            focusUntil,
        });
    }

    // ------------------------------------------------------------------
    // オーバーレイ（コマンドパレット・パネル・候補・入力ダイアログ）
    // ------------------------------------------------------------------
    internal void LayoutOverlay()
    {
        if (overlayView == null || overlayMode == null) return;
        var cs = ClientSize;
        var sc = UiScale;
        Rectangle r;
        int radius = 0;
        switch (overlayMode)
        {
            case "palette":
            {
                int w = (int)Math.Min(680 * sc, cs.Width - 40 * sc);
                int h = (int)Math.Min(paletteHeight * sc, cs.Height - 100 * sc);
                r = new Rectangle((cs.Width - w) / 2, (int)(70 * sc), w, h);
                radius = (int)(14 * sc);
                break;
            }
            case "prompt":
            {
                int w = (int)Math.Min(440 * sc, cs.Width - 40 * sc);
                int h = (int)(promptHasInput ? 230 * sc : 190 * sc);
                r = new Rectangle((cs.Width - w) / 2, (cs.Height - h) / 2 - (int)(30 * sc), w, h);
                radius = (int)(14 * sc);
                break;
            }
            case "panel":
            {
                int w = (int)Math.Min(420 * sc, cs.Width);
                int y = (int)Math.Round(uiTop * sc);
                r = new Rectangle(cs.Width - w, y, w, Math.Max(0, cs.Height - y));
                break;
            }
            default: // suggest
                r = suggestRect;
                radius = (int)(12 * sc);
                break;
        }
        overlayView.SetBounds(r.X, r.Y, Math.Max(1, r.Width), Math.Max(1, r.Height));
        SetRoundedRegion(overlayView, radius);
    }

    // 子ウィンドウの角を丸める（透明は使えないので、領域で切り抜く）
    static void SetRoundedRegion(Control c, int radius)
    {
        if (radius <= 0) { c.Region = null; return; }
        var w = c.Width;
        var h = c.Height;
        using var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(0, 0, d, d, 180, 90);
        path.AddArc(w - d, 0, d, d, 270, 90);
        path.AddArc(w - d, h - d, d, d, 0, 90);
        path.AddArc(0, h - d, d, d, 90, 90);
        path.CloseFigure();
        c.Region = new Region(path);
    }

    bool promptHasInput = true;

    void ShowOverlay(string mode, object payload, bool focus = true)
    {
        if (overlayView?.CoreWebView2 == null) return;
        overlayMode = mode;
        overlayShownAt = DateTime.UtcNow;
        LayoutOverlay();
        overlayView.Visible = true;
        overlayView.BringToFront();
        ToOverlay("k:ov-show", MergeMode(mode, payload));
        if (focus) overlayView.Focus();
    }

    static Dictionary<string, object> MergeMode(string mode, object payload)
    {
        var d = new Dictionary<string, object> { ["mode"] = mode };
        if (payload != null)
            foreach (var p in payload.GetType().GetProperties())
                d[char.ToLowerInvariant(p.Name[0]) + p.Name[1..]] = p.GetValue(payload);
        return d;
    }

    internal void HideOverlay(bool refocus = true)
    {
        if (overlayMode == null) return;
        overlayMode = null;
        overlayView.Visible = false;
        overlayView.Region = null;
        ToOverlay("k:ov-hide");
        if (pendingPrompt != null)
        {
            var p = pendingPrompt;
            pendingPrompt = null;
            p.TrySetResult(null);
        }
        if (refocus) FocusActive();
    }

    void HideSuggest()
    {
        if (overlayMode == "suggest") HideOverlay(false);
        omniItems = new List<SuggestItem>();
        omniSel = -1;
    }

    // パレットは、フォーカスが外れたら閉じる（クリックで他へ移ったとき）
    void OnOverlayLostFocus()
    {
        if (overlayMode == "palette" && (DateTime.UtcNow - overlayShownAt).TotalMilliseconds > 400)
            BeginInvoke(() => { if (overlayMode == "palette") HideOverlay(false); });
    }

    internal void RefreshPanel(string name)
    {
        if (overlayMode == "panel" && panelName == name) ToOverlay("k:panel-refresh", name);
    }

    internal void OpenPanel(string name)
    {
        if (overlayMode == "panel" && panelName == name) { HideOverlay(); return; }
        panelName = name;
        ShowOverlay("panel", new { panel = name });
    }

    internal void TogglePalette()
    {
        if (overlayMode == "palette") { HideOverlay(); return; }
        paletteHeight = 150;
        ShowOverlay("palette", null);
    }

    internal Task<string> AskPrompt(string title, string label = "", string value = "", string confirmLabel = "OK", bool input = true)
    {
        pendingPrompt?.TrySetResult(null);
        var tcs = new TaskCompletionSource<string>();
        pendingPrompt = tcs;
        promptId = ++promptSeq;
        promptHasInput = input;
        ShowOverlay("prompt", new { id = promptId, title, label, value, confirmLabel, input });
        return tcs.Task;
    }

    internal async Task<bool> AskConfirm(string message)
        => await AskPrompt("確認", message, "", "実行", false) != null;
}
