using SUSyphos.Common;
using SUSyphos.Common.Logging;
using SUSyphos.Contracts;
using SUSyphos.Server;
using SUSyphos.Server.Administration;
using SUSyphos.Server.AgentPackages;
using SUSyphos.Server.Agents;
using SUSyphos.Server.Components;
using SUSyphos.Server.Trust;

if (args is [RestoreCommand.Verb, ..])
{
    return await RestoreCommand.RunAsync(args).ConfigureAwait(false);
}

// Data of installations from before the rename (UpdateCoordinator) is taken over once; only as a service, never in tests.
var movedLegacyData = Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService()
    && LegacyData.MoveDataFolder(Path.Combine(LegacyData.Root, "Server"), DataPaths.Server);

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddServerRegistry(Microsoft.Win32.Registry.LocalMachine);

builder.Services.AddWindowsService(options => options.ServiceName = "SUSyphos.Server");
builder.Services.AddRollingFileLogging(builder.Configuration, Path.Combine(DataPaths.Server, "Logs"), "server");
builder.Services.AddCentralServer(builder.Configuration);
builder.Services.AddDashboard(builder.Configuration);
if (movedLegacyData)
{
    builder.Services.AddHostedService<LegacyDataNotice>();
}

var app = builder.Build();

app.UseExceptionHandler();
app.UseSecurityHeaders();

// The API description is only exposed in development so production does not reveal its attack surface.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.UseRateLimiter();

app.MapHealthChecks(ApiRoutes.Health);
app.MapAgentEndpoints();
app.MapTrustEndpoints();
app.MapAgentLogEndpoints();
app.MapBackupEndpoints();
app.MapAgentInstallerEndpoints();
app.MapStaticAssets();
app.MapRazorComponents<App>();

await ServerStartup.RunAsync(app, async started =>
{
    await started.Services.InitializeCentralServerAsync().ConfigureAwait(false);
    await started.Services.InitializeDashboardAsync().ConfigureAwait(false);
}).ConfigureAwait(false);
return 0;

/// <summary>Entry point; public so integration tests can host the application.</summary>
public partial class Program;
