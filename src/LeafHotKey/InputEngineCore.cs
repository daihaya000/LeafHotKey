namespace LeafHotKey;

/// <summary>入力イベントを抑止するか、そのまま通すか。</summary>
public enum InputDecision
{
    /// <summary>元の入力をアプリへ渡す。</summary>
    PassThrough,

    /// <summary>元の入力を破棄する（変換したキーだけを送る）。</summary>
    Suppress,
}

/// <summary>キー送信先。検証では差し替える。</summary>
public interface IKeySink
{
    SendResult Send(IReadOnlyList<SendToken> tokens);
}

/// <summary>
/// フックから渡されたキー操作を、どのルールで処理するか決める中核。
/// フック内で待機やディスクアクセスを行わないよう、ここでは判定と送信要求だけを扱う。
/// </summary>
public sealed class InputEngineCore
{
    private readonly IKeySink _sink;

    /// <summary>現在物理的に押されている前置キーと、その前置キーで組み合わせが発火したか。</summary>
    private readonly Dictionary<string, bool> _heldPrefixes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>保持中の修飾キー。解除キー名で引く。</summary>
    private readonly Dictionary<string, List<string>> _activeHolds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>押下を抑止したキー。解放だけアプリへ漏れないように覚えておく。</summary>
    private readonly HashSet<string> _suppressedDownKeys = new(StringComparer.OrdinalIgnoreCase);

    public InputEngineCore(IKeySink sink)
    {
        _sink = sink;
    }

    public HotkeyProfile? ActiveProfile { get; private set; }

    public IReadOnlyCollection<string> HeldPrefixes => _heldPrefixes.Keys;

    public IReadOnlyCollection<string> ActiveHoldModifiers =>
        _activeHolds.Values.SelectMany(modifiers => modifiers).ToArray();

    /// <summary>前面アプリが変わったときに呼ぶ。保持中のキーは必ず解放する。</summary>
    public void SetActiveProfile(HotkeyProfile? profile)
    {
        if (ReferenceEquals(profile, ActiveProfile)) return;

        ReleaseAll();
        ActiveProfile = profile;
    }

    /// <summary>保持中の修飾キーをすべて解放し、前置キーの状態も捨てる。</summary>
    public void ReleaseAll()
    {
        foreach (var modifier in _activeHolds.Values.SelectMany(list => list).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _sink.Send(new[] { SendToken.Key(modifier, KeyAction.Up, SendModifiers.None) });
        }

        _activeHolds.Clear();
        _heldPrefixes.Clear();
        _suppressedDownKeys.Clear();
    }

    public InputDecision OnKeyDown(string key, SendModifiers modifiers)
    {
        var decision = DecideKeyDown(key, modifiers);
        if (decision == InputDecision.Suppress) _suppressedDownKeys.Add(key);
        return decision;
    }

    private InputDecision DecideKeyDown(string key, SendModifiers modifiers)
    {
        var profile = ActiveProfile;
        if (profile is null || !profile.Enabled) return InputDecision.PassThrough;

        // 1. 押されている前置キーとの組み合わせを最優先する。
        foreach (var prefix in _heldPrefixes.Keys.ToArray())
        {
            var combo = FindRule(profile, rule =>
                string.Equals(rule.Trigger.Prefix, prefix, StringComparison.OrdinalIgnoreCase) &&
                KeyMatches(rule.Trigger.Key, key));

            if (combo is null) continue;

            _heldPrefixes[prefix] = true;
            return Execute(combo, key, firedOnKeyUp: false);
        }

        // 2. 修飾キーまで一致する通常のルール。
        var exact = FindRule(profile, rule =>
            rule.Trigger.Prefix is null &&
            !rule.Trigger.AnyModifier &&
            rule.Trigger.Modifiers == modifiers &&
            KeyMatches(rule.Trigger.Key, key));

        if (exact is not null)
        {
            // 前置キーとしても使うキーは、単体動作を離すときに判定する。
            if (IsPrefixKey(profile, key))
            {
                _heldPrefixes[key] = false;

                // 前置キー自身が Hold の割り当てを持つ場合は、押下中から保持する。
                // 離上時まで遅延すると、F16 -> Space が一瞬のタップになってしまう。
                if (exact.Kind == HotkeyActionKind.Hold)
                {
                    var holdDecision = Execute(exact, key, firedOnKeyUp: false);
                    return HasPassthrough(profile, key) ? InputDecision.PassThrough : holdDecision;
                }

                return HasPassthrough(profile, key) ? InputDecision.PassThrough : InputDecision.Suppress;
            }

            return Execute(exact, key, firedOnKeyUp: false);
        }

        // 3. 前置キーとしてのみ使うキー。
        if (IsPrefixKey(profile, key))
        {
            _heldPrefixes[key] = false;
            return HasPassthrough(profile, key) ? InputDecision.PassThrough : InputDecision.Suppress;
        }

        return InputDecision.PassThrough;
    }

