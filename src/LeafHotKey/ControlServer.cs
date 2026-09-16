using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace LeafHotKey;

/// <summary>
/// 本体側の制御チャネル。1接続ずつ順に処理する。
/// 同一ユーザーのクライアントだけを受け付け、他ユーザーからの接続は拒否する。
/// </summary>
public sealed class ControlServer : IDisposable
{
    private readonly HostState _state;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _pipeName;
    private readonly string _ownerSid;
    private Task? _loop;

    public ControlServer(HostState state, string? pipeName = null)
    {
        _state = state;
        _pipeName = pipeName ?? ControlProtocol.PipeName;
        _ownerSid = WindowsIdentity.GetCurrent().User?.Value ?? string.Empty;
    }

    /// <summary>終了要求を受けたときに発火する。実際の終了処理は呼び出し側が行う。</summary>
    public event Action? ShutdownRequested;

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
                await HandleClientAsync(pipe, token).ConfigureAwait(false);
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
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        // 先にコマンドを受け取る。偽装情報はクライアントが送信した後でないと参照できない。
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
        var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
        if (line is null) return;

        if (!IsSameUser(pipe))
        {
            await WriteLineAsync(pipe, ControlProtocol.Error("FORBIDDEN"), token).ConfigureAwait(false);
            return;
        }

        var response = Handle(line.Trim());
        await WriteLineAsync(pipe, response, token).ConfigureAwait(false);
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
        var parts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var verb = parts.Length > 0 ? parts[0] : string.Empty;
        var argument = parts.Length > 1 ? parts[1].ToUpperInvariant() : string.Empty;

        switch (verb.ToUpperInvariant())
        {
            case ControlProtocol.Ping:
                return ControlProtocol.Pong;
            case ControlProtocol.Status:
                return ControlProtocol.Ok(_state.State.ToString().ToUpperInvariant());
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
