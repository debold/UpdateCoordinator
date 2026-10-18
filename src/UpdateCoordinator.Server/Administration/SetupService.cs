using System.Buffers.Text;
using System.ComponentModel.DataAnnotations;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using UpdateCoordinator.Common;

namespace UpdateCoordinator.Server.Administration;

/// <summary>
/// Settings of the web administration. Bound from the "Administration" configuration section.
/// </summary>
internal sealed class AdministrationOptions
{
    public const string SectionName = "Administration";

    /// <summary>File that receives the one-time setup token while no account exists; readable only by administrators.</summary>
    [Required]
    public string SetupTokenPath { get; set; } = Path.Combine(DataPaths.Server, "setup-token.txt");
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
/// Afterwards the setup is closed for good.
/// </summary>
internal sealed partial class SetupService(IOptions<AdministrationOptions> options, ILogger<SetupService> logger)
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
            return;
        }

        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
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
        await audit.WriteAsync(user.UserName!, "Setup.Completed", user.UserName, "First administrator created.", cancellationToken).ConfigureAwait(false);
        LogSetupCompleted(user.UserName!);
        return (SetupResult.Completed, []);
    }

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    /// <summary>Writes the token readable only by SYSTEM, local administrators and the service account.</summary>
    private static void WriteRestricted(string path, string token)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var identity in new IdentityReference[]
        {
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            WindowsIdentity.GetCurrent().User!,
        })
        {
            security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl, AccessControlType.Allow));
        }

        DeleteTokenFile(path);
        using var stream = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write | FileSystemRights.ReadData, FileShare.None, 4096, FileOptions.None, security);
        stream.Write(Encoding.UTF8.GetBytes(token));
    }

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
