namespace LeafHotKey;

/// <summary>
/// 送信前に IME をオフにする（元 AHK の IME_SET(0) 相当）。
/// フック内から呼ぶため、応答しないウィンドウで固まらないようタイムアウト付きで送る。
/// </summary>
public static class ImeController
{
    private const uint TimeoutMs = 50;

    /// <summary>指定ウィンドウの IME をオフにする。成功したかどうかを返す。</summary>
    public static bool Disable(IntPtr window)
    {
        if (window == IntPtr.Zero) return false;

        var ime = NativeMethods.ImmGetDefaultIMEWnd(window);
        if (ime == IntPtr.Zero) return false;

        var sent = NativeMethods.SendMessageTimeout(
            ime,
            NativeMethods.WmImeControl,
            new IntPtr(NativeMethods.ImcSetOpenStatus),
            IntPtr.Zero,
            NativeMethods.SmtoAbortIfHung,
            TimeoutMs,
            out _);

        return sent != IntPtr.Zero;
    }
}
