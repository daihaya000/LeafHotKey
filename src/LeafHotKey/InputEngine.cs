using System.Runtime.InteropServices;

namespace LeafHotKey;

/// <summary>
/// 低レベルフックと判定中核をつなぐ入力エンジン。
/// フック内では判定と送信だけを行い、待機・ディスクアクセス・設定読み込みはしない。
/// </summary>
public sealed class InputEngine : IDisposable
{
    private volatile IReadOnlyList<HotkeyProfile> _profiles;
    private readonly KeySender _sender;
    private readonly InputEngineCore _core;
    private readonly ForegroundApp _foreground = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private volatile bool _disableIme;

    private NativeMethods.HookProc? _keyboardCallback;
    private NativeMethods.HookProc? _mouseCallback;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private Thread? _thread;
    private uint _threadId;
    private System.Threading.Timer? _holdWatchdog;
    private readonly object _holdSweepGate = new();
    private bool _stopRequested;

    /// <summary>解除キーが物理的に押されているのを確認できたもの（誤解放を避ける）。</summary>
    private readonly HashSet<string> _holdKeysSeenDown = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>保持した修飾キーが実際に押されたのを確認できたもの。</summary>
    private readonly HashSet<string> _holdModifiersSeenDown = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _enabled = true;
    private int _holdSweeps;
    private int _holdReasserts;
    private int _holdReleases;

    public InputEngine(IReadOnlyList<HotkeyProfile> profiles, bool disableIme = true, KeySender? sender = null)
    {
        _profiles = profiles;
        _sender = sender ?? new KeySender();
        _disableIme = disableIme;
        _core = new InputEngineCore(new ImeAwareSink(_sender, _foreground, () => _disableIme))
        {
            // 前置キーの取りこぼしを物理状態で補う。
            PhysicalKeyState = name => KeyResolver.TryVirtualKeyFor(name, out var virtualKey)
                ? (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0
                : null,
        };
    }

    /// <summary>送信前に前面ウィンドウの IME をオフにする送信先（元 AHK の Snd 相当）。</summary>
    private sealed class ImeAwareSink : IKeySink
    {
        private readonly KeySender _sender;
        private readonly ForegroundApp _foreground;
        private readonly Func<bool> _disableIme;

        public ImeAwareSink(KeySender sender, ForegroundApp foreground, Func<bool> disableIme)
        {
            _sender = sender;
            _foreground = foreground;
            _disableIme = disableIme;
        }

        public SendResult Send(IReadOnlyList<SendToken> tokens)
        {
            // IME 操作に失敗しても送信自体は続ける（AHK も戻り値を見ていない）。
            if (_disableIme()) ImeController.Disable(_foreground.CurrentWindow());
            return _sender.Send(tokens);
        }

        public bool VerifyKeyDown(string keyName)
        {
            if (!KeyResolver.TryVirtualKeyFor(keyName, out var virtualKey)) return true;
            return (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
        }

        public bool VerifyKeyUp(string keyName)
        {
            if (!KeyResolver.TryVirtualKeyFor(keyName, out var virtualKey)) return true;
            return (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) == 0;
        }
    }

    /// <summary>入力変換の有効・無効。無効化時は保持中のキーを解放する。</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            lock (_holdSweepGate)
            {
                if (_enabled == value) return;
                _enabled = value;
                if (!value)
                {
                    _core.ReleaseAll();
                    _holdKeysSeenDown.Clear();
                    _holdModifiersSeenDown.Clear();
                }
            }
        }
    }

    public bool Installed => _keyboardHook != IntPtr.Zero && _mouseHook != IntPtr.Zero;

    /// <summary>
    /// 設定保存後に新しいプロファイルへ差し替える。
    /// 入れ替え前に保持中のキーを解放し、古い割り当てのまま押しっぱなしにならないようにする。
    /// </summary>
    public void ApplyProfiles(IReadOnlyList<HotkeyProfile> profiles, bool disableIme)
    {
        lock (_holdSweepGate)
        {
            _core.SetActiveProfile(null);
            _profiles = profiles;
            _disableIme = disableIme;
            _holdKeysSeenDown.Clear();
            _holdModifiersSeenDown.Clear();
        }
    }

    public string ActiveProfileName => _core.ActiveProfile?.Name ?? string.Empty;

