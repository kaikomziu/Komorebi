using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Komorebi;

public partial class MainForm
{
    string chromeFull = "130.0.0.0", chromeMajor = "130";
    internal string DesktopUa, MobileUa;
    readonly HashSet<string> upgraded = new();  // HTTPSへ自動で上げたホスト
    readonly HashSet<string> noUpgrade = new(); // HTTPSで失敗したのでHTTPに戻すホスト

    // 本物の Chrome を名乗る。WebView2 の既定は Edge（UAに Edg/、ブランドに Microsoft Edge）で、
    // Google ログインの「安全でないブラウザ」判定に引っかかりやすいため、UA とクライアントヒントを揃えて上書きする。
    void SetupBrowserIdentity()
    {
        chromeFull = env.BrowserVersionString.Split(' ')[0];
        chromeMajor = chromeFull.Split('.')[0];
        DesktopUa = $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{chromeMajor}.0.0.0 Safari/537.36";
        MobileUa = $"Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{chromeMajor}.0.0.0 Mobile Safari/537.36";
    }

    async Task ApplyIdentity(CoreWebView2 core, bool mobile)
    {
        var brands = new[]
        {
            new { brand = "Chromium", version = chromeMajor },
            new { brand = "Google Chrome", version = chromeMajor },
            new { brand = "Not?A_Brand", version = "99" },
        };
        var full = new[]
        {
            new { brand = "Chromium", version = chromeFull },
            new { brand = "Google Chrome", version = chromeFull },
            new { brand = "Not?A_Brand", version = "99.0.0.0" },
        };
        var json = JsonSerializer.Serialize(new
        {
            userAgent = mobile ? MobileUa : DesktopUa,
            userAgentMetadata = new
            {
                brands,
                fullVersionList = full,
                platform = mobile ? "Android" : "Windows",
                platformVersion = mobile ? "14.0.0" : "15.0.0",
                architecture = mobile ? "" : "x86",
                model = mobile ? "Pixel 8" : "",
                mobile,
                bitness = mobile ? "" : "64",
                wow64 = false,
            },
        });
        await core.CallDevToolsProtocolMethodAsync("Emulation.setUserAgentOverride", json);
    }

    // ------------------------------------------------------------------
    // タブごとのWebView2の設定とイベント配線
    // ------------------------------------------------------------------
    async Task SetupTabCore(Tab t)
    {
        var core = t.Core;
        var ctl = t.View.Ctl();
        var st = core.Settings;
        st.AreBrowserAcceleratorKeysEnabled = false; // ショートカットは自前で処理する
        st.AreDevToolsEnabled = true;
        st.IsZoomControlEnabled = true;
        st.IsStatusBarEnabled = true;
        st.IsPasswordAutosaveEnabled = true;
        st.IsGeneralAutofillEnabled = true;
        MapUiHost(core);

        await core.AddScriptToExecuteOnDocumentCreatedAsync(PageScripts.TabBridgeJs);
        await ApplyIdentity(core, t.Mobile);

        // 広告ドメインへのリクエストだけを受け取る（全リクエストを監視すると重くなる）
        foreach (var p in Blocklist.FilterPatterns())
            core.AddWebResourceRequestedFilter(p, CoreWebView2WebResourceContext.All);

        core.WebResourceRequested += (_, e) => OnResourceRequested(t, e);
        core.NavigationStarting += (_, e) => OnNavStarting(t, e);
        core.NavigationCompleted += (_, e) => OnNavCompleted(t, e);
        core.SourceChanged += (_, _) =>
        {
            var src = core.Source;
            if (!string.IsNullOrEmpty(src) && !src.StartsWith("data:text/html")) t.Url = src;
            ScheduleState();
            SaveSessionSoon();
        };
        core.HistoryChanged += (_, _) =>
        {
            t.CanGoBack = core.CanGoBack;
            t.CanGoForward = core.CanGoForward;
            ScheduleState();
        };
        core.DocumentTitleChanged += (_, _) =>
        {
            t.Title = string.IsNullOrEmpty(core.DocumentTitle) ? t.Url : core.DocumentTitle;
            var last = History.Count > 0 ? History[^1] : null;
            if (last != null && last.Url == t.Url) { last.Title = t.Title; historyStore.Save(); }
            ScheduleState();
        };
        core.FaviconChanged += (_, _) =>
        {
            t.Favicon = string.IsNullOrEmpty(core.FaviconUri) ? null : core.FaviconUri;
            ScheduleState();
        };
        core.DOMContentLoaded += (_, _) => _ = ApplyPageStyles(t);
        core.IsDocumentPlayingAudioChanged += (_, _) =>
        {
            t.Audible = core.IsDocumentPlayingAudio;
            ScheduleState();
        };
        core.NewWindowRequested += (_, e) => OnNewWindow(t, e);
        core.WindowCloseRequested += (_, _) => BeginInvoke(() => CloseTab(t.Id));
        core.ContextMenuRequested += (_, e) => OnContextMenu(t, e);
        core.DownloadStarting += (_, e) => OnDownloadStarting(t, e);
        core.PermissionRequested += (_, e) => OnPermissionRequested(t, e);
        core.WebMessageReceived += (_, e) => OnTabMessage(t, e);
        core.ContainsFullScreenElementChanged += (_, _) => SetHtmlFullscreen(t, core.ContainsFullScreenElement);
        core.ProcessFailed += (_, e) =>
        {
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited ||
                e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
            {
                t.Title = "（クラッシュしました）";
                ScheduleState();
                BeginInvoke(() => t.Core?.Reload());
            }
        };

        ctl.AcceleratorKeyPressed += OnAccelerator;
        ctl.ZoomFactorChanged += (_, _) => OnZoomChanged(t);
        // 分割表示中、クリックした側をアクティブにする
        ctl.GotFocus += (_, _) =>
        {
            if (split != null && activeId != t.Id && (split.A == t.Id || split.B == t.Id))
            {
                activeId = t.Id;
                t.LastActive = Util.Now();
                ScheduleState();
            }
        };
    }

