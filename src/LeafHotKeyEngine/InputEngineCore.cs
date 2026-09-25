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

    /// <summary>
    /// 修飾キーが実際に押下状態になったかを確かめる。
    /// 検証用の差し替え先や判定できない環境では true を返し、再送の判断を誤らない。
    /// </summary>
    bool VerifyKeyDown(string keyName) => true;

    /// <summary>
    /// 修飾キーが実際に解放されたかを確かめる。
    /// 固まったままになるのを防ぐため、解放も確認して必要なら送り直す。
    /// </summary>
    bool VerifyKeyUp(string keyName) => true;
}

/// <summary>保持の定期点検結果。</summary>
public readonly record struct HoldSweepResult(bool HasHolds, IReadOnlyList<string> ReleasedKeys);

/// <summary>
/// フックから渡されたキー操作を、どのルールで処理するか決める中核。
/// フック内で待機やディスクアクセスを行わないよう、ここでは判定と送信要求だけを扱う。
/// </summary>
public sealed class InputEngineCore
{
    private readonly IKeySink _sink;
    private readonly object _gate = new();
    private HotkeyProfile? _activeProfile;

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

    public HotkeyProfile? ActiveProfile
    {
        get
        {
            lock (_gate) return _activeProfile;
        }
    }

    /// <summary>判定の経過を残すための記録先（切り分け用）。</summary>
    public Action<string>? Trace { get; set; }

    /// <summary>
    /// キーが物理的に押されているかを調べる（true/false、判定不能は null）。
    /// フックの取りこぼしを物理状態で補うために使う。
    /// </summary>
    public Func<string, bool?>? PhysicalKeyState { get; set; }

    public IReadOnlyCollection<string> HeldPrefixes
    {
        get
        {
            lock (_gate) return _heldPrefixes.Keys.ToArray();
        }
    }

    public IReadOnlyCollection<string> ActiveHoldModifiers
    {
        get
        {
            lock (_gate) return _activeHolds.Values.SelectMany(modifiers => modifiers).ToArray();
        }
    }

