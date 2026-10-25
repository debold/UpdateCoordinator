namespace SUSyphos.Server.Components;

/// <summary>One tab of a <see cref="Shared.TabBar"/>: key in the address, label and an optional badge (count or mark) in a status class.</summary>
/// <param name="Key">Value of <c>?tab=</c>.</param>
/// <param name="Label">Shown name.</param>
/// <param name="Badge">Count or mark when something waits there; null for none.</param>
/// <param name="BadgeKind">Status class of the badge: "neutral" for a plain count, "queue" or "fail" for attention.</param>
public sealed record TabItem(string Key, string Label, string? Badge = null, string BadgeKind = "neutral");
