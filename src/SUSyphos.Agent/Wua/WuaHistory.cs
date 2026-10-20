using System.Runtime.InteropServices;

namespace SUSyphos.Agent.Wua;

/// <summary>
/// A Windows Update history entry as read from the WUA API, before it is shaped for the wire.
/// </summary>
internal sealed record WuaHistoryRecord(
    string? UpdateId,
    int Revision,
    string? Title,
    DateTime Date,
    int Operation,
    int ResultCode,
    int HResult,
    string? ClientApplicationId);

/// <summary>
/// Reads the Windows Update history (IUpdateSearcher.QueryHistory, local, no network). Every COM object is released
/// explicitly, as in <see cref="WuaSearch"/>.
/// </summary>
internal static class WuaHistory
{
    private const string SessionProgId = "Microsoft.Update.Session";

    /// <summary>At most this many history entries are read; older ones are beyond the reported period anyway.</summary>
    private const int MaxScanned = 2_000;

    /// <summary>Entries newer than <paramref name="sinceUtc"/>, newest first, at most <paramref name="max"/>.</summary>
    public static List<WuaHistoryRecord> Query(DateTime sinceUtc, int max)
    {
        using var com = new ComScope();
        try
        {
            var sessionType = Type.GetTypeFromProgID(SessionProgId, throwOnError: false)
                ?? throw new WuaException("The Windows Update Agent API is not registered.", WuaSearch.ClassNotRegistered);

            dynamic session = com.Track(Activator.CreateInstance(sessionType)!);
            session.ClientApplicationID = Contracts.Agents.UpdateHistoryEntry.AgentClientApplicationId;
            dynamic searcher = com.Track(session.CreateUpdateSearcher());
            int total = searcher.GetTotalHistoryCount();
            var list = new List<WuaHistoryRecord>();
            if (total <= 0)
            {
                return list;
            }

            // Read in pages; the order of the history is not documented, so entries are filtered and sorted here.
            const int PageSize = 100;
            var end = Math.Min(total, MaxScanned);
            for (var start = 0; start < end; start += PageSize)
            {
                using var pageScope = new ComScope();
                dynamic page = pageScope.Track(searcher.QueryHistory(start, Math.Min(PageSize, end - start)));
                int count = page.Count;
                for (var i = 0; i < count; i++)
                {
                    dynamic entry = pageScope.Track(page.Item(i));
                    DateTime date = entry.Date;
                    var utc = DateTime.SpecifyKind(date, DateTimeKind.Utc);
                    if (utc < sinceUtc)
                    {
                        continue;
                    }

                    dynamic identity = pageScope.Track(entry.UpdateIdentity);
                    list.Add(new WuaHistoryRecord(
                        (string?)identity.UpdateID,
                        (int)identity.RevisionNumber,
                        (string?)entry.Title,
                        utc,
                        (int)entry.Operation,
                        (int)entry.ResultCode,
                        (int)entry.HResult,
                        (string?)entry.ClientApplicationID));
                }
            }

            return [.. list.OrderByDescending(r => r.Date).Take(max)];
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or UnauthorizedAccessException)
        {
            throw new WuaException($"Windows Update history cannot be read: {ex.Message}", ex.HResult, ex);
        }
    }
}
