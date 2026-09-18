using System.Text.Json;

namespace LeafHotKey;

/// <summary>
/// 直近の入力イベントを保持するリングバッファ。
/// 不具合の切り分け用で、設定や秘密情報は入れない。
/// </summary>
public sealed class EventLog
{
    private const int Capacity = 200;

    private readonly object _gate = new();
    private readonly Queue<string> _entries = new(Capacity);

    public void Add(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {message}";

        lock (_gate)
        {
            _entries.Enqueue(line);
            while (_entries.Count > Capacity) _entries.Dequeue();
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }

    public string ToJson() => JsonSerializer.Serialize(new { events = Snapshot() });
}
