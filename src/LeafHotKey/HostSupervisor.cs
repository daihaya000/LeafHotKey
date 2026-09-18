using System.Text.Json;

namespace LeafHotKey;

/// <summary>
/// 入力エンジン（別プロセス）の生存監視とゲーム保護を 1 か所で行う。
/// エンジンが退避しても本体と WebUI は常駐を続ける。
/// </summary>
public sealed class HostSupervisor : IDisposable
{
    private readonly LifecycleController? _controller;
    private readonly EngineControl _engine;
    private readonly HostState _state;
    private readonly EventLog _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly ManualResetEventSlim _wake = new(false);

    private Thread? _thread;
    private string _lastMessage = string.Empty;

    /// <summary>トレイ表示の更新が必要になったときに発火する。</summary>
    public event Action? StatusChanged;

    public HostSupervisor(LifecycleController? controller, EngineControl engine, HostState state, EventLog log)
    {
        _controller = controller;
        _engine = engine;
        _state = state;
        _log = log;
    }

    /// <summary>トレイに出す現在の状態。</summary>
    public string StatusText { get; private set; } = "起動しています";

    /// <summary>バックエンドの表示名。</summary>
    public string BackendLabel { get; private set; } = "-";

    /// <summary>AutoHotkey バックエンドで動作しているか。</summary>
    public bool IsAhkBackend { get; private set; }

    public void Start()
    {
        if (_thread is not null) throw new InvalidOperationException("監視は既に開始済みです。");

        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "LeafHotKey supervisor",
        };
        _thread.Start();
    }

    /// <summary>次の判定をすぐに実行する（トレイ操作の直後など）。</summary>
    public void RequestRefresh() => _wake.Set();

    /// <summary>保存されたゲーム保護設定を反映する。</summary>
    public void UpdateSettings(GameProtectionSettings settings) => _controller?.UpdateSettings(settings);

    /// <summary>本体から入力エンジンを起動し直したときに、監視を再開する。</summary>
    public void ResumeWatching() => _controller?.ResumeWatching();

    private void Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                _controller?.Tick(DateTimeOffset.UtcNow);
                Synchronize();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or System.ComponentModel.Win32Exception)
            {
                // 監視の失敗で本体を落とさない。次の周期で判定し直す。
            }

            _wake.Wait(Math.Clamp(_controller?.PollIntervalMs ?? 1000, 200, 10000));
            _wake.Reset();
        }
    }

    /// <summary>入力エンジンの状態を取り込み、表示とログを更新する。</summary>
    private void Synchronize()
    {
        if (_controller is { } controller && controller.LastMessage.Length > 0 && controller.LastMessage != _lastMessage)
        {
            _lastMessage = controller.LastMessage;
            _log.Add(controller.LastMessage);
        }

        var described = Describe(_engine.StatusJson());
        if (described.State == "running") _state.Resume();
        else if (described.State == "paused") _state.Pause();

        IsAhkBackend = described.Backend == "ahk";
        BackendLabel = IsAhkBackend
            ? $"AutoHotkey（{described.BackendStatus}）"
            : described.State == "stopped" ? "-" : "内蔵エンジン";

        var text = BuildStatusText(described.State, controller: _controller);
        if (text == StatusText && BackendLabel == described.Label) return;

        StatusText = text;
        _log.Add("状態: " + text);
        StatusChanged?.Invoke();
    }

    private static string BuildStatusText(string engineState, LifecycleController? controller)
    {
        // 入力エンジンが応答しているときは、実際に動いている状態を優先して見せる。
        if (engineState == "running") return "入力変換は有効";
        if (engineState == "paused") return "一時停止中";

        if (controller is { } c)
        {
            switch (c.State)
            {
                case LifecycleState.WaitingGameExit:
                    return "ゲーム保護で退避中";
                case LifecycleState.WaitingResumeDelay:
                    return "ゲーム終了待ち";
                case LifecycleState.Stopped:
                    return "入力エンジン停止中（自動復帰しません）";
            }
        }

        return engineState switch
        {
            "stopped" => "入力エンジン停止中",
            _ => "状態を確認しています",
        };
    }

    private static (string State, string Backend, string BackendStatus, string Label) Describe(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var state = Read(root, "state", "unknown");
            var backend = Read(root, "backend", "builtin");
            var status = Read(root, "backendStatus", "-");
            return (state, backend, status, state + "/" + backend + "/" + status);
        }
        catch (JsonException)
        {
            return ("unknown", "builtin", "-", "unknown");
        }
    }

    private static string Read(JsonElement root, string name, string fallback)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    public void Dispose()
    {
        _cts.Cancel();
        _wake.Set();
        _thread?.Join(TimeSpan.FromSeconds(3));
        _cts.Dispose();
        _wake.Dispose();
    }
}
