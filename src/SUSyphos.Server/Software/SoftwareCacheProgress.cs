using System.Collections.Concurrent;

namespace SUSyphos.Server.Software;

/// <summary>What the installer cache does with one version right now.</summary>
internal enum CacheStepKind
{
    Downloading,
    Scanning,
}

/// <summary>One step of the installer cache: since when, and how far a download got.</summary>
internal sealed record CacheStep(CacheStepKind Kind, DateTime SinceUtc, long Bytes, long? TotalBytes);

/// <summary>
/// Live state of the installer cache for the update page (Marc, 2026-11-23: the banner "Not in the WinGet source" said
/// nothing about what happened): the version being downloaded (with its bytes) or scanned, when the cache ran last and
/// runs next, and "Check now" to start a run at once. In memory only: after a restart of the central server the next
/// run of the cache starts anew anyway.
/// </summary>
internal sealed class SoftwareCacheProgress(TimeProvider timeProvider) : IDisposable
{
    private readonly ConcurrentDictionary<string, CacheStep> _steps = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private long _nextRunTicks;

    /// <summary>When the cache runs next by itself; null before the first wait.</summary>
    public DateTime? NextRunUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _nextRunTicks);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    public CacheStep? Get(string packageId, string version) => _steps.GetValueOrDefault(Key(packageId, version));

    public void Downloading(string packageId, string version) =>
        _steps[Key(packageId, version)] = new CacheStep(CacheStepKind.Downloading, timeProvider.GetUtcNow().UtcDateTime, 0, null);

    /// <summary>Bytes of the installer being downloaded; with several installers the counter starts again for each.</summary>
    public void Received(string packageId, string version, long bytes, long? total) =>
        _steps.AddOrUpdate(
            Key(packageId, version),
            _ => new CacheStep(CacheStepKind.Downloading, timeProvider.GetUtcNow().UtcDateTime, bytes, total),
            (_, step) => step with { Bytes = bytes, TotalBytes = total });

    public void Scanning(string packageId, string version) =>
        _steps[Key(packageId, version)] = new CacheStep(CacheStepKind.Scanning, timeProvider.GetUtcNow().UtcDateTime, 0, null);

    public void Done(string packageId, string version) => _steps.TryRemove(Key(packageId, version), out _);

    /// <summary>"Check now": the waiting cache runs at once; asking twice while it waits is one run.</summary>
    public void RequestRun()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // A run is requested already.
        }
    }

    /// <summary>Waits for the interval or for <see cref="RequestRun"/>, whichever comes first.</summary>
    public async Task WaitAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _nextRunTicks, (timeProvider.GetUtcNow().UtcDateTime + interval).Ticks);
        using var done = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(interval, timeProvider, done.Token);
        var wake = _wake.WaitAsync(done.Token);
        await Task.WhenAny(delay, wake).ConfigureAwait(false);

        // The other wait ends without taking a later "Check now" with it.
        await done.CancelAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public void Dispose() => _wake.Dispose();

    private static string Key(string packageId, string version) => $"{packageId}|{version}";
}
