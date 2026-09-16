using LeafHotKey;

namespace LeafHotKeyWatcher;

/// <summary>
/// ゲーム終了待機と本体再起動だけを担当するプロセス。
/// Phase 1 では本体の生存確認と起動のみを実装し、プロセス監視は Phase 2 で追加する。
/// </summary>
public static class Program
{
    public const int ExitOk = 0;
    public const int ExitFailed = 1;
    public const int ExitAlreadyRunning = 2;
    public const int ExitHostUnreachable = 3;

    public static int Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0].ToLowerInvariant() : string.Empty;

        switch (mode)
        {
            case "--ping":
                // 本体が応答しない場合は「停止した」と断定せず、到達不能として扱う。
                return ControlClient.IsHostResponding() ? ExitOk : ExitHostUnreachable;

            case "--start-host":
                return StartHost(args.Length > 1 ? args[1] : null);

            case "--check":
                return SelfCheck();

            default:
                Console.Error.WriteLine("usage: LeafHotKeyWatcher.exe [--ping|--start-host <path>|--check]");
                return ExitFailed;
        }
    }

    /// <summary>本体を起動する。既に応答しているなら二重起動しない。</summary>
    private static int StartHost(string? hostPath)
    {
        using var single = SingleInstance.TryAcquire("watcher");
        if (!single.Acquired) return ExitAlreadyRunning;

        if (ControlClient.IsHostResponding(500)) return ExitOk;

        var path = hostPath ?? Path.Combine(AppContext.BaseDirectory, "LeafHotKey.exe");
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"本体が見つかりません: {path}");
            return ExitFailed;
        }

        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(path) ?? AppContext.BaseDirectory,
            });

            return process is null ? ExitFailed : ExitOk;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Console.Error.WriteLine($"本体の起動に失敗しました: {ex.Message}");
            return ExitFailed;
        }
    }

    /// <summary>GUI を起動せずに Watcher 側の前提だけを検証する。</summary>
    private static int SelfCheck()
    {
        var failures = 0;

        void Check(string name, bool ok, string detail)
        {
            if (!ok) failures++;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}: {detail}");
        }

        var role = "watcher.selfcheck." + Guid.NewGuid().ToString("N");
        using (var first = SingleInstance.TryAcquire(role))
        {
            using var second = SingleInstance.TryAcquire(role);
            Check("singleinstance.first", first.Acquired, "1つ目の Watcher は起動できる");
            Check("singleinstance.second", !second.Acquired, "2つ目の Watcher は起動しない");
        }

        Check(
            "host.unreachable",
            !ControlClient.IsHostResponding(300, "LeafHotKey.absent." + Guid.NewGuid().ToString("N")),
            "本体へ接続できない場合は応答ありと判定しない");

        Check(
            "start-host.missing",
            StartHostPathMissing(),
            "本体の実行ファイルが無い場合は失敗を返す");

        Console.WriteLine($"failures={failures}");
        return failures == 0 ? ExitOk : ExitFailed;
    }

    private static bool StartHostPathMissing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "leafhotkey-absent-" + Guid.NewGuid().ToString("N") + ".exe");
        return StartHost(missing) == ExitFailed;
    }
}
