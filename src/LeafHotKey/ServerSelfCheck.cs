using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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
            Check("start.url", server.Url == $"http://127.0.0.1:{server.Port}/", $"トークンを含まない固定 URL を返す（{server.Url}）");

            var host = $"127.0.0.1:{server.Port}";
            var origin = $"http://{host}";

            // トークンなしで取得できる（待受は 127.0.0.1 のみで、Host / Origin は検証する）。
            var settings = Send(server.Port, "GET", "/api/settings", host, origin);
            Check("api.get", settings.Status == 200, $"設定を取得できる（{settings.Status}）");

            var snapshot = store.Load();
            Check(
                "settings.payload",
                settings.Body.Contains(snapshot.Revision, StringComparison.Ordinal),
                "現在の版を返す");

            // Host / Origin の検証（トークンに代わる防御）。
            Check("host.mismatch", Send(server.Port, "GET", "/api/settings", "evil.example", origin).Status == 403, "Host 不一致は 403");
            Check("origin.mismatch", Send(server.Port, "GET", "/api/settings", host, "http://evil.example").Status == 403, "別オリジンは 403");
            Check("origin.absent", Send(server.Port, "GET", "/api/settings", host, origin: null).Status == 200, "Origin なし（同一オリジンの通常要求）は通す");

            // 保存。
            var updated = snapshot.Json.Replace("\"pollIntervalMs\": 1000", "\"pollIntervalMs\": 800", StringComparison.Ordinal);
            var saveBody = JsonSerializer.Serialize(new { revision = snapshot.Revision, json = updated });
            var save = Send(server.Port, "POST", "/api/settings", host, origin, saveBody);
            Check("save.ok", save.Status == 200, $"正しい保存は 200（{save.Status}）");
            Check("save.applied", store.Load().GameProtection.PollIntervalMs == 800, "保存内容が設定へ反映される");

            // 競合・不正。
            var conflict = Send(server.Port, "POST", "/api/settings", host, origin, saveBody);
            Check("save.conflict", conflict.Status == 409, $"古い版の保存は 409（{conflict.Status}）");

            var invalidBody = JsonSerializer.Serialize(new { revision = store.Load().Revision, json = "{ broken" });
            Check("save.invalid", Send(server.Port, "POST", "/api/settings", host, origin, invalidBody).Status == 400, "壊れた設定は 400");

            var missingField = Send(server.Port, "POST", "/api/settings", host, origin, "{}");
            Check("save.missing-field", missingField.Status == 400, "json フィールドなしは 400");

            // 本文サイズの上限。
            var oversized = Send(server.Port, "POST", "/api/settings", host, origin, new string('a', SettingsServer.MaxBodyBytes + 10));
            Check("body.too-large", oversized.Status == 413, $"上限を超える本文は 413（{oversized.Status}）");

            // 既定へ戻す。
            var restore = Send(server.Port, "POST", "/api/settings/restore", host, origin, "{}");
            Check("restore.ok", restore.Status == 200, "既定復帰は 200");
            Check("restore.applied", store.Load().GameProtection.PollIntervalMs == 1000, "既定値へ戻る");

            // 状態取得と未知のパス。
            Check("status.ok", Send(server.Port, "GET", "/api/status", host, origin).Body.Contains("running", StringComparison.Ordinal), "状態を返す");
            Check("unknown.path", Send(server.Port, "GET", "/api/secret", host, origin).Status == 404, "未知のパスは 404");
            Check("traversal", Send(server.Port, "GET", "/../settings.json", host, origin).Status == 404, "パス探索を拒否する");
            Check("static.missing", Send(server.Port, "GET", "/", host, origin).Status == 404, "index.html が無ければ 404");

            File.WriteAllText(Path.Combine(workDirectory, "index.html"), "<!doctype html><title>t</title>", encoding);
            var page = Send(server.Port, "GET", "/", host, origin);
            Check("static.served", page.Status == 200 && page.Body.Contains("doctype", StringComparison.OrdinalIgnoreCase), "index.html を配信できる");

            // 実際に配信する WebUI と、保存後の反映通知。
            var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            if (!Directory.Exists(webRoot))
            {
                Check("webui.present", false, $"wwwroot が見つからない: {webRoot}");
            }
            else
            {
                SettingsSnapshot? applied = null;
                var store2 = new SettingsStore(Path.Combine(workDirectory, "settings2.json"), defaults);
                using var uiServer = new SettingsServer(store2, webRoot: webRoot, onSaved: snapshot => applied = snapshot);
                uiServer.Start();

                var uiHost = $"127.0.0.1:{uiServer.Port}";
                var uiOrigin = $"http://{uiHost}";

                var index = Send(uiServer.Port, "GET", "/", uiHost, uiOrigin);
                Check(
                    "webui.index",
                    index.Status == 200 && index.Body.Contains("LeafHotKey", StringComparison.Ordinal) &&
                    index.Body.Contains("styles.css", StringComparison.Ordinal) &&
                    index.Body.Contains("app.js", StringComparison.Ordinal) &&
                    !index.Body.Contains("__LEAFHOTKEY_TOKEN__", StringComparison.Ordinal),
                    "WebUI の index.html をトークンなしで配信する");
                Check(
                    "webui.css",
                    Send(uiServer.Port, "GET", "/styles.css", uiHost, uiOrigin).Status == 200,
                    "styles.css を配信する");

                // タブのアイコンは HTML に埋め込む（/favicon.ico を探しに行かせない）。
                Check(
                    "webui.favicon",
                    index.Body.Contains("rel=\"icon\"", StringComparison.Ordinal) &&
                    index.Body.Contains("image/svg+xml", StringComparison.Ordinal),
                    "favicon を data URI で埋め込む");

                var script = Send(uiServer.Port, "GET", "/app.js", uiHost, uiOrigin);
                Check(
                    "webui.js",
                    script.Status == 200 && !script.Body.Contains("X-LeafHotKey-Token", StringComparison.Ordinal),
                    "app.js を配信し、トークンヘッダを使わない");

                // app.js が参照する id が index.html に無いと、画面が無言で壊れる。
                var referenced = new HashSet<string>(StringComparer.Ordinal);
                foreach (var line in script.Body.Split('\n'))
                {
                    foreach (Match match in Regex.Matches(line, "el\\(\"([^\"]+)\"\\)")) referenced.Add(match.Groups[1].Value);
                    if (line.Contains(".forEach((id) =>", StringComparison.Ordinal))
                    {
                        foreach (Match match in Regex.Matches(line, "\"([a-z0-9\\-]+)\"")) referenced.Add(match.Groups[1].Value);
                    }
                }

                var missingIds = referenced.Where(id => !index.Body.Contains($"id=\"{id}\"", StringComparison.Ordinal)).ToArray();
                Check(
                    "webui.ids",
                    missingIds.Length == 0,
                    $"app.js が参照する要素は index.html に存在する（不足: {(missingIds.Length == 0 ? "なし" : string.Join(", ", missingIds))}）");

                Check("webui.reload",
                    Send(uiServer.Port, "GET", "/", uiHost, origin: null).Status == 200 &&
                    Send(uiServer.Port, "GET", "/", uiHost, origin: null).Status == 200,
                    "同じ URL で初回表示と再読み込みができる");

                // プロファイル行のアプリアイコン配信。
                var icon = Send(server.Port, "GET", "/api/icon?name=LeafHotKey.exe", host, origin);
                Check(
                    "icon.ok",
                    icon.Status == 200 && icon.Body.Contains("PNG", StringComparison.Ordinal),
                    $"起動中の実行ファイルのアイコンを PNG で返す（{icon.Status}）");
                var iconMissing = Send(server.Port, "GET", "/api/icon?name=leafhotkey-absent-app.exe", host, origin);
                Check("icon.missing", iconMissing.Status == 404, "見つからない実行ファイルは 404");
                var iconTraversal = Send(server.Port, "GET", "/api/icon?name=..%5C..%5CWindows%5CSystem32%5Ccalc.exe", host, origin);
                Check("icon.traversal", iconTraversal.Status == 404, "パス指定や親ディレクトリ参照は受け付けない");

                // 検知した実行ファイルのフルパスを残し、次回以降はそれを使ってアイコンを出す。
                var detectBody = JsonSerializer.Serialize(new { names = new[] { "LeafHotKey.exe", "leafhotkey-absent-app.exe" } });
                var detect = Send(server.Port, "POST", "/api/app-paths", host, origin, detectBody);
                Check(
                    "apppaths.detect",
                    detect.Status == 200 && detect.Body.Contains("LeafHotKey.exe", StringComparison.Ordinal),
                    $"検知した実行ファイルのフルパスを返す（{detect.Status}）");
                Check("apppaths.stored", store.Load().AppPaths.ContainsKey("LeafHotKey.exe"), "検知したパスを設定へ残す");
                Check("apppaths.absent", !store.Load().AppPaths.ContainsKey("leafhotkey-absent-app.exe"), "見つからない名前は記憶しない");

                var iconStored = Send(server.Port, "GET", "/api/icon?name=LeafHotKey.exe", host, origin);
                Check(
                    "icon.stored",
                    iconStored.Status == 200 && iconStored.Body.Contains("PNG", StringComparison.Ordinal),
                    "保存済みのパスからもアイコンを返す");

                var detectInvalid = Send(server.Port, "POST", "/api/app-paths", host, origin, "{ broken");
                Check("apppaths.invalid", detectInvalid.Status == 400, "壊れた本文は 400");

                // 保存済みパスがまだ有効なら探し直さない（毎回の探索を避ける）。
                var fakeExe = Path.Combine(workDirectory, "fake-app.exe");
                File.WriteAllText(fakeExe, string.Empty, encoding);
                store.MergeAppPaths(new Dictionary<string, string> { ["fake-app.exe"] = fakeExe });
                Send(server.Port, "POST", "/api/app-paths", host, origin, JsonSerializer.Serialize(new { names = new[] { "fake-app.exe" } }));
                Check(
                    "apppaths.kept",
                    store.Load().AppPaths.TryGetValue("fake-app.exe", out var keptPath) && keptPath == fakeExe,
                    "有効な保存パスはそのまま残す");

                // 無効になった保存パス（アプリの移動・削除）は検知し直す。
                store.MergeAppPaths(new Dictionary<string, string> { ["LeafHotKey.exe"] = @"C:\missing\LeafHotKey.exe" });
                Send(server.Port, "POST", "/api/app-paths", host, origin, JsonSerializer.Serialize(new { names = new[] { "LeafHotKey.exe" } }));
                var refreshedPath = store.Load().AppPaths.TryGetValue("LeafHotKey.exe", out var refreshed) ? refreshed : null;
                Check(
                    "apppaths.refreshed",
                    refreshedPath is not null && File.Exists(refreshedPath),
                    $"無効になった保存パスは検知し直す（{refreshedPath}）");

                Check(
                    "webui.port.fixed",
                    SettingsServer.DefaultPort == 17832,
                    $"固定ポート {SettingsServer.DefaultPort} で待ち受ける（使用中は空きポートへ退避）");

                var snapshot2 = store2.Load();
                var body2 = JsonSerializer.Serialize(new
                {
                    revision = snapshot2.Revision,
                    json = snapshot2.Json.Replace("\"resumeDelayMs\": 1000", "\"resumeDelayMs\": 1500", StringComparison.Ordinal),
                });

                var applyResult = Send(uiServer.Port, "POST", "/api/settings", uiHost, uiOrigin, body2);
                Check("webui.save", applyResult.Status == 200, "WebUI 経由の保存が通る");
                Check(
                    "webui.applied",
                    applied is not null && applied.Profiles.Count == snapshot2.Profiles.Count && applied.GameProtection.ResumeDelayMs == 1500,
                    "保存後に新しい設定が呼び出し側へ渡る");
            }
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
