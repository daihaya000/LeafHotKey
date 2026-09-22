namespace LeafHotKey;

/// <summary>
/// フックが返す仮想キーを、台帳で使うキー名へ戻す。
/// 文字キーは現在のキーボード配列で解決し、JIS の記号もその配列の刻印で扱う。
/// </summary>
public static class VirtualKeyNames
{
    private const uint MapVkToChar = 2;

    private static readonly IReadOnlyDictionary<ushort, string> Named = Build();

    /// <summary>該当するキー名。判定できない場合は null。</summary>
    public static string? NameFor(ushort virtualKey)
    {
        if (Named.TryGetValue(virtualKey, out var name)) return name;

        var mapped = NativeMethods.MapVirtualKeyEx(virtualKey, MapVkToChar, KeyResolver.CurrentLayout);
        if (mapped == 0) return null;

        var character = (char)(mapped & 0x7FFF);
        return char.IsControl(character) ? null : char.ToLowerInvariant(character).ToString();
    }

    private static IReadOnlyDictionary<ushort, string> Build()
    {
        var map = new Dictionary<ushort, string>
        {
            [0x1B] = "Esc",
            [0x0D] = "Enter",
            [0x20] = "Space",
            [0x09] = "Tab",
            [0x08] = "Backspace",
            [0x2E] = "Delete",
            [0x2D] = "Insert",
            [0x24] = "Home",
            [0x23] = "End",
            [0x21] = "PgUp",
            [0x22] = "PgDn",
            [0x25] = "Left",
            [0x26] = "Up",
            [0x27] = "Right",
            [0x28] = "Down",
            [0x13] = "Pause",
            [0x2C] = "PrintScreen",
            [0x1C] = "Convert",
            [0x1D] = "NonConvert",
            [0xC0] = "ZenkakuHankaku",
        };

        for (var index = 1; index <= 24; index++)
        {
            map[(ushort)(0x70 + index - 1)] = "f" + index;
        }

        return map;
    }
}
