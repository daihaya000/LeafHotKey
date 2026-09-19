using System.Text;

namespace LeafHotKey;

/// <summary>
/// 送信内容の表記。AHK の記号（^ + ! # や {}）は使わず、「Ctrl+Shift+g」「Esc」「Alt↓ g」のように書く。
/// 1 行が 1 つの送信単位で、空白で区切った 1 つ分が 1 キー。
/// 文字は 1 文字ずつ送るため、f と 5 を続けて送る場合は「f 5」と書く。
/// </summary>
public static class SendNotation
{
    /// <summary>1 つの送信単位を解析する。</summary>
    public static IReadOnlyList<SendToken> Parse(string text)
    {
        var tokens = new List<SendToken>();
        foreach (var part in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            tokens.AddRange(ParsePart(part, text));
        }

        if (tokens.Count == 0) throw new FormatException("送るキーが空です。");
        return tokens;
    }

    /// <summary>複数の送信単位をまとめて解析する。</summary>
    public static IReadOnlyList<IReadOnlyList<SendToken>> ParseAll(IEnumerable<string> texts)
        => texts.Select(Parse).ToList();

    /// <summary>1 つの送信単位を表記へ戻す。</summary>
    public static string Format(IEnumerable<SendToken> tokens)
    {
        var parts = tokens.Select(FormatToken).ToList();
        if (parts.Count == 0) throw new FormatException("送るキーが空です。");
        return string.Join(" ", parts);
    }

    /// <summary>複数の送信単位をまとめて表記へ戻す。</summary>
    public static IReadOnlyList<string> FormatAll(IEnumerable<IReadOnlyList<SendToken>> sequences)
        => sequences.Select(Format).ToList();

    private static IEnumerable<SendToken> ParsePart(string part, string text)
    {
        var body = part;
        var action = KeyAction.Press;
        if (body.EndsWith('↓'))
        {
            action = KeyAction.Down;
            body = body[..^1];
        }
        else if (body.EndsWith('↑'))
        {
            action = KeyAction.Up;
            body = body[..^1];
        }

        var (keyPart, modifiers) = SplitModifiers(body, text);

        if (KeyNames.TryNormalize(keyPart, out var name))
        {
            return new[] { SendToken.Key(name, action, modifiers) };
        }

        if (action != KeyAction.Press)
        {
            throw new FormatException($"押しっぱなしにするキー名ではありません: {keyPart}（{text}）");
        }

        // 文字は 1 文字ずつ送る。修飾キーは最初の 1 文字にだけ掛かる。
        var chars = new List<SendToken>(keyPart.Length);
        for (var index = 0; index < keyPart.Length; index++)
        {
            chars.Add(SendToken.Char(keyPart[index], index == 0 ? modifiers : SendModifiers.None));
        }

        return chars;
    }

    private static (string KeyPart, SendModifiers Modifiers) SplitModifiers(string body, string text)
    {
        if (body.Length == 0) throw new FormatException($"キーが指定されていません（{text}）");

        // 「+」キーは単独なら + 、修飾付きなら Ctrl++ と書く。
        if (body == "+") return ("+", SendModifiers.None);
        if (body.EndsWith("++", StringComparison.Ordinal))
        {
            return ("+", ParseModifiers(body[..^2].Split('+', StringSplitOptions.RemoveEmptyEntries), text));
        }

        var segments = body.Split('+');
        var keyPart = segments[^1];
        if (keyPart.Length == 0) throw new FormatException($"キーが指定されていません: {body}（{text}）");

        return (keyPart, ParseModifiers(segments[..^1], text));
    }

    private static SendModifiers ParseModifiers(IEnumerable<string> names, string text)
    {
        var modifiers = SendModifiers.None;
        foreach (var raw in names)
        {
            var name = raw.Trim();
            if (name.Length == 0) throw new FormatException($"修飾キーの指定が不正です（{text}）");

            modifiers |= name.ToLowerInvariant() switch
            {
                "ctrl" or "control" => SendModifiers.Ctrl,
                "shift" => SendModifiers.Shift,
                "alt" => SendModifiers.Alt,
                "win" or "lwin" or "rwin" => SendModifiers.Win,
                _ => throw new FormatException($"未知の修飾キーです: {name}（{text}）"),
            };
        }

        return modifiers;
    }

    private static string FormatToken(SendToken token)
    {
        var text = new StringBuilder();
        if (token.Modifiers.HasFlag(SendModifiers.Ctrl)) text.Append("Ctrl+");
        if (token.Modifiers.HasFlag(SendModifiers.Shift)) text.Append("Shift+");
        if (token.Modifiers.HasFlag(SendModifiers.Alt)) text.Append("Alt+");
        if (token.Modifiers.HasFlag(SendModifiers.Win)) text.Append("Win+");

        text.Append(token.KeyName ?? CharText(token.Character!.Value));
        if (token.Action == KeyAction.Down) text.Append('↓');
        else if (token.Action == KeyAction.Up) text.Append('↑');
        return text.ToString();
    }

    /// <summary>文字を表記へ戻す。空白はキー名（Space）に寄せる。</summary>
    private static string CharText(char character) => character switch
    {
        ' ' => "Space",
        '\t' => "Tab",
        _ => character.ToString(),
    };
}
