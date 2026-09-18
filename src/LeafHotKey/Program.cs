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
            case "--check-backend":
                return BackendSelfCheck.Run(args.Length > 1 ? args[1] : null);
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
            var disableIme = true;
            var backend = BackendSettings.Default;
            try
            {
                if (store is null)
                {
                    profiles = Array.Empty<HotkeyProfile>();
                }
                else
                {
                    var snapshot = store.Load();
                    profiles = snapshot.Profiles.ToArray();
                    disableIme = snapshot.ImeDisableBeforeSend;
                    backend = snapshot.Backend;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException or FormatException)
            {
                profiles = Array.Empty<HotkeyProfile>();
                settingsLoaded = false;
            }

            using (var engine = new InputEngine(profiles, disableIme))
            using (var ahk = new AhkBackend())
            {
                // 保存された設定を、内蔵エンジンと AHK のどちらか一方だけに反映する。
                void ApplySaved(SettingsSnapshot snapshot)
                {
                    engine.ApplyProfiles(snapshot.Profiles, snapshot.ImeDisableBeforeSend);
                    ApplyBackend(snapshot.Backend);
                }

                void ApplyBackend(BackendSettings settings)
                {
                    var previous = backend;
                    backend = settings;

                    if (settings.Mode == InputBackend.Ahk)
                    {
                        // 切り替え時は必ず内蔵フックを外してから AHK を起動する。
                        if (previous.Mode == InputBackend.Builtin) engine.Stop();
                        engine.Enabled = false;
                        ahk.Start(settings);
                        return;
                    }

                    ahk.Stop();
                    if (previous.Mode == InputBackend.Ahk)
                    {
                        engine.Start();
                    }

                    engine.Enabled = state.State == RuntimeState.Running;
                }

                // AHK に任せている間は内蔵フックを設置しない（同時稼働させない）。
                var engineStarted = backend.Mode == InputBackend.Builtin && engine.Start();
                if (backend.Mode == InputBackend.Ahk) ahk.Start(backend);

                // フックを設置できなかった場合は動作中として扱わない。
                if (!engineStarted && backend.Mode == InputBackend.Builtin) state.Pause();
                if (!settingsLoaded) state.Pause();
                state.StateChanged += next => engine.Enabled = next == RuntimeState.Running && backend.Mode == InputBackend.Builtin;

                SettingsServer? web = null;
                try
                {
                    if (store is not null)
                    {
                        // URL を固定するため、トークンを使わず既定ポートで待ち受ける。
                        var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
                        web = new SettingsServer(
                            store,
                            SettingsServer.DefaultPort,
                            statusJson: () => StatusJson(state, engine, ahk),
                            webRoot: webRoot,
                            onSaved: snapshot => ApplySaved(snapshot));
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
                                statusJson: () => StatusJson(state, engine, ahk),
                                webRoot: webRoot,
                                onSaved: snapshot => ApplySaved(snapshot));
                            web.Start();
                        }
                    }

                    ApplicationConfiguration.Initialize();
                    using var tray = new TrayApplication(state, server, web?.Url, () => backend.Mode == InputBackend.Ahk ? "AutoHotkey（バックエンド）" : "内蔵エンジン");
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
    private static string StatusJson(HostState state, InputEngine engine, AhkBackend ahk)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            state = state.State.ToString().ToLowerInvariant(),
            engineInstalled = engine.Installed,
            activeProfile = engine.ActiveProfileName,
            backend = engine.Installed ? "builtin" : "ahk",
            backendStatus = ahk.Status,
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
