using UpdateCoordinator.Contracts;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddWindowsService(options => options.ServiceName = "UpdateCoordinator.Server");
builder.Services.AddHealthChecks();
builder.Services.AddOpenApi();

var app = builder.Build();

// The API description is only exposed in development so production does not reveal its attack surface.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthChecks(ApiRoutes.Health);

await app.RunAsync();

/// <summary>Entry point; public so integration tests can host the application.</summary>
public partial class Program;
