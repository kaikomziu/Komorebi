namespace Komorebi;

// 保存するデータの型。JSONのキーは camelCase（Electron版と同じ形式なので、そのまま引き継げる）

public class Workspace
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Color { get; set; }
}

public class WinBounds
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }
}

public class Settings
{
    public bool Adblock { get; set; } = true;
    public bool HttpsUpgrade { get; set; } = true;
    public bool StripTracking { get; set; } = true;
    public bool RestoreSession { get; set; } = true;
    public int SleepMinutes { get; set; } = 15;
    public string TabLayout { get; set; } = "top"; // "top" | "side"
    public string SearchEngine { get; set; } = "google";
    public bool ForceDark { get; set; } = false;
    public bool ShowBookmarksBar { get; set; } = true;
    public int Opacity { get; set; } = 100;
    public bool AlwaysOnTop { get; set; } = false;
    public bool SidebarCollapsed { get; set; } = false;
    public List<string> FocusSites { get; set; } = new()
    {
        "youtube.com", "x.com", "twitter.com", "reddit.com", "twitch.tv",
        "nicovideo.jp", "tiktok.com", "instagram.com", "facebook.com",
    };
    public List<Workspace> Workspaces { get; set; } = new()
    {
        new Workspace { Id = "w1", Name = "メイン", Color = "#8ab4f8" },
    };
    public string CurrentWs { get; set; } = "w1";
    public WinBounds Bounds { get; set; }
}

public class SiteData
{
    public Dictionary<string, double> Zoom { get; set; } = new();
    public Dictionary<string, bool> Dark { get; set; } = new();
    public List<string> AdblockOff { get; set; } = new();
    public Dictionary<string, List<string>> Hidden { get; set; } = new();
    public Dictionary<string, bool> Perms { get; set; } = new();
}

public class Bookmark
{
    public string Url { get; set; }
    public string Title { get; set; }
    public string Favicon { get; set; }
}

public class HistoryItem
{
    public string Url { get; set; }
    public string Title { get; set; }
    public long Ts { get; set; }
}

public class Note
{
    public long Id { get; set; }
    public string Text { get; set; } = "";
    public long Ts { get; set; }
}

public class SessionTab
{
    public string Url { get; set; }
    public string Title { get; set; }
    public string Favicon { get; set; }
    public string Ws { get; set; }
    public bool Pinned { get; set; }
}

public class SessionData
{
    public List<SessionTab> Tabs { get; set; } = new();
    public int Active { get; set; } = -1;
}

// コマンドパレットの1項目（オーバーレイのJSとやり取りする）
public class PaletteItem
{
    public double S { get; set; }
    public string Kind { get; set; }
    public string Title { get; set; }
    public string Sub { get; set; }
    public string Icon { get; set; }
    public string Id { get; set; }
    public string Url { get; set; }
    public string Value { get; set; }
    public int TabId { get; set; }
}

// アドレスバーの候補1項目
public class SuggestItem
{
    public string Kind { get; set; }
    public string Title { get; set; }
    public string Sub { get; set; }
    public string Url { get; set; }
    public int TabId { get; set; }
}

public class DownloadItem
{
    public int Id { get; set; }
    public string Name { get; set; }
    public string Url { get; set; }
    public long Total { get; set; }
    public long Received { get; set; }
    public string State { get; set; } = "progressing";
    public string Path { get; set; }
    public long Ts { get; set; }
}
