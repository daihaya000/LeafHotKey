using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace LeafHotKey;

/// <summary>
/// 設定サーバーの入口を検証する。
/// 一時ディレクトリの設定を使い、実際の保存先やネットワークへは出ない。
/// </summary>
public static class ServerSelfCheck
{
    public static int Run(string? reportPath, string? defaultsPath)
    {
        var path = reportPath ?? Path.Combine(Path.GetTempPath(), "leafhotkey-servercheck.txt");
        var encoding = new UTF8Encoding(false);
        var failures = 0;

        File.WriteAllText(path, string.Empty, encoding);

        void Check(string name, bool ok, string detail)
        {
            if (!ok) failures++;
            File.AppendAllText(path, $"{(ok ? "PASS" : "FAIL")} {name}: {detail}{Environment.NewLine}", encoding);
        }

        var defaults = defaultsPath ?? FindDefaultSettings();
        if (defaults is null)
        {
            Check("defaults.found", false, "defaults/settings.json が見つからない");
            File.AppendAllText(path, $"failures={failures}{Environment.NewLine}", encoding);
            return 1;
        }

        var workDirectory = Path.Combine(Path.GetTempPath(), "leafhotkey-servercheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);

        try
        {
            var store = new SettingsStore(Path.Combine(workDirectory, "settings.json"), defaults);
            using var server = new SettingsServer(store, statusJson: () => "{\"state\":\"running\"}", webRoot: workDirectory);
            server.Start();

            Check("start.loopback", server.Port > 0, $"127.0.0.1:{server.Port} で待ち受ける");
            Check("start.token", server.Token.Length >= 32 && server.Url.Contains(server.Token, StringComparison.Ordinal), "起動ごとのトークンを発行する");

            var host = $"127.0.0.1:{server.Port}";
            var origin = $"http://{host}";

            // 認証あり。
            var settings = Send(server.Port, "GET", "/api/settings", host, origin, server.Token);
            Check("auth.ok", settings.Status == 200, $"トークン付きの取得は成功する（{settings.Status}）");

            var snapshot = store.Load();
            Check(
                "settings.payload",
                settings.Body.Contains(snapshot.Revision, StringComparison.Ordinal),
                "現在の版を返す");

            // 認証なし・誤りは拒否。
            Check("auth.missing", Send(server.Port, "GET", "/api/settings", host, origin, token: null).Status == 401, "トークンなしは 401");
            Check("auth.wrong", Send(server.Port, "GET", "/api/settings", host, origin, "not-the-token").Status == 401, "誤ったトークンは 401");

            // Host / Origin の検証。
            Check("host.mismatch", Send(server.Port, "GET", "/api/settings", "evil.example", origin, server.Token).Status == 403, "Host 不一致は 403");
            Check("origin.mismatch", Send(server.Port, "GET", "/api/settings", host, "http://evil.example", server.Token).Status == 403, "別オリジンは 403");
            Check("origin.absent", Send(server.Port, "GET", "/api/settings", host, origin: null, server.Token).Status == 200, "Origin なし（同一オリジンの通常要求）は通す");

            // 保存。
            var updated = snapshot.Json.Replace("\"pollIntervalMs\": 1000", "\"pollIntervalMs\": 800", StringComparison.Ordinal);
            var saveBody = JsonSerializer.Serialize(new { revision = snapshot.Revision, json = updated });
            var save = Send(server.Port, "POST", "/api/settings", host, origin, server.Token, saveBody);
            Check("save.ok", save.Status == 200, $"正しい保存は 200（{save.Status}）");
            Check("save.applied", store.Load().GameProtection.PollIntervalMs == 800, "保存内容が設定へ反映される");

            // 競合・不正。
            var conflict = Send(server.Port, "POST", "/api/settings", host, origin, server.Token, saveBody);
            Check("save.conflict", conflict.Status == 409, $"古い版の保存は 409（{conflict.Status}）");

            var invalidBody = JsonSerializer.Serialize(new { revision = store.Load().Revision, json = "{ broken" });
            Check("save.invalid", Send(server.Port, "POST", "/api/settings", host, origin, server.Token, invalidBody).Status == 400, "壊れた設定は 400");

            var missingField = Send(server.Port, "POST", "/api/settings", host, origin, server.Token, "{}");
            Check("save.missing-field", missingField.Status == 400, "json フィールドなしは 400");

            // 本文サイズの上限。
            var oversized = Send(server.Port, "POST", "/api/settings", host, origin, server.Token, new string('a', SettingsServer.MaxBodyBytes + 10));
            Check("body.too-large", oversized.Status == 413, $"上限を超える本文は 413（{oversized.Status}）");

            // 既定へ戻す。
            var restore = Send(server.Port, "POST", "/api/settings/restore", host, origin, server.Token, "{}");
            Check("restore.ok", restore.Status == 200, "既定復帰は 200");
            Check("restore.applied", store.Load().GameProtection.PollIntervalMs == 1000, "既定値へ戻る");

            // 状態取得と未知のパス。
            Check("status.ok", Send(server.Port, "GET", "/api/status", host, origin, server.Token).Body.Contains("running", StringComparison.Ordinal), "状態を返す");
            Check("unknown.path", Send(server.Port, "GET", "/api/secret", host, origin, server.Token).Status == 404, "未知のパスは 404");
            Check("traversal", Send(server.Port, "GET", "/../settings.json", host, origin, server.Token).Status == 404, "パス探索を拒否する");
            Check("static.missing", Send(server.Port, "GET", "/", host, origin, server.Token).Status == 404, "index.html が無ければ 404");

            File.WriteAllText(Path.Combine(workDirectory, "index.html"), "<!doctype html><title>t</title>", encoding);
            var page = Send(server.Port, "GET", "/", host, origin, server.Token);
            Check("static.served", page.Status == 200 && page.Body.Contains("doctype", StringComparison.OrdinalIgnoreCase), "index.html を配信できる");
            Check("static.unauthorized", Send(server.Port, "GET", "/", host, origin, token: null).Status == 401, "ページ自体もトークンを要求する");
        }
        finally
        {
            Directory.Delete(workDirectory, recursive: true);
        }

        File.AppendAllText(path, $"failures={failures}{Environment.NewLine}", encoding);
        return failures == 0 ? 0 : 1;
    }

    private readonly struct Response
    {
        public Response(int status, string body)
        {
            Status = status;
            Body = body;
        }

        public int Status { get; }

        public string Body { get; }
    }

    /// <summary>ヘッダを細かく制御するため、生のソケットで要求を送る。</summary>
    private static Response Send(
        int port,
        string method,
        string target,
        string host,
        string? origin,
        string? token,
        string? body = null)
    {
        var encoding = new UTF8Encoding(false);
        using var client = new TcpClient();
        client.Connect("127.0.0.1", port);
        client.ReceiveTimeout = 5000;
        client.SendTimeout = 5000;

        var payload = body is null ? Array.Empty<byte>() : encoding.GetBytes(body);
        var request = new StringBuilder()
            .Append(method).Append(' ').Append(target).Append(" HTTP/1.1\r\n")
            .Append("Host: ").Append(host).Append("\r\n");

        if (origin is not null) request.Append("Origin: ").Append(origin).Append("\r\n");
        if (token is not null) request.Append("X-LeafHotKey-Token: ").Append(token).Append("\r\n");
        if (body is not null)
        {
            request.Append("Content-Type: application/json\r\n")
                .Append("Content-Length: ").Append(payload.Length).Append("\r\n");
        }

        request.Append("Connection: close\r\n\r\n");

        using var stream = client.GetStream();
        var headerBytes = encoding.GetBytes(request.ToString());
        stream.Write(headerBytes);
        if (payload.Length > 0) stream.Write(payload);
        stream.Flush();

        using var memory = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        try
        {
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) memory.Write(buffer, 0, read);
        }
        catch (IOException)
        {
            // 応答後に切断された場合はそこまでを使う。
        }

        var text = encoding.GetString(memory.ToArray());
        var separator = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var headerText = separator < 0 ? text : text[..separator];
        var responseBody = separator < 0 ? string.Empty : text[(separator + 4)..];
        var statusLine = headerText.Split("\r\n")[0].Split(' ');
        var status = statusLine.Length > 1 && int.TryParse(statusLine[1], out var parsed) ? parsed : 0;

        return new Response(status, responseBody);
    }

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
