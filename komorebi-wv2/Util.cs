using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Komorebi;

static class Util
{
    // IPC・状態通知用（camelCase）
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // ---------------------------------------------------------------- URL
    // http(s) のホスト名。内部ページ(komorebi.app)や data: などは空文字
    public static string HostOf(string url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "";
        if (u.Scheme != "http" && u.Scheme != "https") return "";
        if (u.Host == MainForm.UiHost) return "";
        return u.Host;
    }

    // 外部の http(s) ページか（内部ページ komorebi.app は含まない）。履歴・ブックマーク・復元の対象判定に使う
    public static bool IsHttp(string url) => HostOf(url) != "";

    public static bool IsLocalHost(string host) =>
        !host.Contains('.') || Regex.IsMatch(host, @"^\d{1,3}(\.\d{1,3}){3}$") ||
        Regex.IsMatch(host, @"\.(local|localhost|test|internal|lan)$", RegexOptions.IgnoreCase);

    public static bool LooksLikeUrl(string s)
    {
        if (Regex.IsMatch(s, @"^(https?|file|about|edge|chrome|data|view-source):", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(s, @"^localhost(:\d+)?(/.*)?$", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(s, @"^\d{1,3}(\.\d{1,3}){3}(:\d+)?(/.*)?$")) return true;
        return Regex.IsMatch(s, @"^[^\s./]+\.[^\s]{2,}(/.*)?$") && !s.Contains(' ');
    }

    public static string StripFragment(string url)
    {
        var i = url?.IndexOf('#') ?? -1;
        return i >= 0 ? url[..i] : url;
    }

    // ---------------------------------------------------------------- 文字列検索
    // あいまい一致のスコア（0 = 不一致）
    public static double Fuzzy(string q, string text)
    {
        q = q.ToLowerInvariant();
        text = text.ToLowerInvariant();
        if (q.Length == 0) return 1;
        var i = text.IndexOf(q, StringComparison.Ordinal);
        if (i >= 0) return 1000 - i - (text.Length - q.Length) * 0.1;
        int qi = 0, last = -2;
        double score = 0;
        for (int ti = 0; ti < text.Length && qi < q.Length; ti++)
        {
            if (text[ti] == q[qi])
            {
                score += ti == last + 1 ? 5 : 1;
                last = ti;
                qi++;
            }
        }
        return qi == q.Length ? score : 0;
    }

    // 空白区切りの単語がすべて一致したときだけスコアを返す
    public static double FuzzyAll(string query, string text)
    {
        var words = query.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return 1;
        double total = 0;
        foreach (var w in words)
        {
            var s = Fuzzy(w, text);
            if (s == 0) return 0;
            total += s;
        }
        return total;
    }

    // ---------------------------------------------------------------- 計算
    public static bool TryEval(string s, out string result)
    {
        result = null;
        if (!Regex.IsMatch(s, @"^[\d\s+\-*/().%^]+$") || !Regex.IsMatch(s, @"[+\-*/%^]") || !Regex.IsMatch(s, @"\d")) return false;
        try
        {
            var p = new MathParser(s);
            var v = p.ParseExpr();
            if (!p.AtEnd || !double.IsFinite(v)) return false;
            result = v.ToString("G15", CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            return false;
        }
    }

    sealed class MathParser
    {
        readonly string s;
        int i;
        public MathParser(string s) { this.s = s; }
        public bool AtEnd { get { Skip(); return i >= s.Length; } }
        void Skip() { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }
        bool Eat(char c) { Skip(); if (i < s.Length && s[i] == c) { i++; return true; } return false; }

        public double ParseExpr()
        {
            var v = ParseTerm();
            while (true)
            {
                if (Eat('+')) v += ParseTerm();
                else if (Eat('-')) v -= ParseTerm();
                else return v;
            }
        }
        double ParseTerm()
        {
            var v = ParsePow();
            while (true)
            {
                if (Eat('*')) v *= ParsePow();
                else if (Eat('/')) v /= ParsePow();
                else if (Eat('%')) v %= ParsePow();
                else return v;
            }
        }
        double ParsePow()
        {
            var b = ParseUnary();
            return Eat('^') ? Math.Pow(b, ParsePow()) : b; // 右結合
        }
        double ParseUnary()
        {
            if (Eat('-')) return -ParseUnary();
            if (Eat('+')) return ParseUnary();
            return ParseAtom();
        }
        double ParseAtom()
        {
            if (Eat('('))
            {
                var v = ParseExpr();
                if (!Eat(')')) throw new FormatException();
                return v;
            }
            Skip();
            int st = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
            if (st == i) throw new FormatException();
            return double.Parse(s[st..i], CultureInfo.InvariantCulture);
        }
    }

    // ---------------------------------------------------------------- JSON
    public static string Str(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static bool Bool(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) &&
           (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.Number && v.GetDouble() != 0));

    public static double Num(this JsonElement e, string name, double def = 0)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : def;

    public static string Js(string s) => JsonSerializer.Serialize(s);
}

// 検索エンジンと「アドレスバーのショートカット」
static class Search
{
    public static readonly Dictionary<string, (string Name, string Url)> Engines = new()
    {
        ["google"] = ("Google", "https://www.google.com/search?q=%s"),
        ["duckduckgo"] = ("DuckDuckGo", "https://duckduckgo.com/?q=%s"),
        ["bing"] = ("Bing", "https://www.bing.com/search?q=%s"),
        ["yahoo"] = ("Yahoo! JAPAN", "https://search.yahoo.co.jp/search?p=%s"),
        ["brave"] = ("Brave Search", "https://search.brave.com/search?q=%s"),
    };

    public static readonly Dictionary<string, string> Bangs = new()
    {
        ["g"] = "https://www.google.com/search?q=%s",
        ["ddg"] = "https://duckduckgo.com/?q=%s",
        ["bing"] = "https://www.bing.com/search?q=%s",
        ["yt"] = "https://www.youtube.com/results?search_query=%s",
        ["gh"] = "https://github.com/search?q=%s",
        ["w"] = "https://ja.wikipedia.org/w/index.php?search=%s",
        ["wen"] = "https://en.wikipedia.org/w/index.php?search=%s",
        ["map"] = "https://www.google.com/maps/search/%s",
        ["img"] = "https://www.google.com/search?tbm=isch&q=%s",
        ["amz"] = "https://www.amazon.co.jp/s?k=%s",
        ["nico"] = "https://www.nicovideo.jp/search/%s",
        ["x"] = "https://x.com/search?q=%s",
        ["tr"] = "https://translate.google.com/?sl=auto&tl=ja&text=%s&op=translate",
        ["so"] = "https://stackoverflow.com/search?q=%s",
        ["mdn"] = "https://developer.mozilla.org/ja/search?q=%s",
        ["npm"] = "https://www.npmjs.com/search?q=%s",
        ["r"] = "https://www.reddit.com/search/?q=%s",
    };

    public static (string Name, string Url) Engine(string id)
        => Engines.TryGetValue(id ?? "", out var e) ? e : Engines["google"];

    public static string SearchUrl(string q, string engineId)
        => Engine(engineId).Url.Replace("%s", Uri.EscapeDataString(q));

    // バング形式（"yt ねこ" / "!yt ねこ"）なら (キー, 検索語) を返す
    public static bool TryBang(string input, out string key, out string term)
    {
        key = term = null;
        var m = Regex.Match(input.Trim(), @"^!?(\S+)\s+(.+)$");
        if (!m.Success || !Bangs.ContainsKey(m.Groups[1].Value.ToLowerInvariant())) return false;
        key = m.Groups[1].Value.ToLowerInvariant();
        term = m.Groups[2].Value;
        return true;
    }

    // 入力欄の文字列を、開くURLに変換する
    public static string ToUrl(string input, string engineId, string newtabUrl)
    {
        var s = (input ?? "").Trim();
        if (s.Length == 0) return newtabUrl;
        if (Regex.IsMatch(s, @"^(https?|file|about|edge|chrome|data|view-source):", RegexOptions.IgnoreCase)) return s;
        if (TryBang(s, out var k, out var term)) return Bangs[k].Replace("%s", Uri.EscapeDataString(term));
        if (Util.LooksLikeUrl(s))
            return (Regex.IsMatch(s, @"^(localhost|\d{1,3}(\.\d{1,3}){3})", RegexOptions.IgnoreCase) ? "http://" : "https://") + s;
        return SearchUrl(s, engineId);
    }
}

// Win32 / DWM の呼び出し
static class Native
{
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("user32.dll")]
    static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    // 自分のウィンドウを前面に出す。OSは別プロセスからの前面化を制限するので、
    // 現在の前面ウィンドウのスレッドに入力をアタッチして許可を得る（テストでの利用が目的）
    public static bool ForceForeground(IntPtr hwnd)
    {
        var fg = GetForegroundWindow();
        if (fg == hwnd) return true;
        var fgThread = GetWindowThreadProcessId(fg, out _);
        var me = GetCurrentThreadId();
        var attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(me, fgThread, false);
        }
        return GetForegroundWindow() == hwnd;
    }

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hwnd, int cmd);

    // タイトルバーを外枠と同じダークカラーにする（Windows 11 / 10 20H1+）
    public static void DarkTitleBar(IntPtr hwnd, Color caption)
    {
        try
        {
            int dark = 1;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
            int bgr = caption.R | (caption.G << 8) | (caption.B << 16);
            DwmSetWindowAttribute(hwnd, 35, ref bgr, sizeof(int)); // DWMWA_CAPTION_COLOR
            int text = 0xE8 | (0xEA << 8) | (0xED << 16);
            DwmSetWindowAttribute(hwnd, 36, ref text, sizeof(int)); // DWMWA_TEXT_COLOR
        }
        catch { /* 古いWindowsでは無視 */ }
    }
}
