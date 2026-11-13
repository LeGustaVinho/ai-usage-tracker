using System;

namespace AIUsageTracker.Models;

public enum QuotaProviderType
{
    Codex,
    ClaudeCode,
    Antigravity
}

public enum QuotaStatusLevel
{
    Normal,
    Warning,
    Critical,
    Offline
}

public sealed record QuotaItem
{
    public required string Id { get; init; }
    public required QuotaProviderType Provider { get; init; }
    public required string ProviderBadge { get; init; }
    public required string BadgeColorHex { get; init; }
    public required string Title { get; init; }
    public double UsedPercent { get; init; }
    public double RemainingPercent => Math.Clamp(100.0 - UsedPercent, 0.0, 100.0);
    public DateTimeOffset? ResetTime { get; init; }
    public TimeSpan? WindowDuration { get; init; }
    public bool AllowWindowDurationInference { get; init; } = true;
    public string? Subtitle { get; init; }
    public string? SourceDetail { get; init; }
    public bool IsAvailable { get; init; } = true;
    public bool IsCached { get; init; }
    public string[]? AssociatedModels { get; init; }
    public string? UnavailableMessage { get; init; }
    public int SortOrder { get; init; }
}
