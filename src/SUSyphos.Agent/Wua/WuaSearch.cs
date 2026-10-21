using System.Runtime.InteropServices;

namespace SUSyphos.Agent.Wua;

/// <summary>
/// An update as read from the Windows Update Agent API, before it is shaped for the wire.
/// </summary>
internal sealed record WuaUpdate(
    string UpdateId,
    int Revision,
    IReadOnlyList<string> KbArticles,
    string Title,
    string? Classification,
    decimal? MaxDownloadSize = null,
    int? RebootBehavior = null,
    string? MsrcSeverity = null,
    IReadOnlyList<string>? SupersededUpdateIds = null);

/// <summary>
/// The Windows Update Agent API could not perform a search; <see cref="Exception.HResult"/> carries the WUA error code.
/// </summary>
internal sealed class WuaException : Exception
{
    public WuaException(string message, int hresult, Exception? inner = null)
        : base(message, inner)
    {
        HResult = hresult;
    }
}

/// <summary>
/// Searches through the Windows Update Agent API (COM, late bound). Every COM object is released
/// explicitly: the WUA API is known for leaking memory otherwise. Shared by inventory and update search.
/// </summary>
internal static class WuaSearch
{
    private const string SessionProgId = "Microsoft.Update.Session";
    private const string ClassificationCategoryType = "UpdateClassification";

    /// <summary>Error code when the WUA API is not registered at all (REGDB_E_CLASSNOTREG).</summary>
    public const int ClassNotRegistered = unchecked((int)0x80040154);

    /// <param name="criteria">WUA search criteria, e.g. "IsInstalled=0 and IsHidden=0".</param>
    /// <param name="online">False searches only the local cache, true contacts the update source.</param>
    /// <param name="serverSelection">0 = system default (Windows Update or WSUS by policy), 2 = Windows Update.</param>
    /// <param name="serviceId">Update service to search instead of the server selection (e.g. Microsoft Update); null for the selection.</param>
    public static List<WuaUpdate> Search(string criteria, bool online, int serverSelection, string? serviceId = null)
    {
        using var com = new ComScope();
        try
        {
            var sessionType = Type.GetTypeFromProgID(SessionProgId, throwOnError: false)
                ?? throw new WuaException("The Windows Update Agent API is not registered.", ClassNotRegistered);

            dynamic session = com.Track(Activator.CreateInstance(sessionType)!);
            session.ClientApplicationID = "SUSyphos.Agent";
            dynamic searcher = com.Track(session.CreateUpdateSearcher());
            searcher.Online = online;
            ApplyService(searcher, serverSelection, serviceId);
            dynamic result = com.Track(searcher.Search(criteria));
            dynamic updates = com.Track(result.Updates);

            int count = updates.Count;
            var list = new List<WuaUpdate>(count);
            for (var i = 0; i < count; i++)
            {
                dynamic update = com.Track(updates.Item(i));
                dynamic identity = com.Track(update.Identity);
                dynamic behavior = com.Track(update.InstallationBehavior);
                list.Add(new WuaUpdate(
                    (string)identity.UpdateID,
                    (int)identity.RevisionNumber,
                    ReadStrings(com, update.KBArticleIDs),
                    (string)update.Title,
                    ReadClassification(com, update.Categories),
                    (decimal)update.MaxDownloadSize,
                    (int)behavior.RebootBehavior,
                    (string?)update.MsrcSeverity,
                    ReadStrings(com, update.SupersededUpdateIDs)));
            }

            return list;
        }
        catch (COMException ex)
        {
            throw new WuaException($"Windows Update search failed: {ex.Message}", ex.HResult, ex);
        }
        catch (Exception ex) when (ex is InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or UnauthorizedAccessException)
        {
            throw new WuaException($"Windows Update search failed: {ex.Message}", ex.HResult, ex);
        }
    }

    /// <summary>ssOthers: search the given registered service, e.g. Microsoft Update.</summary>
    private const int OtherServiceSelection = 3;

    /// <summary>Points a searcher at the server selection, or at a registered service such as Microsoft Update.</summary>
    internal static void ApplyService(dynamic searcher, int serverSelection, string? serviceId)
    {
        if (serviceId is null)
        {
            searcher.ServerSelection = serverSelection;
        }
        else
        {
            searcher.ServerSelection = OtherServiceSelection;
            searcher.ServiceID = serviceId;
        }
    }

    private static List<string> ReadStrings(ComScope com, object collectionObject)
    {
        dynamic collection = com.Track(collectionObject);
        int count = collection.Count;
        var values = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            values.Add((string)collection.Item(i));
        }

        return values;
    }

    private static string? ReadClassification(ComScope com, object categoriesObject)
    {
        dynamic categories = com.Track(categoriesObject);
        int count = categories.Count;
        for (var i = 0; i < count; i++)
        {
            dynamic category = com.Track(categories.Item(i));
            if ((string)category.Type == ClassificationCategoryType)
            {
                return (string)category.Name;
            }
        }

        return null;
    }
}
