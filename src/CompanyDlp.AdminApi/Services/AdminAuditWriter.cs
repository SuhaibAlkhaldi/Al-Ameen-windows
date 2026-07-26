using System.Text.Json;
using CompanyDlp.AdminApi.Data;
using CompanyDlp.AdminApi.Domain;
using CompanyDlp.AdminApi.Security;
using CompanyDlp.Contracts;

namespace CompanyDlp.AdminApi.Services;

public sealed class AdminAuditWriter(CompanyDlpDbContext db, IHttpContextAccessor httpContextAccessor)
{
    public async Task WriteAsync(
        Guid tenantId,
        string action,
        string targetType,
        string targetId,
        object? details,
        CancellationToken cancellationToken)
    {
        var http = httpContextAccessor.HttpContext;
        Guid? adminId = null;
        var email = "system";
        if (http?.User.Identity?.IsAuthenticated == true)
        {
            try
            {
                adminId = AdminClaims.GetAdminUserId(http.User);
                email = AdminClaims.GetEmail(http.User);
            }
            catch
            {
                // Preserve the audit event even if claims are malformed.
            }
        }

        db.AdminAuditLogs.Add(new AdminAuditLogEntity
        {
            TenantId = tenantId,
            AdminUserId = adminId,
            AdminEmail = email,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            DetailsJson = JsonSerializer.Serialize(details ?? new { }, JsonDefaults.Options),
            IpAddress = http?.Connection.RemoteIpAddress?.ToString() ?? "",
            OccurredAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}
