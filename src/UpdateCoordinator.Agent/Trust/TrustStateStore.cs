using UpdateCoordinator.Common;
using UpdateCoordinator.Contracts.Trust;

namespace UpdateCoordinator.Agent.Trust;

/// <summary>
/// Persists the trust state between service restarts.
/// </summary>
internal interface ITrustStateStore
{
    TrustState Load();

    void Save(TrustState state);
}

/// <summary>
/// DPAPI-protected trust state; an unreadable or inconsistent file is treated like a first contact.
/// </summary>
internal sealed partial class ProtectedTrustStateStore(string filePath, ILogger<ProtectedTrustStateStore> logger) : ITrustStateStore
{
    /// <summary>Default location: %ProgramData%\UpdateCoordinator\Agent\trust.dat.</summary>
    public static string DefaultPath { get; } = Path.Combine(DataPaths.Agent, "trust.dat");

    private readonly ProtectedJsonFile<TrustState> _file = new(
        new ProtectedFile(filePath, "UpdateCoordinator.Agent.Trust.v1", logger),
        logger);

    public TrustState Load()
    {
        var state = _file.Load();
        if (state is null)
        {
            return TrustState.Empty;
        }

        if (!IsValid(state))
        {
            LogInvalid(filePath);
            return TrustState.Empty;
        }

        return state;
    }

    public void Save(TrustState state) => _file.Save(state);

    /// <summary>Stored values are input too: decode everything once so later code can rely on it.</summary>
    internal static bool IsValid(TrustState state) =>
        state.HighestSerial >= 0
        && IsBase64(state.AnchorPublicKey, maxBytes: TrustBindingFormat.MaxEncodedLength)
        && IsBase64(state.AcceptedCertificateSpkiSha256, exactBytes: TrustBindingFormat.HashLength)
        && (state.LastResetToken is null || state.LastResetToken.Length <= 2048);

    private static bool IsBase64(string? value, int maxBytes = int.MaxValue, int? exactBytes = null)
    {
        if (value is null)
        {
            return true;
        }

        var buffer = new byte[value.Length];
        return value.Length <= TrustBindingFormat.MaxEncodedLength
            && Convert.TryFromBase64String(value, buffer, out var written)
            && written > 0
            && written <= maxBytes
            && (exactBytes is null || written == exactBytes);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stored trust state at {Path} is inconsistent; the next contact is treated as first contact.")]
    private partial void LogInvalid(string path);
}