    /// <summary>判定の経過を残すための記録先（切り分け用）。</summary>
    public Action<string>? Trace
    {
        get => _core.Trace;
        set => _core.Trace = value;
    }

    /// <summary>フックを設置する。設置できない場合は false を返し、動作中と偽らない。</summary>
    public bool Start()
    {
        if (_thread is not null) throw new InvalidOperationException("入力エンジンは既に開始済みです。");

        // Stop 後に再開できるよう、前回の Pump 完了状態を持ち越さない。
        _ready.Reset();
        lock (_holdSweepGate) _stopRequested = false;

        _thread = new Thread(Pump)
        {
            IsBackground = true,
            Name = "LeafHotKey input",
        };

        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        var started = _ready.Wait(TimeSpan.FromSeconds(5)) && Installed;
        if (!started) return false;

        lock (_holdSweepGate)
        {
            // 待機中に Stop された場合は監視タイマーを残さない。
            if (_stopRequested || !Installed) return false;

            // 保持キーの取りこぼし（解除イベントの見逃し）を定期的に回収する。
            _holdWatchdog?.Dispose();
            _holdWatchdog = new System.Threading.Timer(_ => SweepHolds(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        return true;
    }

    /// <summary>
    /// 保持中のキーが既に離れていないかを確かめ、離れていれば解放する。
    /// 解除イベントを取りこぼすと修飾キーが押しっぱなしになるため、AHK の KeyWait より確実にする。
    /// </summary>
    private void SweepHolds()
    {
        lock (_holdSweepGate)
        {
            Interlocked.Increment(ref _holdSweeps);
            if (!_enabled) return;

            // 再保持と解放の判定を Core の同一ロック内で行う。
            // ActiveHolds のスナップショット後にフック側が解放すると、
            // 古いスナップショットを使った再保持で修飾キーが再び固まるため。
            var result = _core.SweepHolds(
                modifier =>
                {
                    if (KeyResolver.TryVirtualKeyFor(modifier, out var modifierKey) &&
                        (NativeMethods.GetAsyncKeyState(modifierKey) & 0x8000) != 0)
                    {
                        _holdModifiersSeenDown.Add(modifier);
                        return true;
                    }

                    if (!_holdModifiersSeenDown.Contains(modifier)) return true;

                    Interlocked.Increment(ref _holdReasserts);
                    return false;
                },
                releaseKey =>
                {
                    if (!KeyResolver.TryVirtualKeyFor(releaseKey, out var virtualKey)) return true;
                    if ((NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0)
                    {
                        _holdKeysSeenDown.Add(releaseKey);
                        return true;
                    }

                    // 押下を確認できた後の「離れている」だけを、見逃した解除として扱う。
                    if (!_holdKeysSeenDown.Remove(releaseKey)) return true;

                    Interlocked.Increment(ref _holdReleases);
                    return false;
                });

            if (!result.HasHolds)
            {
                _holdKeysSeenDown.Clear();
                _holdModifiersSeenDown.Clear();
            }

            foreach (var releaseKey in result.ReleasedKeys)
            {
                Trace?.Invoke($"sweep release {releaseKey}");
            }
        }
    }

    /// <summary>保持の安全網が動いた回数（検証用）。</summary>
    public int HoldSweeps => Volatile.Read(ref _holdSweeps);

    public int HoldReasserts => Volatile.Read(ref _holdReasserts);

    public int HoldReleases => Volatile.Read(ref _holdReleases);

    /// <summary>現在保持している修飾キー（表示用）。</summary>
    public IReadOnlyCollection<string> HeldModifiers => _core.ActiveHoldModifiers;

    /// <summary>
    /// フックを解除し、保持中のキーを解放する。
    /// ゲーム保護で本体を終了する前に必ず呼ぶ。
    /// </summary>
    public void Stop()
    {
        lock (_holdSweepGate)
        {
            _stopRequested = true;
            _enabled = false;
            _holdWatchdog?.Dispose();
            _holdWatchdog = null;
            _core.ReleaseAll();
            _holdKeysSeenDown.Clear();
            _holdModifiersSeenDown.Clear();
        }

        if (_threadId != 0)
        {
            NativeMethods.PostThreadMessage(_threadId, NativeMethods.WmQuit, IntPtr.Zero, IntPtr.Zero);
        }

        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        _threadId = 0;
    }

    private void Pump()
    {
        _threadId = NativeMethods.GetCurrentThreadId();
        _keyboardCallback = KeyboardHook;
        _mouseCallback = MouseHook;
        _keyboardHook = NativeMethods.SetWindowsHookEx(NativeMethods.WhKeyboardLowLevel, _keyboardCallback, IntPtr.Zero, 0);
        _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WhMouseLowLevel, _mouseCallback, IntPtr.Zero, 0);
        _ready.Set();

        if (_keyboardHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
        {
            Uninstall();
            return;
        }

        while (NativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            if (message.Message == NativeMethods.WmQuit) break;
        }

        Uninstall();
    }

    private void Uninstall()
    {
        if (_keyboardHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }

        if (_mouseHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
    }

    private IntPtr KeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode != NativeMethods.HcAction) return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

        var data = Marshal.PtrToStructure<NativeMethods.KeyboardLowLevelHookStruct>(lParam);

        // 自分が送ったキーは再び処理しない（再帰防止）。
        if (data.ExtraInfo == _sender.Signature) return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

        var message = (int)wParam;
        var keyUp = message is NativeMethods.WmKeyUp or NativeMethods.WmSysKeyUp;
        var name = VirtualKeyNames.NameFor((ushort)data.VirtualKey);

        if (name is null || !_enabled) return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

        return Dispatch(name, keyUp, nCode, wParam, lParam);
    }

    private IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode != NativeMethods.HcAction || !_enabled)
        {
            return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        var data = Marshal.PtrToStructure<NativeMethods.MouseLowLevelHookStruct>(lParam);
        if (data.ExtraInfo == _sender.Signature) return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

        var message = (int)wParam;
        string? name;
        bool keyUp;

        switch (message)
        {
            case NativeMethods.WmMButtonDown:
                name = "MButton";
                keyUp = false;
                break;
            case NativeMethods.WmMButtonUp:
                name = "MButton";
                keyUp = true;
                break;
            case NativeMethods.WmLButtonDown:
                name = "LButton";
                keyUp = false;
                break;
            case NativeMethods.WmLButtonUp:
                name = "LButton";
                keyUp = true;
                break;
            case NativeMethods.WmRButtonDown:
                name = "RButton";
                keyUp = false;
                break;
            case NativeMethods.WmRButtonUp:
                name = "RButton";
                keyUp = true;
                break;
            case NativeMethods.WmMouseWheel:
                // 上位ワードが符号付きの回転量。
                name = unchecked((short)(data.MouseData >> 16)) > 0 ? "WheelUp" : "WheelDown";
                keyUp = false;
                break;
            default:
                return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        return Dispatch(name, keyUp, nCode, wParam, lParam);
    }

    private IntPtr Dispatch(string name, bool keyUp, int nCode, IntPtr wParam, IntPtr lParam)
    {
        // 無効化・設定差し替え・保持監視と入力判定を同じ境界で直列化する。
        // 無効化直前に通過したフックが、ReleaseAll 後に古いルールを発火させないため。
        lock (_holdSweepGate)
        {
            if (!_enabled) return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

            UpdateActiveProfile();

            var modifiers = CurrentModifiers();
            var decision = keyUp ? _core.OnKeyUp(name, modifiers) : _core.OnKeyDown(name, modifiers);

            return decision == InputDecision.Suppress
                ? new IntPtr(1)
                : NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }
    }

    private void UpdateActiveProfile()
    {
        var processName = _foreground.CurrentProcessName();
        var profile = processName.Length == 0
            ? null
            : _profiles.FirstOrDefault(candidate => candidate.Enabled && candidate.Matches(processName));

        _core.SetActiveProfile(profile);
    }

    /// <summary>現在押されている物理修飾キー。</summary>
    private static SendModifiers CurrentModifiers()
    {
        var modifiers = SendModifiers.None;
        if (IsDown(0x11)) modifiers |= SendModifiers.Ctrl;
        if (IsDown(0x10)) modifiers |= SendModifiers.Shift;
        if (IsDown(0x12)) modifiers |= SendModifiers.Alt;
        if (IsDown(0x5B) || IsDown(0x5C)) modifiers |= SendModifiers.Win;
        return modifiers;

        static bool IsDown(int virtualKey) => (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }

    /// <summary>IME を無効化する設定か。</summary>
    public bool ImeDisableEnabled => _disableIme;

    public void Dispose()
    {
        Stop();
        _ready.Dispose();
    }
}