    /// <summary>保持中の修飾キー（解除キー名→修飾キー）。取りこぼし検出に使う。</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ActiveHolds
    {
        get
        {
            lock (_gate)
            {
                return _activeHolds.ToDictionary(
                    entry => entry.Key,
                    entry => (IReadOnlyList<string>)entry.Value.ToArray(),
                    StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>指定した解除キーの保持を解放する。取りこぼした解除を補うときに使う。</summary>
    public void ReleaseHolds(string releaseKey)
    {
        lock (_gate) ReleaseHoldsCore(releaseKey);
    }

    private void ReleaseHoldsCore(string releaseKey)
    {
        if (!_activeHolds.Remove(releaseKey, out var modifiers)) return;

        foreach (var modifier in modifiers.ToArray())
        {
            SendUp(modifier);
        }
    }

    /// <summary>
    /// 保持中の修飾キーが外れていれば押し直す。
    /// 対象アプリや OS が途中で解放してしまうケースの安全網。
    /// </summary>
    public void ReassertHolds(Func<string, bool> isModifierDown)
    {
        ArgumentNullException.ThrowIfNull(isModifierDown);
        lock (_gate) ReassertHoldsCore(isModifierDown);
    }

    private void ReassertHoldsCore(Func<string, bool> isModifierDown)
    {
        foreach (var modifier in _activeHolds.Values
                     .SelectMany(modifiers => modifiers)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .ToArray())
        {
            if (isModifierDown(modifier)) continue;
            _sink.Send(new[] { SendToken.Key(modifier, KeyAction.Down, SendModifiers.None) });
        }
    }

    /// <summary>
    /// 保持の再保持と、解除キーの取りこぼし回収を同じロック内で行う。
    /// 保持のスナップショット取得と解放を別々に呼ぶと、解放と再保持が逆順になり得る。
    /// </summary>
    public HoldSweepResult SweepHolds(
        Func<string, bool> isModifierDown,
        Func<string, bool> isReleaseKeyDown)
    {
        ArgumentNullException.ThrowIfNull(isModifierDown);
        ArgumentNullException.ThrowIfNull(isReleaseKeyDown);

        lock (_gate)
        {
            ReassertHoldsCore(isModifierDown);

            var releasedKeys = new List<string>();
            foreach (var releaseKey in _activeHolds.Keys.ToArray())
            {
                if (isReleaseKeyDown(releaseKey)) continue;

                ReleaseHoldsCore(releaseKey);
                releasedKeys.Add(releaseKey);
            }

            return new HoldSweepResult(_activeHolds.Count > 0, releasedKeys);
        }
    }

    /// <summary>前面アプリが変わったときに呼ぶ。保持中のキーは必ず解放する。</summary>
    public void SetActiveProfile(HotkeyProfile? profile)
    {
        lock (_gate)
        {
            if (ReferenceEquals(profile, _activeProfile)) return;

            ReleaseAllCore();
            _activeProfile = profile;
        }
    }

    /// <summary>保持中の修飾キーをすべて解放し、前置キーの状態も捨てる。</summary>
    public void ReleaseAll()
    {
        lock (_gate) ReleaseAllCore();
    }

    private void ReleaseAllCore()
    {
        var modifiers = _activeHolds.Values
            .SelectMany(list => list)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // 送信中に再入した入力を、今回の解放処理で消さないよう先に状態を捨てる。
        _activeHolds.Clear();
        _heldPrefixes.Clear();
        _suppressedDownKeys.Clear();

        foreach (var modifier in modifiers)
        {
            SendUp(modifier);
        }
    }

    /// <summary>解放を送り、実際に離れたかを確かめる（離れていなければ一度だけ送り直す）。</summary>
    private void SendUp(string modifier)
    {
        foreach (var attempt in Enumerable.Range(0, 2))
        {
            _sink.Send(new[] { SendToken.Key(modifier, KeyAction.Up, SendModifiers.None) });
            if (_sink.VerifyKeyUp(modifier)) break;
        }
    }

    public InputDecision OnKeyDown(string key, SendModifiers modifiers)
    {
        lock (_gate)
        {
            var decision = DecideKeyDown(key, modifiers);
            if (decision == InputDecision.Suppress) _suppressedDownKeys.Add(key);
            return decision;
        }
    }

    private InputDecision DecideKeyDown(string key, SendModifiers modifiers)
    {
        var profile = _activeProfile;
        Trace?.Invoke($"down {key} mods={modifiers} profile={profile?.Id ?? "-"} prefixes=[{string.Join(",", _heldPrefixes.Keys)}] holds=[{string.Join(",", _activeHolds.Values.SelectMany(list => list))}]");
        if (profile is null || !profile.Enabled) return InputDecision.PassThrough;

        // 保持中の解除キーのリピートは、自分で押した修飾キーが付いて届くため規則に一致しない。
        // 通すとアプリに Alt+f16 等の押下だけが届き、解放が抑止されて押しっぱなし扱いになる。
        if (_activeHolds.TryGetValue(key, out var heldModifiers))
        {
            foreach (var modifier in heldModifiers)
            {
                if (!_sink.VerifyKeyDown(modifier))
                {
                    _sink.Send(new[] { SendToken.Key(modifier, KeyAction.Down, SendModifiers.None) });
                }
            }

            return HasPassthrough(profile, key) ? InputDecision.PassThrough : InputDecision.Suppress;
        }

        // 前置キーの押下/解放を取りこぼしても、物理状態が正なら前置として扱う（AHK と同じ発想）。
        SyncPrefixes(profile);

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
                // AHK の ~ 付き前置キー（*~key）は、単体動作を押下時に発火する。
                // 離上まで遅延するのは、~ が無い前置キーだけ。
                var tilde = HasPassthrough(profile, key);
                _heldPrefixes[key] = tilde;
                Trace?.Invoke($"prefix {key} held (tilde={tilde})");
                if (tilde)
                {
                    Execute(exact, key, firedOnKeyUp: false);
                    return InputDecision.PassThrough;
                }

                // ~ が無くても Hold は押下中から保持する（離上では一瞬のタップになる）。
                if (exact.Kind == HotkeyActionKind.Hold)
                {
                    return Execute(exact, key, firedOnKeyUp: false);
                }

                return InputDecision.Suppress;
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
        lock (_gate) return OnKeyUpCore(key, modifiers);
    }

    private InputDecision OnKeyUpCore(string key, SendModifiers modifiers)
    {
        var profile = _activeProfile;
        Trace?.Invoke($"up   {key} mods={modifiers} profile={profile?.Id ?? "-"} holds=[{string.Join(",", _activeHolds.Values.SelectMany(list => list))}]");
        if (profile is null || !profile.Enabled) return InputDecision.PassThrough;

        // 押下を抑止したキーは、解放も抑止して対を揃える。
        var decision = _suppressedDownKeys.Remove(key) ? InputDecision.Suppress : InputDecision.PassThrough;

        // 保持中の修飾キーは、解除キーを離した時点で必ず解放する。
        if (_activeHolds.Remove(key, out var holdModifiers))
        {
            foreach (var modifier in holdModifiers.ToArray())
            {
                SendUp(modifier);
            }

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
        Trace?.Invoke($"rule {(rule.Trigger.Prefix is { } p ? p + "&" : string.Empty)}{rule.Trigger.Key} {rule.Kind} {(rule.Kind == HotkeyActionKind.Hold ? rule.HoldModifier : string.Join(" | ", rule.SequenceTexts))} (keyUp={firedOnKeyUp})");

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
                    foreach (var attempt in Enumerable.Range(0, 2))
                    {
                        _sink.Send(new[] { SendToken.Key(modifier, KeyAction.Down, SendModifiers.None) });

                        // 送信が無視された場合（対象アプリに拒否された等）は一度だけやり直す。
                        if (_sink.VerifyKeyDown(modifier)) break;
                    }

                    list.Add(modifier);
                }
                else if (!_sink.VerifyKeyDown(modifier))
                {
                    // 保持が途中で外れていたら押し直す（キーリピート時の安全網）。
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

    /// <summary>プロファイルが前置キーとして使っているキー名。</summary>
    private static IEnumerable<string> PrefixKeys(HotkeyProfile profile) => profile.Rules
        .Where(rule => rule.Trigger.Prefix is { Length: > 0 })
        .Select(rule => rule.Trigger.Prefix!)
        .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 前置キーの状態を物理キーに合わせる。
    /// 押下イベントを見逃しても物理的に押されていれば組み合わせを成立させ、
    /// 解放を見逃しても離れていれば前置状態を捨てる。
    /// </summary>
    private void SyncPrefixes(HotkeyProfile profile)
    {
        if (PhysicalKeyState is null) return;

        foreach (var prefix in PrefixKeys(profile))
        {
            var down = PhysicalKeyState(prefix);
            if (down is null) continue;

            if (down.Value && !_heldPrefixes.ContainsKey(prefix))
            {
                _heldPrefixes[prefix] = false;
                Trace?.Invoke($"prefix {prefix} held (physical)");
                continue;
            }

            if (!down.Value && _heldPrefixes.Remove(prefix)) Trace?.Invoke($"prefix {prefix} released (physical)");
        }
    }
}
