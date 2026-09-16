using System.Text;

namespace LeafHotKey;

/// <summary>
/// 実際に SendInput でキーを送り、注入されたイベント列を確認する。
/// 送ったキーはフックで破棄するため、他のアプリには入力されない。
/// </summary>
public static class SendSelfCheck
{
    public static int Run(string? reportPath)
    {
        var path = reportPath ?? Path.Combine(Path.GetTempPath(), "leafhotkey-sendcheck.txt");
        var encoding = new UTF8Encoding(false);
        var failures = 0;

        File.WriteAllText(path, string.Empty, encoding);

        void Check(string name, bool ok, string detail)
        {
            if (!ok) failures++;
            File.AppendAllText(path, $"{(ok ? "PASS" : "FAIL")} {name}: {detail}{Environment.NewLine}", encoding);
        }

        var sender = new KeySender();
        using var capture = new InjectedKeyCapture(sender.Signature);
        Check("hook.installed", capture.Installed, "低レベルキーボードフックを設置できる");
        if (!capture.Installed)
        {
            File.AppendAllText(path, $"failures={failures}{Environment.NewLine}", encoding);
            return 1;
        }

        var layout = KeyResolver.CurrentLayout;

        IReadOnlyList<CapturedKey> SendAndCapture(string sequence, int expectedEvents)
        {
            capture.Clear();
            var tokens = SendSequenceParser.Parse(sequence);
            var result = sender.Send(tokens, layout);
            if (result.Unresolved.Count > 0)
            {
                File.AppendAllText(path, $"  UNRESOLVED {sequence}: {string.Join(", ", result.Unresolved)}{Environment.NewLine}", encoding);
            }

            capture.WaitFor(expectedEvents, TimeSpan.FromSeconds(2));
            var events = capture.Snapshot();
            File.AppendAllText(path, $"  SENT {sequence} -> {string.Join(", ", events)}{Environment.NewLine}", encoding);
            return events;
        }

        // 低レベルフックは VK_CONTROL などの汎用キーを左右どちらかの具体キーとして報告する。
        static ushort Normalize(ushort virtualKey) => virtualKey switch
        {
            0xA0 or 0xA1 => 0x10,
            0xA2 or 0xA3 => 0x11,
            0xA4 or 0xA5 => 0x12,
            _ => virtualKey,
        };

        bool Is(CapturedKey captured, ushort expected, bool? keyUp = null)
            => Normalize(captured.VirtualKey) == Normalize(expected) && (keyUp is null || captured.KeyUp == keyUp);

        const ushort VkControl = 0x11;
        const ushort VkShift = 0x10;
        const ushort VkAlt = 0x12;
        const ushort VkEscape = 0x1B;
        const ushort VkTab = 0x09;
        const ushort VkF12 = 0x7B;
        const ushort VkZ = 0x5A;
        const ushort VkB = 0x42;
        const ushort VkK = 0x4B;

        var ctrlZ = SendAndCapture("^z", 4);
        Check(
            "send.ctrl-z",
            ctrlZ.Count == 4 &&
            Is(ctrlZ[0], VkControl, false) &&
            Is(ctrlZ[1], VkZ, false) &&
            Is(ctrlZ[2], VkZ, true) &&
            Is(ctrlZ[3], VkControl, true),
            "^z は Ctrl押下→z押下→z解放→Ctrl解放");

        Check("send.injected", ctrlZ.All(e => e.Injected), "自分が注入したイベントとして識別できる");

        var escape = SendAndCapture("{Esc}", 2);
        Check(
            "send.named",
            escape.Count == 2 && escape.All(e => e.VirtualKey == VkEscape) && !escape[0].KeyUp && escape[1].KeyUp,
            "{Esc} は Esc の押下と解放");

        var ctrlShiftTab = SendAndCapture("^+{Tab}", 6);
        Check(
            "send.two-modifiers",
            ctrlShiftTab.Count == 6 &&
            Is(ctrlShiftTab[0], VkControl, false) && Is(ctrlShiftTab[1], VkShift, false) &&
            Is(ctrlShiftTab[2], VkTab, false) &&
            Is(ctrlShiftTab[3], VkTab, true) &&
            Is(ctrlShiftTab[4], VkShift, true) &&
            Is(ctrlShiftTab[5], VkControl, true),
            "^+{Tab} は修飾キーを対称に押して離す");

        var altF12 = SendAndCapture("!{f12}", 4);
        Check(
            "send.function-key",
            altF12.Count == 4 && Is(altF12[0], VkAlt, false) && Is(altF12[1], VkF12, false),
            "!{f12} は Alt+F12");

        var shiftB = SendAndCapture("+B", 4);
        Check(
            "send.uppercase",
            shiftB.Count == 4 && Is(shiftB[0], VkShift, false) && Is(shiftB[1], VkB, false),
            "+B は Shift+B（大文字は配列解決で Shift が付く）");

        var down = SendAndCapture("{ShiftDown}{AltDown}k", 4);
        Check(
            "send.hold-down",
            down.Count == 4 &&
            Is(down[0], VkShift, false) &&
            Is(down[1], VkAlt, false) &&
            Is(down[2], VkK, false) &&
            Is(down[3], VkK, true),
            "{ShiftDown}{AltDown}k は修飾キーを押したままにする");

        var up = SendAndCapture("{ShiftUp}{AltUp}", 2);
        Check(
            "send.hold-up",
            up.Count == 2 && up.All(e => e.KeyUp) && Is(up[0], VkShift, true) && Is(up[1], VkAlt, true),
            "{ShiftUp}{AltUp} は解放のみ");

        // JIS 配列を含む現在の配列で記号が解決できること。
        var colonScan = NativeMethods.VkKeyScanEx(':', layout);
        var colon = SendAndCapture(":", colonScan == -1 ? 0 : 2);
        Check(
            "send.symbol",
            colonScan != -1 && colon.Count >= 2 && colon.Any(e => e.VirtualKey == (ushort)(colonScan & 0xFF)),
            $": は現在の配列の仮想キー 0x{(colonScan & 0xFF):X2} として送られる");

        var slash = SendAndCapture("/", 2);
        var slashScan = NativeMethods.VkKeyScanEx('/', layout);
        Check(
            "send.symbol.slash",
            slashScan != -1 && slash.Any(e => e.VirtualKey == (ushort)(slashScan & 0xFF)),
            $"/ は仮想キー 0x{(slashScan & 0xFF):X2} として送られる");

        // 未解決トークンは送らずに報告する。
        var unresolvedResult = sender.Send(new[] { SendToken.Key("MButton", KeyAction.Press, SendModifiers.None) }, layout);
        Check(
            "send.unresolved",
            !unresolvedResult.Success && unresolvedResult.SentEvents == 0,
            "解決できないキーは送信せず未解決として返す");

        // 台帳のルールが現在の配列で解決できるか（送信はしない）。
        var settings = FindDefaultSettings();
        if (settings is null)
        {
            Check("rules.resolve", false, "defaults/settings.json が見つからない");
        }
        else
        {
            var profiles = HotkeyProfileLoader.Load(settings);
            var unresolvedTokens = new List<string>();
            foreach (var token in profiles
                         .SelectMany(profile => profile.Rules)
                         .Where(rule => rule.Kind == HotkeyActionKind.Send)
                         .SelectMany(rule => rule.Sequences)
                         .SelectMany(sequence => sequence))
            {
                if (!KeyResolver.TryResolve(token, layout, out _)) unresolvedTokens.Add(token.ToString());
            }

            Check(
                "rules.resolve",
                unresolvedTokens.Count == 0,
                unresolvedTokens.Count == 0
                    ? "全 send ルールのトークンが現在の配列で解決できる"
                    : $"解決できないトークン: {string.Join(", ", unresolvedTokens.Distinct())}");
        }

        File.AppendAllText(path, $"failures={failures}{Environment.NewLine}", encoding);
        return failures == 0 ? 0 : 1;
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
