using System.Diagnostics;

namespace LeafHotKey;

/// <summary>
/// 対象プロセスの有無だけを判定する。ゲームプロセスを終了させる操作は一切行わない。
/// 監視に失敗した場合は「存在する」と見なし、安全側（本体を復帰させない）に倒す。
/// </summary>
public sealed class GameMonitor
{
    private readonly Func<string, bool> _probe;

    public GameMonitor(Func<string, bool>? probe = null)
    {
        _probe = probe ?? ProcessExists;
    }

    /// <summary>実行中のプロセス名を列挙する。判定できなかった名前も「実行中」として返す。</summary>
    public IReadOnlyList<string> Running(IEnumerable<string> names)
    {
        var found = new List<string>();
        foreach (var name in names)
        {
            if (_probe(name)) found.Add(name);
        }

        return found;
    }

    public bool AnyRunning(IEnumerable<string> names) => Running(names).Count > 0;

    /// <summary>実プロセスの存在確認。例外時は判定不能として true を返す。</summary>
    public static bool ProcessExists(string imageName)
    {
        var bare = imageName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? imageName[..^4]
            : imageName;

        try
        {
            var processes = Process.GetProcessesByName(bare);
            try
            {
                return processes.Length > 0;
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return true;
        }
    }
}
