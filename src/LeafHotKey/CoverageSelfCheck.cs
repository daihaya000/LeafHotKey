using System.Text;

namespace LeafHotKey;

/// <summary>
/// 台帳の全ルールをエンジンへ実際に入力し、宣言どおりの送信になるか突き合わせる。
/// ルール同士が衝突して発火しないケース（取りこぼし）を検出するための検証。
/// </summary>
public static class CoverageSelfCheck
{
    private sealed class RecordingSink : IKeySink
    {
        public List<string> Sent { get; } = new();

        public SendResult Send(IReadOnlyList<SendToken> tokens)
        {
            Sent.Add(string.Join(" ", tokens.Select(token => token.ToString())));
            return new SendResult { SentEvents = tokens.Count, Unresolved = Array.Empty<string>() };
        }
    }

    public static int Run(string? reportPath, string? settingsPath)
    {
        var path = reportPath ?? Path.Combine(Path.GetTempPath(), "leafhotkey-coverage.txt");
        var encoding = new UTF8Encoding(false);

        File.WriteAllText(path, string.Empty, encoding);

        var settings = settingsPath ?? FindDefaultSettings();
        if (settings is null)
        {
            File.AppendAllText(path, $"FAIL settings: defaults/settings.json が見つからない{Environment.NewLine}failures=1{Environment.NewLine}", encoding);
            return 1;
        }

        IReadOnlyList<HotkeyProfile> profiles;
        try
        {
            profiles = HotkeyProfileLoader.Load(settings);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or FormatException or IOException)
        {
            File.AppendAllText(path, $"FAIL settings: 設定を読み込めない（{ex.Message}）{Environment.NewLine}failures=1{Environment.NewLine}", encoding);
            return 1;
        }

        var sink = new RecordingSink();
        var engine = new InputEngineCore(sink);

        var checkedRules = 0;
        var mismatches = new List<string>();

        foreach (var profile in profiles)
        {
            engine.SetActiveProfile(null);
            engine.SetActiveProfile(profile);

            foreach (var rule in profile.Rules)
            {
                if (rule.Kind == HotkeyActionKind.Passthrough) continue;

                checkedRules++;
                engine.ReleaseAll();
                sink.Sent.Clear();

                var trigger = rule.Trigger;
                var suppressed = false;

                if (trigger.Prefix is { } prefix)
                {
                    engine.OnKeyDown(prefix, SendModifiers.None);
                    suppressed = engine.OnKeyDown(trigger.Key, trigger.Modifiers) == InputDecision.Suppress;
                    engine.OnKeyUp(trigger.Key, trigger.Modifiers);
                    engine.OnKeyUp(prefix, SendModifiers.None);
                }
                else
                {
                    var down = engine.OnKeyDown(trigger.Key, trigger.Modifiers);
                    var up = engine.OnKeyUp(trigger.Key, trigger.Modifiers);
                    suppressed = down == InputDecision.Suppress || up == InputDecision.Suppress;
                }

                var expected = Expected(profile, rule);
                var actual = sink.Sent.ToList();

                // *~key が定義されたキーは、元の入力を通すのが正しい挙動。
                var requiresSuppression = !profile.Rules.Any(other =>
                    other.Kind == HotkeyActionKind.Passthrough &&
                    other.Trigger.PassThroughNative &&
                    string.Equals(other.Trigger.Key, trigger.Key, StringComparison.OrdinalIgnoreCase));

                if (!expected.SequenceEqual(actual, StringComparer.Ordinal) || (requiresSuppression && !suppressed))
                {
                    mismatches.Add(
                        $"{profile.Id}: {Describe(trigger)} 期待[{string.Join(" | ", expected)}] 実際[{string.Join(" | ", actual)}] 抑止={suppressed}");
                }
            }
        }

        foreach (var mismatch in mismatches)
        {
            File.AppendAllText(path, $"MISMATCH {mismatch}{Environment.NewLine}", encoding);
        }

        var ok = mismatches.Count == 0;
        File.AppendAllText(
            path,
            $"{(ok ? "PASS" : "FAIL")} coverage: {checkedRules} 件を検証し、不一致 {mismatches.Count} 件{Environment.NewLine}",
            encoding);
        File.AppendAllText(path, $"checked={checkedRules}{Environment.NewLine}", encoding);
        File.AppendAllText(path, $"failures={(ok ? 0 : 1)}{Environment.NewLine}", encoding);
        return ok ? 0 : 1;
    }

    /// <summary>ルールの宣言から期待される送信内容を組み立てる。</summary>
    private static IReadOnlyList<string> Expected(HotkeyProfile profile, HotkeyRule rule)
    {
        var expected = rule.Kind switch
        {
            HotkeyActionKind.Send => rule.Sequences
                .Select(sequence => string.Join(" ", sequence.Select(token => token.ToString())))
                .ToList(),
            HotkeyActionKind.Hold when rule.HoldModifier is { } modifier
                => new[] { $"{{{modifier}}} down", $"{{{modifier}}} up" }.ToList(),
            _ => new List<string>(),
        };

        if (rule.Trigger.Prefix is not { } prefix) return expected;

        // Hold を持つ前置キーは、組み合わせ入力中も押下状態を維持する。
        var prefixHold = profile.Rules.FirstOrDefault(candidate =>
            candidate.Trigger.Prefix is null &&
            string.Equals(candidate.Trigger.Key, prefix, StringComparison.OrdinalIgnoreCase) &&
            candidate.Kind == HotkeyActionKind.Hold &&
            string.Equals(candidate.ReleaseOn, prefix, StringComparison.OrdinalIgnoreCase));
        if (prefixHold?.HoldModifier is not { } prefixModifier) return expected;

        return new[] { $"{{{prefixModifier}}} down" }
            .Concat(expected)
            .Append($"{{{prefixModifier}}} up")
            .ToList();
    }

    private static string Describe(HotkeyTrigger trigger)
    {
        var prefix = trigger.Prefix is null ? string.Empty : trigger.Prefix + " & ";
        var modifiers = trigger.Modifiers == SendModifiers.None ? string.Empty : trigger.Modifiers + "+";
        return prefix + modifiers + trigger.Key;
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
