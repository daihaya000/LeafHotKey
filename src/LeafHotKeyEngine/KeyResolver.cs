namespace LeafHotKey;

/// <summary>解決済みのキー入力。</summary>
public readonly struct ResolvedKey
{
    public ResolvedKey(ushort virtualKey, SendModifiers modifiers, KeyAction action)
    {
        VirtualKey = virtualKey;
        Modifiers = modifiers;
        Action = action;
    }

    public ushort VirtualKey { get; }

    /// <summary>この入力を送るときに押しておく必要がある修飾キー。</summary>
    public SendModifiers Modifiers { get; }

    public KeyAction Action { get; }
}

/// <summary>
/// 送信トークンを仮想キーへ解決する。
/// 文字は現在のキーボード配列（JIS を含む）で解決し、必要な Shift/Ctrl/Alt を足す。
/// </summary>
public static class KeyResolver
{
    private static readonly IReadOnlyDictionary<string, ushort> VirtualKeys = BuildVirtualKeys();

    public static IntPtr CurrentLayout => NativeMethods.GetKeyboardLayout(0);

    public static bool TryResolve(SendToken token, IntPtr layout, out ResolvedKey resolved)
    {
        if (token.KeyName is { } name)
        {
            if (!VirtualKeys.TryGetValue(name, out var virtualKey))
            {
                resolved = default;
                return false;
            }

            resolved = new ResolvedKey(virtualKey, token.Modifiers, token.Action);
            return true;
        }

        if (token.Character is not { } character)
        {
            resolved = default;
            return false;
        }

        var scan = NativeMethods.VkKeyScanEx(character, layout);
        if (scan == -1)
        {
            // 現在の配列で入力できない文字。無理に別のキーへ割り当てない。
            resolved = default;
            return false;
        }

        var modifiers = token.Modifiers;
        var state = (scan >> 8) & 0xFF;
        if ((state & 1) != 0) modifiers |= SendModifiers.Shift;
        if ((state & 2) != 0) modifiers |= SendModifiers.Ctrl;
        if ((state & 4) != 0) modifiers |= SendModifiers.Alt;

        resolved = new ResolvedKey((ushort)(scan & 0xFF), modifiers, token.Action);
        return true;
    }

    /// <summary>修飾キー自体の仮想キー。</summary>
    public static ushort VirtualKeyFor(SendModifiers modifier) => modifier switch
    {
        SendModifiers.Ctrl => 0x11,
        SendModifiers.Shift => 0x10,
        SendModifiers.Alt => 0x12,
        SendModifiers.Win => 0x5B,
        _ => 0,
    };

    /// <summary>
    /// 台帳のキー名から仮想キーを引く（保持キーの取りこぼし検出用）。
    /// マウスボタンや未知の名前は false を返し、呼び出し側は判定を諦める。
    /// </summary>
    public static bool TryVirtualKeyFor(string keyName, out ushort virtualKey)
    {
        if (VirtualKeys.TryGetValue(keyName, out virtualKey)) return true;

        virtualKey = keyName switch
        {
            "MButton" => 0x04,
            "LButton" => 0x01,
            "RButton" => 0x02,
            _ => 0,
        };

        if (virtualKey != 0) return true;

        // 文字キー（現在の配列で刻印されるキー）も引けるようにする。
        if (keyName.Length == 1)
        {
            var scan = NativeMethods.VkKeyScanEx(keyName[0], CurrentLayout);
            if (scan != -1)
            {
                virtualKey = (ushort)(scan & 0xFF);
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyDictionary<string, ushort> BuildVirtualKeys()
    {
        var map = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            ["Esc"] = 0x1B,
            ["Enter"] = 0x0D,
            ["Space"] = 0x20,
            ["Tab"] = 0x09,
            ["Backspace"] = 0x08,
            ["Delete"] = 0x2E,
            ["Insert"] = 0x2D,
            ["Home"] = 0x24,
            ["End"] = 0x23,
            ["PgUp"] = 0x21,
            ["PgDn"] = 0x22,
            ["Left"] = 0x25,
            ["Up"] = 0x26,
            ["Right"] = 0x27,
            ["Down"] = 0x28,
            ["Pause"] = 0x13,
            ["PrintScreen"] = 0x2C,
            ["AppsKey"] = 0x5D,
            ["Ctrl"] = 0x11,
            ["Shift"] = 0x10,
            ["Alt"] = 0x12,
            ["LWin"] = 0x5B,
            ["RWin"] = 0x5C,
            ["Convert"] = 0x1C,
            ["NonConvert"] = 0x1D,
            ["ZenkakuHankaku"] = 0xC0,
        };

        for (var index = 1; index <= 24; index++)
        {
            map["F" + index] = (ushort)(0x70 + index - 1);
        }

        return map;
    }
}
