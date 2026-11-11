using System;
using System.Collections.Generic;
using System.Linq;
using AIUsageTracker.Models;

namespace AIUsageTracker.ViewModels;

public sealed class QuotaCardViewModel : ObservableObject
{
    private readonly ISet<string> _collapsedSessions;

    public QuotaBarViewModel Quota { get; }
    public IReadOnlyList<QuotaBarViewModel> SessionQuotas { get; }
    public QuotaBarViewModel? SessionPreview { get; }
    public bool HasSessionQuotas => SessionQuotas.Count > 0;

    public bool IsSessionExpanded
    {
        get => !_collapsedSessions.Contains(Quota.Id);
        set
        {
            bool changed = value ? _collapsedSessions.Remove(Quota.Id) : _collapsedSessions.Add(Quota.Id);
            if (changed)
            {
                OnPropertyChanged();
            }
        }
    }

    private QuotaCardViewModel(QuotaBarViewModel quota, IReadOnlyList<QuotaBarViewModel> sessions,
        ISet<string> collapsedSessions)
    {
        Quota = quota;
        SessionQuotas = sessions;
        SessionPreview = sessions.Where(session => session.IsAvailable).MaxBy(session => session.UsedPercent);
        _collapsedSessions = collapsedSessions;
    }

    internal static IReadOnlyList<QuotaCardViewModel> Group(IEnumerable<QuotaBarViewModel> quotas,
        ISet<string> collapsedSessions)
    {
        var bars = quotas.ToList();
        var sessionsByParent = bars.ToLookup(bar => FindWeeklyParent(bar, bars));
        return sessionsByParent[null]
            .Select(bar => new QuotaCardViewModel(bar, sessionsByParent[bar].ToList(), collapsedSessions))
            .ToList();
    }

    private static QuotaBarViewModel? FindWeeklyParent(QuotaBarViewModel bar, IReadOnlyList<QuotaBarViewModel> bars)
    {
        // Pair the shared windows by quota identity, including older cache entries with no duration.
        string? weeklyId = (bar.Provider, bar.Id) switch
        {
            (QuotaProviderType.ClaudeCode, "claude.session") => "claude.weekly",
            (QuotaProviderType.Antigravity, "agy.gemini_session" or "agy.gemini_flash") => "agy.gemini_weekly",
            (QuotaProviderType.Antigravity, var id) when id.StartsWith("agy.gemini_flash.", StringComparison.Ordinal)
                => "agy.gemini_weekly",
            (QuotaProviderType.Codex, "codex.primary") when bar.WindowDuration == TimeSpan.FromHours(5)
                => "codex.secondary",
            (QuotaProviderType.Codex, "codex.secondary") when bar.WindowDuration == TimeSpan.FromHours(5)
                => "codex.primary",
            _ => null
        };

        return weeklyId == null ? null : bars.FirstOrDefault(parent =>
            parent.Provider == bar.Provider && parent.Id == weeklyId &&
            (bar.Provider != QuotaProviderType.Codex || parent.WindowDuration == TimeSpan.FromDays(7)));
    }
}
