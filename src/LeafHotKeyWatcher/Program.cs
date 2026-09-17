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
        // 日本語ログをリダイレクトしても化けないよう、出力を UTF-8（BOMなし）に固定する。
        try
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        }
        catch (IOException)
        {
            // コンソールが無い環境では既定のまま使う。
        }

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

            case "--check-lifecycle":
                return WatcherSelfCheck.Run(args.Length > 1 ? args[1] : null);

            case "--run":
                return RunLoop(args.Length > 1 ? args[1] : null);

            default:
                Console.Error.WriteLine("usage: LeafHotKeyWatcher.exe [--ping|--start-host <path>|--check|--check-lifecycle [settings.json]|--run [settings.json]]");
                return ExitFailed;
        }
    }

    /// <summary>
    /// 本体の実行ファイルを探す。
    /// 発行物は同じフォルダーに置かれるが、開発時は別プロジェクトのビルド出力にあるため、
    /// どちらの配置でも復帰できるようにリポジトリ内も辿る。
    /// </summary>
    internal static string ResolveHostPath(string baseDirectory)
    {
        var local = Path.Combine(baseDirectory, "LeafHotKey.exe");
        if (File.Exists(local)) return local;

        var directory = new DirectoryInfo(baseDirectory);
        while (directory is not null)
        {
            foreach (var configuration in new[] { "Release", "Debug" })
            {
                var candidate = Path.Combine(directory.FullName, "src", "LeafHotKey", "bin", configuration, "net8.0-windows", "LeafHotKey.exe");
                if (File.Exists(candidate)) return candidate;
            }

            directory = directory.Parent;
        }

        return local;
    }

    /// <summary>本体を起動する。既に応答しているなら二重起動しない。</summary>
    private static int StartHost(string? hostPath)
    {
        using var single = SingleInstance.TryAcquire("watcher");
        if (!single.Acquired) return ExitAlreadyRunning;

        if (ControlClient.IsHostResponding(500)) return ExitOk;

        return LaunchHost(hostPath);
    }

    /// <summary>
    /// 本体の実行ファイルを起動する。
    /// 多重起動防止と生存確認は呼び出し側の責任とし、ここではパスの妥当性だけを扱う。
    /// </summary>
    internal static int LaunchHost(string? hostPath)
    {
        var path = hostPath ?? ResolveHostPath(AppContext.BaseDirectory);
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

    /// <summary>監視ループ。停止状態（手動終了・退避失敗・起動失敗）になったら終了する。</summary>
    private static int RunLoop(string? settingsPath)
    {
        using var single = SingleInstance.TryAcquire("watcher");
        if (!single.Acquired)
        {
            Console.Error.WriteLine("Watcher は既に動作しています。");
            return ExitAlreadyRunning;
        }

        var path = settingsPath ?? FindDefaultSettings();
        if (path is null)
        {
            Console.Error.WriteLine("settings.json が見つかりません。");
            return ExitFailed;
        }

        GameProtectionSettings settings;
        try
        {
            settings = GameProtectionSettings.Load(path);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or IOException)
        {
            Console.Error.WriteLine($"設定を読み込めません: {ex.Message}");
            return ExitFailed;
        }

        var hostPath = ResolveHostPath(AppContext.BaseDirectory);
        var controller = new LifecycleController(settings, new GameMonitor(), new HostControl(hostPath));

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stop.Cancel();
        };

        var lastMessage = string.Empty;
        while (!stop.IsCancellationRequested && controller.State != LifecycleState.Stopped)
        {
            controller.Tick(DateTimeOffset.UtcNow);
            if (controller.LastMessage != lastMessage && controller.LastMessage.Length > 0)
            {
                lastMessage = controller.LastMessage;
                Console.WriteLine(lastMessage);
            }

            if (stop.Token.WaitHandle.WaitOne(settings.PollIntervalMs)) break;
        }

        if (controller.LastMessage.Length > 0 && controller.LastMessage != lastMessage)
        {
            Console.WriteLine(controller.LastMessage);
        }

        return controller.StopCause is StopCause.ShutdownFailed or StopCause.StartFailed ? ExitFailed : ExitOk;
    }

    /// <summary>実行ディレクトリから上位へ defaults/settings.json を探す。</summary>
    private static string? FindDefaultSettings()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "defaults", "settings.json");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        return null;
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

    /// <summary>
    /// 実行ファイルが無い場合の扱いを確かめる。
    /// 本体や Watcher が実際に動作していても結果が変わらないよう、起動部分だけを呼ぶ。
    /// </summary>
    private static bool StartHostPathMissing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "leafhotkey-absent-" + Guid.NewGuid().ToString("N") + ".exe");
        return LaunchHost(missing) == ExitFailed;
    }
}
