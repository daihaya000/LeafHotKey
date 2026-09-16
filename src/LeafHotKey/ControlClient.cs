using System.IO.Pipes;
using System.Text;

namespace LeafHotKey;

/// <summary>制御チャネルのクライアント。接続できない場合は必ず失敗として返す。</summary>
public static class ControlClient
{
    /// <summary>接続処理の進行状況を受け取る診断用フック。通常運用では未設定。</summary>
    internal static Action<string>? Trace { get; set; }

    /// <summary>
    /// コマンドを 1 件送って応答を返す。接続断・タイムアウトは null を返し、
    /// 呼び出し側は「本体が動作していない」ではなく「確認できない」として扱う。
    /// </summary>
    public static string? Send(string command, int timeoutMs = 2000, string? pipeName = null)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                pipeName ?? ControlProtocol.PipeName,
                PipeDirection.InOut,
                // 読み取りのタイムアウトを効かせるため、必ず非同期モードで開く。
                PipeOptions.Asynchronous,
                System.Security.Principal.TokenImpersonationLevel.Impersonation);

            Trace?.Invoke("connect.begin");
            pipe.Connect(timeoutMs);
            Trace?.Invoke("connect.done");

            var encoding = new UTF8Encoding(false);
            var payload = encoding.GetBytes(command + "\n");
            pipe.Write(payload, 0, payload.Length);
            pipe.Flush();
            Trace?.Invoke("write.done");

            var line = ReadLineWithTimeout(pipe, encoding, timeoutMs);
            Trace?.Invoke("read.done");
            return line;
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 応答を 1 行読む。相手が応答しないまま接続を維持した場合でも無期限に待たない。
    /// </summary>
    private static string? ReadLineWithTimeout(Stream stream, UTF8Encoding encoding, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var buffer = new byte[256];
        var text = new StringBuilder();

        try
        {
            while (true)
            {
                var read = stream.ReadAsync(buffer.AsMemory(), cts.Token).AsTask().GetAwaiter().GetResult();
                if (read == 0) break;

                text.Append(encoding.GetString(buffer, 0, read));
                var current = text.ToString();
                var newline = current.IndexOf('\n');
                if (newline >= 0) return current[..newline].TrimEnd('\r');
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        var remainder = text.ToString().TrimEnd('\r', '\n');
        return remainder.Length == 0 ? null : remainder;
    }

    /// <summary>本体が応答するかどうか。応答なしは false。</summary>
    public static bool IsHostResponding(int timeoutMs = 2000, string? pipeName = null)
        => Send(ControlProtocol.Ping, timeoutMs, pipeName) == ControlProtocol.Pong;
}
