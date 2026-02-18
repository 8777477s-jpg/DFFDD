namespace BoltMacro;

public sealed class TimelineService
{
    private readonly Storage _storage;
    private readonly object _lock = new();
    private readonly List<TimelineEvent> _events = new();

    public TimelineService(Storage storage)
    {
        _storage = storage;
    }

    public void Add(TimelineEvent ev)
    {
        lock (_lock) _events.Add(ev);
        try { _storage.InsertTimelineEvent(ev); } catch { /* MVP: never crash due to timeline IO */ }
    }

    public IReadOnlyList<TimelineEvent> Snapshot(int max = 200)
    {
        lock (_lock)
        {
            int start = Math.Max(0, _events.Count - max);
            return _events.Skip(start).ToList();
        }
    }
}
