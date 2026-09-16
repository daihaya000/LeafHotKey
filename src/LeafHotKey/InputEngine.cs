using System.Runtime.InteropServices;

namespace LeafHotKey;

/// <summary>
/// 低レベルフックと判定中核をつなぐ入力エンジン。
/// フック内では判定と送信だけを行い、待機・ディスクアクセス・設定読み込みはしない。
/// </summary>
public sealed class InputEngine : IDisposable
{
    private readonly IReadOnlyList<HotkeyProfile> _profiles;
    private readonly KeySender _sender;
    private readonly InputEngineCore _core;
    private readonly ForegroundApp _foreground = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly bool _disableIme;

    private NativeMethods.HookProc? _keyboardCallback;
    private NativeMethods.HookProc? _mouseCallback;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private Thread? _thread;
    private uint _threadId;
    private volatile bool _enabled = true;

    public InputEngine(IReadOnlyList<HotkeyProfile> profiles, bool disableIme = true, KeySender? sender = null)
    {
        _profiles = profiles;
        _sender = sender ?? new KeySender();
        _disableIme = disableIme;
        _core = new InputEngineCore(new ImeAwareSink(_sender, _foreground, disableIme));
    }

    /// <summary>送信前に前面ウィンドウの IME をオフにする送信先（元 AHK の Snd 相当）。</summary>
    private sealed class ImeAwareSink : IKeySink
    {
        private readonly KeySender _sender;
        private readonly ForegroundApp _foreground;
        private readonly bool _disableIme;

        public ImeAwareSink(KeySender sender, ForegroundApp foreground, bool disableIme)
        {
            _sender = sender;
            _foreground = foreground;
            _disableIme = disableIme;
        }

        public SendResult Send(IReadOnlyList<SendToken> tokens)
        {
            // IME 操作に失敗しても送信自体は続ける（AHK も戻り値を見ていない）。
            if (_disableIme) ImeController.Disable(_foreground.CurrentWindow());
            return _sender.Send(tokens);
        }
    }

    /// <summary>入力変換の有効・無効。無効化時は保持中のキーを解放する。</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            if (!value) _core.ReleaseAll();
        }
    }

    public bool Installed => _keyboardHook != IntPtr.Zero && _mouseHook != IntPtr.Zero;

    public string ActiveProfileName => _core.ActiveProfile?.Name ?? string.Empty;

    /// <summary>フックを設置する。設置できない場合は false を返し、動作中と偽らない。</summary>
    public bool Start()
    {
        if (_thread is not null) throw new InvalidOperationException("入力エンジンは既に開始済みです。");

        _thread = new Thread(Pump)
        {
            IsBackground = true,
            Name = "LeafHotKey input",
        };

        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        return _ready.Wait(TimeSpan.FromSeconds(5)) && Installed;
    }

    /// <summary>
    /// フックを解除し、保持中のキーを解放する。
    /// ゲーム保護で本体を終了する前に必ず呼ぶ。
    /// </summary>
    public void Stop()
    {
        _enabled = false;
        _core.ReleaseAll();

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
        UpdateActiveProfile();

        var modifiers = CurrentModifiers();
        var decision = keyUp ? _core.OnKeyUp(name, modifiers) : _core.OnKeyDown(name, modifiers);

        return decision == InputDecision.Suppress
            ? new IntPtr(1)
            : NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
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
