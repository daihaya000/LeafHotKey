using System.Text.Json;

namespace LeafHotKey;

/// <summary>ホットキーの発火条件。</summary>
public sealed class HotkeyTrigger
{
    public required string Key { get; init; }

    /// <summary>MButton &amp; f13 の MButton にあたる前置キー。</summary>
    public string? Prefix { get; init; }

    /// <summary>^+e のような通常の修飾キー。</summary>
    public SendModifiers Modifiers { get; init; }

    /// <summary>AHK の * に相当。修飾キーの有無を問わない。</summary>
    public bool AnyModifier { get; init; }

    /// <summary>AHK の ~ に相当。元の入力を抑止しない。</summary>
    public bool PassThroughNative { get; init; }
}

/// <summary>ホットキーの動作種別。</summary>
public enum HotkeyActionKind
{
    /// <summary>キー列を送る（AHK の Snd）。</summary>
    Send,

    /// <summary>トリガを離すまで修飾キーを保持する（AHK の Hold）。</summary>
    Hold,

    /// <summary>元の入力を通すだけ（AHK の *~key::return）。</summary>
    Passthrough,
}

/// <summary>1 つのホットキー割り当て。</summary>
public sealed class HotkeyRule
{
    public required HotkeyTrigger Trigger { get; init; }

    public required HotkeyActionKind Kind { get; init; }

    /// <summary>Send の送信単位。Snd の第2引数は別要素として保持する。</summary>
    public IReadOnlyList<IReadOnlyList<SendToken>> Sequences { get; init; } = Array.Empty<IReadOnlyList<SendToken>>();

    /// <summary>Hold で押し続ける修飾キー。</summary>
    public string? HoldModifier { get; init; }

    /// <summary>Hold を解除する契機となるキー。</summary>
    public string? ReleaseOn { get; init; }

    /// <summary>Hold に {Blind} を付けるか。</summary>
    public bool Blind { get; init; } = true;
}

/// <summary>アプリ単位のプロファイル。</summary>
public sealed class HotkeyProfile
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required bool Enabled { get; init; }

    public required IReadOnlyList<string> ProcessNames { get; init; }

    public required IReadOnlyList<HotkeyRule> Rules { get; init; }

    /// <summary>前面ウィンドウの実行ファイル名が対象かどうか。</summary>
    public bool Matches(string processName)
        => ProcessNames.Any(name => string.Equals(name, processName, StringComparison.OrdinalIgnoreCase));
}

/// <summary>settings.json からプロファイルを読み込む。</summary>
public static class HotkeyProfileLoader
{
    public static IReadOnlyList<HotkeyProfile> Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);

        if (!document.RootElement.TryGetProperty("profiles", out var profiles) ||
            profiles.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"profiles セクションがありません: {path}");
        }

        return profiles.EnumerateArray().Select(ReadProfile).ToList();
    }

    private static HotkeyProfile ReadProfile(JsonElement element)
    {
        var id = ReadRequiredString(element, "id");

        try
        {
            return new HotkeyProfile
            {
                Id = id,
                Name = ReadRequiredString(element, "name"),
                Enabled = !element.TryGetProperty("enabled", out var enabled) || enabled.ValueKind != JsonValueKind.False,
                ProcessNames = ReadStrings(element, "processNames"),
                Rules = ReadRules(element),
            };
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException)
        {
            throw new InvalidDataException($"プロファイル {id} の解析に失敗しました: {ex.Message}", ex);
        }
    }

    private static IReadOnlyList<HotkeyRule> ReadRules(JsonElement profile)
    {
        if (!profile.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<HotkeyRule>();
        }

        return rules.EnumerateArray().Select(ReadRule).ToList();
    }

    private static HotkeyRule ReadRule(JsonElement element)
    {
        if (!element.TryGetProperty("trigger", out var trigger) || !element.TryGetProperty("action", out var action))
        {
            throw new InvalidDataException("rule には trigger と action が必要です。");
        }

        var parsedTrigger = ReadTrigger(trigger);
        var kind = ReadRequiredString(action, "type") switch
        {
            "send" => HotkeyActionKind.Send,
            "hold" => HotkeyActionKind.Hold,
            "passthrough" => HotkeyActionKind.Passthrough,
            var other => throw new InvalidDataException($"未知の action.type です: {other}"),
        };

        return kind switch
        {
            HotkeyActionKind.Send => new HotkeyRule
            {
                Trigger = parsedTrigger,
                Kind = kind,
                Sequences = SendSequenceParser.ParseAll(ReadStrings(action, "sequence")),
            },
            HotkeyActionKind.Hold => new HotkeyRule
            {
                Trigger = parsedTrigger,
                Kind = kind,
                HoldModifier = ReadHoldModifier(action),
                ReleaseOn = ReadRequiredString(action, "releaseOn"),
                Blind = !action.TryGetProperty("blind", out var blind) || blind.ValueKind != JsonValueKind.False,
            },
            _ => new HotkeyRule { Trigger = parsedTrigger, Kind = kind },
        };
    }

    private static string ReadHoldModifier(JsonElement action)
    {
        var raw = ReadRequiredString(action, "modifier");
        if (!KeyNames.TryNormalize(raw, out var normalized))
        {
            throw new InvalidDataException($"未知の保持キーです: {raw}");
        }

        return normalized;
    }

    private static HotkeyTrigger ReadTrigger(JsonElement element)
    {
        var key = ReadRequiredString(element, "key");
        if (!KeyNames.IsKnown(key) && key.Length != 1)
        {
            throw new InvalidDataException($"未知のトリガキーです: {key}");
        }

        var prefix = element.TryGetProperty("prefix", out var prefixValue) && prefixValue.ValueKind == JsonValueKind.String
            ? prefixValue.GetString()
            : null;

        if (prefix is not null && !KeyNames.IsKnown(prefix) && prefix.Length != 1)
        {
            throw new InvalidDataException($"未知の前置キーです: {prefix}");
        }

        return new HotkeyTrigger
        {
            Key = key,
            Prefix = prefix,
            Modifiers = ReadModifiers(element),
            AnyModifier = element.TryGetProperty("anyModifier", out var any) && any.ValueKind == JsonValueKind.True,
            PassThroughNative = element.TryGetProperty("passThroughNative", out var pass) && pass.ValueKind == JsonValueKind.True,
        };
    }

    private static SendModifiers ReadModifiers(JsonElement element)
    {
        if (!element.TryGetProperty("modifiers", out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return SendModifiers.None;
        }

        var modifiers = SendModifiers.None;
        foreach (var item in value.EnumerateArray())
        {
            modifiers |= item.GetString() switch
            {
                "Ctrl" => SendModifiers.Ctrl,
                "Shift" => SendModifiers.Shift,
                "Alt" => SendModifiers.Alt,
                "Win" => SendModifiers.Win,
                var other => throw new InvalidDataException($"未知の修飾キーです: {other}"),
            };
        }

        return modifiers;
    }

    private static string ReadRequiredString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"{name} が必要です。");
        }

        return value.GetString()!;
    }

    private static IReadOnlyList<string> ReadStrings(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .ToList();
    }
}
