using System.Runtime.InteropServices;

namespace UpdateCoordinator.Agent.Inventory;

/// <summary>
/// An installed update as read from the system, before sanitizing for the wire.
/// </summary>
internal sealed record RawInstalledUpdate(string UpdateId, int Revision, IReadOnlyList<string> KbArticles, string Title, string? Classification);

/// <summary>
/// Reads the installed updates. Returns null if they cannot be read (e.g. Windows Update service disabled).
/// </summary>
internal interface IInstalledUpdateSource
{
    Task<IReadOnlyList<RawInstalledUpdate>?> GetInstalledUpdatesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Windows Update Agent API (COM, late bound). Searches only the local cache (Online = false), so no internet access is needed.
/// Every COM object is released explicitly: the WUA API is known for leaking memory otherwise.
/// </summary>
internal sealed partial class WuaInstalledUpdateSource(ILogger<WuaInstalledUpdateSource> logger) : IInstalledUpdateSource
{
    private const string SessionProgId = "Microsoft.Update.Session";
    private const string ClassificationCategoryType = "UpdateClassification";

    public Task<IReadOnlyList<RawInstalledUpdate>?> GetInstalledUpdatesAsync(CancellationToken cancellationToken) =>
        // The search is synchronous COM; run it off the caller's thread. It cannot be cancelled once started.
        Task.Run(Search, cancellationToken);

    private IReadOnlyList<RawInstalledUpdate>? Search()
    {
        var com = new ComScope();
        try
        {
            var sessionType = Type.GetTypeFromProgID(SessionProgId, throwOnError: false);
            if (sessionType is null)
            {
                LogUnavailable("Windows Update Agent API is not registered");
                return null;
            }

            dynamic session = com.Track(Activator.CreateInstance(sessionType)!);
            session.ClientApplicationID = "UpdateCoordinator.Agent";
            dynamic searcher = com.Track(session.CreateUpdateSearcher());
            searcher.Online = false;
            dynamic result = com.Track(searcher.Search("IsInstalled=1 and IsHidden=0"));
            dynamic updates = com.Track(result.Updates);

            int count = updates.Count;
            var list = new List<RawInstalledUpdate>(count);
            for (var i = 0; i < count; i++)
            {
                dynamic update = com.Track(updates.Item(i));
                dynamic identity = com.Track(update.Identity);
                list.Add(new RawInstalledUpdate(
                    (string)identity.UpdateID,
                    (int)identity.RevisionNumber,
                    ReadStrings(com, update.KBArticleIDs),
                    (string)update.Title,
                    ReadClassification(com, update.Categories)));
            }

            return list;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or UnauthorizedAccessException)
        {
            LogUnavailable(ex.Message);
            return null;
        }
        finally
        {
            com.Dispose();
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Installed updates cannot be read ({Reason}); the inventory is sent without them.")]
    private partial void LogUnavailable(string reason);

    /// <summary>Collects COM objects and releases them in reverse order.</summary>
    private sealed class ComScope : IDisposable
    {
        private readonly Stack<object> _objects = new();

        public object Track(object instance)
        {
            if (instance is not null && Marshal.IsComObject(instance))
            {
                _objects.Push(instance);
            }

            return instance!;
        }

        public void Dispose()
        {
            while (_objects.TryPop(out var instance))
            {
                Marshal.FinalReleaseComObject(instance);
            }
        }
    }
}
