using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace LeafHotKey;

/// <summary>タスクトレイ常駐の最小実装。Phase 1 では入力フックと WebUI を持たない。</summary>
public sealed class TrayApplication : ApplicationContext
{
    private readonly HostState _state;
    private readonly ControlServer _server;
    private readonly NotifyIcon _icon;
    private readonly Icon _trayIcon;
    private readonly ToolStripMenuItem _toggleItem;

    private readonly string? _settingsUrl;
    private readonly Func<string>? _statusText;
    private readonly Action? _toggle;
    private readonly Action? _startEngine;

    /// <summary>すべてのリソースを解放した後に、本体を起動し直す要求。</summary>
    public event Action? RestartRequested;

    public TrayApplication(
        HostState state,
        ControlServer server,
        string? settingsUrl = null,
        Func<string>? statusText = null,
        Action? toggle = null,
        Action? startEngine = null)
    {
        _state = state;
        _server = server;
        _settingsUrl = settingsUrl;
        _statusText = statusText;
        _toggle = toggle;
        _startEngine = startEngine;

        _toggleItem = new ToolStripMenuItem("一時停止", null, (_, _) => Toggle());
        var restartItem = new ToolStripMenuItem("再起動", null, (_, _) => RequestRestart());
        var exitItem = new ToolStripMenuItem("終了", null, (_, _) => RequestExit(ExitReason.Manual));
        var settingsItem = new ToolStripMenuItem("設定を開く", null, (_, _) => OpenSettings())
        {
            Enabled = _settingsUrl is not null,
        };
        var versionItem = new ToolStripMenuItem(ReadCommitLabel()) { Enabled = false };
        var startEngineItem = new ToolStripMenuItem("入力エンジンを起動", null, (_, _) => _startEngine?.Invoke());

        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) =>
        {
            // 入力エンジンが停止している間は、停止・再開のどちらも送り先が無い。
            var engineResponds = ControlClient.IsHostResponding(300, ControlProtocol.EnginePipeName);
            startEngineItem.Enabled = !engineResponds;
            _toggleItem.Enabled = engineResponds;
        };
        menu.Items.Add(versionItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(settingsItem);
        menu.Items.Add(restartItem);
        menu.Items.Add(startEngineItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = LoadTrayIcon();
        _icon = new NotifyIcon
        {
            Icon = _trayIcon,
            Visible = true,
            ContextMenuStrip = menu,
        };

        _state.StateChanged += OnStateChanged;
        _server.ShutdownRequested += OnShutdownRequested;
        UpdateSurface(_state.State);
    }

    /// <summary>既定のブラウザで設定画面を開く。URL にはこの起動だけのトークンが含まれる。</summary>
    private void OpenSettings()
    {
        if (_settingsUrl is null) return;

        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _settingsUrl,
                UseShellExecute = true,
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 既定のブラウザが無い環境では何もしない。
        }
    }

    /// <summary>トレイに通知（バルーン）を出す。フックやバックエンドの異常に気付けるようにする。</summary>
    public void Notify(string message)
    {
        if (_icon.ContextMenuStrip is { } menu && menu.InvokeRequired)
        {
            menu.BeginInvoke(() => Notify(message));
            return;
        }

        _icon.BalloonTipTitle = "LeafHotKey";
        _icon.BalloonTipText = message;
        _icon.ShowBalloonTip(5000);
    }

    private static string ReadCommitLabel()
    {
        var version = typeof(TrayApplication).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        var separator = version?.IndexOf('+') ?? -1;
        if (separator < 0 || separator == version!.Length - 1) return "Commit: unknown";

        var commit = version[(separator + 1)..];
        return $"Commit: {commit[..Math.Min(7, commit.Length)]}";
    }

    private static Icon LoadTrayIcon()
    {
        try
        {
            return Icon.ExtractAssociatedIcon(Application.ExecutablePath)
                ?? new Icon(SystemIcons.Application, SystemInformation.SmallIconSize);
        }
        catch (Exception)
        {
            return new Icon(SystemIcons.Application, SystemInformation.SmallIconSize);
        }
    }

    private void Toggle()
    {
        // 入力エンジンを操作する場合（別プロセス構成）は、そちらへ委譲する。
        if (_toggle is not null)
        {
            _toggle();
            return;
        }

        if (_state.State == RuntimeState.Running) _state.Pause();
        else if (_state.State == RuntimeState.Paused) _state.Resume();
    }

    /// <summary>トレイの表示を最新の状態へ更新する（監視側から呼ぶ）。</summary>
    public void RefreshStatus()
    {
        if (_icon.ContextMenuStrip is { } menu && menu.InvokeRequired)
        {
            menu.BeginInvoke(RefreshStatus);
            return;
        }

        UpdateSurface(_state.State);
    }

    private void OnShutdownRequested()
    {
        // 制御チャネルはワーカースレッドで動くため、UI スレッドへ戻してから終了する。
        if (_icon.ContextMenuStrip is { } menu && menu.InvokeRequired)
        {
            menu.BeginInvoke(() => ExitThread());
            return;
        }

        ExitThread();
    }

    private void RequestRestart()
    {
        _state.BeginShutdown(ExitReason.Manual);
        RestartRequested?.Invoke();
        ExitThread();
    }

    private void RequestExit(ExitReason reason)
    {
        _state.BeginShutdown(reason);
        ExitThread();
    }

    private void OnStateChanged(RuntimeState next)
    {
        if (_icon.ContextMenuStrip is { } menu && menu.InvokeRequired)
        {
            menu.BeginInvoke(() => UpdateSurface(next));
            return;
        }

        UpdateSurface(next);
    }

    private void UpdateSurface(RuntimeState next)
    {
        _toggleItem.Text = next == RuntimeState.Paused ? "再開" : "一時停止";
        var stateText = _statusText?.Invoke() ?? next switch
        {
            RuntimeState.Running => "入力変換は有効",
            RuntimeState.Paused => "一時停止中",
            _ => "終了処理中",
        };
        _icon.Text = $"LeafHotKey — {stateText}";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _state.StateChanged -= OnStateChanged;
            _server.ShutdownRequested -= OnShutdownRequested;
            _icon.Visible = false;
            _icon.Dispose();
            _trayIcon.Dispose();
        }

        base.Dispose(disposing);
    }
}
