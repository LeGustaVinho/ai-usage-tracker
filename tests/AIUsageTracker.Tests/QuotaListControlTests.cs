using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AIUsageTracker.Models;
using AIUsageTracker.ViewModels;
using AIUsageTracker.Views.Controls;

namespace AIUsageTracker.Tests;

public class QuotaListControlTests
{
    [Fact]
    public Task ResolvesTheMatchingTooltipForWeeklyAndSessionQuotas()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var weekly = CreateBar("claude.weekly", "Claude semanal");
                var session = CreateBar("claude.session", "Claude sessão");
                var control = new QuotaListControl
                {
                    DataContext = new { QuotaCards = QuotaCardViewModel.Group([weekly, session], new HashSet<string>()) }
                };
                control.Measure(new Size(350, double.PositiveInfinity));
                control.Arrange(new Rect(control.DesiredSize));
                control.UpdateLayout();

                Assert.Equal(weekly.ToolTipText, FindTooltipForTitle(control, weekly.Title));
                Assert.Equal(session.ToolTipText, FindTooltipForTitle(control, session.Title));
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static QuotaBarViewModel CreateBar(string id, string title) => new(new QuotaItem
    {
        Id = id,
        Title = title,
        Provider = QuotaProviderType.ClaudeCode,
        ProviderBadge = "CLAUDE",
        BadgeColorHex = "#3B82F6",
        UsedPercent = 35
    }, new AppSettings { CompactMode = true });

    private static object? FindTooltipForTitle(QuotaListControl control, string title)
    {
        var label = Assert.Single(Descendants(control).OfType<TextBlock>(), text => text.Text == title);
        for (DependencyObject? visual = label; visual != null; visual = VisualTreeHelper.GetParent(visual))
        {
            var tooltip = ToolTipService.GetToolTip(visual);
            if (tooltip != null)
            {
                return tooltip;
            }
        }

        return null;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }
}
