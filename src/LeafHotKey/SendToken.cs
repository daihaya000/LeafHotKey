namespace LeafHotKey;

/// <summary>AHK の修飾キー記号に対応する。</summary>
[Flags]
public enum SendModifiers
{
    None = 0,

    /// <summary>^</summary>
    Ctrl = 1,

    /// <summary>+</summary>
    Shift = 2,

    /// <summary>!</summary>
    Alt = 4,

    /// <summary>#</summary>
    Win = 8,
}

/// <summary>キーの押下種別。</summary>
public enum KeyAction
{
    /// <summary>押して離す。</summary>
    Press,

    /// <summary>押したままにする。</summary>
    Down,

    /// <summary>離す。</summary>
    Up,
}

/// <summary>
/// 送信文字列を構成する 1 要素。
/// 文字は仮想キーへ変換せずそのまま保持し、キーボード配列の解決は送信時に行う。
/// </summary>
public sealed class SendToken
{
    private SendToken(string? keyName, char? character, KeyAction action, SendModifiers modifiers)
    {
        KeyName = keyName;
        Character = character;
        Action = action;
        Modifiers = modifiers;
    }

    /// <summary>{Esc} のような名前付きキー。文字トークンでは null。</summary>
    public string? KeyName { get; }

    /// <summary>a や : のような文字。名前付きキーでは null。</summary>
    public char? Character { get; }

    public KeyAction Action { get; }

    public SendModifiers Modifiers { get; }

    public static SendToken Key(string name, KeyAction action, SendModifiers modifiers)
        => new(name, null, action, modifiers);

    public static SendToken Char(char character, SendModifiers modifiers)
        => new(null, character, KeyAction.Press, modifiers);

    public override string ToString()
    {
        var target = KeyName is not null ? "{" + KeyName + "}" : Character?.ToString() ?? "?";
        var action = Action == KeyAction.Press ? string.Empty : " " + Action.ToString().ToLowerInvariant();
        var modifiers = Modifiers == SendModifiers.None ? string.Empty : Modifiers + "+";
        return modifiers + target + action;
    }
}
