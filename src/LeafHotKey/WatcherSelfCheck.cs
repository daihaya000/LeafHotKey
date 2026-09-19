using System.Text;

namespace LeafHotKey;

/// <summary>GUI や実ゲームに依存せず、ゲーム保護のライフサイクル判断を検証する。</summary>
public static class WatcherSelfCheck
{
    /// <summary>入力エンジン操作の差し替え実装。呼び出し回数と成否を制御する。</summary>
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

    public static int Run(string? reportPath = null, string? settingsPath = null)
    {
        var path = reportPath ?? Path.Combine(Path.GetTempPath(), "leafhotkey-watchcheck.txt");
        var encoding = new UTF8Encoding(false);
        var failures = 0;

        File.WriteAllText(path, string.Empty, encoding);

        void Check(string name, bool ok, string detail)
        {
            if (!ok) failures++;
            File.AppendAllText(path, $"{(ok ? "PASS" : "FAIL")} {name}: {detail}{Environment.NewLine}", encoding);
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
        Check("detect.shutdown", host.ShutdownCalls == 1 && !host.Running, "入力エンジンへ退避を要求する");
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
        Check("resume.start", host.StartCalls == 1 && host.Running, "入力エンジンを 1 回だけ起動する");

        // 手動終了は自動復帰しない。
        var manualHost = new FakeHost { Running = false };
        var manual = new LifecycleController(settings, new GameMonitor(_ => false), manualHost);
        manual.Tick(now);
        Check("manual.stop", manual.State == LifecycleState.Stopped && manual.StopCause == StopCause.ManualExit, "手動終了を停止として扱う");
        manual.Tick(now.AddMinutes(1));
        Check("manual.norestart", manualHost.StartCalls == 0, "手動終了後に再起動しない");

        // 本体から再開を指示された場合は監視へ戻る。
        manual.ResumeWatching();
        Check("manual.rearm", manual.State == LifecycleState.HostRunning && manual.StopCause == StopCause.None, "見直し指示で監視を再開する");

        // 入力エンジンが動き出したら、停止状態からも監視を再開する。
        var recoveredHost = new FakeHost { Running = false };
        var recovered = new LifecycleController(settings, new GameMonitor(_ => false), recoveredHost);
        recovered.Tick(now);
        Check("recovered.stop", recovered.State == LifecycleState.Stopped, "エンジンが消えている間は停止として扱う");
        recoveredHost.Running = true;
        recovered.Tick(now.AddSeconds(1));
        Check("recovered.rearm", recovered.State == LifecycleState.HostRunning, "エンジンが戻れば監視を再開する");
        recoveredHost.Running = false;
        recovered.Tick(now.AddSeconds(2));
        Check("recovered.restop", recovered.State == LifecycleState.Stopped, "再び停止したら停止として扱う");

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

        // 保存された設定の反映。
        controller.UpdateSettings(new GameProtectionSettings
        {
            Enabled = false,
            PollIntervalMs = 500,
            ResumeDelayMs = 1000,
            StopTriggerProcessNames = new[] { "GameA.exe" },
            ResumeProcessNames = Array.Empty<string>(),
        });
        Check("settings.update", controller.PollIntervalMs == 500, "保存された監視間隔を反映する");

        // 実プロセス判定。
        var selfImage = System.Diagnostics.Process.GetCurrentProcess().ProcessName + ".exe";
        Check("probe.self", GameMonitor.ProcessExists(selfImage), $"実行中プロセス {selfImage} を検出する");
        Check("probe.absent", !GameMonitor.ProcessExists("leafhotkey-absent-process.exe"), "存在しないプロセスは検出しない");

        // 既定設定の読み込み。
        var settingsFile = settingsPath ?? FindDefaultSettings();
        if (settingsFile is null)
        {
            Check("settings.load", false, "defaults/settings.json が見つからない");
        }
        else
        {
            var loaded = GameProtectionSettings.Load(settingsFile);
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

        // トレイ再起動は起動バッチを cmd.exe 経由で呼ぶ。引数の渡し方が崩れるとバッチが 1 回も実行されない。
        var restartProbe = Path.Combine(Path.GetTempPath(), "leafhotkey-restart-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(restartProbe);
            var probeLauncher = Path.Combine(restartProbe, "Start-LeafHotKey.bat");
            var probeResult = Path.Combine(restartProbe, "ran.txt");
            // 起動バッチは引数 restart で「旧プロセスの終了待ち」を行うため、引数も検証する。
            File.WriteAllText(probeLauncher, "@echo off\r\necho %~1>\"%~dp0ran.txt\"\r\n");

            Program.StartRestartLauncher(probeLauncher);
            var launched = false;
            for (var i = 0; i < 40 && !launched; i++)
            {
                Thread.Sleep(250);
                launched = File.Exists(probeResult);
            }

            var argument = launched ? File.ReadAllText(probeResult).Trim() : string.Empty;
            Check("restart.launcher", argument == "restart", "トレイ再起動のバッチを restart 引数付きで実行できる");
        }
        finally
        {
            Directory.Delete(restartProbe, recursive: true);
        }

        // ゲーム保護の開始・終了はトレイのバルーンで知らせる。
        Check(
            "protection.notice.start",
            Program.ProtectionNotice(false, LifecycleState.WaitingGameExit, StopCause.None) == "ゲームを検知しました。入力エンジンを停止しました。",
            "保護の作動開始で通知する");
        Check(
            "protection.notice.end",
            Program.ProtectionNotice(true, LifecycleState.HostRunning, StopCause.None) == "ゲーム終了を検知しました。入力エンジンを再開しました。",
            "保護の終了で通知する");
        Check(
            "protection.notice.once",
            Program.ProtectionNotice(true, LifecycleState.WaitingResumeDelay, StopCause.None) is null,
            "作動中は同じ通知を繰り返さない");
        Check(
            "protection.notice.failed",
            Program.ProtectionNotice(true, LifecycleState.Stopped, StopCause.StartFailed) is not null,
            "復帰できない停止は通知する");
        Check(
            "protection.notice.shutdownfailed",
            Program.ProtectionNotice(false, LifecycleState.Stopped, StopCause.ShutdownFailed) is not null,
            "退避に失敗した場合は保護が作動していなくても通知する");
        Check(
            "protection.notice.manual",
            Program.ProtectionNotice(true, LifecycleState.Stopped, StopCause.ManualExit) is null
                && Program.ProtectionNotice(false, LifecycleState.HostRunning, StopCause.None) is null,
            "手動停止と保護外の遷移では通知しない");

        // 入力エンジンの場所は、発行物（同じフォルダー）と開発時のビルド出力の両方を解決する。
        var probeRoot = Path.Combine(Path.GetTempPath(), "leafhotkey-engine-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            var publishedDirectory = Path.Combine(probeRoot, "published");
            Directory.CreateDirectory(publishedDirectory);
            var publishedEngine = Path.Combine(publishedDirectory, "LeafHotKeyEngine.exe");
            File.WriteAllText(publishedEngine, string.Empty);
            Check("enginepath.published", Program.ResolveEnginePath(publishedDirectory) == publishedEngine, "同じフォルダーにエンジンがあればそれを選ぶ");

            var devDirectory = Path.Combine(probeRoot, "src", "LeafHotKey", "bin", "Release", "net8.0-windows");
            var devEngine = Path.Combine(probeRoot, "src", "LeafHotKeyEngine", "bin", "Release", "net8.0-windows", "LeafHotKeyEngine.exe");
            Directory.CreateDirectory(devDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(devEngine)!);
            File.WriteAllText(devEngine, string.Empty);
            Check("enginepath.development", Program.ResolveEnginePath(devDirectory) == devEngine, "開発時のビルド出力から入力エンジンを解決する");
        }
        finally
        {
            Directory.Delete(probeRoot, recursive: true);
        }

        var resolvedEngine = Program.ResolveEnginePath(AppContext.BaseDirectory);
        Check("enginepath.deployment", File.Exists(resolvedEngine), $"現在の配置で入力エンジンを解決できる（{resolvedEngine}）");

        File.AppendAllText(path, $"failures={failures}{Environment.NewLine}", encoding);
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
