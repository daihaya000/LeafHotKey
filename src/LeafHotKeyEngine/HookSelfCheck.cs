using System.Text;

namespace LeafHotKey;

/// <summary>
/// 実際のフックを通してキー変換が行われることを確認する。
/// 検証に使うキーは捕捉フックで破棄するため、他のアプリへは入力されない。
/// </summary>
public static class HookSelfCheck
{
    /// <summary>ユーザー操作を模したキーに使う署名。エンジンの署名とは別にする。</summary>
    private const ulong SimulatedUserSignature = 0x4C48_4B55_5352;

    public static int Run(string? reportPath)
    {
        var path = reportPath ?? Path.Combine(Path.GetTempPath(), "leafhotkey-hookcheck.txt");
        var encoding = new UTF8Encoding(false);
        var failures = 0;

        File.WriteAllText(path, string.Empty, encoding);

        void Check(string name, bool ok, string detail)
        {
            if (!ok) failures++;
            File.AppendAllText(path, $"{(ok ? "PASS" : "FAIL")} {name}: {detail}{Environment.NewLine}", encoding);
        }

        var foregroundName = new ForegroundApp().CurrentProcessName();
        if (foregroundName.Length == 0)
        {
            Check("foreground", false, "前面ウィンドウを特定できないため検証できない");
            File.AppendAllText(path, $"failures={failures}{Environment.NewLine}", encoding);
            return 1;
        }

        Check("foreground", true, $"前面アプリを {foregroundName} として認識する");

        // 前面アプリに合わせた検証用プロファイルを組み立てる。
        var profile = new HotkeyProfile
        {
            Id = "hookcheck",
            Name = "Hook check",
            Enabled = true,
            ProcessNames = new[] { foregroundName },
            Rules = new[]
            {
                new HotkeyRule
                {
                    Trigger = new HotkeyTrigger { Key = "f13" },
                    Kind = HotkeyActionKind.Send,
                    Sequences = SendNotation.ParseAll(new[] { "Esc" }),
                },
                new HotkeyRule
                {
                    Trigger = new HotkeyTrigger { Key = "f14" },
                    Kind = HotkeyActionKind.Hold,
                    HoldModifier = "Ctrl",
                    ReleaseOn = "f14",
                },
            },
        };

        var engineSender = new KeySender();
        var userSender = new KeySender(SimulatedUserSignature);

        // 捕捉フックを先に設置し、エンジンのフックが先に呼ばれるようにする。
        using var capture = new InjectedKeyCapture(engineSender.Signature, captureAllInjected: true);
        Check("capture.installed", capture.Installed, "検証用フックを設置できる");

        using var engine = new InputEngine(new[] { profile }, disableIme: false, sender: engineSender);
        var started = engine.Start();
        Check("engine.installed", started && engine.Installed, "キーボードとマウスのフックを設置できる");
        if (!started)
        {
            File.AppendAllText(path, $"failures={failures}{Environment.NewLine}", encoding);
            return 1;
        }

        const ushort VkF13 = 0x7C;
        const ushort VkEscape = 0x1B;
        const ushort VkControl = 0x11;

        static ushort Normalize(ushort virtualKey) => virtualKey switch
        {
            0xA0 or 0xA1 => 0x10,
            0xA2 or 0xA3 => 0x11,
            0xA4 or 0xA5 => 0x12,
            _ => virtualKey,
        };

        IReadOnlyList<CapturedKey> Simulate(string sequence, int expected)
        {
            capture.Clear();
            userSender.Send(SendNotation.Parse(sequence));
            capture.WaitFor(expected, TimeSpan.FromSeconds(2));
            Thread.Sleep(120);
            var events = capture.Snapshot();
            File.AppendAllText(path, $"  {sequence} -> {string.Join(", ", events)}{Environment.NewLine}", encoding);
            return events;
        }

        var converted = Simulate("F13", 2);
        Check(
            "hook.suppress",
            converted.All(e => Normalize(e.VirtualKey) != VkF13),
            "変換されたトリガキーは下位へ流れない");
        Check(
            "hook.convert",
            converted.Count(e => e.VirtualKey == VkEscape) == 2,
            "f13 が Esc の押下・解放へ変換される");

        var holdDown = Simulate("F14↓", 1);
        Check(
            "hook.hold.down",
            holdDown.Any(e => Normalize(e.VirtualKey) == VkControl && !e.KeyUp),
            "f14 押下で Ctrl が押される");

        var holdUp = Simulate("F14↑", 1);
        Check(
            "hook.hold.up",
            holdUp.Any(e => Normalize(e.VirtualKey) == VkControl && e.KeyUp),
            "f14 解放で Ctrl が解放される");

        // 停止中は変換しない。
        engine.Enabled = false;
        var disabled = Simulate("F13", 2);
        Check(
            "hook.disabled",
            disabled.Count(e => Normalize(e.VirtualKey) == VkF13) == 2 && disabled.All(e => e.VirtualKey != VkEscape),
            "一時停止中は元のキーをそのまま通す");

        engine.Enabled = true;
        var reenabled = Simulate("F13", 2);
        Check(
            "hook.reenabled",
            reenabled.Count(e => e.VirtualKey == VkEscape) == 2,
            "再開すると再び変換する");

        // 停止時にフックを解除し、保持キーを解放する。
        Simulate("F14↓", 1);
        capture.Clear();
        engine.Stop();
        Thread.Sleep(150);
        var afterStop = capture.Snapshot();
        Check(
            "engine.stop.release",
            afterStop.Any(e => Normalize(e.VirtualKey) == VkControl && e.KeyUp),
            "停止時に保持中の Ctrl を解放する");
        Check("engine.stop.uninstall", !engine.Installed, "停止後はフックが残らない");

        // Stop 後の再開で ready 状態と監視タイマーを正しく再初期化する。
        var restarted = engine.Start();
        engine.Enabled = true;
        Check("engine.restart.installed", restarted && engine.Installed, "停止後も再開できる");
        var afterRestart = Simulate("F13", 2);
        Check(
            "engine.restart.convert",
            afterRestart.Count(e => e.VirtualKey == VkEscape) == 2,
            "再開後も変換が有効になる");
        engine.Stop();

        var afterStopEvents = Simulate("F13", 2);
        Check(
            "engine.stop.passthrough",
            afterStopEvents.Count(e => Normalize(e.VirtualKey) == VkF13) == 2,
            "停止後は変換されない");

        File.AppendAllText(path, $"failures={failures}{Environment.NewLine}", encoding);
        return failures == 0 ? 0 : 1;
    }
}
