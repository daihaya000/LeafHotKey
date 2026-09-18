using System.Text;

namespace LeafHotKey;

/// <summary>
/// 設定画面のプロファイルから AutoHotkey (v1) スクリプトを生成する。
/// 生成物は保存のたびに上書きされるため、手で編集しても次回の保存で戻る。
/// ゲーム保護は本体側の Watcher が担当するので、ここには入れない。
/// </summary>
public static class AhkScriptWriter
{
    /// <summary>生成したスクリプトの保存先。元スクリプトと同じフォルダーに置く。</summary>
    public static string PathFor(string scriptPath)
    {
        var full = scriptPath.Trim();
        if (full.Length == 0)
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LeafHotKey",
                "MySet.generated.ahk");
        }

        var directory = Path.GetDirectoryName(full);
        var name = Path.GetFileNameWithoutExtension(full);
        if (string.IsNullOrEmpty(directory)) directory = Environment.CurrentDirectory;

        return Path.Combine(directory, name + ".generated.ahk");
    }

    /// <summary>設定 JSON から生成して書き出す。書き出したパスを返す。</summary>
    public static string Write(string settingsJson, string targetPath)
    {
        var script = Build(settingsJson);
        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        File.WriteAllText(targetPath, script, new UTF8Encoding(false));
        return targetPath;
    }

    /// <summary>設定 JSON から AHK スクリプトを組み立てる。</summary>
    public static string Build(string settingsJson)
    {
        var profiles = HotkeyProfileLoader.LoadJson(settingsJson);
        return Build(profiles);
    }

    /// <summary>プロファイルから AHK スクリプトを組み立てる。</summary>
    public static string Build(IReadOnlyList<HotkeyProfile> profiles)
    {
        var text = new StringBuilder();

        text.AppendLine("; LeafHotKey が設定画面の内容から生成したファイルです。");
        text.AppendLine("; 保存のたびに上書きされるので、直接編集しても次回の保存で戻ります。");
        text.AppendLine("; ゲーム保護は LeafHotKey 本体（Watcher）が担当します。");
        text.AppendLine("#NoEnv");
        text.AppendLine("#InstallKeybdHook");
        text.AppendLine("#UseHook");
        text.AppendLine("#IfWinActive");
        text.AppendLine("#WinActivateForce");
        text.AppendLine("#SingleInstance force");
        text.AppendLine("SetBatchLines, -1");
        text.AppendLine();
        text.AppendLine(Helpers);
        text.AppendLine();

        // 複数の実行ファイルを持つプロファイルはグループにまとめる（元 AHK と同じ扱い）。
        var groups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles)
        {
            if (!profile.Enabled || profile.ProcessNames.Count <= 1) continue;
            var group = "LeafHotKeyGroup_" + profile.Id;
            groups[profile.Id] = group;
            foreach (var name in profile.ProcessNames)
            {
                text.AppendLine($"GroupAdd, {group}, ahk_exe {name}");
            }
        }

        if (groups.Count > 0) text.AppendLine();

        foreach (var profile in profiles)
        {
            if (!profile.Enabled) continue;

            text.AppendLine($"; --- {profile.Name} ({string.Join(", ", profile.ProcessNames)}) ---");
            if (groups.TryGetValue(profile.Id, out var groupName))
            {
                text.AppendLine($"#IfWinActive, ahk_group {groupName}");
            }
            else if (profile.ProcessNames.Count == 1)
            {
                text.AppendLine($"#IfWinActive, ahk_exe {profile.ProcessNames[0]}");
            }
            else
            {
                text.AppendLine("; 対象プロセスが未設定のため、このプロファイルは出力しません。");
                text.AppendLine("#IfWinActive");
                text.AppendLine();
                continue;
            }

            foreach (var rule in profile.Rules)
            {
                AppendRule(text, rule);
            }

            text.AppendLine("#IfWinActive");
            text.AppendLine();
        }

        return text.ToString();
    }

    private static void AppendRule(StringBuilder text, HotkeyRule rule)
    {
        var trigger = rule.Trigger;

        // *~key::return は元入力を通しつつ前置キーとして使う。
        if (rule.Kind == HotkeyActionKind.Passthrough)
        {
            var mark = trigger.PassThroughNative ? "*~" : "*";
            if (trigger.AnyModifier && !trigger.PassThroughNative) mark = "*";
            text.AppendLine($"    {mark}{HotkeyText(trigger)}::return");
            if (trigger.PassThroughNative) text.AppendLine($"    {mark}{HotkeyText(trigger)} up::return");
            return;
        }

        var hotkey = HotkeyText(trigger);
        var body = BodyLines(rule);
        if (body.Count == 1)
        {
            text.AppendLine($"    {hotkey}::{body[0]}");
            return;
        }

        text.AppendLine($"    {hotkey}::");
        foreach (var line in body) text.AppendLine($"        {line}");
        text.AppendLine("    return");
    }

    /// <summary>本文（1 行ならそのまま、複数ならブロック）を組み立てる。</summary>
    private static List<string> BodyLines(HotkeyRule rule)
    {
        var trigger = rule.Trigger;
        switch (rule.Kind)
        {
            case HotkeyActionKind.Hold when rule.HoldModifier is { } modifier:
            {
                var release = rule.ReleaseOn ?? trigger.Key;
                var blind = rule.Blind ? string.Empty : ", 0";
                return new List<string> { $"Hold(\"{Escape(modifier)}\", \"{Escape(release)}\"{blind})" };
            }

            default:
            {
                // 台帳の表記をそのまま使う（AHK の送信文字列として正しい形）。
                var sequences = rule.SequenceTexts.Count > 0
                    ? rule.SequenceTexts
                    : rule.Sequences.Select(sequence => string.Join(string.Empty, sequence.Select(token => token.ToString()))).ToList();

                var lines = sequences.Select(sequence => $"Snd(\"{Escape(sequence)}\")").ToList();
                return lines.Count > 0 ? lines : new List<string> { "return" };
            }
        }
    }

    /// <summary>MButton &amp; f13 や ^+e のようなホットキー表記。</summary>
    private static string HotkeyText(HotkeyTrigger trigger)
    {
        var prefix = trigger.Prefix is { Length: > 0 } value ? value + " & " : string.Empty;
        var wildcard = trigger.AnyModifier && !trigger.PassThroughNative ? "*" : string.Empty;

        var modifiers = new StringBuilder();
        if (trigger.Modifiers.HasFlag(SendModifiers.Ctrl)) modifiers.Append('^');
        if (trigger.Modifiers.HasFlag(SendModifiers.Shift)) modifiers.Append('+');
        if (trigger.Modifiers.HasFlag(SendModifiers.Alt)) modifiers.Append('!');
        if (trigger.Modifiers.HasFlag(SendModifiers.Win)) modifiers.Append('#');

        return $"{wildcard}{prefix}{modifiers}{trigger.Key}";
    }

    /// <summary>AHK v1 の文字列はバッククォートで逃がす。</summary>
    private static string Escape(string value) => value
        .Replace("`", "``", StringComparison.Ordinal)
        .Replace("\"", "`\"", StringComparison.Ordinal);

    /// <summary>元 AHK と同じ共通処理。生成物にそのまま埋め込む。</summary>
    private const string Helpers = """"
IME_SET(SetSts, WinTitle="A") {
    ControlGet,hwnd,HWND,,,%WinTitle%
    if    (WinActive(WinTitle))    {
        ptrSize := !A_PtrSize ? 4 : A_PtrSize
        VarSetCapacity(stGTI, cbSize:=4+4+(PtrSize*6)+16, 0)
        NumPut(cbSize, stGTI, 0, "UInt")
        hwnd := DllCall("GetGUIThreadInfo", Uint,0, Uint,&stGTI)
            ? NumGet(stGTI,8+PtrSize,"UInt") : hwnd
    }

    return DllCall("SendInputMessage"
        , UInt, DllCall("imm32\ImmGetDefaultIMEWnd", Uint,hwnd)
        , UInt, 0x0283
        , Int, 0x006
        , Int, SetSts)
}

Snd(keys, then="") {
    IME_SET(0)
    Sleep,2
    SendInput,%keys%
    if (then != "")
        SendInput,%then%
}

Hold(mod, trigger, blind=1) {
    b := blind ? "{Blind}" : ""
    IME_SET(0)
    Sleep,2
    SendInput,%b%{%mod% Down}
    KeyWait,%trigger%
    SendInput,%b%{%mod% Up}
}
"""";
}
