using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace LeafHotKey;

/// <summary>
/// 本体側の制御チャネル。接続ごとに独立して処理する。
/// 同一ユーザーのクライアントだけを受け付け、他ユーザーからの接続は拒否する。
/// </summary>
public sealed class ControlServer : IDisposable
{
    private readonly HostState _state;
    private readonly Func<string>? _statusJson;
    private readonly Func<string>? _logJson;
    private readonly Func<bool>? _reload;
    private readonly Func<bool>? _restartBackend;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _pipeName;
    private readonly string _ownerSid;
    private readonly object _clientsGate = new();
    private readonly HashSet<Task> _clients = new();
    private Task? _loop;

    private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(5);

    public ControlServer(
        HostState state,
        string? pipeName = null,
        Func<string>? statusJson = null,
        Func<string>? logJson = null,
        Func<bool>? reload = null,
        Func<bool>? restartBackend = null)
    {
        _state = state;
        _pipeName = pipeName ?? ControlProtocol.PipeName;
        _statusJson = statusJson;
        _logJson = logJson;
        _reload = reload;
        _restartBackend = restartBackend;
        _ownerSid = WindowsIdentity.GetCurrent().User?.Value ?? string.Empty;
    }

    /// <summary>
    /// コマンド処理を差し替える。戻り値が null 以外ならそれを応答に使う。
    /// 本体側は、入力エンジン（別プロセス）へ転送するために使う。
    /// </summary>
    public Func<string, string?>? Override { get; set; }

    /// <summary>終了要求を受けたときに発火する。実際の終了処理は呼び出し側が行う。</summary>
    public event Action? ShutdownRequested;

    /// <summary>終了要求として扱う。コマンド転送側（本体）から終了処理を促すために使う。</summary>
    public void RequestShutdown() => ShutdownRequested?.Invoke();

    public void Start()
    {
        if (_loop is not null) throw new InvalidOperationException("制御チャネルは既に開始済みです。");
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                // バッファサイズを 0 にすると、クライアントの書き込みがサーバーの読み取りまでブロックする。
                pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 4096,
                    outBufferSize: 4096);

                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                if (token.IsCancellationRequested) break;

