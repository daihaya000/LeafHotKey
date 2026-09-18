using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;

namespace LeafHotKey;

/// <summary>
/// AHK バックエンド。外部の AutoHotkey にスクリプトを実行させる。
/// 本体の入力フックとは同時に動かさない（呼び出し側が内蔵エンジンを止めてから開始する）。
/// </summary>
public sealed class AhkBackend : IDisposable
{
    private Process? _process;

    /// <summary>直近の状態（トレイと設定画面に表示する）。</summary>
    public string Status { get; private set; } = "停止中";

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>スクリプトを起動する。既に起動済みなら何もしない。</summary>
    public bool Start(BackendSettings settings)
    {
        if (IsRunning)
        {
            Status = "動作中";
            return true;
        }

        var script = ResolveScript(settings.AhkScript);
        if (script is null)
        {
            Status = "スクリプトが見つかりません";
            return false;
        }

        var executable = ResolveExecutable(settings.AhkExecutable, script);
        if (executable is null)
        {
            Status = "AutoHotkey が見つかりません";
            return false;
        }

        try
        {
            _process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "\"" + script + "\"",
                WorkingDirectory = Path.GetDirectoryName(script) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
            });

            Status = _process is null ? "起動に失敗しました" : "動作中";
            return _process is not null;
        }
        catch (Win32Exception ex)
        {
            Status = $"起動に失敗しました（{ex.Message}）";
            return false;
        }
    }

    /// <summary>このアプリが起動したスクリプトを止める。restart_ahk.bat 経由の再起動も止める。</summary>
    public void Stop()
    {
        if (_process is null) return;

        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // 既に終了している場合は何もしない。
        }
        finally
        {
            _process.Dispose();
            _process = null;
            Status = "停止中";
        }
    }

    /// <summary>設定されたスクリプトの実体。未設定や存在しない場合は null。</summary>
    internal static string? ResolveScript(string configured)
    {
        var path = configured.Trim();
        if (path.Length == 0) return null;

        return File.Exists(path) ? path : null;
    }

    /// <summary>AutoHotkey 本体を探す。指定 → スクリプトと同じフォルダー → インストール先 → PATH の順。</summary>
    internal static string? ResolveExecutable(string configured, string scriptPath)
    {
        var explicitPath = configured.Trim();
        if (explicitPath.Length > 0 && File.Exists(explicitPath)) return explicitPath;

        var directory = Path.GetDirectoryName(scriptPath);
        if (!string.IsNullOrEmpty(directory))
        {
            foreach (var name in new[] { "AutoHotkeyU64.exe", "AutoHotkeyU32.exe", "AutoHotkeyA32.exe", "AutoHotkey.exe" })
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) return candidate;
            }
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\AutoHotkey");
            if (key?.GetValue("InstallDir") is string install && install.Length > 0)
            {
                var candidate = Path.Combine(install, "AutoHotkey.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            // レジストリを読めない環境では PATH だけを見る。
        }

        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (folder.Trim().Length == 0) continue;
            var candidate = Path.Combine(folder.Trim(), "AutoHotkey.exe");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    public void Dispose() => Stop();
}
