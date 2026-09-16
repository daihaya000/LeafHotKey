namespace LeafHotKey;

/// <summary>捕捉した 1 イベント。</summary>
public readonly struct CapturedKey
{
    public CapturedKey(ushort virtualKey, bool keyUp, bool injected)
    {
        VirtualKey = virtualKey;
        KeyUp = keyUp;
        Injected = injected;
    }

    public ushort VirtualKey { get; }

    public bool KeyUp { get; }

    public bool Injected { get; }

    public override string ToString() => $"0x{VirtualKey:X2} {(KeyUp ? "up" : "down")}";
}

/// <summary>
/// 指定した署名を持つ注入イベントだけを捕捉し、下位へ渡さずに破棄する。
/// 送信内容を実環境で確認するために使い、他アプリへキーが届かないようにする。
/// </summary>
public sealed class InjectedKeyCapture : IDisposable
{
    private readonly UIntPtr _signature;
    private readonly List<CapturedKey> _events = new();
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Thread _thread;

    private NativeMethods.HookProc? _callback;
    private IntPtr _hook;
    private uint _threadId;

    private readonly bool _captureAllInjected;

    public InjectedKeyCapture(UIntPtr signature, bool captureAllInjected = false)
    {
        _signature = signature;
        _captureAllInjected = captureAllInjected;
        _thread = new Thread(Pump)
        {
            IsBackground = true,
            Name = "LeafHotKey capture",
        };

        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new InvalidOperationException("フックの準備が完了しませんでした。");
        }
    }

    public bool Installed => _hook != IntPtr.Zero;

    public IReadOnlyList<CapturedKey> Snapshot()
    {
        lock (_gate) return _events.ToArray();
    }

    public void Clear()
    {
        lock (_gate) _events.Clear();
    }

    /// <summary>期待件数に達するまで待つ。達しない場合も現在の内容で判定できるよう false を返す。</summary>
    public bool WaitFor(int count, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (_gate)
            {
                if (_events.Count >= count) return true;
            }

            Thread.Sleep(10);
        }

        lock (_gate) return _events.Count >= count;
    }

    private void Pump()
    {
        _threadId = NativeMethods.GetCurrentThreadId();
        _callback = HookProc;
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WhKeyboardLowLevel, _callback, IntPtr.Zero, 0);
        _ready.Set();

        if (_hook == IntPtr.Zero) return;

        while (NativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            if (message.Message == NativeMethods.WmQuit) break;
        }

        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode != NativeMethods.HcAction) return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

        var data = System.Runtime.InteropServices.Marshal
            .PtrToStructure<NativeMethods.KeyboardLowLevelHookStruct>(lParam);

        var injected = (data.Flags & 0x10) != 0;
        var mine = data.ExtraInfo == _signature;

        if (!mine && !(_captureAllInjected && injected))
        {
            // 対象外の入力（特に実のユーザー操作）には触れない。
            return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        var message = (int)wParam;
        var keyUp = message is NativeMethods.WmKeyUp or NativeMethods.WmSysKeyUp;

        lock (_gate)
        {
            _events.Add(new CapturedKey((ushort)data.VirtualKey, keyUp, injected: true));
        }

        // 検証用の入力を他のアプリへ届けない。
        return new IntPtr(1);
    }

    public void Dispose()
    {
        if (_threadId != 0)
        {
            NativeMethods.PostThreadMessage(_threadId, NativeMethods.WmQuit, IntPtr.Zero, IntPtr.Zero);
        }

        _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }
}
