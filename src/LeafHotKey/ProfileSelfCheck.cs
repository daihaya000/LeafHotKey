using System.Text;

namespace LeafHotKey;

/// <summary>
/// 移行台帳（defaults/settings.json）の全ルールが解釈できることを確認する。
/// 実際のキー送信はまだ行わない。解析段階の取りこぼしを先に検出するための検証。
/// </summary>
public static class ProfileSelfCheck
{
    public static int Run(string? reportPath, string? settingsPath)
    {
        var path = reportPath ?? Path.Combine(Path.GetTempPath(), "leafhotkey-profilecheck.txt");
        var encoding = new UTF8Encoding(false);
        var failures = 0;

        File.WriteAllText(path, string.Empty, encoding);

        void Check(string name, bool ok, string detail)
        {
            if (!ok) failures++;
            File.AppendAllText(path, $"{(ok ? "PASS" : "FAIL")} {name}: {detail}{Environment.NewLine}", encoding);
        }

        var settings = settingsPath ?? FindDefaultSettings();
        if (settings is null)
        {
            Check("settings.found", false, "defaults/settings.json が見つからない");
            File.AppendAllText(path, $"failures={failures}{Environment.NewLine}", encoding);
            return 1;
        }

        IReadOnlyList<HotkeyProfile> profiles;
        try
        {
            profiles = HotkeyProfileLoader.Load(settings);
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException)
        {
            Check("profiles.load", false, ex.Message);
            File.AppendAllText(path, $"failures={failures}{Environment.NewLine}", encoding);
            return 1;
        }

        Check("profiles.count", profiles.Count == 13, $"プロファイル数 {profiles.Count}（期待 13）");

        var ruleCount = profiles.Sum(profile => profile.Rules.Count);
        Check("rules.count", ruleCount == 251, $"ルール数 {ruleCount}（期待 251）");

        var sendRules = profiles.SelectMany(p => p.Rules).Where(r => r.Kind == HotkeyActionKind.Send).ToList();
        Check("rules.send.nonempty", sendRules.All(r => r.Sequences.Count > 0 && r.Sequences.All(s => s.Count > 0)), "全 send ルールが空でないトークン列になる");

        var holdRules = profiles.SelectMany(p => p.Rules).Where(r => r.Kind == HotkeyActionKind.Hold).ToList();
        Check("rules.hold.fields", holdRules.All(r => r.HoldModifier is not null && r.ReleaseOn is not null), $"hold ルール {holdRules.Count} 件に保持キーと解除キーがある");

        var passthrough = profiles.SelectMany(p => p.Rules).Count(r => r.Kind == HotkeyActionKind.Passthrough);
        Check("rules.passthrough", passthrough == 8, $"passthrough ルール {passthrough} 件（元 AHK の *~ 定義 8 件）");

        Check("profile.match", profiles.Single(p => p.Id == "clipstudio").Matches("CLIPStudioPaint.exe"), "前面 exe 名でプロファイルを選べる");
        Check("profile.match.case", profiles.Single(p => p.Id == "explorer").Matches("explorer.exe"), "exe 名の大文字小文字を区別しない");
        Check("profile.match.other", !profiles.Single(p => p.Id == "chrome").Matches("firefox.exe"), "対象外の exe には一致しない");
        Check("profile.unreal.multi", profiles.Single(p => p.Id == "unreal").ProcessNames.Count == 2, "UE は 2 つの exe を持つ");

        // 個々の解析結果を元 AHK の表記と突き合わせる。
        var photoshop = profiles.Single(p => p.Id == "photoshop");
        var ctrlShiftE = photoshop.Rules[0];
        Check(
            "parse.modifiers",
            ctrlShiftE.Trigger.Modifiers == (SendModifiers.Ctrl | SendModifiers.Shift) && ctrlShiftE.Trigger.Key == "e",
            "^+e を Ctrl+Shift+e として読む");
        Check(
            "parse.literal-f5",
            ctrlShiftE.Sequences[0].Count == 2 &&
            ctrlShiftE.Sequences[0][0].Character == 'f' &&
            ctrlShiftE.Sequences[0][1].Character == '5',
            "Snd(\"f5\") は {F5} ではなく文字 f と 5");
        Check("parse.split", ctrlShiftE.Sequences.Count == 2 && ctrlShiftE.Sequences[1][0].KeyName == "Esc", "第2引数は別の送信単位になる");

        var clipStudio = profiles.Single(p => p.Id == "clipstudio");
        var pgdn = clipStudio.Rules.Single(r => r.Trigger.Key == "PgDn" && r.Trigger.Prefix is null);
        Check(
            "parse.named-key",
            pgdn.Sequences[0][0].KeyName == "Esc" && pgdn.Sequences[1][0].Character == 'z' &&
            pgdn.Sequences[1][0].Modifiers == SendModifiers.Ctrl,
            "{Esc} と ^z を分けて読む");

        var colon = clipStudio.Rules.Single(r => r.Trigger.Prefix == "MButton" && r.Trigger.Key == "f24");
        Check("parse.symbol", colon.Sequences[0][0].Character == ':', "記号 : をそのまま保持する");

        var unreal = profiles.Single(p => p.Id == "unreal");
        var shift4 = unreal.Rules.Single(r => r.Trigger.Prefix == "Shift" && r.Trigger.Key == "4");
        var first = shift4.Sequences[0];
        Check(
            "parse.down-up",
            first.Count == 3 &&
            first[0].KeyName == "Shift" && first[0].Action == KeyAction.Down &&
            first[1].KeyName == "Alt" && first[1].Action == KeyAction.Down &&
            first[2].Character == 'k',
            "{ShiftDown}{AltDown}k を 3 トークンとして読む");
        Check(
            "parse.release",
            shift4.Sequences[1].Count == 2 && shift4.Sequences[1].All(t => t.Action == KeyAction.Up),
            "{ShiftUp}{AltUp} は解放 2 トークン");

        var blender = profiles.Single(p => p.Id == "blender");
        var tab = blender.Rules.Single(r => r.Trigger.Prefix == "MButton" && r.Trigger.Key == "f24");
        Check(
            "parse.modified-named",
            tab.Sequences[0][0].KeyName == "Tab" && tab.Sequences[0][0].Modifiers == (SendModifiers.Ctrl | SendModifiers.Shift),
            "^+{Tab} は Ctrl+Shift+Tab");

        var shiftB = blender.Rules.Single(r => r.Trigger.Prefix == "MButton" && r.Trigger.Key == "f23");
        Check("parse.case", shiftB.Sequences[0][0].Character == 'B', "+B の大文字を保持する");

        var maya = profiles.Single(p => p.Id == "maya");
        var f24 = maya.Rules.Single(r => r.Trigger.Prefix is null && r.Trigger.Key == "f24");
        Check(
            "parse.function-key",
            f24.Sequences[0][0].KeyName == "F12" && f24.Sequences[0][0].Modifiers == SendModifiers.Alt,
            "!{f12} は Alt+F12");

        var explorer = profiles.Single(p => p.Id == "explorer");
        var hold = explorer.Rules.Single(r => r.Trigger.Key == "f14");
        Check(
            "parse.hold",
            hold.Kind == HotkeyActionKind.Hold && hold.HoldModifier == "Ctrl" && hold.ReleaseOn == "f14",
            "Hold(\"Ctrl\", \"f14\") を保持動作として読む");

        var passthroughRule = clipStudio.Rules.Single(r => r.Kind == HotkeyActionKind.Passthrough && r.Trigger.Key == "MButton");
        Check(
            "parse.passthrough",
            passthroughRule.Trigger.AnyModifier && passthroughRule.Trigger.PassThroughNative,
            "*~MButton は修飾無視かつ元入力を通す");

        // 現行表記の解析器そのものの異常系。
        Check("parser.unknown-modifier", Throws(() => SendNotation.Parse("Hyper+g")), "未知の修飾キーを拒否する");
        Check("parser.dangling-modifier", Throws(() => SendNotation.Parse("Ctrl+")), "対象キーのない修飾キーを拒否する");
        Check("parser.empty", Throws(() => SendNotation.Parse("  ")), "空の内容を拒否する");
        Check("parser.char-hold", Throws(() => SendNotation.Parse("g↓")), "文字は押しっぱなしにできない");
        Check("parser.space-separated", SendNotation.Parse("f 5").Count == 2, "空白区切りは別の文字として扱う");
        Check("parser.spaced-modifier", Throws(() => SendNotation.Parse("Ctrl + g")), "空白入りの修飾は誤入力として弾く");
        Check("parser.plus", SendNotation.Parse("Ctrl++")[0].Character == '+' && SendNotation.Parse("+")[0].Character == '+', "「+」キーを修飾と区別できる");

        // 旧 AHK 表記の解析（設定ファイルの移行専用）。
        Check("legacy.braced", LegacyAhkNotation.Parse("{Esc}")[0].KeyName == "Esc", "旧表記の {Esc} を読み替えられる");
        Check("legacy.unclosed", Throws(() => LegacyAhkNotation.Parse("{Esc")), "旧表記の閉じ括弧なしを拒否する");
        Check("legacy.dangling-modifier", Throws(() => LegacyAhkNotation.Parse("^")), "旧表記の対象キーのない修飾キーを拒否する");

        File.AppendAllText(path, $"failures={failures}{Environment.NewLine}", encoding);
        return failures == 0 ? 0 : 1;
    }

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (FormatException)
        {
            return true;
        }
    }

    private static string? FindDefaultSettings()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "defaults", "settings.json");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        return null;
    }
}
