using System.Globalization;
using System.Runtime.InteropServices;

using SUSyphos.Agent.Wua;
using SUSyphos.Contracts.Agents;

namespace SUSyphos.Agent.Installation;

/// <summary>
/// Downloads and installs a batch of updates. Never reboots: a needed reboot is only reported.
/// </summary>
internal interface IUpdateInstaller
{
    Task<IReadOnlyList<InstallResult>> InstallAsync(IReadOnlyList<Release> batch, CancellationToken cancellationToken);
}

/// <summary>
/// Installation through the Windows Update Agent API (COM, late bound). The batch is downloaded and
/// installed in one cycle, so several updates share one reboot later. Download and installation run as asynchronous
/// WUA jobs, so their progress reaches the live status; if WUA refuses the asynchronous calls, the synchronous ones are used.
/// </summary>
internal sealed partial class WuaUpdateInstaller(Status.ActivityTracker activity, ILogger<WuaUpdateInstaller> logger) : IUpdateInstaller
{
    /// <summary>How often a running job is asked for its progress.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private const string SessionProgId = "Microsoft.Update.Session";
    private const string CollectionProgId = "Microsoft.Update.UpdateColl";

    /// <summary>WU_E_NOT_FOUND-like marker for released updates the source no longer offers.</summary>
    public const int UpdateNotFound = unchecked((int)0x80240024);

    public Task<IReadOnlyList<InstallResult>> InstallAsync(IReadOnlyList<Release> batch, CancellationToken cancellationToken) =>
        // Synchronous COM; it runs off the caller's thread and cannot be cancelled once started.
        Task.Run<IReadOnlyList<InstallResult>>(() => Install(batch), cancellationToken);

    private List<InstallResult> Install(IReadOnlyList<Release> batch)
    {
        using var phase = activity.Begin(AgentPhase.Downloading, [.. batch.Select(r => r.UpdateId)]);
        using var com = new ComScope();
        try
        {
            dynamic session = com.Track(Activator.CreateInstance(Type.GetTypeFromProgID(SessionProgId, throwOnError: true)!)!);
            session.ClientApplicationID = "SUSyphos.Agent";

            // Look up exactly the released update IDs and revisions; nothing else is installed.
            var criteria = string.Join(
                " or ",
                batch.Select(r => string.Create(CultureInfo.InvariantCulture, $"(UpdateID='{r.UpdateId:D}' and RevisionNumber={r.Revision})")));
            dynamic searcher = com.Track(session.CreateUpdateSearcher());
            searcher.Online = true;
            dynamic found = com.Track(com.Track(searcher.Search(criteria)).Updates);

            dynamic collection = com.Track(Activator.CreateInstance(Type.GetTypeFromProgID(CollectionProgId, throwOnError: true)!)!);
            var order = new List<(Guid UpdateId, int Revision)>();
            int count = found.Count;
            for (var i = 0; i < count; i++)
            {
                dynamic update = com.Track(found.Item(i));
                dynamic identity = com.Track(update.Identity);
                if (!(bool)update.EulaAccepted)
                {
                    update.AcceptEula();
                }

                collection.Add(update);
                order.Add((Guid.Parse((string)identity.UpdateID), (int)identity.RevisionNumber));
            }

            var results = new List<InstallResult>();
            if (order.Count > 0)
            {
                dynamic downloader = com.Track(session.CreateUpdateDownloader());
                downloader.Updates = collection;
                com.Track(Download(downloader, com));

                activity.Progress(AgentPhase.Installing, 0);
                dynamic installer = com.Track(session.CreateUpdateInstaller());
                installer.Updates = collection;
                installer.AllowSourcePrompts = false;
                dynamic installation = com.Track(InstallJob(installer, com));

                for (var i = 0; i < order.Count; i++)
                {
                    dynamic result = com.Track(installation.GetUpdateResult(i));
                    results.Add(new InstallResult
                    {
                        UpdateId = order[i].UpdateId,
                        Revision = order[i].Revision,
                        ResultCode = ToResultCode((int)result.ResultCode),
                        HResult = (int)result.HResult,
                        RebootRequired = (bool)result.RebootRequired,
                    });
                }
            }

            // Released updates the source does not offer (any more) are reported, not silently dropped.
            results.AddRange(batch
                .Where(r => !order.Contains((r.UpdateId, r.Revision)))
                .Select(r => new InstallResult { UpdateId = r.UpdateId, Revision = r.Revision, ResultCode = InstallResultCode.NotStarted, HResult = UpdateNotFound }));
            return results;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or UnauthorizedAccessException)
        {
            LogInstallFailed(ex.HResult, ex.Message);
            return [.. batch.Select(r => new InstallResult { UpdateId = r.UpdateId, Revision = r.Revision, ResultCode = InstallResultCode.Failed, HResult = ex.HResult })];
        }
    }

    /// <summary>Downloads as an asynchronous job with progress; falls back to the synchronous call if WUA refuses the job.</summary>
    private object Download(dynamic downloader, ComScope com)
    {
        using var callback = new WuaJobCallback();
        dynamic job;
        try
        {
            job = com.Track(downloader.BeginDownload(new UnknownWrapper(callback), new UnknownWrapper(callback), null));
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            LogNoProgress("download", ex.HResult);
            return downloader.Download();
        }

        try
        {
            WaitFor(job, callback, AgentPhase.Downloading);
            return downloader.EndDownload(job);
        }
        finally
        {
            // Waits for outstanding callbacks, so the callback object is not used after it was disposed.
            job.CleanUp();
        }
    }

    /// <summary>Installs as an asynchronous job with progress; falls back to the synchronous call if WUA refuses the job.</summary>
    private object InstallJob(dynamic installer, ComScope com)
    {
        using var callback = new WuaJobCallback();
        dynamic job;
        try
        {
            job = com.Track(installer.BeginInstall(new UnknownWrapper(callback), new UnknownWrapper(callback), null));
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            LogNoProgress("installation", ex.HResult);
            return installer.Install();
        }

        try
        {
            WaitFor(job, callback, AgentPhase.Installing);
            return installer.EndInstall(job);
        }
        finally
        {
            job.CleanUp();
        }
    }

    /// <summary>Waits for the job and reports its overall progress; a failed progress query never stops the job.</summary>
    private void WaitFor(dynamic job, WuaJobCallback callback, AgentPhase phase)
    {
        while (!callback.WaitForCompletion(PollInterval) && !(bool)job.IsCompleted)
        {
            object? progress = null;
            try
            {
                progress = job.GetProgress();
                activity.Progress(phase, (int)((dynamic)progress).PercentComplete);
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                // Progress is informative only.
            }
            finally
            {
                if (progress is not null && Marshal.IsComObject(progress))
                {
                    Marshal.FinalReleaseComObject(progress);
                }
            }
        }

        activity.Progress(phase, 100);
    }

    private static InstallResultCode ToResultCode(int code) =>
        Enum.IsDefined((InstallResultCode)code) ? (InstallResultCode)code : InstallResultCode.Failed;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Windows Update refused the asynchronous {Step} (0x{HResult:X8}); continuing without progress.")]
    private partial void LogNoProgress(string step, int hresult);

    [LoggerMessage(Level = LogLevel.Error, Message = "Installation failed with 0x{HResult:X8}: {Reason}")]
    private partial void LogInstallFailed(int hresult, string reason);
}
