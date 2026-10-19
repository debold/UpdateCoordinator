using System.ServiceProcess;


using UpdateCoordinator.Server.Administration;
using UpdateCoordinator.Server.Data;
using UpdateCoordinator.Server.Trust;

namespace UpdateCoordinator.Server;

/// <summary>
/// Command line restore: <c>UpdateCoordinator.Server.exe restore &lt;backup.zip&gt;</c>, run as administrator while the service
/// is stopped. The password comes from UPDATECOORDINATOR_BACKUP_PASSWORD or an interactive prompt, never from the command line.
/// </summary>
internal static class RestoreCommand
{
    public const string Verb = "restore";
    public const string PasswordVariable = "UPDATECOORDINATOR_BACKUP_PASSWORD";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2)
        {
            await Console.Error.WriteLineAsync("Usage: UpdateCoordinator.Server.exe restore <backup.zip>").ConfigureAwait(false);
            return 2;
        }

        if (IsServiceRunning())
        {
            await Console.Error.WriteLineAsync("Stop the service UpdateCoordinator.Server before restoring.").ConfigureAwait(false);
            return 3;
        }

        // Same configuration sources as the service, so the paths match what the service will load.
        var builder = WebApplication.CreateBuilder([]);
        builder.Configuration.AddServerRegistry(Microsoft.Win32.Registry.LocalMachine);
        var databasePath = builder.Configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>()?.Path ?? new DatabaseOptions().Path;
        var anchorPath = builder.Configuration.GetSection(TrustOptions.SectionName).Get<TrustOptions>()?.AnchorPath ?? new TrustOptions().AnchorPath;

        var password = Environment.GetEnvironmentVariable(PasswordVariable) ?? ReadPassword();
        var outcome = await BackupService.RestoreAsync(args[1], password, databasePath, anchorPath, CancellationToken.None).ConfigureAwait(false);
        if (!outcome.Succeeded)
        {
            await Console.Error.WriteLineAsync(outcome.Error).ConfigureAwait(false);
            return 1;
        }

        await Console.Out.WriteLineAsync($"Restored database to {databasePath} and the trust anchor to {anchorPath}. Start the service now.").ConfigureAwait(false);
        return 0;
    }

    private static bool IsServiceRunning()
    {
        try
        {
            using var service = new ServiceController("UpdateCoordinator.Server");
            return service.Status != ServiceControllerStatus.Stopped;
        }
        catch (InvalidOperationException)
        {
            // Not installed as a service.
            return false;
        }
    }

    private static string ReadPassword()
    {
        Console.Write("Backup password: ");
        var password = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return password.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (password.Length > 0)
                {
                    password.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                password.Append(key.KeyChar);
            }
        }
    }
}
