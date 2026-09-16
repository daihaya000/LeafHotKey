namespace LeafHotKey;

/// <summary>入力変換の稼働状態。</summary>
public enum RuntimeState
{
    /// <summary>入力変換が有効。</summary>
    Running,

    /// <summary>ユーザー操作で停止中。ゲーム終了などで勝手に再開しない。</summary>
    Paused,

    /// <summary>終了処理中。</summary>
    ShuttingDown,
}

/// <summary>本体の終了理由。Watcher が自動再起動の可否を判断するために使う。</summary>
public enum ExitReason
{
    /// <summary>まだ終了していない。</summary>
    None,

    /// <summary>ユーザーが明示的に終了した。自動再起動しない。</summary>
    Manual,

    /// <summary>危険プロセス検知による退避。ゲーム終了後に再起動してよい。</summary>
    GameProtection,
}

/// <summary>プロセス内で共有する稼働状態。Phase 1 では入力フックを持たない。</summary>
public sealed class HostState
{
    private readonly object _gate = new();
    private RuntimeState _state = RuntimeState.Running;
    private ExitReason _exitReason = ExitReason.None;

    public event Action<RuntimeState>? StateChanged;

    public RuntimeState State
    {
        get
        {
            lock (_gate) return _state;
        }
    }

    public ExitReason ExitReason
    {
        get
        {
            lock (_gate) return _exitReason;
        }
    }

    /// <summary>ユーザー操作による一時停止。既に停止中なら false。</summary>
    public bool Pause() => Transition(RuntimeState.Paused, RuntimeState.Running);

    /// <summary>一時停止からの再開。停止中でなければ false。</summary>
    public bool Resume() => Transition(RuntimeState.Running, RuntimeState.Paused);

    /// <summary>終了へ遷移する。理由は最初の1回だけ記録する。</summary>
    public void BeginShutdown(ExitReason reason)
    {
        RuntimeState next;
        lock (_gate)
        {
            if (_exitReason == ExitReason.None) _exitReason = reason;
            _state = RuntimeState.ShuttingDown;
            next = _state;
        }

        StateChanged?.Invoke(next);
    }

    private bool Transition(RuntimeState to, RuntimeState requiredFrom)
    {
        lock (_gate)
        {
            if (_state != requiredFrom) return false;
            _state = to;
        }

        StateChanged?.Invoke(to);
        return true;
    }
}
