using System.Text.Json;

namespace LeafHotKey;

/// <summary>
/// ゲーム保護の設定。停止トリガと復帰条件は別リストとして扱う
/// （元AHKでも停止6種・復帰待機3種で一致していないため、勝手に統一しない）。
/// </summary>
public sealed class GameProtectionSettings
{
    public required bool Enabled { get; init; }

    public required int PollIntervalMs { get; init; }

    /// <summary>対象プロセスが消えてから復帰するまでの待機時間。</summary>
    public required int ResumeDelayMs { get; init; }

    /// <summary>検知したら本体を退避させるプロセス名。</summary>
    public required IReadOnlyList<string> StopTriggerProcessNames { get; init; }

    /// <summary>これらが 1 つでも残っている間は本体を復帰させないプロセス名。</summary>
    public required IReadOnlyList<string> ResumeProcessNames { get; init; }

    /// <summary>設定として成立しない値を拒否する。空リストは「保護なし」と区別する。</summary>
    public void Validate()
    {
        if (PollIntervalMs <= 0) throw new InvalidDataException("pollIntervalMs は 1 以上にしてください。");
        if (ResumeDelayMs < 0) throw new InvalidDataException("resumeDelayMs は 0 以上にしてください。");
        if (Enabled && StopTriggerProcessNames.Count == 0)
        {
            throw new InvalidDataException("ゲーム保護が有効な場合、stopTriggerProcessNames を空にできません。");
        }

        foreach (var name in StopTriggerProcessNames.Concat(ResumeProcessNames))
        {
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("プロセス名に空文字を含められません。");
        }
    }

    public static GameProtectionSettings Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);

        if (!document.RootElement.TryGetProperty("gameProtection", out var node))
        {
            throw new InvalidDataException($"gameProtection セクションがありません: {path}");
        }

        var settings = new GameProtectionSettings
        {
            Enabled = ReadBool(node, "enabled", true),
            PollIntervalMs = ReadInt(node, "pollIntervalMs", 1000),
            ResumeDelayMs = ReadInt(node, "resumeDelayMs", 1000),
            StopTriggerProcessNames = ReadStrings(node, "stopTriggerProcessNames"),
            ResumeProcessNames = ReadStrings(node, "resumeProcessNames"),
        };

        settings.Validate();
        return settings;
    }

    private static bool ReadBool(JsonElement node, string name, bool fallback)
        => node.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    private static int ReadInt(JsonElement node, string name, int fallback)
        => node.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : fallback;

    private static IReadOnlyList<string> ReadStrings(JsonElement node, string name)
    {
        if (!node.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var items = new List<string>();
        foreach (var element in value.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.String && element.GetString() is { } text) items.Add(text);
        }

        return items;
    }
}
