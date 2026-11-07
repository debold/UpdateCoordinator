using System.Text.RegularExpressions;

namespace SUSyphos.WinGetProbe;

/// <summary>Which WinGet engine the probe loads.</summary>
public enum EngineChoice
{
    /// <summary>The one of the installed App Installer.</summary>
    Installed,

    /// <summary>The one shipped in engine\ next to the exe.</summary>
    Bundled,
}

/// <summary>A checked command line of the probe.</summary>
public sealed record ProbeArguments(
    string Command,
    string? Target,
    string? Version,
    Uri? Source,
    EngineChoice Engine,
    bool AcceptAgreements,
    bool Online,
    string? OfflineFolder,
    string? OutputFolder)
{
    public const string Usage =
        """
        SUSyphos WinGet Probe: lab test of WinGet on Windows Server (run as SYSTEM, e.g. psexec -s).

        Usage: susyphos-wingetprobe <command> [options]

        Commands:
          info                         Machine, account, App Installer, engine and sources
          inventory                    Uninstall keys compared with WinGet's installed list
          bootstrap --online           Install App Installer through the module Microsoft.WinGet.Client
          bootstrap --offline <folder> Provision App Installer from the files of a GitHub release
          search <text>                Search a source
          install <id>                 Install a package for the machine, silently
          upgrade <id>                 Upgrade an installed package, silently

        Options:
          --engine installed|bundled   WinGet engine to load (default installed)
          --version <version>          install/upgrade: this version instead of the newest
          --source <https url>         search/install/upgrade: a private REST source instead of winget
          --accept-agreements          install/upgrade: accept the package's agreements (read them first)
          --out <folder>               Where results go (default probe-results next to the exe)
        """;

    private static readonly string[] Commands = ["info", "inventory", "bootstrap", "search", "install", "upgrade"];

    /// <summary>Package ids of WinGet: Publisher.Name with dots, e.g. Microsoft.VCRedist.2015+.x64.</summary>
    private static readonly Regex PackageId = new(@"^[A-Za-z0-9][A-Za-z0-9.+_-]{0,127}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex VersionText = new(@"^[A-Za-z0-9][A-Za-z0-9.+_-]{0,63}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Parses the command line; returns null and an error for anything it does not accept.</summary>
    public static ProbeArguments? Parse(IReadOnlyList<string> args, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        try
        {
            error = null;
            return ParseOrThrow(args);
        }
        catch (FormatException exception)
        {
            error = exception.Message;
            return null;
        }
    }

    private static ProbeArguments ParseOrThrow(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || !Commands.Contains(args[0], StringComparer.Ordinal))
        {
            throw new FormatException(args.Count == 0 ? "No command." : $"Unknown command '{args[0]}'.");
        }

        var command = args[0];
        string? target = null, version = null, offline = null, output = null;
        Uri? source = null;
        var engine = EngineChoice.Installed;
        bool accept = false, online = false;

        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            string Value() =>
                i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                    ? args[++i]
                    : throw new FormatException($"{arg} needs a value.");

            switch (arg)
            {
                case "--engine":
                    var name = Value();
                    if (!Enum.TryParse(name, ignoreCase: true, out engine) || !Enum.IsDefined(engine) || int.TryParse(name, out _))
                    {
                        throw new FormatException($"Unknown engine '{name}' (installed or bundled).");
                    }

                    break;
                case "--version":
                    version = Value();
                    if (!VersionText.IsMatch(version))
                    {
                        throw new FormatException($"'{version}' is not a version.");
                    }

                    break;
                case "--source":
                    var address = Value();
                    if (!Uri.TryCreate(address, UriKind.Absolute, out source) || source.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(source.UserInfo))
                    {
                        throw new FormatException($"'{address}' is not an https address.");
                    }

                    break;
                case "--out":
                    output = Value();
                    break;
                case "--offline":
                    offline = Value();
                    break;
                case "--online":
                    online = true;
                    break;
                case "--accept-agreements":
                    accept = true;
                    break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal) || target is not null)
                    {
                        throw new FormatException($"Unexpected argument '{arg}'.");
                    }

                    target = arg;
                    break;
            }
        }

        var problem = Check(command, target, version, source, accept, online, offline);
        return problem is null
            ? new ProbeArguments(command, target, version, source, engine, accept, online, offline, output)
            : throw new FormatException(problem);
    }

    private static string? Check(string command, string? target, string? version, Uri? source, bool accept, bool online, string? offline)
    {
        var installs = command is "install" or "upgrade";
        if (command is "bootstrap" ? online == (offline is not null) : online || offline is not null)
        {
            return command is "bootstrap" ? "bootstrap needs either --online or --offline <folder>." : "--online and --offline belong to bootstrap.";
        }

        if (command is "search" or "install" or "upgrade" ? target is null : target is not null)
        {
            return target is null ? $"{command} needs {(command is "search" ? "a search text" : "a package id")}." : $"{command} takes no '{target}'.";
        }

        if (installs && !PackageId.IsMatch(target!))
        {
            return $"'{target}' is not a WinGet package id.";
        }

        if (command is "search" && (target!.Length > 200 || target.Any(char.IsControl)))
        {
            return "The search text is too long or contains control characters.";
        }

        if (!installs && (version is not null || accept))
        {
            return "--version and --accept-agreements belong to install and upgrade.";
        }

        return source is not null && command is not ("search" or "install" or "upgrade") ? "--source belongs to search, install and upgrade." : null;
    }
}
