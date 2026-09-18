using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LeafHotKey;

/// <summary>
/// 設定用の最小 HTTP サーバー。127.0.0.1 だけで待ち受け、
/// Host / Origin / トークンを確認してから設定を読み書きする。
/// 任意コマンド実行 API は持たない。
/// </summary>
public sealed class SettingsServer : IDisposable
{
    /// <summary>受け付ける本文の上限。設定ファイルより十分大きく、無制限にはしない。</summary>
    public const int MaxBodyBytes = 1024 * 1024;

    private readonly SettingsStore _store;
    private readonly Func<string>? _statusJson;
    private readonly string? _webRoot;
    private readonly Action<SettingsSnapshot>? _onSaved;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(5);

    public SettingsServer(
        SettingsStore store,
        int port = 0,
        Func<string>? statusJson = null,
        string? webRoot = null,
        Action<SettingsSnapshot>? onSaved = null)
    {
        _store = store;
        _statusJson = statusJson;
        _webRoot = webRoot;
        _onSaved = onSaved;
        _listener = new TcpListener(IPAddress.Loopback, port);
        Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    /// <summary>この起動でだけ有効な認証トークン。</summary>
    public string Token { get; }

    public int Port { get; private set; }

    /// <summary>ブラウザで開く URL。トークンを含む。</summary>
    public string Url => $"http://127.0.0.1:{Port}/?token={Token}";

    private string ExpectedHost => $"127.0.0.1:{Port}";

    private string ExpectedOrigin => $"http://127.0.0.1:{Port}";

    public void Start()
    {
        if (_loop is not null) throw new InvalidOperationException("サーバーは既に開始済みです。");

        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }

            _ = Task.Run(() => HandleClientAsync(client, token), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverToken)
    {
        using (client)
        {
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 5000;
            using var clientCts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
            clientCts.CancelAfter(ClientTimeout);
            var clientToken = clientCts.Token;

            try
            {
                using var stream = client.GetStream();

                // ループバック以外からの接続は扱わない。
                if (client.Client.RemoteEndPoint is not IPEndPoint remote || !IPAddress.IsLoopback(remote.Address))
                {
                    await WriteAsync(stream, 403, "text/plain; charset=utf-8", "forbidden").ConfigureAwait(false);
                    return;
                }

                var request = await ReadRequestAsync(stream, clientToken).ConfigureAwait(false);
                if (request is null)
                {
                    await WriteAsync(stream, 400, "text/plain; charset=utf-8", "bad request").ConfigureAwait(false);
                    return;
                }

                if (request.TooLarge)
                {
                    await WriteAsync(stream, 413, "text/plain; charset=utf-8", "payload too large").ConfigureAwait(false);
                    return;
                }

                await RouteAsync(stream, request).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 接続単位のタイムアウトまたはサーバー終了による切断。
            }
            catch (IOException)
            {
                // 接続が切れた場合は何もしない。
            }
            catch (SocketException)
            {
            }
        }
    }

    private async Task RouteAsync(Stream stream, HttpRequest request)
    {
        // Host ヘッダの偽装や、別オリジンからの操作を受け付けない。
        if (!string.Equals(request.Header("host"), ExpectedHost, StringComparison.OrdinalIgnoreCase))
        {
            await WriteAsync(stream, 403, "text/plain; charset=utf-8", "host mismatch").ConfigureAwait(false);
            return;
        }

        var origin = request.Header("origin");
        if (origin is not null && !string.Equals(origin, ExpectedOrigin, StringComparison.OrdinalIgnoreCase))
        {
            await WriteAsync(stream, 403, "text/plain; charset=utf-8", "origin mismatch").ConfigureAwait(false);
            return;
        }

        // CSS/JS はブラウザがカスタムヘッダを付けられない。中身に秘密はないので認証しない。
        if (RequiresAuth(request) && !IsAuthorized(request))
        {
            var message = request.Method == "GET" && (request.Path == "/" || request.Path == "/index.html")
                ? "認証情報がないか、期限が切れています。\nタスクトレイの LeafHotKey を右クリックし「設定を開く」から開き直してください。\n再起動前のタブやURLは使えません。"
                : "unauthorized";
            await WriteAsync(stream, 401, "text/plain; charset=utf-8", message).ConfigureAwait(false);
            return;
        }

        switch (request.Method, request.Path)
        {
            case ("GET", "/api/settings"):
            {
                var snapshot = _store.Load();
                var payload = JsonSerializer.Serialize(new { revision = snapshot.Revision, json = snapshot.Json });
                await WriteAsync(stream, 200, "application/json; charset=utf-8", payload).ConfigureAwait(false);
                return;
            }

            case ("POST", "/api/settings"):
            {
                string? json;
                string? revision;
                try
                {
                    using var document = JsonDocument.Parse(request.Body);
                    json = document.RootElement.TryGetProperty("json", out var jsonNode) ? jsonNode.GetString() : null;
                    revision = document.RootElement.TryGetProperty("revision", out var revisionNode)
                        ? revisionNode.GetString()
                        : null;
                }
                catch (JsonException ex)
                {
                    await WriteJsonError(stream, 400, ex.Message).ConfigureAwait(false);
                    return;
                }

                if (json is null)
                {
                    await WriteJsonError(stream, 400, "json フィールドが必要です。").ConfigureAwait(false);
                    return;
                }

                var result = _store.Save(json, revision);
                if (result.Success) NotifySaved();

                var status = result.Status switch
                {
                    SaveStatus.Saved => 200,
                    SaveStatus.Conflict => 409,
                    SaveStatus.Invalid => 400,
                    _ => 500,
                };

                var body = JsonSerializer.Serialize(new
                {
                    status = result.Status.ToString().ToLowerInvariant(),
                    message = result.Message,
                    revision = result.Revision,
                });

                await WriteAsync(stream, status, "application/json; charset=utf-8", body).ConfigureAwait(false);
                return;
            }

            case ("POST", "/api/settings/restore"):
            {
                var result = _store.RestoreDefaults();
                if (result.Success) NotifySaved();

                var body = JsonSerializer.Serialize(new
                {
                    status = result.Status.ToString().ToLowerInvariant(),
                    message = result.Message,
                    revision = result.Revision,
                });

                await WriteAsync(stream, result.Success ? 200 : 500, "application/json; charset=utf-8", body).ConfigureAwait(false);
                return;
            }

            case ("GET", "/api/status"):
            {
                var body = _statusJson?.Invoke() ?? "{}";
                await WriteAsync(stream, 200, "application/json; charset=utf-8", body).ConfigureAwait(false);
                return;
            }

            case ("GET", "/api/icon"):
            {
                // ブラウザの img はヘッダを付けられないため、トークンはクエリでも受け付ける（IsAuthorized）。
                var payload = request.Query("name") is { } name ? ProcessIcons.PngFor(name) : null;
                if (payload is null)
                {
                    await WriteAsync(stream, 404, "text/plain; charset=utf-8", "no icon").ConfigureAwait(false);
                    return;
                }

                await WriteBinaryAsync(stream, 200, "image/png", payload).ConfigureAwait(false);
                return;
            }

            default:
                await ServeStaticAsync(stream, request).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>保存された内容を呼び出し側へ渡す。反映に失敗しても保存自体は完了している。</summary>
    private void NotifySaved()
    {
        if (_onSaved is null) return;

        try
        {
            _onSaved(_store.Load());
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or FormatException)
        {
            // 反映できない場合も応答は返す。呼び出し側が状態表示で扱う。
        }
    }

    /// <summary>既知のファイル名だけを配信する（パス探索を許さない）。</summary>
    private async Task ServeStaticAsync(Stream stream, HttpRequest request)
    {
        if (request.Method != "GET" || _webRoot is null)
        {
            await WriteAsync(stream, 404, "text/plain; charset=utf-8", "not found").ConfigureAwait(false);
            return;
        }

        var name = request.Path == "/" ? "index.html" : request.Path.TrimStart('/');
        var allowed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["index.html"] = "text/html; charset=utf-8",
            ["styles.css"] = "text/css; charset=utf-8",
            ["app.js"] = "text/javascript; charset=utf-8",
        };

        if (!allowed.TryGetValue(name, out var contentType))
        {
            await WriteAsync(stream, 404, "text/plain; charset=utf-8", "not found").ConfigureAwait(false);
            return;
        }

        var file = Path.Combine(_webRoot, name);
        if (!File.Exists(file))
        {
            await WriteAsync(stream, 404, "text/plain; charset=utf-8", "not found").ConfigureAwait(false);
            return;
        }

        var content = await File.ReadAllTextAsync(file, new UTF8Encoding(false)).ConfigureAwait(false);
        if (string.Equals(name, "index.html", StringComparison.OrdinalIgnoreCase))
        {
            // ブラウザのサブリソース要求にも、初回ページと同じ認証トークンを付ける。
            content = content.Replace(
                "__LEAFHOTKEY_TOKEN__",
                Uri.EscapeDataString(Token),
                StringComparison.Ordinal);
        }

        await WriteAsync(stream, 200, contentType, content).ConfigureAwait(false);
    }

    private static bool RequiresAuth(HttpRequest request)
    {
        if (request.Method != "GET") return true;
        var name = request.Path == "/" ? "index.html" : request.Path.TrimStart('/');
        return !name.Equals("styles.css", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("app.js", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsAuthorized(HttpRequest request)
    {
        var header = request.Header("x-leafhotkey-token");
        if (header is not null && FixedEquals(header, Token)) return true;

        var authorization = request.Header("authorization");
        if (authorization is not null &&
            authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) &&
            FixedEquals(authorization[7..].Trim(), Token))
        {
            return true;
        }

        // 初回のページ表示だけクエリのトークンを許す。
        return request.Query("token") is { } queryToken && FixedEquals(queryToken, Token);
    }

    private static bool FixedEquals(string left, string right)
    {
        var encoding = new UTF8Encoding(false);
        return CryptographicOperations.FixedTimeEquals(encoding.GetBytes(left), encoding.GetBytes(right));
    }

    private static async Task WriteJsonError(Stream stream, int status, string message)
    {
        var body = JsonSerializer.Serialize(new { status = "invalid", message });
        await WriteAsync(stream, status, "application/json; charset=utf-8", body).ConfigureAwait(false);
    }

    private static async Task WriteAsync(Stream stream, int status, string contentType, string body)
    {
        var encoding = new UTF8Encoding(false);
        var payload = encoding.GetBytes(body);
        var header = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status).Append(' ').Append(ReasonFor(status)).Append("\r\n")
            .Append("Content-Type: ").Append(contentType).Append("\r\n")
            .Append("Content-Length: ").Append(payload.Length).Append("\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("X-Content-Type-Options: nosniff\r\n")
            .Append("Connection: close\r\n\r\n")
            .ToString();

        var headerBytes = encoding.GetBytes(header);
        await stream.WriteAsync(headerBytes).ConfigureAwait(false);
        await stream.WriteAsync(payload).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private static async Task WriteBinaryAsync(Stream stream, int status, string contentType, byte[] payload)
    {
        var encoding = new UTF8Encoding(false);
        var header = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status).Append(' ').Append(ReasonFor(status)).Append("\r\n")
            .Append("Content-Type: ").Append(contentType).Append("\r\n")
            .Append("Content-Length: ").Append(payload.Length).Append("\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("X-Content-Type-Options: nosniff\r\n")
            .Append("Connection: close\r\n\r\n")
            .ToString();

        await stream.WriteAsync(encoding.GetBytes(header)).ConfigureAwait(false);
        await stream.WriteAsync(payload).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private static string ReasonFor(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        409 => "Conflict",
        413 => "Payload Too Large",
        _ => "Internal Server Error",
    };

    private static async Task<HttpRequest?> ReadRequestAsync(Stream stream, CancellationToken token)
    {
        var buffer = new List<byte>(1024);
        var single = new byte[1];
        var headerEnd = -1;

        while (buffer.Count < 16 * 1024)
        {
            var read = await stream.ReadAsync(single, token).ConfigureAwait(false);
            if (read == 0) break;

            buffer.Add(single[0]);
            if (buffer.Count >= 4 &&
                buffer[^4] == (byte)'\r' && buffer[^3] == (byte)'\n' &&
                buffer[^2] == (byte)'\r' && buffer[^1] == (byte)'\n')
            {
                headerEnd = buffer.Count;
                break;
            }
        }

        if (headerEnd < 0) return null;

        var encoding = new UTF8Encoding(false);
        var headerText = encoding.GetString(buffer.ToArray(), 0, headerEnd);
        var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return null;

        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0) continue;
            headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        var target = requestLine[1];
        var queryIndex = target.IndexOf('?');
        var path = queryIndex < 0 ? target : target[..queryIndex];
        var query = queryIndex < 0 ? string.Empty : target[(queryIndex + 1)..];

        var contentLength = 0;
        if (headers.TryGetValue("content-length", out var lengthText) && int.TryParse(lengthText, out var parsed))
        {
            contentLength = parsed;
        }

        if (contentLength > MaxBodyBytes)
        {
            return new HttpRequest(requestLine[0], path, query, headers, string.Empty) { TooLarge = true };
        }

        var body = string.Empty;
        if (contentLength > 0)
        {
            var bodyBuffer = new byte[contentLength];
            var offset = 0;
            while (offset < contentLength)
            {
                var read = await stream.ReadAsync(bodyBuffer.AsMemory(offset, contentLength - offset), token).ConfigureAwait(false);
                if (read == 0) break;
                offset += read;
            }

            body = encoding.GetString(bodyBuffer, 0, offset);
        }

        return new HttpRequest(requestLine[0], path, query, headers, body);
    }

    private sealed class HttpRequest
    {
        private readonly IReadOnlyDictionary<string, string> _headers;
        private readonly string _query;

        public HttpRequest(string method, string path, string query, IReadOnlyDictionary<string, string> headers, string body)
        {
            Method = method.ToUpperInvariant();
            Path = path;
            _query = query;
            _headers = headers;
            Body = body;
        }

        public string Method { get; }

        public string Path { get; }

        public string Body { get; }

        public bool TooLarge { get; init; }

        public string? Header(string name) => _headers.TryGetValue(name, out var value) ? value : null;

        public string? Query(string name)
        {
            foreach (var pair in _query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = pair.IndexOf('=');
                if (separator <= 0) continue;
                if (!string.Equals(Uri.UnescapeDataString(pair[..separator]), name, StringComparison.Ordinal)) continue;
                return Uri.UnescapeDataString(pair[(separator + 1)..]);
            }

            return null;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
        }
        catch (SocketException)
        {
        }

        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _cts.Dispose();
    }
}