    // ------------------------------------------------------------------
    // 広告・トラッカーのブロック
    // ------------------------------------------------------------------
    void OnResourceRequested(Tab t, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!S.Adblock) return;
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var u) || !Blocklist.IsAdHost(u.Host)) return;
        var pageHost = Util.HostOf(t.Url);
        if (pageHost != "" && Sites.AdblockOff.Contains(pageHost)) return;
        e.Response = env.CreateWebResourceResponse(null, 403, "Blocked", "Content-Type: text/plain");
        t.Blocked++;
        ScheduleState();
    }

    // ------------------------------------------------------------------
    // ナビゲーション制御（集中モード・追跡パラメータ除去・HTTPS化）
    // ------------------------------------------------------------------
    void OnNavStarting(Tab t, CoreWebView2NavigationStartingEventArgs e)
    {
        var uri = e.Uri;
        t.LastNavUri = uri;
        string redirect = null;

        if (Util.IsHttp(uri))
        {
            if (IsFocusBlocked(uri))
            {
                redirect = FocusPageUrl(uri);
            }
            else
            {
                var u = uri;
                if (S.StripTracking) u = Blocklist.CleanTrackingUrl(u);
                var host = Util.HostOf(u);
                if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && S.HttpsUpgrade &&
                    !Util.IsLocalHost(host) && !noUpgrade.Contains(host))
                {
                    upgraded.Add(host);
                    u = "https://" + u[7..];
                }
                if (u != uri) redirect = u;
            }
        }

        if (redirect != null)
        {
            e.Cancel = true;
            BeginInvoke(() => t.Core?.Navigate(redirect));
            return;
        }

        // フラグメントだけの移動ではカウンタやリーダー状態を保つ
        if (Util.StripFragment(uri) != Util.StripFragment(t.Url))
        {
            t.Blocked = 0;
            t.Reader = false;
        }
        t.Loading = true;
        ScheduleState();
    }

    void OnNavCompleted(Tab t, CoreWebView2NavigationCompletedEventArgs e)
    {
        t.Loading = false;
        var core = t.Core;
        if (core != null)
        {
            t.CanGoBack = core.CanGoBack;
            t.CanGoForward = core.CanGoForward;
        }
        if (!e.IsSuccess)
        {
            var uri = t.LastNavUri ?? "";
            var host = Util.HostOf(uri);
            // HTTPSへ自動で上げたサイトが開けなければ、HTTPに戻して再試行する
            if (uri.StartsWith("https://") && upgraded.Contains(host) && !noUpgrade.Contains(host) &&
                e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
            {
                noUpgrade.Add(host);
                BeginInvoke(() => t.Core?.Navigate("http://" + uri[8..]));
                return;
            }
        }
        else
        {
            RecordHistory(t);
        }
        ScheduleState();
        SaveSessionSoon();
    }

    // window.open や target=_blank は新しいタブで開く（window.opener を保つため、WebView2側へ接続する）
    async void OnNewWindow(Tab opener, CoreWebView2NewWindowRequestedEventArgs e)
    {
        var d = e.GetDeferral();
        try
        {
            e.Handled = true;
            var uri = string.IsNullOrEmpty(e.Uri) ? "about:blank" : e.Uri;
            var nt = CreateTab(uri, new TabOpts { After = opener.Id, Popup = true });
            await nt.Ready.Task;
            e.NewWindow = nt.Core;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("新しいウィンドウの作成に失敗: " + ex.Message);
        }
        finally
        {
            d.Complete();
        }
    }

    // HTML5 のフルスクリーン（動画など）
    void SetHtmlFullscreen(Tab t, bool on)
    {
        if (on)
        {
            fullscreenTab = t;
            if (FormBorderStyle != FormBorderStyle.None) { savedBorder = FormBorderStyle; savedState = WindowState; }
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
        }
        else if (fullscreenTab == t)
        {
            fullscreenTab = null;
            FormBorderStyle = savedBorder;
            WindowState = savedState;
        }
        ApplyVisibility();
        Relayout();
    }

    FormBorderStyle savedBorder = FormBorderStyle.Sizable;
    FormWindowState savedState = FormWindowState.Normal;

    internal void ToggleFullscreen()
    {
        if (FormBorderStyle == FormBorderStyle.None)
        {
            FormBorderStyle = savedBorder;
            WindowState = savedState;
        }
        else
        {
            savedBorder = FormBorderStyle;
            savedState = WindowState;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
        }
        Relayout();
    }

    // ------------------------------------------------------------------
    // 内部ページ（新しいタブ・集中モード画面）からの要求。送信元URLを必ず確認する
    // ------------------------------------------------------------------
    void OnTabMessage(Tab t, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var src = e.Source ?? "";
        if (!src.StartsWith(UiBase)) return;
        JsonElement root;
        try { root = JsonDocument.Parse(e.WebMessageAsJson).RootElement.Clone(); } catch { return; }
        var c = root.Str("c");
        root.TryGetProperty("p", out var p);
        var isNewtab = src.StartsWith(UiBase + "newtab.html");
        var isFocus = src.StartsWith(UiBase + "focus.html");

        if (c == "k:newtab-data" && isNewtab && root.TryGetProperty("id", out var idEl))
        {
            var data = new
            {
                bookmarks = Bookmarks.Take(12).ToList(),
                engine = Search.Engine(S.SearchEngine).Name,
                focusUntil,
                recentClosed = Enumerable.Reverse(closedStack).Take(4).Select(x => new { url = x.url, title = x.title }).ToList(),
            };
            t.Core?.PostWebMessageAsJson(JsonSerializer.Serialize(new { id = idEl.GetInt32(), r = data }, Util.Json));
        }
        else if (c == "k:newtab-go" && isNewtab && p.ValueKind == JsonValueKind.String)
        {
            NavigateTab(t, Search.ToUrl(p.GetString(), S.SearchEngine, NewtabUrl));
        }
        else if (c == "k:newtab-cmd" && isNewtab && p.ValueKind == JsonValueKind.String)
        {
            var id = p.GetString();
            if (new[] { "focus.start25", "focus.start50", "panel.notes", "view.palette", "tab.restore", "panel.history" }.Contains(id))
                RunCommand(id);
        }
        else if (c == "k:focus-stop" && isFocus)
        {
            StopFocus();
        }
    }

    // ------------------------------------------------------------------
    // ダウンロード
    // ------------------------------------------------------------------
    internal readonly List<DownloadItem> downloads = new();
    readonly Dictionary<int, CoreWebView2DownloadOperation> dlOps = new();
    int dlSeq;
    internal string DownloadsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    internal string PicturesDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Komorebi");

    static string UniquePath(string dir, string name)
    {
        var ext = Path.GetExtension(name);
        var baseName = Path.GetFileNameWithoutExtension(name);
        var p = Path.Combine(dir, name);
        for (int i = 1; File.Exists(p); i++) p = Path.Combine(dir, $"{baseName} ({i}){ext}");
        return p;
    }

    void OnDownloadStarting(Tab t, CoreWebView2DownloadStartingEventArgs e)
    {
        Directory.CreateDirectory(DownloadsDir);
        var path = UniquePath(DownloadsDir, Path.GetFileName(e.ResultFilePath));
        e.ResultFilePath = path;
        e.Handled = true; // 既定のダウンロードUIは使わず、Komorebi のパネルで管理する
        var op = e.DownloadOperation;
        var d = new DownloadItem
        {
            Id = ++dlSeq, Name = Path.GetFileName(path), Url = op.Uri, Path = path,
            Total = (long)(op.TotalBytesToReceive ?? 0), Ts = Util.Now(),
        };
        downloads.Insert(0, d);
        dlOps[d.Id] = op;
        op.BytesReceivedChanged += (_, _) =>
        {
            d.Received = (long)op.BytesReceived;
            d.Total = op.TotalBytesToReceive.HasValue ? (long)op.TotalBytesToReceive.Value : d.Total;
            PushDownloads();
        };
        op.StateChanged += (_, _) =>
        {
            d.State = op.State switch
            {
                CoreWebView2DownloadState.Completed => "completed",
                CoreWebView2DownloadState.Interrupted =>
                    op.InterruptReason == CoreWebView2DownloadInterruptReason.UserCanceled ? "cancelled" : "interrupted",
                _ => "progressing",
            };
            if (d.State == "completed")
            {
                d.Received = (long)op.BytesReceived;
                dlOps.Remove(d.Id);
                Toast("ダウンロード完了: " + d.Name);
            }
            if (d.State != "progressing") CloseDownloadTab(d.Id);
            PushDownloads();
        };
        Toast("ダウンロード開始: " + d.Name);
        PushDownloads();

        // ダウンロードのためだけに開かれた空のタブは、見えなくして完了後に閉じる。
        // （WebView2 ではダウンロードは元のタブに属するので、すぐ閉じるとダウンロードが中断される）
        var src = t.Core?.Source;
        if ((string.IsNullOrEmpty(src) || src == "about:blank") && tabs.Any(x => !x.Hidden && x.Id != t.Id))
        {
            t.Hidden = true;
            dlTabs[d.Id] = t.Id;
            if (activeId == t.Id)
            {
                var o = WsOrdered().FirstOrDefault() ?? tabs.FirstOrDefault(x => !x.Hidden);
                if (o != null) ActivateTab(o.Id);
            }
            ScheduleState();
            SaveSessionSoon();
        }
    }

    readonly Dictionary<int, int> dlTabs = new(); // ダウンロードID → 完了待ちの空タブID

    void CloseDownloadTab(int dlId)
    {
        if (dlTabs.Remove(dlId, out var tid)) BeginInvoke(() => CloseTab(tid, true));
    }

    bool dlPending;
    void PushDownloads()
    {
        if (dlPending) return;
        dlPending = true;
        BeginInvoke(async () =>
        {
            await Task.Delay(120);
            dlPending = false;
            RefreshPanel("downloads");
            ScheduleState();
        });
    }

    // ------------------------------------------------------------------
    // 権限: カメラ等は確認してサイトごとに記憶する
    // ------------------------------------------------------------------
    void OnPermissionRequested(Tab t, CoreWebView2PermissionRequestedEventArgs e)
    {
        var label = e.PermissionKind switch
        {
            CoreWebView2PermissionKind.Microphone or CoreWebView2PermissionKind.Camera => "カメラ・マイク",
            CoreWebView2PermissionKind.Geolocation => "位置情報",
            CoreWebView2PermissionKind.Notifications => "通知",
            CoreWebView2PermissionKind.ClipboardRead => "クリップボードの読み取り",
            CoreWebView2PermissionKind.MidiSystemExclusiveMessages => "MIDIデバイス",
            _ => null,
        };
        if (label == null) return; // それ以外は既定の動作に任せる

        var host = Util.HostOf(e.Uri);
        var key = host + "|" + (e.PermissionKind is CoreWebView2PermissionKind.Microphone or CoreWebView2PermissionKind.Camera ? "media" : e.PermissionKind.ToString());
        if (Sites.Perms.TryGetValue(key, out var saved))
        {
            e.State = saved ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
            return;
        }
        if (SelfTest) { e.State = CoreWebView2PermissionState.Deny; return; }

        var d = e.GetDeferral();
        BeginInvoke(() =>
        {
            try
            {
                var allow = new TaskDialogButton("許可");
                var block = new TaskDialogButton("ブロック");
                var page = new TaskDialogPage
                {
                    Caption = "権限のリクエスト",
                    Heading = $"{(host == "" ? "このページ" : host)} が{label}の使用を求めています",
                    Buttons = { allow, block },
                    DefaultButton = block,
                    Verification = new TaskDialogVerificationCheckBox("この選択を記憶する"),
                };
                var result = TaskDialog.ShowDialog(this, page);
                var ok = result == allow;
                if (page.Verification.Checked && host != "")
                {
                    Sites.Perms[key] = ok;
                    sitesStore.Save();
                }
                e.State = ok ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
            }
            finally
            {
                d.Complete();
            }
        });
    }

    // ------------------------------------------------------------------
    // ページの右クリックメニュー（標準メニューの先頭に Komorebi の項目を足す）
    // ------------------------------------------------------------------
    void OnContextMenu(Tab t, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        var tgt = e.ContextMenuTarget;
        var items = e.MenuItems;
        var extra = new List<CoreWebView2ContextMenuItem>();

        CoreWebView2ContextMenuItem Mk(string label, Action act)
        {
            var it = env.CreateContextMenuItem(label, null, CoreWebView2ContextMenuItemKind.Command);
            it.CustomItemSelected += (_, _) => BeginInvoke(act);
            return it;
        }

        if (tgt.HasLinkUri)
        {
            var link = tgt.LinkUri;
            extra.Add(Mk("リンクを新しいタブで開く", () => CreateTab(link, new TabOpts { After = t.Id })));
            extra.Add(Mk("リンクを横に並べて開く（分割表示）", () => OpenInSplit(link)));
            extra.Add(Mk("リンクのURLをコピー（追跡情報を除く）", () => Clipboard.SetText(Blocklist.CleanTrackingUrl(link))));
        }
        if (tgt.HasSelection && !tgt.IsEditable)
        {
            var sel = tgt.SelectionText.Trim();
            var shortSel = sel.Length > 16 ? sel[..16] : sel;
            extra.Add(Mk($"「{shortSel}」を検索", () => CreateTab(Search.SearchUrl(sel, S.SearchEngine), new TabOpts { After = t.Id })));
            extra.Add(Mk("選択範囲をメモに追加", () => AddSelectionToNote(sel, t)));
            if (Util.TryEval(sel, out var calc))
                extra.Add(Mk($"計算結果をコピー（= {calc}）", () => Clipboard.SetText(calc)));
        }
        if (!tgt.IsEditable && !tgt.HasLinkUri && !tgt.HasSelection)
        {
            extra.Add(Mk("リーダーモード", () => RunCommand("tools.reader")));
            extra.Add(Mk("要素ブロッカー", () => RunCommand("tools.picker")));
            extra.Add(Mk("スクリーンショット", () => RunCommand("tools.shot")));
            extra.Add(Mk("分割表示", () => RunCommand("view.split")));
        }
        if (extra.Count == 0) return;
        int pos = 0;
        foreach (var it in extra) items.Insert(pos++, it);
        items.Insert(pos, env.CreateContextMenuItem("", null, CoreWebView2ContextMenuItemKind.Separator));
    }
}
