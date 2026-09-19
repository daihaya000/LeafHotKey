using System.Text.Json;

namespace LeafHotKey;

/// <summary>
/// 入力エンジン本体。入力フックを設置してキー変換を行う。
/// ゲーム保護の退避ではこのプロセスだけが終了し、WebUI（LeafHotKey.exe）は動き続ける。
/// </summary>
public static class Program
{
    /// <summary>終了コード: 0 正常 / 1 検証失敗・起動失敗 / 2 多重起動。</summary>
    public const int ExitOk = 0;
    public const int ExitFailed = 1;
    public const int ExitAlreadyRunning = 2;

    [STAThread]
    public static int Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0].ToLowerInvariant() : string.Empty;

        switch (mode)
        {
            case "--check":
                return SelfCheck.Run(args.Length > 1 ? args[1] : null);
            case "--check-hook":
                return HookSelfCheck.Run(args.Length > 1 ? args[1] : null);
            case "--check-engine":
                return EngineSelfCheck.Run(
                    args.Length > 1 ? args[1] : null,
                    args.Length > 2 ? args[2] : null);
            case "--check-send":
                return SendSelfCheck.Run(args.Length > 1 ? args[1] : null);
            case "--check-coverage":
                return CoverageSelfCheck.Run(
                    args.Length > 1 ? args[1] : null,
                    args.Length > 2 ? args[2] : null);
            case "--run":
                return RunEngine();
            default:
                return RunEngine();
        }
    }

    /// <summary>
    /// 入力エンジンを常駐させる。終了要求を受けるまでここで待つ。
    /// 本体（LeafHotKey.exe）が起動し、停止後は同じ本体が起動し直す。
    /// </summary>
    private static int RunEngine()
    {
        // フックが二重にならないよう、エンジンは 1 つだけ動作する。
        using var single = SingleInstance.TryAcquire("engine");
        if (!single.Acquired) return ExitAlreadyRunning;

        var defaultsPath = FindDefaultSettings();
        if (defaultsPath is null) return ExitFailed;

        var store = new SettingsStore(SettingsStore.DefaultSettingsPath, defaultsPath);

        HotkeyProfile[] profiles;
        var settingsLoaded = true;
        var disableIme = true;
        try
        {
            var snapshot = store.Load();
            profiles = snapshot.Profiles.ToArray();
            disableIme = snapshot.ImeDisableBeforeSend;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or FormatException)
        {
            profiles = Array.Empty<HotkeyProfile>();
            settingsLoaded = false;
        }

        var state = new HostState();
        var eventLog = new EventLog();
        using var engine = new InputEngine(profiles, disableIme);
        engine.Trace = eventLog.Add;

        // 本体（WebUI）が設定を保存した後の再読込。保存の検証は本体側で済んでいる。
        bool Reload()
        {
            try
            {
                var snapshot = store.Load();
                engine.ApplyProfiles(snapshot.Profiles, snapshot.ImeDisableBeforeSend);
                eventLog.Add("設定を読み直しました。");
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or FormatException)
            {
                eventLog.Add("設定を読み直せませんでした: " + ex.Message);
                return false;
            }
        }

        using var server = new ControlServer(
            state,
            ControlProtocol.EnginePipeName,
            statusJson: () => StatusJson(state, engine),
            logJson: () => eventLog.ToJson(),
            reload: Reload);

        using var quit = new ManualResetEventSlim(false);
        server.ShutdownRequested += () => quit.Set();
        server.Start();

        // フックを設置できなかった場合は動作中として扱わない。
        var engineStarted = engine.Start();
        if (!engineStarted) state.Pause();
        if (!settingsLoaded) state.Pause();
        state.StateChanged += next => engine.Enabled = next == RuntimeState.Running;

        quit.Wait();

        // ゲーム保護の退避を含め、終了前に必ずフック解除とキー解放を行う。
        engine.Stop();
        return ExitOk;
    }

    /// <summary>本体の WebUI へ返す現在の状態。本体はそのまま設定画面へ渡す。</summary>
    private static string StatusJson(HostState state, InputEngine engine)
        => JsonSerializer.Serialize(new
        {
            state = state.State.ToString().ToLowerInvariant(),
            engineInstalled = engine.Installed,
            activeProfile = engine.ActiveProfileName,
            heldModifiers = engine.HeldModifiers.ToArray(),
            holdSweeps = engine.HoldSweeps,
            holdReasserts = engine.HoldReasserts,
            holdReleases = engine.HoldReleases,
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
