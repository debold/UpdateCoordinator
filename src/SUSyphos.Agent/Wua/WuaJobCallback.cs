using System.Runtime.InteropServices;

namespace SUSyphos.Agent.Wua;

// Callback interfaces of the Windows Update Agent API (wuapi.h). WUA requires them for the asynchronous
// BeginDownload/BeginInstall calls; the agent itself polls the job for progress and only waits for completion here.
// Public because only public types are visible to COM.

[ComImport]
[Guid("8C3F1CDD-6173-4591-AEBD-A56A53CA77C1")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDownloadProgressChangedCallback
{
    void Invoke([MarshalAs(UnmanagedType.IDispatch)] object downloadJob, [MarshalAs(UnmanagedType.IDispatch)] object callbackArgs);
}

[ComImport]
[Guid("77254866-9F5B-4C8E-B9E2-C77A8530D64B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDownloadCompletedCallback
{
    void Invoke([MarshalAs(UnmanagedType.IDispatch)] object downloadJob, [MarshalAs(UnmanagedType.IDispatch)] object callbackArgs);
}

[ComImport]
[Guid("E01402D5-F8DA-43BA-A012-38894BD048F1")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IInstallationProgressChangedCallback
{
    void Invoke([MarshalAs(UnmanagedType.IDispatch)] object installationJob, [MarshalAs(UnmanagedType.IDispatch)] object callbackArgs);
}

[ComImport]
[Guid("45F4F6F3-D602-4F98-9A8A-3EFA152AD2D3")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IInstallationCompletedCallback
{
    void Invoke([MarshalAs(UnmanagedType.IDispatch)] object installationJob, [MarshalAs(UnmanagedType.IDispatch)] object callbackArgs);
}

/// <summary>
/// Callback object for one asynchronous WUA download or installation: progress notifications are ignored (the job is
/// polled instead), completion releases <see cref="WaitForCompletion"/>. Holds no COM references of its own.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class WuaJobCallback : IDownloadProgressChangedCallback, IDownloadCompletedCallback, IInstallationProgressChangedCallback, IInstallationCompletedCallback, IDisposable
{
    private readonly ManualResetEventSlim _completed = new();

    /// <summary>True once WUA reported completion; false if the timeout passed first.</summary>
    public bool WaitForCompletion(TimeSpan timeout) => _completed.Wait(timeout);

    void IDownloadProgressChangedCallback.Invoke(object downloadJob, object callbackArgs)
    {
        // Progress is read from the job by polling, so nothing to do on WUA's thread.
    }

    void IDownloadCompletedCallback.Invoke(object downloadJob, object callbackArgs) => _completed.Set();

    void IInstallationProgressChangedCallback.Invoke(object installationJob, object callbackArgs)
    {
        // See the download progress callback.
    }

    void IInstallationCompletedCallback.Invoke(object installationJob, object callbackArgs) => _completed.Set();

    public void Dispose() => _completed.Dispose();
}
