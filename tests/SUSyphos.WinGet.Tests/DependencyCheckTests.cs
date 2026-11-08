namespace SUSyphos.WinGet.Tests;

public sealed class DependencyCheckTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("susyphos-deps-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void ImportsOf_ReadsTheImportTable()
    {
        // The in-proc wrapper of the bundled engine (copied next to the probe and these tests) links the C runtime only.
        var imports = DependencyCheck.ImportsOf(Path.Combine(AppContext.BaseDirectory, "engine", WinGetEngine.InProcFileName));

        Assert.Contains("KERNEL32.dll", imports, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("api-ms-win-crt-runtime-l1-1-0.dll", imports, StringComparer.OrdinalIgnoreCase);
        Assert.All(imports, name => Assert.EndsWith(".dll", name, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MissingSystemLibraries_FindsNothingMissingForTheBundledEngineOnThisWindows()
    {
        Assert.Empty(DependencyCheck.MissingSystemLibraries(Path.Combine(AppContext.BaseDirectory, "engine")));
    }

    [Fact]
    public void ImportsOf_RejectsAFileThatIsNoProgram()
    {
        var path = Path.Combine(_folder, "fake.dll");
        File.WriteAllText(path, "not a program");

        Assert.Throws<BadImageFormatException>(() => DependencyCheck.ImportsOf(path));
    }

    [Fact]
    public void MissingSystemLibraries_IsEmptyWhenWindowsHasEverything()
    {
        File.Copy(Path.Combine(Environment.SystemDirectory, "shlwapi.dll"), Path.Combine(_folder, "copy.dll"));

        Assert.Empty(DependencyCheck.MissingSystemLibraries(_folder));
    }

    [Fact]
    public void MissingSystemLibraries_IgnoresAnEmptyFolder()
    {
        Assert.Empty(DependencyCheck.MissingSystemLibraries(_folder));
    }
}
