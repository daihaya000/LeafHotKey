using System.Windows.Forms;

namespace LeafHotKey;

public static class Program
{
    /// <summary>終了コード: 0 正常 / 1 検証失敗 / 2 多重起動 / 3 本体へ接続できない。</summary>
    public const int ExitOk = 0;
    public const int ExitCheckFailed = 1;
    public const int ExitAlreadyRunning = 2;
    public const int ExitHostUnreachable = 3;

    [STAThread]
    public static int Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0].ToLowerInvariant() : string.Empty;

        switch (mode)
        {
            case "--check":
                return SelfCheck.Run(args.Length > 1 ? args[1] : null);
            case "--check-profiles":
                return ProfileSelfCheck.Run(
                    args.Length > 1 ? args[1] : null,
                    args.Length > 2 ? args[2] : null);
            case "--status":
                return SendToHost(ControlProtocol.Status);
            case "--pause":
                return SendToHost(ControlProtocol.Pause);
            case "--resume":
                return SendToHost(ControlProtocol.Resume);
            case "--shutdown":
                return SendToHost(ControlProtocol.Shutdown);
            default:
                return RunTray();
        }
    }

    private static int SendToHost(string command)
    {
        var response = ControlClient.Send(command);
        if (response is null) return ExitHostUnreachable;
        return response.StartsWith(ControlProtocol.ErrorPrefix, StringComparison.Ordinal)
            ? ExitCheckFailed
            : ExitOk;
    }

    private static int RunTray()
    {
        using var single = SingleInstance.TryAcquire("host");
        if (!single.Acquired) return ExitAlreadyRunning;

        var state = new HostState();
        using var server = new ControlServer(state);
        server.Start();

        ApplicationConfiguration.Initialize();
        using var tray = new TrayApplication(state, server);
        Application.Run(tray);

        // 終了理由が未設定のまま Application.Run を抜けた場合も手動終了として扱う。
        if (state.ExitReason == ExitReason.None) state.BeginShutdown(ExitReason.Manual);
        return ExitOk;
    }
}
