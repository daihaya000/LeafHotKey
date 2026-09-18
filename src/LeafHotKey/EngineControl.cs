using System.Diagnostics;
using System.Text.Json;

namespace LeafHotKey;

/// <summary>入力エンジン（別プロセス）に対する操作。自己検証では差し替えられるように抽象化する。</summary>
public interface IHostControl
{
    /// <summary>入力エンジンが制御チャネルに応答するか。</summary>
    bool IsRunning();

    /// <summary>
    /// ゲーム保護として入力エンジンの退避を要求する。
    /// エンジン側でフック解除とキー解放が終わり、応答しなくなるまで確認する。
    /// </summary>
    bool RequestGameShutdown();

    /// <summary>入力エンジンを起動する。</summary>
    bool Start();
}

/// <summary>
/// 入力エンジンを制御する既定実装。
/// 本体（WebUI）は動作したまま、エンジンだけを停止・起動できる。
/// </summary>
public sealed class EngineControl : IHostControl
{
    /// <summary>エンジンが動作していないときに設定画面へ返す状態。</summary>
    private const string StoppedJson =
        "{\"state\":\"stopped\",\"engineInstalled\":false,\"activeProfile\":\"\",\"heldModifiers\":[]," +
        "\"holdSweeps\":0,\"holdReasserts\":0,\"holdReleases\":0,\"backend\":\"builtin\",\"backendStatus\":\"停止中\"," +
        "\"backendNote\":\"入力エンジンは停止しています。\",\"backendScript\":\"\",\"backendGenerated\":\"\"," +
        "\"backendPid\":null,\"backendRestarts\":0}";

    private readonly string? _enginePath;
    private readonly int _shutdownTimeoutMs;

    public EngineControl(string? enginePath, int shutdownTimeoutMs = 10000)
    {
        _enginePath = enginePath;
        _shutdownTimeoutMs = shutdownTimeoutMs;
    }

    /// <summary>エンジンの実行ファイルが見つかっているか。</summary>
    public bool Available => _enginePath is not null;

    public bool IsRunning() => ControlClient.IsHostResponding(1000, ControlProtocol.EnginePipeName);

    public bool RequestGameShutdown()
    {
        var response = ControlClient.Send(
            ControlProtocol.Shutdown + " " + ControlProtocol.ReasonGame,
            2000,
            ControlProtocol.EnginePipeName);
        if (response is null) return !IsRunning();
        if (response.StartsWith(ControlProtocol.ErrorPrefix, StringComparison.Ordinal)) return false;

        // 応答だけでは終了完了と判断しない。応答が止まるまで確認する。
        var deadline = DateTime.UtcNow.AddMilliseconds(_shutdownTimeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (!IsRunning()) return true;
            Thread.Sleep(200);
        }

        return false;
    }

    public bool Start()
    {
        if (_enginePath is null || !File.Exists(_enginePath)) return false;

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = _enginePath,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(_enginePath) ?? AppContext.BaseDirectory,
            });

            return process is not null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>エンジンへコマンドを転送する。停止中は null。</summary>
    public string? Forward(string command) => ControlClient.Send(command, 2000, ControlProtocol.EnginePipeName);

    /// <summary>設定画面へ渡す状態。エンジンが停止していても必ず JSON を返す。</summary>
    public string StatusJson()
    {
        var response = Forward(ControlProtocol.Status + " " + ControlProtocol.StatusJson);
        if (response is null || !response.StartsWith("OK ", StringComparison.Ordinal)) return StoppedJson;

        var json = response[3..].Trim();
        return json.Length > 0 && json[0] == '{' ? json : StoppedJson;
    }

    /// <summary>
    /// 切り分け用のログ。エンジンの入力イベントに、本体側の監視イベントを続けて返す。
    /// </summary>
    public string LogJson(IReadOnlyList<string> hostEvents)
    {
        var events = new List<string>();

        var response = Forward(ControlProtocol.Log);
        if (response is not null && response.StartsWith("OK ", StringComparison.Ordinal))
        {
            try
            {
                using var document = JsonDocument.Parse(response[3..]);
                if (document.RootElement.TryGetProperty("events", out var node) && node.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in node.EnumerateArray())
                    {
                        if (entry.GetString() is { } line) events.Add(line);
                    }
                }
            }
            catch (JsonException)
            {
                // 読めない応答は無視する。ログ表示のために状態を壊さない。
            }
        }

        events.AddRange(hostEvents);
        return JsonSerializer.Serialize(new { events });
    }
}
