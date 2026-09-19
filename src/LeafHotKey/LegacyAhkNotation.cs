namespace LeafHotKey;

/// <summary>
/// 旧 AHK 表記（^ + ! # と {}）の解析。設定ファイルの移行だけで使う。
/// アプリの表記は <see cref="SendNotation"/> で、この表記は新しく使わない。
/// </summary>
public static class LegacyAhkNotation
{
    /// <summary>修飾キーとして「押しっぱなし」を表現できるキー名。</summary>
    private static readonly IReadOnlyDictionary<string, string> HoldableAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ctrl"] = "Ctrl",
            ["control"] = "Ctrl",
            ["shift"] = "Shift",
            ["alt"] = "Alt",
            ["lwin"] = "LWin",
            ["rwin"] = "RWin",
            ["space"] = "Space",
        };

    public static IReadOnlyList<SendToken> Parse(string sequence)
    {
        var tokens = new List<SendToken>();
        var modifiers = SendModifiers.None;

        for (var index = 0; index < sequence.Length; index++)
        {
            var current = sequence[index];

            switch (current)
            {
                case '^':
                    modifiers |= SendModifiers.Ctrl;
                    continue;
                case '+':
                    modifiers |= SendModifiers.Shift;
                    continue;
                case '!':
                    modifiers |= SendModifiers.Alt;
                    continue;
                case '#':
                    modifiers |= SendModifiers.Win;
                    continue;
                case '{':
                {
                    var close = sequence.IndexOf('}', index + 1);
                    if (close < 0) throw new FormatException($"閉じ括弧がありません: {sequence}");

                    var body = sequence[(index + 1)..close];
                    if (body.Length == 0) throw new FormatException($"空の波括弧があります: {sequence}");

                    tokens.Add(ParseBracedToken(body, modifiers, sequence));
                    modifiers = SendModifiers.None;
                    index = close;
                    continue;
                }

                default:
                    tokens.Add(SendToken.Char(current, modifiers));
                    modifiers = SendModifiers.None;
                    continue;
            }
        }

        if (modifiers != SendModifiers.None)
        {
            throw new FormatException($"修飾キーの後に対象キーがありません: {sequence}");
        }

        return tokens;
    }

    private static SendToken ParseBracedToken(string body, SendModifiers modifiers, string sequence)
    {
        var trimmed = body.Trim();

        // {Alt Down} 形式。
        var space = trimmed.LastIndexOf(' ');
        if (space > 0)
        {
            var suffix = trimmed[(space + 1)..];
            if (TryParseAction(suffix, out var spacedAction))
            {
                return SendToken.Key(NormalizeName(trimmed[..space].Trim(), sequence), spacedAction, modifiers);
            }
        }

        // {AltDown} 形式（元 AHK スクリプトで使われている表記）。
        foreach (var suffix in new[] { "Down", "Up" })
        {
            if (trimmed.Length <= suffix.Length) continue;
            if (!trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;

            var name = trimmed[..^suffix.Length];
            if (!HoldableAliases.ContainsKey(name)) continue;

            var action = suffix.Equals("Down", StringComparison.OrdinalIgnoreCase) ? KeyAction.Down : KeyAction.Up;
            return SendToken.Key(NormalizeName(name, sequence), action, modifiers);
        }

        return SendToken.Key(NormalizeName(trimmed, sequence), KeyAction.Press, modifiers);
    }

    private static bool TryParseAction(string value, out KeyAction action)
    {
        if (value.Equals("down", StringComparison.OrdinalIgnoreCase))
        {
            action = KeyAction.Down;
            return true;
        }

        if (value.Equals("up", StringComparison.OrdinalIgnoreCase))
        {
            action = KeyAction.Up;
            return true;
        }

        action = KeyAction.Press;
        return false;
    }

    /// <summary>キー名を既知の表記へ寄せる。未知の名前は誤送信を避けるため例外にする。</summary>
    private static string NormalizeName(string name, string sequence)
    {
        if (HoldableAliases.TryGetValue(name, out var alias)) return alias;
        if (KeyNames.TryNormalize(name, out var normalized)) return normalized;
        throw new FormatException($"未知のキー名です: {{{name}}} （{sequence}）");
    }
}
