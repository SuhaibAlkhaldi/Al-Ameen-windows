using CompanyDlp.AdminApi.Data;
using Microsoft.EntityFrameworkCore;

namespace CompanyDlp.AdminApi.Services;

public sealed class PolicyRevisionService(CompanyDlpDbContext db)
{
    public async Task<long> IncrementAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var affected = await db.Tenants
            .Where(value => value.Id == tenantId && value.IsActive)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.PolicyRevision, value => value.PolicyRevision + 1)
                .SetProperty(value => value.UpdatedAtUtc, now), cancellationToken);
        if (affected != 1) throw new InvalidOperationException("The tenant was not found or is inactive.");
        return await db.Tenants.AsNoTracking()
            .Where(value => value.Id == tenantId)
            .Select(value => value.PolicyRevision)
            .SingleAsync(cancellationToken);
    }
}
