using SUSyphos.Contracts.Agents;

namespace SUSyphos.Agent.Status;

/// <summary>
/// What the agent is doing right now (live status, docs/05-agent-design.md, section 5). Long steps (search, download,
/// installation, inventory) announce themselves here; heartbeat and <see cref="ActivityPulse"/> read it. Thread-safe,
/// because WUA reports progress on its own threads.
/// </summary>
internal sealed class ActivityTracker(TimeProvider timeProvider)
{
    private readonly Lock _lock = new();
    private AgentActivity? _current;
    private object? _owner;
    private long _version;

    /// <summary>Current activity; null when idle.</summary>
    public AgentActivity? Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    /// <summary>Increases with every change, so the pulse sends only news.</summary>
    public long Version
    {
        get
        {
            lock (_lock)
            {
                return _version;
            }
        }
    }

    /// <summary>Starts a step; disposing the returned scope ends it. Steps do not nest: a new one replaces the current one.</summary>
    public IDisposable Begin(AgentPhase phase, IReadOnlyList<Guid>? updateIds = null)
    {
        var activity = new AgentActivity
        {
            Phase = phase,
            UpdateIds = updateIds is null ? [] : [.. updateIds.Take(AgentActivity.MaxUpdates)],
            StartedAt = timeProvider.GetUtcNow(),
        };
        var scope = new Scope(this);
        lock (_lock)
        {
            _current = activity;
            _owner = scope;
            _version++;
        }

        return scope;
    }

    /// <summary>Moves the current step to another phase (download to installation) or reports its progress in percent.</summary>
    public void Progress(AgentPhase phase, int? percent)
    {
        lock (_lock)
        {
            if (_current is null)
            {
                return;
            }

            var clamped = percent is { } value ? Math.Clamp(value, 0, 100) : (int?)null;
            if (_current.Phase == phase && _current.Percent == clamped)
            {
                return;
            }

            _current = _current with { Phase = phase, Percent = clamped };
            _version++;
        }
    }

    private void End(Scope scope)
    {
        lock (_lock)
        {
            // Only the step that is still current ends; a later step that replaced it stays.
            if (ReferenceEquals(_owner, scope))
            {
                _current = null;
                _owner = null;
                _version++;
            }
        }
    }

    private sealed class Scope(ActivityTracker tracker) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                tracker.End(this);
            }
        }
    }
}
