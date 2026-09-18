using System.Security.Principal;

namespace LeafHotKey;

/// <summary>本体と CLI、および本体と入力エンジン（LeafHotKeyEngine）間の制御通信で使う名前と応答文字列。</summary>
public static class ControlProtocol
{
    public const string Ping = "PING";
    public const string Status = "STATUS";
    public const string Pause = "PAUSE";
    public const string Resume = "RESUME";
    public const string Shutdown = "SHUTDOWN";

    /// <summary>STATUS の引数。WebUI へ渡す状態を JSON で返す。</summary>
    public const string StatusJson = "JSON";

    /// <summary>直近の入力イベントを JSON で返す。</summary>
    public const string Log = "LOG";

    /// <summary>設定ファイルを読み直して反映する。</summary>
    public const string Reload = "RELOAD";

    /// <summary>AutoHotkey バックエンドを起動し直す。</summary>
    public const string RestartBackend = "RESTART-AHK";

    /// <summary>エンジンが動作していない場合に本体が返す状態。</summary>
    public const string StateStopped = "STOPPED";

    /// <summary>SHUTDOWN の引数。ゲーム保護による退避を示す（復帰対象）。</summary>
    public const string ReasonGame = "GAME";

    /// <summary>SHUTDOWN の引数。手動終了を示す（自動復帰しない）。</summary>
    public const string ReasonManual = "MANUAL";

    public const string Pong = "OK PONG";
    public const string ErrorPrefix = "ERR ";

    /// <summary>ユーザーごとに一意なパイプ名。別ユーザーのインスタンスへ接続しない。</summary>
    public static string PipeName
    {
        get
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            return $"LeafHotKey.control.{sid}";
        }
    }

    /// <summary>
    /// エンジン（LeafHotKeyEngine.exe）の制御チャネル。
    /// 本体（LeafHotKey.exe）は常駐したまま、入力エンジンだけを退避・再起動できるようにする。
    /// </summary>
    public static string EnginePipeName
    {
        get
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            return $"LeafHotKey.engine.{sid}";
        }
    }

    public static string Ok(string value) => "OK " + value;

    public static string Error(string value) => ErrorPrefix + value;
}
