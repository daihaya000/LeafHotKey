namespace LeafHotKey;

/// <summary>
/// 移行台帳で使われるキー名を正規表記へ寄せる。
/// 未知の名前を黙って通すと誤ったキーを送るため、呼び出し側で失敗として扱えるようにする。
/// </summary>
public static class KeyNames
{
    private static readonly IReadOnlyDictionary<string, string> Known = Build();

    public static bool TryNormalize(string name, out string normalized)
        => Known.TryGetValue(name.Trim(), out normalized!);

    public static bool IsKnown(string name) => TryNormalize(name, out _);

    private static IReadOnlyDictionary<string, string> Build()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Add(string canonical, params string[] aliases)
        {
            map[canonical] = canonical;
            foreach (var alias in aliases) map[alias] = canonical;
        }

        for (var index = 1; index <= 24; index++) Add("F" + index);

        Add("Esc", "Escape");
        Add("Enter", "Return");
        Add("Space");
        Add("Tab");
        Add("Backspace", "BS");
        Add("Delete", "Del");
        Add("Insert", "Ins");
        Add("Home");
        Add("End");
        Add("PgUp", "PageUp");
        Add("PgDn", "PageDown");
        Add("Up");
        Add("Down");
        Add("Left");
        Add("Right");
        Add("Pause");
        Add("AppsKey");
        Add("PrintScreen");
        Add("Ctrl", "Control", "LCtrl", "RCtrl");
        Add("Shift", "LShift", "RShift");
        Add("Alt", "LAlt", "RAlt");
        Add("LWin");
        Add("RWin");

        // マウス入力（トリガ側で使う）。
        Add("LButton");
        Add("RButton");
        Add("MButton");
        Add("WheelUp");
        Add("WheelDown");

        return map;
    }
}
