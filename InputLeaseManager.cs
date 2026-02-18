namespace BoltMacro;

public enum LeaseOwnerType
{
    None = 0,
    Recording = 1,
    ManualMacroRun = 2,
    RuleRun = 3
}

public sealed class InputLeaseManager
{
    private readonly object _lock = new();
    private readonly TimelineService _timeline;

    private LeaseOwnerType _ownerType = LeaseOwnerType.None;
    private string? _ownerId = null;

    public InputLeaseManager(TimelineService timeline)
    {
        _timeline = timeline;
    }

    public bool TryAcquire(LeaseOwnerType type, string id)
    {
        lock (_lock)
        {
            string requested = $"{type}:{id}";
            if (_ownerType != LeaseOwnerType.None)
            {
                _timeline.Add(new TimelineEvent
                {
                    Source = TimelineSource.System,
                    Severity = TimelineSeverity.Warn,
                    Message = $"[Lease] TryAcquire owner={requested} result=deny currentOwner={_ownerType}:{_ownerId}"
                });
                return false;
            }

            _ownerType = type;
            _ownerId = id;
            _timeline.Add(new TimelineEvent
            {
                Source = TimelineSource.System,
                Severity = TimelineSeverity.Info,
                Message = $"[Lease] TryAcquire owner={requested} result=ok currentOwner={_ownerType}:{_ownerId}"
            });
            return true;
        }
    }

    public void Release(LeaseOwnerType type, string id)
    {
        lock (_lock)
        {
            if (_ownerType == type && _ownerId == id)
            {
                _timeline.Add(new TimelineEvent
                {
                    Source = TimelineSource.System,
                    Severity = TimelineSeverity.Info,
                    Message = $"Input lease released ({type}:{id})."
                });
                _ownerType = LeaseOwnerType.None;
                _ownerId = null;
            }
        }
    }

    public void ForceRelease()
    {
        lock (_lock)
        {
            if (_ownerType != LeaseOwnerType.None)
            {
                _timeline.Add(new TimelineEvent
                {
                    Source = TimelineSource.System,
                    Severity = TimelineSeverity.Warn,
                    Message = $"Input lease force-released ({_ownerType}:{_ownerId})."
                });
            }
            _ownerType = LeaseOwnerType.None;
            _ownerId = null;
        }
    }

    public (LeaseOwnerType type, string? id) Current()
    {
        lock (_lock) return (_ownerType, _ownerId);
    }
}
