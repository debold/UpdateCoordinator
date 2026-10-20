using System.Text.Json;

using Microsoft.Extensions.Logging;

namespace SUSyphos.Common;

/// <summary>
/// Stores one JSON document in a <see cref="ProtectedFile"/>.
/// </summary>
public sealed partial class ProtectedJsonFile<T>(ProtectedFile file, ILogger logger) where T : class
{
    public string Path => file.Path;

    /// <summary>Returns the stored document, or null if it is missing, unreadable or not valid JSON.</summary>
    public T? Load()
    {
        var content = file.Read();
        if (content is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(content);
        }
        catch (JsonException)
        {
            LogInvalid(file.Path);
            return null;
        }
    }

    public void Save(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        file.Write(JsonSerializer.SerializeToUtf8Bytes(value));
    }

    public void Delete() => file.Delete();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Protected file {Path} does not contain valid data.")]
    private partial void LogInvalid(string path);
}
