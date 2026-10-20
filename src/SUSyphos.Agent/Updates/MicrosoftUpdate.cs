using System.Runtime.InteropServices;

using SUSyphos.Agent.Configuration;
using SUSyphos.Agent.Inventory;
using SUSyphos.Agent.Wua;

namespace SUSyphos.Agent.Updates;

/// <summary>
/// Which update service searches and installations use. Set from the release list; read by the WUA searches and the
/// installer, so search, inventory and installation always look at the same source.
/// </summary>
internal sealed class UpdateServiceSelection
{
    /// <summary>Service ID of Microsoft Update (updates for other Microsoft products besides Windows).</summary>
    public const string MicrosoftUpdateServiceId = "7971f918-a847-4430-9279-4a52d1efe18d";

    private volatile bool _useMicrosoftUpdate;

    /// <summary>True while Microsoft Update is configured centrally and registered on this server.</summary>
    public bool UseMicrosoftUpdate
    {
        get => _useMicrosoftUpdate;
        set => _useMicrosoftUpdate = value;
    }

    /// <summary>The service to search, or null for the system default (Windows Update or WSUS by policy).</summary>
    public string? ServiceId => UseMicrosoftUpdate ? MicrosoftUpdateServiceId : null;
}

/// <summary>Update services registered with the Windows Update Agent; replaced by a fake in tests, so no test changes the machine.</summary>
internal interface IUpdateServiceRegistry
{
    bool IsRegistered(string serviceId);

    void Register(string serviceId);

    void Unregister(string serviceId);
}

/// <summary>The WUA service manager (COM "Microsoft.Update.ServiceManager"), the way "Receive updates for other Microsoft products" works.</summary>
internal sealed class WuaUpdateServiceRegistry : IUpdateServiceRegistry
{
    private const string ServiceManagerProgId = "Microsoft.Update.ServiceManager";

    /// <summary>asfAllowPendingRegistration | asfAllowOnlineRegistration | asfRegisterServiceWithAU.</summary>
    private const int RegistrationFlags = 7;

    public bool IsRegistered(string serviceId)
    {
        using var com = new ComScope();
        dynamic manager = com.Track(CreateManager());
        dynamic services = com.Track(manager.Services);
        int count = services.Count;
        for (var i = 0; i < count; i++)
        {
            dynamic service = com.Track(services.Item(i));
            if (string.Equals((string)service.ServiceID, serviceId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public void Register(string serviceId)
    {
        using var com = new ComScope();
        dynamic manager = com.Track(CreateManager());
        manager.ClientApplicationID = Contracts.Agents.UpdateHistoryEntry.AgentClientApplicationId;
        com.Track(manager.AddService2(serviceId, RegistrationFlags, string.Empty));
    }

    public void Unregister(string serviceId)
    {
        using var com = new ComScope();
        dynamic manager = com.Track(CreateManager());
        manager.ClientApplicationID = Contracts.Agents.UpdateHistoryEntry.AgentClientApplicationId;
        manager.RemoveService(serviceId);
    }

    private static object CreateManager() =>
        Activator.CreateInstance(Type.GetTypeFromProgID(ServiceManagerProgId, throwOnError: true)!)!;
}

/// <summary>
/// Registers Microsoft Update when the central server asks for it (opt-in, Marc 2026-10-20), so updates for other Microsoft
/// products are found and installed as well. A marker in the agent key records that the agent registered the service, so
/// switching the option off only removes a registration the agent made itself, never one an administrator made.
/// </summary>
internal sealed partial class MicrosoftUpdateManager(
    IUpdateServiceRegistry services,
    ISystemProbe probe,
    IRegistryWriter registry,
    UpdateServiceSelection selection,
    ILogger<MicrosoftUpdateManager> logger)
{
    internal const string MarkerName = "RegisteredMicrosoftUpdate";

    private bool? _lastApplied;

    /// <summary>Applies the central setting; the registration is checked only when the setting changes or after a failure.</summary>
    public void Apply(bool use)
    {
        if (_lastApplied == use)
        {
            return;
        }

        try
        {
            var registered = services.IsRegistered(UpdateServiceSelection.MicrosoftUpdateServiceId);
            if (use && !registered)
            {
                services.Register(UpdateServiceSelection.MicrosoftUpdateServiceId);
                registry.SetDword(AgentRegistry.InstallerKeyPath, MarkerName, 1);
                registered = true;
                LogRegistered();
            }
            else if (!use && registered && probe.GetRegistryValue(AgentRegistry.InstallerKeyPath, MarkerName) == "1")
            {
                services.Unregister(UpdateServiceSelection.MicrosoftUpdateServiceId);
                registry.DeleteValue(AgentRegistry.InstallerKeyPath, MarkerName);
                registered = false;
                LogUnregistered();
            }

            selection.UseMicrosoftUpdate = use && registered;
            _lastApplied = use;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            // Searches fall back to the system default; the next cycle tries again.
            selection.UseMicrosoftUpdate = false;
            LogFailed(ex.HResult, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Microsoft Update registered as configured on the central server: updates for other Microsoft products are searched as well.")]
    private partial void LogRegistered();

    [LoggerMessage(Level = LogLevel.Information, Message = "Microsoft Update unregistered: the agent removed the registration it had made.")]
    private partial void LogUnregistered();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Microsoft Update could not be configured (0x{HResult:X8}: {Reason}); searching the system default source.")]
    private partial void LogFailed(int hresult, string reason);
}
