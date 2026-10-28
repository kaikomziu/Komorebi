using System.Text.Json;

namespace Komorebi;

public interface IStore
{
    void Flush();
}

// すべてのストアを覚えておき、終了時にまとめて書き出す
// （ジェネリッククラスの static は型ごとに別物になるので、ここに分ける）
public static class StoreRegistry
{
    static readonly List<IStore> All = new();

    public static void Add(IStore s)
    {
        lock (All) All.Add(s);
    }

    public static void FlushAll()
    {
        IStore[] list;
        lock (All) list = All.ToArray();
        foreach (var s in list) s.Flush();
    }
}

// JSONファイルに保存する小さなストア。書き込みは遅延させ、UIスレッドで行う。
public sealed class Store<T> : IStore where T : class
{
    static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    readonly string file;
    readonly SynchronizationContext ui;
    System.Threading.Timer timer;
    public T Data;

    public Store(string file, Func<T> defaults)
    {
        this.file = file;
        ui = SynchronizationContext.Current;
        try
        {
            Data = JsonSerializer.Deserialize<T>(File.ReadAllText(file), Opts) ?? defaults();
        }
        catch
        {
            Data = defaults();
        }
        StoreRegistry.Add(this);
    }

    public void Save()
    {
        timer?.Dispose();
        timer = new System.Threading.Timer(_ =>
        {
            if (ui != null) ui.Post(__ => Flush(), null);
            else Flush();
        }, null, 600, Timeout.Infinite);
    }

    public void Flush()
    {
        timer?.Dispose();
        timer = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Data, Opts));
            File.Move(tmp, file, true); // 書き込み途中で壊れないよう差し替える
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("ストア保存に失敗: " + file + " " + e.Message);
        }
    }
}
