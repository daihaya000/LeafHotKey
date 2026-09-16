using System.Security.Principal;

namespace LeafHotKey;

/// <summary>
/// 同一ユーザー・同一セッション内での多重起動を防ぐ。
/// 名前付き Mutex はハンドルが 1 つも無くなると消えるため、
/// 「オブジェクトが既に存在する = 別インスタンスが動作中」として判定できる。
/// 所有権（WaitOne）は取得しない。スレッド親和性の問題を避け、異常終了後も再取得できる。
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex? _mutex;

    private SingleInstance(Mutex? mutex, bool acquired)
    {
        _mutex = mutex;
        Acquired = acquired;
    }

    /// <summary>この呼び出しが起動枠を確保できたか。false なら既に別インスタンスが動作中。</summary>
    public bool Acquired { get; }

    /// <summary>ユーザーごとに一意な名前を作る。別ユーザーの起動を妨げない。</summary>
    public static string NameFor(string role)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        return $@"Local\LeafHotKey.{role}.{sid}";
    }

    public static SingleInstance TryAcquire(string role)
    {
        var mutex = new Mutex(initiallyOwned: false, NameFor(role), out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return new SingleInstance(null, false);
        }

        return new SingleInstance(mutex, true);
    }

    public void Dispose() => _mutex?.Dispose();
}
