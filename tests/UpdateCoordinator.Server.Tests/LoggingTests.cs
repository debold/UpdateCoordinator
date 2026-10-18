using System.Net.Http.Json;

using UpdateCoordinator.Contracts;
using UpdateCoordinator.Contracts.Agents;
using UpdateCoordinator.TestSupport;

namespace UpdateCoordinator.Server.Tests;

public sealed class LoggingTests
{
    [Fact]
    public async Task Registration_IsLoggedToFile_WithoutClientSecret()
    {
        string secret;
        string logDirectory;
        await using (var factory = new ServerFactory())
        {
            logDirectory = factory.LogDirectory;
            using var client = factory.CreateClient();
            using var response = await client.PostAsJsonAsync(
                new Uri(ApiRoutes.AgentRegister, UriKind.Relative),
                RegisterTests.ValidRequest("SRV-LOGGING"),
                TestContext.Current.CancellationToken);
            secret = (await response.Content.ReadFromJsonAsync<RegisterResponse>(TestContext.Current.CancellationToken))!.ClientSecret;

            // The file sink writes unbuffered, so the entry is on disk once the request has completed.
            var content = ReadLogs(logDirectory);

            Assert.Contains("SRV-LOGGING", content, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, content, StringComparison.Ordinal);
        }
    }

    private static string ReadLogs(string directory)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var file in Directory.GetFiles(directory, "server-*.log"))
        {
            // The sink keeps the file open; share it for reading.
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            builder.Append(reader.ReadToEnd());
        }

        return builder.ToString();
    }
}
