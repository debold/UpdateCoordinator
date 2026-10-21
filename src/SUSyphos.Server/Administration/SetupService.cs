using System.Buffers.Text;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using SUSyphos.Common;

namespace SUSyphos.Server.Administration;

/// <summary>
/// Settings of the web administration. Bound from the "Administration" configuration section.
/// </summary>
internal sealed class AdministrationOptions
{
    public const string SectionName = "Administration";

    /// <summary>File that receives the one-time setup token while no account exists; readable only by administrators.</summary>
    [Required]
    public string SetupTokenPath { get; set; } = Path.Combine(DataPaths.Server, "setup-token.txt");

    /// <summary>Folder of agent log archives uploaded on request; readable only by administrators.</summary>
    [Required]
    public string AgentLogDirectory { get; set; } = Path.Combine(DataPaths.Server, "AgentLogs");

    /// <summary>Agent log archives are deleted after this many days.</summary>
    [Range(1, 365)]
    public int AgentLogRetentionDays { get; set; } = 14;
}

internal enum SetupResult
{
    Completed,
    NotAvailable,
    InvalidToken,
    InvalidAccount,
}

/// <summary>
/// One-time setup of the first administrator. While no account exists, a random token is written to a file
/// that only local administrators can read; whoever presents it may create the first Admin account.
/// Afterwards the setup is closed for good. A token the interactive installer created and showed is taken over, so the
/// installer can open the setup with it.
/// </summary>
internal sealed partial class SetupService(IOptions<AdministrationOptions> options, IInstallerSetupToken installerToken, ILogger<SetupService> logger)
{
    private readonly Lock _lock = new();
    private byte[]? _tokenHash;

    public bool IsAvailable
    {
        get
        {
            lock (_lock)
            {
                return _tokenHash is not null;
            }
        }
    }

    /// <summary>File that holds the token while the setup is open; shown on the sign-in and setup pages.</summary>
    public string TokenPath => options.Value.SetupTokenPath;

    /// <summary>True for a token as the installer creates it: 32 to 128 URL-safe characters (letters, digits, - and _).</summary>
    public static bool IsWellFormed(string? token) =>
        token is { Length: >= 32 and <= 128 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>Called on startup: opens the setup if no account exists, otherwise removes any leftover token file.</summary>
    public async Task InitializeAsync(UserManager<IdentityUser> users, CancellationToken cancellationToken)
    {
        var path = options.Value.SetupTokenPath;
        if (await users.Users.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            lock (_lock)
            {
                _tokenHash = null;
            }

            DeleteTokenFile(path);
            installerToken.Remove();
            return;
        }

        // The installer's token, if it left a usable one; otherwise a new one. Both end up in the token file.
        var token = installerToken.Read() is { } fromInstaller && IsWellFormed(fromInstaller)
            ? fromInstaller
            : Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        WriteRestricted(path, token);
        lock (_lock)
        {
            _tokenHash = Hash(token);
        }

        LogSetupOpen(path);
    }

    public async Task<(SetupResult Result, IEnumerable<string> Errors)> CompleteAsync(
        string? token,
        string? userName,
        string? password,
        UserManager<IdentityUser> users,
        RoleManager<IdentityRole> roles,
        AuditLog audit,
        CancellationToken cancellationToken)
    {
        byte[]? expected;
        lock (_lock)
        {
            expected = _tokenHash;
        }

        if (expected is null || await users.Users.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return (SetupResult.NotAvailable, []);
        }

        if (string.IsNullOrEmpty(token) || token.Length > 128 || !CryptographicOperations.FixedTimeEquals(Hash(token), expected))
        {
            await audit.WriteAsync(AuditLog.SystemActor, "Setup.InvalidToken", cancellationToken: cancellationToken).ConfigureAwait(false);
            return (SetupResult.InvalidToken, []);
        }

        await AdminAccounts.EnsureRolesAsync(roles).ConfigureAwait(false);
        var user = new IdentityUser(userName ?? string.Empty);
        var created = await users.CreateAsync(user, password ?? string.Empty).ConfigureAwait(false);
        if (!created.Succeeded)
        {
            return (SetupResult.InvalidAccount, created.Errors.Select(e => e.Description));
        }

        await users.AddToRoleAsync(user, AdminRoles.Admin).ConfigureAwait(false);
        lock (_lock)
        {
            _tokenHash = null;
        }

        DeleteTokenFile(options.Value.SetupTokenPath);
        installerToken.Remove();
        await audit.WriteAsync(user.UserName!, "Setup.Completed", user.UserName, "First administrator created.", cancellationToken).ConfigureAwait(false);
        LogSetupCompleted(user.UserName!);
        return (SetupResult.Completed, []);
    }

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    /// <summary>Writes the token readable only by SYSTEM, local administrators and the service account.</summary>
    private static void WriteRestricted(string path, string token) => RestrictedFile.Write(path, Encoding.UTF8.GetBytes(token));

    private static void DeleteTokenFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No administrator account exists. Open /setup in the browser and enter the one-time token from {Path} (readable by local administrators only).")]
    private partial void LogSetupOpen(string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Setup completed; first administrator {UserName} created.")]
    private partial void LogSetupCompleted(string userName);
}

internal static class AdminAccounts
{
    public static async Task EnsureRolesAsync(RoleManager<IdentityRole> roles)
    {
        foreach (var role in AdminRoles.All)
        {
            if (!await roles.RoleExistsAsync(role).ConfigureAwait(false))
            {
                await roles.CreateAsync(new IdentityRole(role)).ConfigureAwait(false);
            }
        }
    }
}
