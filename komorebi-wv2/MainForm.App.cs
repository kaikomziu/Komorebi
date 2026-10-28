using System.IO.Pipes;

namespace Komorebi;

public partial class MainForm
{
    // 2つ目のインスタンスから届いたURLを開いて、ウィンドウを前面に出す
    void StartPipeServer()
    {
        if (SelfTest) return;
        _ = Task.Run(async () =>
        {
            while (!IsDisposed)
            {
                try
                {
                    using var s = new NamedPipeServerStream(Program.PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await s.WaitForConnectionAsync();
                    using var r = new StreamReader(s);
                    var line = (await r.ReadLineAsync())?.Trim();
                    if (IsDisposed) return;
                    BeginInvoke(() =>
                    {
                        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                        Activate();
                        if (!string.IsNullOrEmpty(line) && Util.IsHttp(line)) CreateTab(line, new TabOpts { After = activeId });
                    });
                }
                catch
                {
                    await Task.Delay(500);
                }
            }
        });
    }
}