                var connectedPipe = pipe;
                pipe = null;
                var client = HandleClientLifetimeAsync(connectedPipe, token);
                lock (_clientsGate)
                {
                    // 完了済みタスクを保持し続けない。登録との競合を避けるため、追加前に掃除する。
                    _clients.RemoveWhere(static task => task.IsCompleted);
                    _clients.Add(client);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                // クライアントが途中で切断した場合は次の接続を待つ。
            }
            catch (InvalidOperationException)
            {
                // 接続状態が想定外になった場合も、受付ループ自体は止めない。
            }
            finally
            {
                pipe?.Dispose();
            }
        }

        Task[] clients;
        lock (_clientsGate) clients = _clients.ToArray();
        await Task.WhenAll(clients).ConfigureAwait(false);
    }

    private async Task HandleClientLifetimeAsync(NamedPipeServerStream pipe, CancellationToken serverToken)
    {
        try
        {
            await HandleClientAsync(pipe, serverToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 接続単位のタイムアウトまたはサーバー終了による切断。
        }
        catch (IOException)
        {
            // クライアントが途中で切断した場合は次の接続を受け付ける。
        }
        catch (InvalidOperationException)
        {
            // 接続状態が想定外になった場合も、他の接続処理は継続する。
        }
        finally
        {
            pipe.Dispose();
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        using var clientCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        clientCts.CancelAfter(ClientTimeout);
        var clientToken = clientCts.Token;

        // 先にコマンドを受け取る。偽装情報はクライアントが送信した後でないと参照できない。
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
        var line = await reader.ReadLineAsync(clientToken).ConfigureAwait(false);
        if (line is null) return;

        if (!IsSameUser(pipe))
        {
            await WriteLineAsync(pipe, ControlProtocol.Error("FORBIDDEN"), clientToken).ConfigureAwait(false);
            return;
        }

        var response = Handle(line.Trim());
        await WriteLineAsync(pipe, response, clientToken).ConfigureAwait(false);
    }

    private bool IsSameUser(NamedPipeServerStream pipe)
    {
        if (_ownerSid.Length == 0) return false;

        try
        {
            // 表示名の書式に依存しないよう、接続元を偽装して SID を直接比較する。
            string? clientSid = null;
            pipe.RunAsClient(() => clientSid = WindowsIdentity.GetCurrent().User?.Value);
            return string.Equals(clientSid, _ownerSid, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            // クライアントが偽装を許可していない場合は本人確認できないため拒否する。
            return false;
        }
    }

    internal string Handle(string command)
    {
        // 転送先を持つ本体では、応答が返った時点で既定動作を行わない。
        if (Override is { } custom && custom(command) is { } customResponse) return customResponse;

        var parts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var verb = parts.Length > 0 ? parts[0] : string.Empty;
        var argument = parts.Length > 1 ? parts[1].ToUpperInvariant() : string.Empty;

        switch (verb.ToUpperInvariant())
        {
            case ControlProtocol.Ping:
                return ControlProtocol.Pong;
            case ControlProtocol.Status:
                // WebUI は入力エンジン内の状態を必要とする。
                if (argument == ControlProtocol.StatusJson)
                {
                    return _statusJson is null
                        ? ControlProtocol.Error("UNAVAILABLE")
                        : ControlProtocol.Ok(_statusJson());
                }

                return ControlProtocol.Ok(_state.State.ToString().ToUpperInvariant());
            case ControlProtocol.Log:
                return _logJson is null
                    ? ControlProtocol.Error("UNAVAILABLE")
                    : ControlProtocol.Ok(_logJson());
            case ControlProtocol.Reload:
                if (_reload is null) return ControlProtocol.Error("UNAVAILABLE");
                return _reload() ? ControlProtocol.Ok("RELOADED") : ControlProtocol.Error("RELOAD_FAILED");
            case ControlProtocol.RestartBackend:
                if (_restartBackend is null) return ControlProtocol.Error("UNAVAILABLE");
                return _restartBackend() ? ControlProtocol.Ok("RESTARTED") : ControlProtocol.Error("RESTART_FAILED");
            case ControlProtocol.Pause:
                return _state.Pause()
                    ? ControlProtocol.Ok("PAUSED")
                    : ControlProtocol.Error("NOT_RUNNING");
            case ControlProtocol.Resume:
                return _state.Resume()
                    ? ControlProtocol.Ok("RUNNING")
                    : ControlProtocol.Error("NOT_PAUSED");
            case ControlProtocol.Shutdown:
            {
                // 引数が無い場合は手動終了として扱い、自動復帰対象にしない。
                if (argument.Length > 0 && argument != ControlProtocol.ReasonGame && argument != ControlProtocol.ReasonManual)
                {
                    return ControlProtocol.Error("UNKNOWN_REASON");
                }

                var reason = argument == ControlProtocol.ReasonGame ? ExitReason.GameProtection : ExitReason.Manual;
                _state.BeginShutdown(reason);
                ShutdownRequested?.Invoke();
                return ControlProtocol.Ok("SHUTTINGDOWN " + (reason == ExitReason.GameProtection
                    ? ControlProtocol.ReasonGame
                    : ControlProtocol.ReasonManual));
            }
            default:
                return ControlProtocol.Error("UNKNOWN_COMMAND");
        }
    }

    private static async Task WriteLineAsync(Stream stream, string value, CancellationToken token)
    {
        var bytes = new UTF8Encoding(false).GetBytes(value + "\n");
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            // 待機中の WaitForConnectionAsync を解除するためにダミー接続する。
            using var wake = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut);
            wake.Connect(200);
        }
        catch (TimeoutException)
        {
            // 既に停止していれば何もしない。
        }
        catch (IOException)
        {
        }

        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // 停止時の例外は無視する。
        }

        _cts.Dispose();
    }
}
