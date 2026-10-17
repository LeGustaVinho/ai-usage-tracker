using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIUsageTracker.Models;

namespace AIUsageTracker.Services.Providers;

public interface IQuotaProvider
{
    QuotaProviderType ProviderType { get; }
    Task<IReadOnlyList<QuotaItem>> FetchQuotasAsync(AppSettings settings, CancellationToken cancellationToken = default);
}
