using System.ComponentModel;
using System.Runtime.InteropServices;

using Microsoft.Management.Deployment;

namespace SUSyphos.WinGet;

/// <summary>
/// The WinGet COM API, activated in-proc from the folder of an engine: the one App Installer installed
/// (Microsoft.DesktopAppInstaller) or one the program brings along (NuGet package Microsoft.WindowsPackageManager.InProcCom).
/// In-proc works under SYSTEM, where winget.exe and the out-of-process server are not supported; every object of the API
/// is created through the same engine, so nothing is activated out of process.
/// </summary>
public sealed partial class WinGetEngine : IDisposable
{
    /// <summary>Thin in-proc wrapper of the NuGet package Microsoft.WindowsPackageManager.InProcCom.</summary>
    public const string InProcFileName = "Microsoft.Management.Deployment.InProc.dll";

    /// <summary>The engine itself; App Installer ships it without the wrapper.</summary>
    public const string EngineFileName = "WindowsPackageManager.dll";

    private const string Namespace = "Microsoft.Management.Deployment.";
    private const uint LoadWithAlteredSearchPath = 0x00000008;
    private const uint SearchDllLoadDir = 0x00000100;
    private const uint SearchDefaultDirs = 0x00001000;

    private readonly nint _module;
    private readonly DllGetActivationFactory _getFactory;

    private WinGetEngine(string folder, string file, nint module, DllGetActivationFactory getFactory)
    {
        Folder = folder;
        File = file;
        _module = module;
        _getFactory = getFactory;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DllGetActivationFactory(nint activatableClassId, out nint factory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ActivateInstance(nint factory, out nint instance);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ModuleInitialize();

    /// <summary>Folder the engine was loaded from.</summary>
    public string Folder { get; }

    /// <summary>File that was loaded: the wrapper or the engine itself.</summary>
    public string File { get; }

    /// <summary>True when the folder holds an engine this class can load.</summary>
    public static bool IsEngineFolder(string folder) =>
        System.IO.File.Exists(Path.Combine(folder, InProcFileName)) || System.IO.File.Exists(Path.Combine(folder, EngineFileName));

    /// <summary>
    /// Loads the engine from a folder: through the wrapper <see cref="InProcFileName"/> when it is there (bundled engine),
    /// otherwise from <see cref="EngineFileName"/> directly (App Installer). Dependencies are looked up in that folder,
    /// then in <paramref name="dependencyFolders"/> (e.g. the VCLibs framework package App Installer needs), then in
    /// the system folders.
    /// </summary>
    /// <exception cref="FileNotFoundException">The folder holds no engine.</exception>
    /// <exception cref="Win32Exception">Windows could not load it (e.g. a missing runtime library).</exception>
    public static WinGetEngine Load(string folder, IEnumerable<string>? dependencyFolders = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        var wrapper = Path.Combine(folder, InProcFileName);
        var direct = Path.Combine(folder, EngineFileName);
        var path = System.IO.File.Exists(wrapper) ? wrapper : direct;
        if (!System.IO.File.Exists(path))
        {
            throw new FileNotFoundException($"No WinGet engine in {folder}.", path);
        }

        // In-proc COM of WinRT needs the apartment of the thread; an already initialized one is fine.
        _ = RoInitialize(1);
        var extra = dependencyFolders?.Where(Directory.Exists).ToList() ?? [];
        foreach (var dependencies in extra)
        {
            _ = AddDllDirectory(dependencies);
        }

        var module = extra.Count == 0
            ? LoadLibraryExW(path, 0, LoadWithAlteredSearchPath)
            : LoadLibraryExW(path, 0, SearchDllLoadDir | SearchDefaultDirs);
        if (module == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new Win32Exception(error, $"Windows could not load {path} (error {error}: {new Win32Exception(error).Message}).");
        }

        try
        {
            if (path == direct)
            {
                // Without the wrapper the module is initialized by hand, as the wrapper does, and hands out the
                // factories of its in-proc classes.
                Marshal.ThrowExceptionForHR(Export<ModuleInitialize>(module, path, "WindowsPackageManagerInProcModuleInitialize")());
                return new WinGetEngine(folder, path, module, Export<DllGetActivationFactory>(module, path, "WindowsPackageManagerInProcModuleGetActivationFactory"));
            }

            return new WinGetEngine(folder, path, module, Export<DllGetActivationFactory>(module, path, "DllGetActivationFactory"));
        }
        catch
        {
            _ = FreeLibrary(module);
            throw;
        }
    }

    private static T Export<T>(nint module, string path, string name)
        where T : Delegate
    {
        var export = GetProcAddress(module, name);
        return export == 0
            ? throw new Win32Exception(Marshal.GetLastPInvokeError(), $"{path} exports no {name}.")
            : Marshal.GetDelegateForFunctionPointer<T>(export);
    }

    public PackageManager CreatePackageManager() => Create("PackageManager", PackageManager.FromAbi);

    public FindPackagesOptions CreateFindPackagesOptions() => Create("FindPackagesOptions", FindPackagesOptions.FromAbi);

    public PackageMatchFilter CreatePackageMatchFilter() => Create("PackageMatchFilter", PackageMatchFilter.FromAbi);

    public InstallOptions CreateInstallOptions() => Create("InstallOptions", InstallOptions.FromAbi);

    public CreateCompositePackageCatalogOptions CreateCompositeOptions() => Create("CreateCompositePackageCatalogOptions", CreateCompositePackageCatalogOptions.FromAbi);

    public AddPackageCatalogOptions CreateAddCatalogOptions() => Create("AddPackageCatalogOptions", AddPackageCatalogOptions.FromAbi);

    public UninstallOptions CreateUninstallOptions() => Create("UninstallOptions", UninstallOptions.FromAbi);

    public void Dispose()
    {
        // The engine stays loaded for the life of the process: objects of the API may still be referenced, and WinRT
        // modules are not meant to be unloaded while in use.
        GC.KeepAlive(_module);
    }

    /// <summary>Activates a class of the API through the engine's own factory and wraps it in its projection.</summary>
    private T Create<T>(string className, Func<nint, T> fromAbi)
    {
        var name = Namespace + className;
        Marshal.ThrowExceptionForHR(WindowsCreateString(name, name.Length, out var hstring));
        try
        {
            Marshal.ThrowExceptionForHR(_getFactory(hstring, out var factory));
            try
            {
                // IActivationFactory: IUnknown (3 slots), IInspectable (3 slots), then ActivateInstance.
                var vtable = Marshal.ReadIntPtr(factory);
                var activate = Marshal.GetDelegateForFunctionPointer<ActivateInstance>(Marshal.ReadIntPtr(vtable, 6 * nint.Size));
                Marshal.ThrowExceptionForHR(activate(factory, out var instance));
                try
                {
                    return fromAbi(instance);
                }
                finally
                {
                    _ = Marshal.Release(instance);
                }
            }
            finally
            {
                _ = Marshal.Release(factory);
            }
        }
        finally
        {
            _ = WindowsDeleteString(hstring);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadLibraryExW(string fileName, nint file, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint AddDllDirectory(string newDirectory);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetProcAddress(nint module, string procName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeLibrary(nint module);

    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WindowsCreateString(string sourceString, int length, out nint hstring);

    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(nint hstring);

    [LibraryImport("combase.dll")]
    private static partial int RoInitialize(int initType);
}
