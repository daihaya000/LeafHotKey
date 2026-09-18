using System.Diagnostics;
using System.Windows.Forms;

namespace LeafHotKey;

/// <summary>
/// 本体（トレイ常駐）。設定画面（WebUI）を配信し、ゲーム保護の監視と入力エンジンの起動・停止を担当する。
/// 入力変換そのものは LeafHotKeyEngine.exe が行うため、ゲーム保護でエンジンが退避してもこの画面は動き続ける。
/// </summary>
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
            case "--check-server":
                return ServerSelfCheck.Run(
                    args.Length > 1 ? args[1] : null,
                    args.Length > 2 ? args[2] : null);
            case "--check-settings":
                return SettingsSelfCheck.Run(
                    args.Length > 1 ? args[1] : null,
                    args.Length > 2 ? args[2] : null);
            case "--check-profiles":
                return ProfileSelfCheck.Run(
                    args.Length > 1 ? args[1] : null,
                    args.Length > 2 ? args[2] : null);
            case "--check-watch":
                return WatcherSelfCheck.Run(
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
                return RunHost();
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

    private static int RunHost()
    {
        var restartRequested = false;

        // 再起動する本体は、このブロックを抜けて Mutex・WebUI・監視を全て解放してから起動する。
        using (var single = SingleInstance.TryAcquire("host"))
        {
            if (!single.Acquired) return ExitAlreadyRunning;

            var state = new HostState();
            var eventLog = new EventLog();

            // 設定の正本はユーザーごとの保存先。無ければ既定設定から作る。
            var defaultsPath = FindDefaultSettings();
            var store = defaultsPath is null ? null : new SettingsStore(SettingsStore.DefaultSettingsPath, defaultsPath);

            // ゲーム保護の監視。設定を読めない場合は監視しない（勝手に退避しない）。
            GameProtectionSettings? protection = null;
            try
            {
                if (store is not null)
                {
                    var snapshot = store.Load();
                    protection = snapshot.GameProtection;

                    // 起動時にも AHK 用スクリプトを合わせておく（前回終了後の変更を取り込む）。
                    PrepareAhkScript(snapshot.Backend, snapshot.Json);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException or FormatException)
            {
                protection = null;
            }

            var engine = new EngineControl(ResolveEnginePath(AppContext.BaseDirectory));
            var controller = protection is null ? null : new LifecycleController(protection, new GameMonitor(), engine);

            using var server = new ControlServer(state);
            server.Override = command => HandleCommand(command, state, server, engine, eventLog);
            server.Start();

            using var supervisor = new HostSupervisor(controller, engine, state, eventLog);

            SettingsServer? web = null;
            try
            {
                if (store is not null)
                {
                    // URL を固定するため、トークンを使わず既定ポートで待ち受ける。
                    var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
                    web = CreateServer(store, SettingsServer.DefaultPort, webRoot, engine, supervisor, eventLog);
                    try
                    {
                        web.Start();
                    }
                    catch (System.Net.Sockets.SocketException)
                    {
                        // 固定ポートが他のアプリに使われている場合だけ空きポートへ退避する。
                        web.Dispose();
                        web = CreateServer(store, port: 0, webRoot, engine, supervisor, eventLog);
                        web.Start();
                    }
                }

                // 入力エンジンが動いていなければ起動する（多重起動の判定はエンジン側）。
                if (!engine.IsRunning()) engine.Start();
                supervisor.Start();

                ApplicationConfiguration.Initialize();
                using var tray = new TrayApplication(
                    state,
                    server,
                    web?.Url,
                    () => supervisor.BackendLabel,
                    () => supervisor.IsAhkBackend,
                    restartAhk: () =>
                    {
                        var response = engine.Forward(ControlProtocol.RestartBackend);
                        eventLog.Add(response is null
                            ? "入力エンジンが停止しているため、AHK を再起動できません。"
                            : "AHK を再起動しました。");
                        supervisor.RequestRefresh();
                    },
                    statusText: () => supervisor.StatusText,
                    toggle: () =>
                    {
                        var command = state.State == RuntimeState.Running ? ControlProtocol.Pause : ControlProtocol.Resume;
                        var response = engine.Forward(command);
                        if (response is null) eventLog.Add("入力エンジンが停止しているため、操作できません。");
                        supervisor.RequestRefresh();
                    },
                    startEngine: () =>
                    {
                        if (engine.Start()) eventLog.Add("入力エンジンを起動しました。");
                        else eventLog.Add("入力エンジンを起動できませんでした。");
                        supervisor.ResumeWatching();
                        supervisor.RequestRefresh();
                    });

                void OnStatusChanged() => tray.RefreshStatus();
                supervisor.StatusChanged += OnStatusChanged;

                Action requestRestart = () => restartRequested = true;
                tray.RestartRequested += requestRestart;
                Application.Run(tray);
                tray.RestartRequested -= requestRestart;
                supervisor.StatusChanged -= OnStatusChanged;
            }
            finally
            {
                supervisor.Dispose();
                web?.Dispose();

                // 本体の終了時は入力エンジンも止める（フック解除とキー解放はエンジン側で行う）。
                engine.Forward(ControlProtocol.Shutdown + " " + ControlProtocol.ReasonManual);

                // 終了理由が未設定のまま Application.Run を抜けた場合も手動終了として扱う。
                if (state.ExitReason == ExitReason.None) state.BeginShutdown(ExitReason.Manual);
            }
        }

        return restartRequested ? RestartHost() : ExitOk;
    }

    /// <summary>設定画面のサーバーを作る。保存された内容は入力エンジンへ通知して反映する。</summary>
    private static SettingsServer CreateServer(
        SettingsStore store,
        int port,
        string webRoot,
        EngineControl engine,
        HostSupervisor supervisor,
        EventLog eventLog)
        => new(
            store,
            port,
            statusJson: () => engine.StatusJson(),
            webRoot: webRoot,
            onSaved: snapshot =>
            {
                // AHK の生成スクリプトは本体側で書き出し、入力エンジンには再読込だけを伝える。
                PrepareAhkScript(snapshot.Backend, snapshot.Json);
                supervisor.UpdateSettings(snapshot.GameProtection);

                var response = engine.Forward(ControlProtocol.Reload);
                eventLog.Add(response is null
                    ? "設定を保存しました（入力エンジンは停止中）。"
                    : "設定を保存して反映しました。");
                supervisor.RequestRefresh();
            },
            logJson: () => engine.LogJson(eventLog.Snapshot()));

    /// <summary>CLI からのコマンドを、本体の終了または入力エンジンへの転送として扱う。</summary>
    private static string? HandleCommand(string command, HostState state, ControlServer server, EngineControl engine, EventLog eventLog)
    {
        var parts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var verb = parts.Length > 0 ? parts[0].ToUpperInvariant() : string.Empty;
        var argument = parts.Length > 1 ? parts[1].ToUpperInvariant() : string.Empty;

        switch (verb)
        {
            case ControlProtocol.Ping:
                return ControlProtocol.Pong;

            case ControlProtocol.Status when argument == ControlProtocol.StatusJson:
                return ControlProtocol.Ok(engine.StatusJson());

            case ControlProtocol.Log:
                return ControlProtocol.Ok(engine.LogJson(eventLog.Snapshot()));

            case ControlProtocol.Status:
            case ControlProtocol.Pause:
            case ControlProtocol.Resume:
            case ControlProtocol.Reload:
            case ControlProtocol.RestartBackend:
                return engine.Forward(command) ?? StoppedResponse(verb);

            case ControlProtocol.Shutdown:
            {
                // 本体（WebUI）を終了する。入力エンジンは終了処理で止める。
                if (argument.Length > 0 && argument != ControlProtocol.ReasonGame && argument != ControlProtocol.ReasonManual)
                {
                    return ControlProtocol.Error("UNKNOWN_REASON");
                }

                var reason = argument == ControlProtocol.ReasonGame ? ExitReason.GameProtection : ExitReason.Manual;
                state.BeginShutdown(reason);
                server.RequestShutdown();
                return ControlProtocol.Ok("SHUTTINGDOWN " + (reason == ExitReason.GameProtection
                    ? ControlProtocol.ReasonGame
                    : ControlProtocol.ReasonManual));
            }

            default:
                return ControlProtocol.Error("UNKNOWN_COMMAND");
        }
    }

    /// <summary>入力エンジンが停止している場合の応答。停止を「動作中」と偽らない。</summary>
    private static string StoppedResponse(string verb)
    {
        if (verb == ControlProtocol.Status) return ControlProtocol.Ok(ControlProtocol.StateStopped);
        return ControlProtocol.Error("ENGINE_STOPPED");
    }

    /// <summary>設定画面の内容を AHK 用スクリプトへ書き出す。</summary>
    private static void PrepareAhkScript(BackendSettings settings, string json)
    {
        if (settings.Mode != InputBackend.Ahk || !settings.GenerateScript || json.Length == 0) return;

        try
        {
            AhkScriptWriter.Write(json, AhkScriptWriter.PathFor(settings.AhkScript));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 書き出せない場合は設定済みのスクリプトをそのまま使う。
        }
    }

    /// <summary>
    /// 入力エンジンの実行ファイルを探す。
    /// 発行物は同じフォルダーに置かれるが、開発時は別プロジェクトのビルド出力にあるため、
    /// どちらの配置でも見つけられるようにする。
    /// </summary>
    internal static string ResolveEnginePath(string baseDirectory)
    {
        var local = Path.Combine(baseDirectory, "LeafHotKeyEngine.exe");
        if (File.Exists(local)) return local;

        var directory = new DirectoryInfo(baseDirectory);
        while (directory is not null)
        {
            foreach (var configuration in new[] { "Release", "Debug" })
            {
                var candidate = Path.Combine(directory.FullName, "src", "LeafHotKeyEngine", "bin", configuration, "net8.0-windows", "LeafHotKeyEngine.exe");
                if (File.Exists(candidate)) return candidate;
            }

            directory = directory.Parent;
        }

        return local;
    }

    private static int RestartHost()
    {
        try
        {
            var launcher = FindRestartLauncher();
            if (launcher is not null)
            {
                // トレイ再起動も通常起動と同じバッチを通し、ソース更新時の Release ビルド判定を行う。
                // 旧プロセスが Mutex を解放してからバッチが --status を見るよう短く待つ。
                var command = $"timeout /t 1 /nobreak >nul & call \"{launcher}\"";
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                    ArgumentList = { "/d", "/c", command },
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(launcher) ?? AppContext.BaseDirectory,
                });
                return process is null ? ExitCheckFailed : ExitOk;
            }

            // 発行物だけの環境にはリポジトリの起動バッチが無いため、従来どおり本体を直接再起動する。
            using var fallback = Process.Start(new ProcessStartInfo
            {
                FileName = Application.ExecutablePath,
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory,
            });
            return fallback is null ? ExitCheckFailed : ExitOk;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return ExitCheckFailed;
        }
    }

    private static string? FindRestartLauncher()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Start-LeafHotKey.bat");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        return null;
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
}
