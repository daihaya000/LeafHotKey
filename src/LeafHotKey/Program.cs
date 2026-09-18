using System.Net.Sockets;
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
            case "--check-server":
                return ServerSelfCheck.Run(
                    args.Length > 1 ? args[1] : null,
                    args.Length > 2 ? args[2] : null);
            case "--check-settings":
                return SettingsSelfCheck.Run(
                    args.Length > 1 ? args[1] : null,
                    args.Length > 2 ? args[2] : null);
            case "--check-coverage":
                return CoverageSelfCheck.Run(
                    args.Length > 1 ? args[1] : null,
                    args.Length > 2 ? args[2] : null);
            case "--check-hook":
                return HookSelfCheck.Run(args.Length > 1 ? args[1] : null);
            case "--check-engine":
                return EngineSelfCheck.Run(
                    args.Length > 1 ? args[1] : null,
                    args.Length > 2 ? args[2] : null);
            case "--check-send":
                return SendSelfCheck.Run(args.Length > 1 ? args[1] : null);
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
        var restartRequested = false;

        // 再起動する本体は、このブロックを抜けて Mutex・フック・WebUI を全て解放してから起動する。
        using (var single = SingleInstance.TryAcquire("host"))
        {
            if (!single.Acquired) return ExitAlreadyRunning;

            var state = new HostState();
            using var server = new ControlServer(state);
            server.Start();

            // 設定の正本はユーザーごとの保存先。無ければ既定設定から作る。
            var defaultsPath = FindDefaultSettings();
            var store = defaultsPath is null ? null : new SettingsStore(SettingsStore.DefaultSettingsPath, defaultsPath);

            // 設定を読めない場合でもトレイは起動させる（無言で終了しない）。
            HotkeyProfile[] profiles;
            var settingsLoaded = true;
            try
            {
                profiles = store is null ? Array.Empty<HotkeyProfile>() : store.Load().Profiles.ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException or FormatException)
            {
                profiles = Array.Empty<HotkeyProfile>();
                settingsLoaded = false;
            }

            using (var engine = new InputEngine(profiles))
            {
                var engineStarted = engine.Start();

                // フックを設置できなかった場合は動作中として扱わない。
                if (!engineStarted || !settingsLoaded) state.Pause();
                state.StateChanged += next => engine.Enabled = next == RuntimeState.Running;

                SettingsServer? web = null;
                try
                {
                    if (store is not null)
                    {
                        // URL を起動ごとに変えないよう、固定ポートと保存したトークンを使う。
                        var token = WebUiIdentity.LoadOrCreate(WebUiIdentity.TokenPath);
                        var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
                        web = new SettingsServer(
                            store,
                            WebUiIdentity.DefaultPort,
                            statusJson: () => StatusJson(state, engine),
                            webRoot: webRoot,
                            onSaved: snapshot => engine.ApplyProfiles(snapshot.Profiles),
                            token: token);
                        try
                        {
                            web.Start();
                        }
                        catch (SocketException)
                        {
                            // 固定ポートが他のアプリに使われている場合だけ空きポートへ退避する。
                            web.Dispose();
                            web = new SettingsServer(
                                store,
                                port: 0,
                                statusJson: () => StatusJson(state, engine),
                                webRoot: webRoot,
                                onSaved: snapshot => engine.ApplyProfiles(snapshot.Profiles),
                                token: token);
                            web.Start();
                        }
                    }

                    ApplicationConfiguration.Initialize();
                    using var tray = new TrayApplication(state, server, web?.Url);
                    Action requestRestart = () => restartRequested = true;
                    tray.RestartRequested += requestRestart;
                    Application.Run(tray);
                    tray.RestartRequested -= requestRestart;
                }
                finally
                {
                    web?.Dispose();

                    // ゲーム保護の退避を含め、終了前に必ずフック解除とキー解放を行う。
                    engine.Stop();

                    // 終了理由が未設定のまま Application.Run を抜けた場合も手動終了として扱う。
                    if (state.ExitReason == ExitReason.None) state.BeginShutdown(ExitReason.Manual);
                }
            }
        }

        return restartRequested ? RestartHost() : ExitOk;
    }

    private static int RestartHost()
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Application.ExecutablePath,
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory,
            });
            return process is null ? ExitCheckFailed : ExitOk;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return ExitCheckFailed;
        }
    }

    /// <summary>WebUI へ返す現在の状態。</summary>
    private static string StatusJson(HostState state, InputEngine engine)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            state = state.State.ToString().ToLowerInvariant(),
            engineInstalled = engine.Installed,
            activeProfile = engine.ActiveProfileName,
        });

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
