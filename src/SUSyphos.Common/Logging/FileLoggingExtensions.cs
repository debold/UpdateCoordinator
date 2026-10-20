using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Serilog;
using Serilog.Events;

namespace SUSyphos.Common.Logging;

/// <summary>
/// Adds rolling file logs (Serilog) next to the built-in providers, so agent and central server
/// log identically. The Windows event log provider keeps receiving warnings and errors.
/// </summary>
public static class FileLoggingExtensions
{
    public const string FileTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    /// <param name="services">Service collection of the host.</param>
    /// <param name="configuration">Configuration containing the optional "Logging:File" section.</param>
    /// <param name="defaultDirectory">Log folder used when none is configured.</param>
    /// <param name="filePrefix">File name prefix, e.g. "agent" gives agent-20261018.log.</param>
    public static IServiceCollection AddRollingFileLogging(
        this IServiceCollection services,
        IConfiguration configuration,
        string defaultDirectory,
        string filePrefix)
    {
        var options = new FileLoggingOptions();
        configuration.GetSection(FileLoggingOptions.SectionName).Bind(options);
        var validation = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        if (!System.ComponentModel.DataAnnotations.Validator.TryValidateObject(options, new(options), validation, validateAllProperties: true))
        {
            throw new InvalidOperationException("Invalid file logging configuration: " + string.Join(" ", validation.Select(v => v.ErrorMessage)));
        }

        var directory = string.IsNullOrWhiteSpace(options.Directory) ? defaultDirectory : options.Directory;
        var path = Path.Combine(directory, filePrefix + "-.log");

        services.AddSerilog(
            logger => logger
                .MinimumLevel.Is(ToSerilog(options.MinimumLevel))
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
                .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .WriteTo.File(
                    path,
                    outputTemplate: FileTemplate,
                    rollingInterval: RollingInterval.Day,
                    rollOnFileSizeLimit: true,
                    fileSizeLimitBytes: options.FileSizeLimitBytes,
                    retainedFileCountLimit: options.RetainedFileCountLimit,
                    formatProvider: System.Globalization.CultureInfo.InvariantCulture),
            preserveStaticLogger: true,
            writeToProviders: true);

        return services;
    }

    internal static LogEventLevel ToSerilog(LogLevel level) => level switch
    {
        LogLevel.Trace => LogEventLevel.Verbose,
        LogLevel.Debug => LogEventLevel.Debug,
        LogLevel.Information => LogEventLevel.Information,
        LogLevel.Warning => LogEventLevel.Warning,
        LogLevel.Error => LogEventLevel.Error,
        _ => LogEventLevel.Fatal,
    };
}
