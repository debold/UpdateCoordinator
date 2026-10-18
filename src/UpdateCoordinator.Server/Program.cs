using UpdateCoordinator.Common;
using UpdateCoordinator.Common.Logging;
using UpdateCoordinator.Contracts;
using UpdateCoordinator.Server;
using UpdateCoordinator.Server.Administration;
using UpdateCoordinator.Server.Agents;
using UpdateCoordinator.Server.Components;
using UpdateCoordinator.Server.Trust;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddWindowsService(options => options.ServiceName = "UpdateCoordinator.Server");
builder.Services.AddRollingFileLogging(builder.Configuration, Path.Combine(DataPaths.Server, "Logs"), "server");
builder.Services.AddCentralServer(builder.Configuration);
builder.Services.AddDashboard(builder.Configuration);

var app = builder.Build();

await app.Services.InitializeCentralServerAsync().ConfigureAwait(false);
await app.Services.InitializeDashboardAsync().ConfigureAwait(false);

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
app.MapStaticAssets();
app.MapRazorComponents<App>();

await app.RunAsync().ConfigureAwait(false);

/// <summary>Entry point; public so integration tests can host the application.</summary>
public partial class Program;
