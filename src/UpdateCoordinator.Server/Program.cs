using UpdateCoordinator.Common;
using UpdateCoordinator.Common.Logging;
using UpdateCoordinator.Contracts;
using UpdateCoordinator.Server;
using UpdateCoordinator.Server.Agents;
using UpdateCoordinator.Server.Trust;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddWindowsService(options => options.ServiceName = "UpdateCoordinator.Server");
builder.Services.AddRollingFileLogging(builder.Configuration, Path.Combine(DataPaths.Server, "Logs"), "server");
builder.Services.AddCentralServer(builder.Configuration);

var app = builder.Build();

await app.Services.InitializeCentralServerAsync().ConfigureAwait(false);

app.UseExceptionHandler();
app.UseStatusCodePages();

// The API description is only exposed in development so production does not reveal its attack surface.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapHealthChecks(ApiRoutes.Health);
app.MapAgentEndpoints();
app.MapTrustEndpoints();

await app.RunAsync().ConfigureAwait(false);

/// <summary>Entry point; public so integration tests can host the application.</summary>
public partial class Program;
