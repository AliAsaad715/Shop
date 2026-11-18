namespace Shop.Modules.Notifications.Realtime;

// Local to this API process: a shared inbox would prevent the other instances from receiving their copy.
internal sealed class RecentRealtimeMessages(TimeProvider time)
{
    internal const int Capacity = 10_000;
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, DateTimeOffset> _expires = [];
    private readonly Queue<(Guid Id, DateTimeOffset Expires)> _oldest = [];

    public bool Contains(Guid id)
    {
        lock (_gate)
        {
            Trim();
            return _expires.ContainsKey(id);
        }
    }

    public void Record(Guid id)
    {
        lock (_gate)
        {
            Trim();
            if (_expires.ContainsKey(id)) return;
            while (_expires.Count >= Capacity) _expires.Remove(_oldest.Dequeue().Id);
            var expires = time.GetUtcNow() + Lifetime;
            _expires.Add(id, expires);
            _oldest.Enqueue((id, expires));
        }
    }

    private void Trim()
    {
        var now = time.GetUtcNow();
        while (_oldest.TryPeek(out var entry) && entry.Expires <= now)
        {
            _oldest.Dequeue();
            _expires.Remove(entry.Id);
        }
    }
}
