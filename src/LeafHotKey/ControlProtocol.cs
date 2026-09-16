using System.Security.Principal;

namespace LeafHotKey;

/// <summary>本体と Watcher / CLI 間の制御通信で使う名前と応答文字列。</summary>
public static class ControlProtocol
{
    public const string Ping = "PING";
    public const string Status = "STATUS";
    public const string Pause = "PAUSE";
    public const string Resume = "RESUME";
    public const string Shutdown = "SHUTDOWN";

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

    public static string Ok(string value) => "OK " + value;

    public static string Error(string value) => ErrorPrefix + value;
}
