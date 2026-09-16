using LeafHotKey;

namespace LeafHotKeyWatcher;

/// <summary>Watcher の状態。</summary>
public enum LifecycleState
{
    /// <summary>本体が動作中。危険プロセスを監視している。</summary>
    HostRunning,

    /// <summary>ゲーム保護で本体を退避済み。対象プロセスの終了を待っている。</summary>
    WaitingGameExit,

    /// <summary>対象プロセスが消えた後の待機中。再出現したら待機をやり直す。</summary>
    WaitingResumeDelay,

    /// <summary>自動復帰しない停止状態（手動終了・退避失敗・起動失敗）。</summary>
    Stopped,
}

/// <summary>Watcher が停止した理由。</summary>
public enum StopCause
{
    None,
    ManualExit,
    ShutdownFailed,
    StartFailed,
}

/// <summary>
/// 危険プロセス検知から本体の退避・復帰までを 1 か所で管理する。
/// ゲームプロセスは終了させない。復帰条件を満たすまで本体を起動しない。
/// </summary>
public sealed class LifecycleController
{
    private readonly GameProtectionSettings _settings;
    private readonly GameMonitor _monitor;
    private readonly IHostControl _host;
    private readonly int _maxStartAttempts;

    private DateTimeOffset _resumeDeadline;
    private int _startAttempts;

    public LifecycleController(
        GameProtectionSettings settings,
        GameMonitor monitor,
        IHostControl host,
        int maxStartAttempts = 5)
    {
        _settings = settings;
        _monitor = monitor;
        _host = host;
        _maxStartAttempts = maxStartAttempts;
    }

    public LifecycleState State { get; private set; } = LifecycleState.HostRunning;

    public StopCause StopCause { get; private set; } = StopCause.None;

    /// <summary>直近の遷移理由。ログとトレイ表示に使う。</summary>
    public string LastMessage { get; private set; } = string.Empty;

    /// <summary>監視ループの 1 ステップ。時刻は呼び出し側から渡し、検証で固定できるようにする。</summary>
    public void Tick(DateTimeOffset now)
    {
        switch (State)
        {
            case LifecycleState.HostRunning:
                TickHostRunning();
                break;
            case LifecycleState.WaitingGameExit:
                TickWaitingGameExit(now);
                break;
            case LifecycleState.WaitingResumeDelay:
                TickWaitingResumeDelay(now);
                break;
            case LifecycleState.Stopped:
                break;
        }
    }

    private void TickHostRunning()
    {
        if (!_settings.Enabled) return;

        var triggers = _monitor.Running(_settings.StopTriggerProcessNames);
        if (triggers.Count == 0)
        {
            // 危険プロセスが無いのに本体が消えた場合は手動終了とみなし、自動復帰しない。
            if (!_host.IsRunning())
            {
                State = LifecycleState.Stopped;
                StopCause = StopCause.ManualExit;
                LastMessage = "本体が手動で終了したため、自動再起動しません。";
            }

            return;
        }

        if (_host.IsRunning() && !_host.RequestGameShutdown())
        {
            // 退避できていない状態を「保護済み」と扱わない。
            State = LifecycleState.Stopped;
            StopCause = StopCause.ShutdownFailed;
            LastMessage = $"本体の退避に失敗しました（検知: {string.Join(", ", triggers)}）。";
            return;
        }

        State = LifecycleState.WaitingGameExit;
        _startAttempts = 0;
        LastMessage = $"危険プロセスを検知したため本体を退避しました（{string.Join(", ", triggers)}）。";
    }

    private void TickWaitingGameExit(DateTimeOffset now)
    {
        if (_monitor.AnyRunning(_settings.ResumeProcessNames)) return;

        State = LifecycleState.WaitingResumeDelay;
        _resumeDeadline = now.AddMilliseconds(_settings.ResumeDelayMs);
        LastMessage = "対象プロセスの終了を確認しました。復帰待機に入ります。";
    }

    private void TickWaitingResumeDelay(DateTimeOffset now)
    {
        if (_monitor.AnyRunning(_settings.ResumeProcessNames))
        {
            // 待機中に再出現したら、待機をやり直す。
            State = LifecycleState.WaitingGameExit;
            LastMessage = "対象プロセスが再び検出されたため、復帰待機をやり直します。";
            return;
        }

        if (now < _resumeDeadline) return;

        // 復帰直前にも停止トリガを再確認する。
        if (_monitor.AnyRunning(_settings.StopTriggerProcessNames))
        {
            State = LifecycleState.WaitingGameExit;
            LastMessage = "復帰直前に危険プロセスを検出したため、復帰を中止しました。";
            return;
        }

        _startAttempts++;
        if (_host.Start())
        {
            State = LifecycleState.HostRunning;
            LastMessage = "本体を再起動しました。";
            return;
        }

        if (_startAttempts >= _maxStartAttempts)
        {
            State = LifecycleState.Stopped;
            StopCause = StopCause.StartFailed;
            LastMessage = $"本体の再起動に{_startAttempts}回失敗したため停止しました。";
            return;
        }

        // 次のTickで再試行する。無制限にはループさせない。
        _resumeDeadline = now.AddMilliseconds(_settings.ResumeDelayMs);
        LastMessage = $"本体の再起動に失敗しました（{_startAttempts}回目）。再試行します。";
    }
}
