using System.Globalization;
using System.Runtime.InteropServices;

using UpdateCoordinator.Agent.Wua;
using UpdateCoordinator.Contracts.Agents;

namespace UpdateCoordinator.Agent.Installation;

/// <summary>
/// Downloads and installs a batch of updates. Never reboots: a needed reboot is only reported.
/// </summary>
internal interface IUpdateInstaller
{
    Task<IReadOnlyList<InstallResult>> InstallAsync(IReadOnlyList<Release> batch, CancellationToken cancellationToken);
}

/// <summary>
/// Installation through the Windows Update Agent API (COM, late bound). The batch is downloaded and
/// installed in one cycle, so several updates share one reboot later.
/// </summary>
internal sealed partial class WuaUpdateInstaller(ILogger<WuaUpdateInstaller> logger) : IUpdateInstaller
{
    private const string SessionProgId = "Microsoft.Update.Session";
    private const string CollectionProgId = "Microsoft.Update.UpdateColl";

    /// <summary>WU_E_NOT_FOUND-like marker for released updates the source no longer offers.</summary>
    public const int UpdateNotFound = unchecked((int)0x80240024);

    public Task<IReadOnlyList<InstallResult>> InstallAsync(IReadOnlyList<Release> batch, CancellationToken cancellationToken) =>
        // Synchronous COM; it runs off the caller's thread and cannot be cancelled once started.
        Task.Run<IReadOnlyList<InstallResult>>(() => Install(batch), cancellationToken);

    private List<InstallResult> Install(IReadOnlyList<Release> batch)
    {
        using var com = new ComScope();
        try
        {
            dynamic session = com.Track(Activator.CreateInstance(Type.GetTypeFromProgID(SessionProgId, throwOnError: true)!)!);
            session.ClientApplicationID = "UpdateCoordinator.Agent";

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
                com.Track(downloader.Download());

                dynamic installer = com.Track(session.CreateUpdateInstaller());
                installer.Updates = collection;
                installer.AllowSourcePrompts = false;
                dynamic installation = com.Track(installer.Install());

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

    private static InstallResultCode ToResultCode(int code) =>
        Enum.IsDefined((InstallResultCode)code) ? (InstallResultCode)code : InstallResultCode.Failed;

    [LoggerMessage(Level = LogLevel.Error, Message = "Installation failed with 0x{HResult:X8}: {Reason}")]
    private partial void LogInstallFailed(int hresult, string reason);
}
