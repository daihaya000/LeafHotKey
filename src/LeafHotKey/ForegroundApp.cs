using System.Diagnostics;

namespace LeafHotKey;

/// <summary>
/// 前面ウィンドウの実行ファイル名を取得する。
/// フック内から呼ぶため、同じウィンドウなら前回の結果を再利用する。
/// </summary>
public sealed class ForegroundApp
{
    private IntPtr _cachedWindow;
    private string _cachedName = string.Empty;

    /// <summary>前面ウィンドウの exe 名（例: CLIPStudioPaint.exe）。取得できない場合は空文字。</summary>
    public string CurrentProcessName()
    {
        var window = NativeMethods.GetForegroundWindow();
        if (window == IntPtr.Zero) return string.Empty;
        if (window == _cachedWindow) return _cachedName;

        _cachedWindow = window;
        _cachedName = ResolveProcessName(window);
        return _cachedName;
    }

    public IntPtr CurrentWindow() => NativeMethods.GetForegroundWindow();

    private static string ResolveProcessName(IntPtr window)
    {
        if (NativeMethods.GetWindowThreadProcessId(window, out var processId) == 0) return string.Empty;

        try
        {
            using var process = Process.GetProcessById((int)processId);
            var name = process.ProcessName;
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }
}