    public InputDecision OnKeyUp(string key, SendModifiers modifiers)
    {
        var profile = ActiveProfile;
        if (profile is null || !profile.Enabled) return InputDecision.PassThrough;

        // 押下を抑止したキーは、解放も抑止して対を揃える。
        var decision = _suppressedDownKeys.Remove(key) ? InputDecision.Suppress : InputDecision.PassThrough;

        // 保持中の修飾キーは、解除キーを離した時点で必ず解放する。
        if (_activeHolds.TryGetValue(key, out var holdModifiers))
        {
            foreach (var modifier in holdModifiers)
            {
                _sink.Send(new[] { SendToken.Key(modifier, KeyAction.Up, SendModifiers.None) });
            }

            _activeHolds.Remove(key);
            decision = HasPassthrough(profile, key) ? decision : InputDecision.Suppress;

            // Hold の前置キーは押下時に発火済み。単体ルールをもう一度実行しない。
            if (_heldPrefixes.Remove(key)) return decision;
        }

        if (!_heldPrefixes.TryGetValue(key, out var consumed)) return decision;

        _heldPrefixes.Remove(key);

        // 組み合わせに使われなかった前置キーは、単体の割り当てをここで発火させる。
        if (consumed) return HasPassthrough(profile, key) ? decision : InputDecision.Suppress;

        var standalone = FindRule(profile, rule =>
            rule.Trigger.Prefix is null &&
            !rule.Trigger.AnyModifier &&
            rule.Trigger.Modifiers == modifiers &&
            KeyMatches(rule.Trigger.Key, key));

        if (standalone is null) return decision;

        var standaloneDecision = Execute(standalone, key, firedOnKeyUp: true);
        return HasPassthrough(profile, key) ? decision : standaloneDecision;
    }

    /// <param name="firedOnKeyUp">
    /// 前置キーの単体動作のように、トリガキーが既に離された後で実行する場合は true。
    /// このとき保持は成立しないため、押して即座に離す（保持キーが残らないようにする）。
    /// </param>
    private InputDecision Execute(HotkeyRule rule, string key, bool firedOnKeyUp)
    {
        switch (rule.Kind)
        {
            case HotkeyActionKind.Send:
                foreach (var sequence in rule.Sequences) _sink.Send(sequence);
                break;

            case HotkeyActionKind.Hold when rule.HoldModifier is { } modifier:
            {
                var releaseOn = rule.ReleaseOn ?? key;

                if (firedOnKeyUp && KeyMatches(releaseOn, key))
                {
                    // 解除条件のキーが既に離れているため、押しっぱなしにせず一度だけ押す。
                    _sink.Send(new[] { SendToken.Key(modifier, KeyAction.Down, SendModifiers.None) });
                    _sink.Send(new[] { SendToken.Key(modifier, KeyAction.Up, SendModifiers.None) });
                    break;
                }

                if (!_activeHolds.TryGetValue(releaseOn, out var list))
                {
                    list = new List<string>();
                    _activeHolds[releaseOn] = list;
                }

                // 同じ修飾キーを二重に押さない（解放漏れを防ぐ）。
                if (!list.Contains(modifier, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(modifier);
                    _sink.Send(new[] { SendToken.Key(modifier, KeyAction.Down, SendModifiers.None) });
                }

                break;
            }

            case HotkeyActionKind.Passthrough:
                return InputDecision.PassThrough;
        }

        return rule.Trigger.PassThroughNative ? InputDecision.PassThrough : InputDecision.Suppress;
    }

    private static HotkeyRule? FindRule(HotkeyProfile profile, Func<HotkeyRule, bool> predicate)
        => profile.Rules.FirstOrDefault(rule => rule.Kind != HotkeyActionKind.Passthrough && predicate(rule));

    private static bool IsPrefixKey(HotkeyProfile profile, string key)
        => profile.Rules.Any(rule => string.Equals(rule.Trigger.Prefix, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>*~key が定義されているか（元の入力を抑止しない指定）。</summary>
    private static bool HasPassthrough(HotkeyProfile profile, string key)
        => profile.Rules.Any(rule =>
            rule.Kind == HotkeyActionKind.Passthrough &&
            rule.Trigger.PassThroughNative &&
            KeyMatches(rule.Trigger.Key, key));

    private static bool KeyMatches(string ruleKey, string key)
        => string.Equals(ruleKey, key, StringComparison.OrdinalIgnoreCase);
}
