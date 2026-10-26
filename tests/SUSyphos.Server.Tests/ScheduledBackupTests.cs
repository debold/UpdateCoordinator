using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using SUSyphos.Server.Administration;
using SUSyphos.Server.Data;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

public sealed class ScheduledBackupTests : IAsyncDisposable
{
    private const string Password = "a long backup password";
    private readonly ServerFactory _factory = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "SUSyphos.Tests", Guid.NewGuid().ToString("N"), "Backups");

    [Theory]
    [InlineData("relative\\folder")]
    [InlineData("C:\\Backup\\..\\Windows")]
    [InlineData("http://example/backup")]
    [InlineData("C:")]
    [InlineData("\\\\server")]
    [InlineData("C:\\Backup\\<bad>")]
    public async Task InvalidFolders_AreRefused(string folder)
    {
        var outcome = await WithAsync(s => s.SaveAsync(new ScheduledBackupSettings { Enabled = true, Folder = folder }, Password, "admin", CancellationToken.None));

        Assert.False(outcome.Succeeded);
    }

    [Theory]
    [InlineData(0, Password)]
    [InlineData(366, Password)]
    [InlineData(7, "too short")]
    public async Task InvalidKeepOrPassword_IsRefused(int keep, string password)
    {
        var outcome = await WithAsync(s => s.SaveAsync(new ScheduledBackupSettings { Enabled = true, Folder = _folder, Keep = keep }, password, "admin", CancellationToken.None));

        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public async Task FolderThatCannotBeWritten_IsRefusedWhenSaving_AndAWritableOneKeepsNoTestFile()
    {
        // A file where the folder should be: Windows cannot create the folder below it.
        Directory.CreateDirectory(Path.GetDirectoryName(_folder)!);
        var blocker = _folder + ".file";
        await File.WriteAllTextAsync(blocker, "not a folder", TestContext.Current.CancellationToken);

        var refused = await WithAsync(s => s.SaveAsync(new ScheduledBackupSettings { Enabled = true, Folder = Path.Combine(blocker, "Backups") }, Password, "admin", CancellationToken.None));
        var saved = await WithAsync(s => s.SaveAsync(new ScheduledBackupSettings { Enabled = true, Folder = _folder }, Password, "admin", CancellationToken.None));

        Assert.False(refused.Succeeded);
        Assert.Contains("cannot write to", refused.Error, StringComparison.Ordinal);
        Assert.True(saved.Succeeded, saved.Error);
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task Enabling_WithoutAnyPassword_IsRefused()
    {
        var outcome = await WithAsync(s => s.SaveAsync(new ScheduledBackupSettings { Enabled = true, Folder = _folder }, null, "admin", CancellationToken.None));

        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public async Task Run_WritesARestorableArchive_AndKeepsOnlyTheNewest()
    {
        Assert.True((await WithAsync(s => s.SaveAsync(new ScheduledBackupSettings { Enabled = true, Folder = _folder, Keep = 2 }, Password, "admin", CancellationToken.None))).Succeeded);
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(Path.Combine(_folder, "susyphos-backup-scheduled-20200101-000000.zip"), "old", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_folder, "susyphos-backup-scheduled-20200102-000000.zip"), "old", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_folder, "keep-me.zip"), "foreign", TestContext.Current.CancellationToken);

        var run = await WithAsync(async s => await s.RunAsync(await s.GetSettingsAsync(CancellationToken.None), CancellationToken.None));

        Assert.Null(run.Error);
        var files = Directory.GetFiles(_folder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["keep-me.zip", "susyphos-backup-scheduled-20200102-000000.zip", run.FileName], files);
        var restoreDirectory = Path.Combine(_folder, "restore");
        Directory.CreateDirectory(restoreDirectory);
        var restored = await BackupService.RestoreAsync(Path.Combine(_folder, run.FileName!), Password, Path.Combine(restoreDirectory, "server.db"), Path.Combine(restoreDirectory, "anchor.key"), TestContext.Current.CancellationToken);
        Assert.True(restored.Succeeded, restored.Error);
        Assert.True(await _factory.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "Backup.ScheduleChanged", TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Failure_IsRecordedAndNotified()
    {
        Assert.True((await WithAsync(s => s.SaveAsync(new ScheduledBackupSettings { Enabled = true, Folder = _folder }, Password, "admin", CancellationToken.None))).Succeeded);
        // A file where the folder should be makes the run fail (saving created the folder, so it goes first).
        Directory.Delete(_folder, recursive: true);
        await File.WriteAllTextAsync(_folder, "not a folder", TestContext.Current.CancellationToken);

        var run = await WithAsync(async s => await s.RunAsync(await s.GetSettingsAsync(CancellationToken.None), CancellationToken.None));

        Assert.NotNull(run.Error);
        _factory.StartServer();
        await using var scope = _factory.Services.CreateAsyncScope();
        var events = await scope.ServiceProvider.GetRequiredService<Notifications.NotificationEvents>().CollectAsync(CancellationToken.None);
        Assert.Contains(events, e => e.Kind == Notifications.NotificationKind.BackupFailed);
    }

    [Fact]
    public async Task RunIfDue_RunsOncePerDayAfterTheConfiguredTime()
    {
        Assert.True((await WithAsync(s => s.SaveAsync(new ScheduledBackupSettings { Enabled = true, Folder = _folder, TimeOfDay = new TimeSpan(2, 0, 0) }, Password, "admin", CancellationToken.None))).Succeeded);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 1, 0, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.Utc);

        Assert.False(await RunIfDueAsync(time));
        time.SetUtcNow(new DateTimeOffset(2026, 10, 20, 2, 30, 0, TimeSpan.Zero));
        Assert.True(await RunIfDueAsync(time));
        Assert.False(await RunIfDueAsync(time));
        time.SetUtcNow(new DateTimeOffset(2026, 10, 21, 1, 59, 0, TimeSpan.Zero));
        Assert.False(await RunIfDueAsync(time));
        time.SetUtcNow(new DateTimeOffset(2026, 10, 21, 2, 0, 0, TimeSpan.Zero));
        Assert.True(await RunIfDueAsync(time));
    }

    [Fact]
    public async Task Page_SavesTheSchedule()
    {
        await _factory.CreateUserAsync("admin", "correct horse battery", Administration.AdminRoles.Admin);
        using var admin = new DashboardClient(_factory);
        using (await admin.LoginAsync("admin", "correct horse battery"))
        {
        }

        var form = new Dictionary<string, string>
        {
            ["_handler"] = "backup-schedule", ["ScheduleAction"] = "save", ["ScheduleEnabled"] = "yes", ["Folder"] = _folder,
            ["Time"] = "03:30", ["Keep"] = "10", ["SchedulePassword"] = Password, ["SchedulePasswordRepeat"] = Password,
        };
        using (var saved = await admin.PostFormAsync("/administration/backup", form))
        {
            Assert.Contains("The scheduled backup was saved.", await saved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        var settings = await WithAsync(s => s.GetSettingsAsync(CancellationToken.None));
        Assert.Equal(new ScheduledBackupSettings { Enabled = true, Folder = _folder, TimeOfDay = new TimeSpan(3, 30, 0), Keep = 10 }, settings);

        form["SchedulePasswordRepeat"] = "something else entirely";
        form["Folder"] = _folder + "-changed";
        form["Time"] = "04:00";
        form["Keep"] = "12";
        using (var mismatch = await admin.PostFormAsync("/administration/backup", form))
        {
            // The entered values come back (not the saved ones), the passwords do not.
            var html = await mismatch.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("The two passwords differ. Enter the password again.", html, StringComparison.Ordinal);
            Assert.Contains($"value=\"{_folder}-changed\"", html, StringComparison.Ordinal);
            Assert.Contains("<option value=\"04:00\" selected", html, StringComparison.Ordinal);
            Assert.Contains("value=\"12\"", html, StringComparison.Ordinal);
            Assert.DoesNotContain(Password, html, StringComparison.Ordinal);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        var root = Path.GetDirectoryName(_folder)!;
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private async Task<bool> RunIfDueAsync(TimeProvider time)
    {
        _factory.StartServer();
        await using var scope = _factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var schedule = new ScheduledBackup(
            services.GetRequiredService<ServerDbContext>(),
            services.GetRequiredService<BackupService>(),
            services.GetRequiredService<IOptions<ScheduledBackupOptions>>(),
            services.GetRequiredService<AuditLog>(),
            time,
            NullLogger<ScheduledBackup>.Instance);
        return await schedule.RunIfDueAsync(CancellationToken.None);
    }

    private async Task<T> WithAsync<T>(Func<ScheduledBackup, Task<T>> action)
    {
        _factory.StartServer();
        await using var scope = _factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<ScheduledBackup>());
    }
}
