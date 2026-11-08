using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;

namespace SUSyphos.WinGet;

/// <summary>
/// Finds out why Windows refuses to load the engine with "module not found" (error 126): reads the libraries the engine
/// files import and tries each one that is not in the engine folder from the system folder.
/// </summary>
public static partial class DependencyCheck
{
    private const uint LoadLibrarySearchSystem32 = 0x00000800;

    /// <summary>The libraries the engine files in <paramref name="folder"/> import that Windows cannot load.</summary>
    public static IReadOnlyList<string> MissingSystemLibraries(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        var local = Directory.EnumerateFiles(folder, "*.dll").Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Directory.EnumerateFiles(folder, "*.dll")
            .SelectMany(ImportsOf)
            .Where(name => !local.Contains(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => !CanLoad(name))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Names of the libraries a PE file imports directly (import table, not delay-loaded).</summary>
    /// <exception cref="BadImageFormatException">The file is not a PE file.</exception>
    public static IReadOnlyList<string> ImportsOf(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var stream = File.OpenRead(path);
        using var reader = new PEReader(stream);
        var headers = reader.PEHeaders;
        var directory = headers.PEHeader?.ImportTableDirectory ?? throw new BadImageFormatException($"{path} has no PE header.");
        if (directory.Size == 0)
        {
            return [];
        }

        var image = reader.GetEntireImage().GetContent().AsSpan().ToArray();
        int Offset(int rva)
        {
            foreach (var section in headers.SectionHeaders)
            {
                if (rva >= section.VirtualAddress && rva < section.VirtualAddress + Math.Max(section.VirtualSize, section.SizeOfRawData))
                {
                    return rva - section.VirtualAddress + section.PointerToRawData;
                }
            }

            throw new BadImageFormatException($"{path}: address 0x{rva:X} is in no section.");
        }

        var names = new List<string>();

        // IMAGE_IMPORT_DESCRIPTOR: 20 bytes, the name RVA at offset 12; a descriptor of zeros ends the table.
        for (var descriptor = Offset(directory.RelativeVirtualAddress); descriptor + 20 <= image.Length; descriptor += 20)
        {
            var nameRva = BitConverter.ToInt32(image.AsSpan(descriptor + 12, 4));
            if (nameRva == 0)
            {
                break;
            }

            var start = Offset(nameRva);
            var end = image.AsSpan(start).IndexOf((byte)0);
            names.Add(Encoding.ASCII.GetString(image.AsSpan(start, end < 0 ? image.Length - start : end)));
        }

        return names;
    }

    private static bool CanLoad(string name)
    {
        var module = LoadLibraryExW(name, 0, LoadLibrarySearchSystem32);
        if (module == 0)
        {
            return false;
        }

        _ = FreeLibrary(module);
        return true;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadLibraryExW(string fileName, nint file, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeLibrary(nint module);
}
