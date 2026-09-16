using LeafHotKey;

namespace LeafHotKeyWatcher;

/// <summary>GUI や実ゲームに依存せず、Watcher のライフサイクル判断を検証する。</summary>
public static class WatcherSelfCheck
{
    /// <summary>本体操作の差し替え実装。呼び出し回数と成否を制御する。</summary>
    private sealed class FakeHost : IHostControl
    {
        public bool Running { get; set; } = true;
        public bool ShutdownSucceeds { get; set; } = true;
        public bool StartSucceeds { get; set; } = true;
        public int ShutdownCalls { get; private set; }
        public int StartCalls { get; private set; }

        public bool IsRunning() => Running;

        public bool RequestGameShutdown()
        {
            ShutdownCalls++;
            if (!ShutdownSucceeds) return false;
            Running = false;
            return true;
        }

        public bool Start()
        {
            StartCalls++;
            if (!StartSucceeds) return false;
            Running = true;
            return true;
        }
    }

    public static int Run(string? settingsPath)
    {
        var failures = 0;

        void Check(string name, bool ok, string detail)
        {
            if (!ok) failures++;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}: {detail}");
        }

        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var monitor = new GameMonitor(name => running.Contains(name));
        var settings = new GameProtectionSettings
        {
            Enabled = true,
            PollIntervalMs = 1000,
            ResumeDelayMs = 1000,
            StopTriggerProcessNames = new[] { "GameA.exe", "AntiCheat.exe" },
            ResumeProcessNames = new[] { "GameA.exe" },
        };

        var now = DateTimeOffset.UnixEpoch;
        var host = new FakeHost();
        var controller = new LifecycleController(settings, monitor, host);

        controller.Tick(now);
        Check("idle", controller.State == LifecycleState.HostRunning && host.ShutdownCalls == 0, "危険プロセスが無ければ何もしない");

        running.Add("GameA.exe");
        controller.Tick(now);
        Check("detect", controller.State == LifecycleState.WaitingGameExit, "検知したら退避状態へ移る");
        Check("detect.shutdown", host.ShutdownCalls == 1 && !host.Running, "本体へ退避を要求する");
        Check("detect.nokill", running.Contains("GameA.exe"), "ゲームプロセスは終了させない");

        now = now.AddSeconds(5);
        controller.Tick(now);
        Check("wait.running", controller.State == LifecycleState.WaitingGameExit && host.StartCalls == 0, "対象が残る間は復帰しない");

        running.Remove("GameA.exe");
        controller.Tick(now);
        Check("wait.cleared", controller.State == LifecycleState.WaitingResumeDelay, "対象が消えたら復帰待機に入る");

        now = now.AddMilliseconds(500);
        controller.Tick(now);
        Check("wait.delay", controller.State == LifecycleState.WaitingResumeDelay && host.StartCalls == 0, "待機時間中は復帰しない");

        running.Add("GameA.exe");
        controller.Tick(now);
        Check("wait.reappear", controller.State == LifecycleState.WaitingGameExit && host.StartCalls == 0, "再出現したら待機をやり直す");

        running.Remove("GameA.exe");
        controller.Tick(now);
        now = now.AddSeconds(2);
        running.Add("AntiCheat.exe");
        controller.Tick(now);
        Check("wait.recheck", controller.State == LifecycleState.WaitingGameExit && host.StartCalls == 0, "復帰直前の再確認で停止トリガが残れば復帰しない");

        running.Remove("AntiCheat.exe");
        controller.Tick(now);
        now = now.AddSeconds(2);
        controller.Tick(now);
        Check("resume", controller.State == LifecycleState.HostRunning, "条件が揃えば復帰する");
        Check("resume.start", host.StartCalls == 1 && host.Running, "本体を 1 回だけ起動する");

        // 手動終了は自動復帰しない。
        var manualHost = new FakeHost { Running = false };
        var manual = new LifecycleController(settings, new GameMonitor(_ => false), manualHost);
        manual.Tick(now);
        Check("manual.stop", manual.State == LifecycleState.Stopped && manual.StopCause == StopCause.ManualExit, "手動終了を停止として扱う");
        manual.Tick(now.AddMinutes(1));
        Check("manual.norestart", manualHost.StartCalls == 0, "手動終了後に再起動しない");

        // 退避に失敗したら保護済みとして扱わない。
        var failingHost = new FakeHost { ShutdownSucceeds = false };
        var failing = new LifecycleController(settings, new GameMonitor(name => name == "GameA.exe"), failingHost);
        failing.Tick(now);
        Check("shutdown.failed", failing.State == LifecycleState.Stopped && failing.StopCause == StopCause.ShutdownFailed, "退避失敗は停止として報告する");

        // 起動失敗は上限まで再試行してから停止する。
        // 復帰待機まで進める必要があるため、一度検知させてから対象を消す。
        var startFailRunning = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "GameA.exe" };
        var startFailHost = new FakeHost { StartSucceeds = false };
        var startFail = new LifecycleController(
            settings,
            new GameMonitor(name => startFailRunning.Contains(name)),
            startFailHost,
            maxStartAttempts: 3);

        var startFailTime = now;
        startFail.Tick(startFailTime);
        startFailRunning.Clear();
        startFail.Tick(startFailTime);
        for (var i = 0; i < 10 && startFail.State != LifecycleState.Stopped; i++)
        {
            startFailTime = startFailTime.AddSeconds(2);
            startFail.Tick(startFailTime);
        }

        Check(
            "start.failed",
            startFail.State == LifecycleState.Stopped && startFail.StopCause == StopCause.StartFailed && startFailHost.StartCalls == 3,
            "起動失敗は上限 3 回で停止する");

        // 実プロセス判定。
        var selfImage = System.Diagnostics.Process.GetCurrentProcess().ProcessName + ".exe";
        Check("probe.self", GameMonitor.ProcessExists(selfImage), $"実行中プロセス {selfImage} を検出する");
        Check("probe.absent", !GameMonitor.ProcessExists("leafhotkey-absent-process.exe"), "存在しないプロセスは検出しない");

        // 既定設定の読み込み。
        var path = settingsPath ?? FindDefaultSettings();
        if (path is null)
        {
            Check("settings.load", false, "defaults/settings.json が見つからない");
        }
        else
        {
            var loaded = GameProtectionSettings.Load(path);
            Check("settings.load", loaded.StopTriggerProcessNames.Count == 6 && loaded.ResumeProcessNames.Count == 3, "停止6種・復帰3種を読み込む");
            Check("settings.poll", loaded.PollIntervalMs == 1000 && loaded.ResumeDelayMs == 1000, "監視間隔と待機時間を読み込む");
        }

        var invalid = new GameProtectionSettings
        {
            Enabled = true,
            PollIntervalMs = 1000,
            ResumeDelayMs = 0,
            StopTriggerProcessNames = Array.Empty<string>(),
            ResumeProcessNames = Array.Empty<string>(),
        };

        var rejected = false;
        try
        {
            invalid.Validate();
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }

        Check("settings.invalid", rejected, "保護有効なのに停止対象が空の設定を拒否する");

        Console.WriteLine($"failures={failures}");
        return failures == 0 ? 0 : 1;
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
