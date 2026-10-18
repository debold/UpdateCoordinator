using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.Extensions.Options;

using UpdateCoordinator.Contracts.Agents;

namespace UpdateCoordinator.Agent.Inventory;

/// <summary>
/// Decides when the inventory is collected and sent: right after start, then every
/// <see cref="InventoryOptions.CollectInterval"/>; it is sent only if it changed or
/// <see cref="InventoryOptions.MaxSendInterval"/> has passed since the last successful send.
/// </summary>
internal sealed class InventoryScheduler(IOptions<InventoryOptions> options, TimeProvider timeProvider)
{
    private DateTimeOffset? _lastCollected;
    private DateTimeOffset? _lastSent;
    private byte[]? _lastSentHash;
    private byte[]? _pendingHash;

    public bool IsCollectionDue => _lastCollected is null || timeProvider.GetUtcNow() - _lastCollected >= options.Value.CollectInterval;

    /// <summary>Records a collection and returns whether the result must be sent.</summary>
    public bool ShouldSend(InventoryRequest inventory)
    {
        var now = timeProvider.GetUtcNow();
        _lastCollected = now;
        _pendingHash = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(inventory));

        return _lastSent is null
            || _lastSentHash is null
            || !_pendingHash.AsSpan().SequenceEqual(_lastSentHash)
            || now - _lastSent >= options.Value.MaxSendInterval;
    }

    /// <summary>Called after the central server accepted the inventory.</summary>
    public void MarkSent()
    {
        _lastSent = timeProvider.GetUtcNow();
        _lastSentHash = _pendingHash;
    }

    /// <summary>Called when sending failed; the next cycle collects and sends again.</summary>
    public void MarkSendFailed() => _lastCollected = null;

    /// <summary>Forces a new send with the next cycle, e.g. after a new registration.</summary>
    public void Reset()
    {
        _lastCollected = null;
        _lastSent = null;
        _lastSentHash = null;
    }
}
