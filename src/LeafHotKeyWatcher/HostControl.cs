using System.Diagnostics;
using LeafHotKey;

namespace LeafHotKeyWatcher;

/// <summary>本体プロセスに対する操作。自己検証では差し替えられるように抽象化する。</summary>
public interface IHostControl
{
    /// <summary>本体が制御チャネルに応答するか。</summary>
    bool IsRunning();

    /// <summary>
    /// ゲーム保護として本体の退避を要求する。
    /// 本体側でフック解除とキー解放が終わり、プロセスが応答しなくなるまで確認する。
    /// </summary>
    bool RequestGameShutdown();

    /// <summary>本体を起動する。</summary>
    bool Start();
}

/// <summary>実プロセスを操作する既定実装。</summary>
public sealed class HostControl : IHostControl
{
    private readonly string _hostPath;
    private readonly int _shutdownTimeoutMs;

    public HostControl(string hostPath, int shutdownTimeoutMs = 10000)
    {
        _hostPath = hostPath;
        _shutdownTimeoutMs = shutdownTimeoutMs;
    }

    public bool IsRunning() => ControlClient.IsHostResponding(1000);

    public bool RequestGameShutdown()
    {
        var response = ControlClient.Send(ControlProtocol.Shutdown + " " + ControlProtocol.ReasonGame, 2000);
        if (response is null) return !IsRunning();
        if (response.StartsWith(ControlProtocol.ErrorPrefix, StringComparison.Ordinal)) return false;

        // 応答だけでは終了完了と判断しない。応答が止まるまで確認する。
        var deadline = DateTime.UtcNow.AddMilliseconds(_shutdownTimeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (!IsRunning()) return true;
            Thread.Sleep(200);
        }

        return false;
    }

    public bool Start()
    {
        if (!File.Exists(_hostPath)) return false;

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = _hostPath,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(_hostPath) ?? AppContext.BaseDirectory,
            });

            return process is not null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
