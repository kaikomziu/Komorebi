using System.IO.Pipes;

namespace Komorebi;

static class Program
{
    internal const string PipeName = "Komorebi.WV2.Pipe";

    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // 想定外の例外はログに残す（原因調査用）。UIスレッドの例外ではアプリを落とさず続行する
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => LogError("UIスレッド", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogError("未処理", e.ExceptionObject as Exception);

        var st = Array.IndexOf(args, "--selftest");
        var selfTest = st >= 0;
        var script = selfTest && st + 1 < args.Length ? args[st + 1] : null;
        var dev = args.Contains("--dev");

        // 多重起動の防止（2つ目はURLを渡して終了する）
        using var mutex = new Mutex(true, "Komorebi.WV2.SingleInstance", out var first);
        if (!first && !selfTest)
        {
            SendToRunning(args.FirstOrDefault(a => a.StartsWith("http://") || a.StartsWith("https://")) ?? "");
            return;
        }

        Application.Run(new MainForm(selfTest, script, dev));
    }

    internal static void LogError(string where, Exception e)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KomorebiWV2");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {where}: {e}\n\n");
        }
        catch { /* ログに書けなくても続行 */ }
    }

    static void SendToRunning(string url)
    {
        try
        {
            using var c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            c.Connect(1500);
            using var w = new StreamWriter(c) { AutoFlush = true };
            w.WriteLine(url);
        }
        catch { /* 先に起動している側が応答しなければ何もしない */ }
    }
}
