using System.Drawing;
using System.Windows.Forms;

namespace LeafHotKey;

/// <summary>タスクトレイ常駐の最小実装。Phase 1 では入力フックと WebUI を持たない。</summary>
public sealed class TrayApplication : ApplicationContext
{
    private readonly HostState _state;
    private readonly ControlServer _server;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _toggleItem;

    public TrayApplication(HostState state, ControlServer server)
    {
        _state = state;
        _server = server;

        _toggleItem = new ToolStripMenuItem("一時停止", null, (_, _) => Toggle());
        var exitItem = new ToolStripMenuItem("終了", null, (_, _) => RequestExit(ExitReason.Manual));

        var menu = new ContextMenuStrip();
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = menu,
        };

        _state.StateChanged += OnStateChanged;
        _server.ShutdownRequested += OnShutdownRequested;
        UpdateSurface(_state.State);
    }

    private void Toggle()
    {
        if (_state.State == RuntimeState.Running) _state.Pause();
        else if (_state.State == RuntimeState.Paused) _state.Resume();
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
        _icon.Text = next switch
        {
            RuntimeState.Running => "LeafHotKey — 入力変換は有効",
            RuntimeState.Paused => "LeafHotKey — 一時停止中",
            _ => "LeafHotKey — 終了処理中",
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _state.StateChanged -= OnStateChanged;
            _server.ShutdownRequested -= OnShutdownRequested;
            _icon.Visible = false;
            _icon.Dispose();
        }

        base.Dispose(disposing);
    }
}
