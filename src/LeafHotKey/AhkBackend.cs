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
    private const int MaxRestartAttempts = 3;

    private Process? _process;
    private DateTimeOffset _startedAt;
    private DateTime _scriptStamp;
    private int _restartAttempts;
    private string? _notice;

    /// <summary>直近の状態（トレイと設定画面に表示する）。</summary>
    public string Status { get; private set; } = "停止中";

    /// <summary>このアプリが再起動した回数。</summary>
    public int Restarts { get; private set; }

    /// <summary>直近の通知（トレイと設定画面に出す）。</summary>
    public string? LastNotice { get; private set; }

    /// <summary>実行中のスクリプト。</summary>
    public string ScriptPath { get; private set; } = string.Empty;

    public bool IsRunning => _process is { HasExited: false };

    public int? ProcessId
    {
        get
        {
            try
            {
                return IsRunning ? _process!.Id : null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    /// <summary>トレイへ一度だけ出す通知。取り出すと消える。</summary>
    public string? ConsumeNotice()
    {
        var notice = _notice;
        _notice = null;
        return notice;
    }
    /// <summary>スクリプトを起動する。既に同じスクリプトが動いていれば何もしない。</summary>
    public bool Start(BackendSettings settings)
    {
        var script = ResolveScript(settings.AhkScript);
        if (script is null)
        {
            Status = "スクリプトが見つかりません";
            return false;
        }

        if (IsRunning)
        {
            // 別のスクリプトへ切り替える指示なら、古いものを止めてから起動し直す。
            if (string.Equals(ScriptPath, script, StringComparison.OrdinalIgnoreCase))
            {
                Status = "動作中";
                return true;
            }

            Stop();
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
            if (_process is not null)
            {
                _startedAt = DateTimeOffset.Now;
                ScriptPath = script;
                _scriptStamp = File.GetLastWriteTimeUtc(script);
            }

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

            // 明示的な停止は新しい運用の開始なので、再試行回数を戻す。
            _restartAttempts = 0;
        }
    }

    /// <summary>プロセスは止めずに監視から外す（ホスト終了時）。AHK 側の保護に任せる。</summary>
    public void Release()
    {
        _process?.Dispose();
        _process = null;
        Status = "停止中（ホスト終了）";
    }

    /// <summary>
    /// 数秒ごとの監視。落ちていれば再起動し、スクリプトが更新されていれば読み直させる。
    /// ゲーム保護などで AHK 自身が入れ替わっている場合は、外部インスタンスを尊重して何もしない。
    /// </summary>
    public void Tick(BackendSettings settings)
    {
        if (_process is null) return;

        if (_process.HasExited)
        {
            _process.Dispose();
            _process = null;

            var external = ExternalInstanceRunning();
            if (!ShouldRestart(true, external, false, _restartAttempts, MaxRestartAttempts))
            {
                Status = external
                    ? "停止中（外部の AutoHotkey が動作中）"
                    : _restartAttempts >= MaxRestartAttempts ? "再起動を中止しました" : "停止中";
                return;
            }

            _restartAttempts++;
            Start(settings);
            if (IsRunning)
            {
                Restarts++;
                SetNotice($"AutoHotkey を再起動しました（{_restartAttempts} 回目）");
            }

            return;
        }

        // しばらく安定して動いていれば再試行回数を戻す。
        if (_restartAttempts > 0 && DateTimeOffset.Now - _startedAt > TimeSpan.FromMinutes(2)) _restartAttempts = 0;

        if (ScriptPath.Length == 0) return;

        try
        {
            // スクリプトが更新されたら読み直させる（編集内容を反映）。
            if (File.GetLastWriteTimeUtc(ScriptPath) > _scriptStamp)
            {
                Stop();
                Start(settings);
                if (IsRunning) SetNotice("スクリプトの更新を検出し、AutoHotkey を読み直しました");
            }
        }
        catch (IOException)
        {
            // 読み取り中の一時的な失敗は次回に任せる。
        }
    }

    /// <summary>監視の判断（検証で固定できるよう純関数にする）。</summary>
    internal static bool ShouldRestart(bool processExited, bool externalInstanceRunning, bool releasedByHost, int attempts, int maxAttempts)
        => processExited && !externalInstanceRunning && !releasedByHost && attempts < maxAttempts;

    private void SetNotice(string message)
    {
        _notice = message;
        LastNotice = message;
    }

    /// <summary>他の AutoHotkey が動いているか（restart_ahk.bat などが起動した場合）。</summary>
    private static bool ExternalInstanceRunning()
    {
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    string name;
                    try
                    {
                        name = process.ProcessName;
                    }
                    catch (InvalidOperationException)
                    {
                        continue;
                    }

                    if (name.StartsWith("AutoHotkey", StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
        }
        catch (InvalidOperationException)
        {
            // 列挙に失敗した場合は「無い」と断定せず、再起動しない側に寄せる。
            return true;
        }

        return false;
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

    public void Dispose() => Stop();}
