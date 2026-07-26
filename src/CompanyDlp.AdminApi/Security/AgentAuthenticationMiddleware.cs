using System.Net.Http.Headers;
using CompanyDlp.AdminApi.Data;
using Microsoft.EntityFrameworkCore;

namespace CompanyDlp.AdminApi.Security;

public sealed class AgentAuthenticationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, CompanyDlpDbContext db)
    {
        var path = context.Request.Path;
        if (!path.StartsWithSegments("/api/v1/agent", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Value, "/api/v1/agent/enroll", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        if (!AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization.ToString(), out var authorization)
            || !authorization.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(authorization.Parameter)
            || authorization.Parameter.Length > 512)
        {
            await WriteUnauthorizedAsync(context, "MissingDeviceBearerToken");
            return;
        }

        if (!Guid.TryParse(context.Request.Headers["X-CompanyDlp-TenantId"].ToString(), out var tenantId)
            || !Guid.TryParse(context.Request.Headers["X-CompanyDlp-DeviceId"].ToString(), out var deviceId))
        {
            await WriteUnauthorizedAsync(context, "MissingAgentIdentityHeaders");
            return;
        }

        var tokenHash = TokenUtilities.HashToken(authorization.Parameter);
        var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(
            value => value.Id == deviceId
                && value.TenantId == tenantId
                && value.IsActive
                && db.Tenants.Any(tenant => tenant.Id == tenantId && tenant.IsActive),
            context.RequestAborted);
        if (device is null
            || string.IsNullOrWhiteSpace(device.TokenHashHex)
            || !CryptographicEquals(device.TokenHashHex, tokenHash)
            || device.TokenExpiresAtUtc is null
            || device.TokenExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            await WriteUnauthorizedAsync(context, "InvalidOrExpiredDeviceToken");
            return;
        }

        context.SetAgentContext(new AgentRequestContext(
            tenantId,
            deviceId,
            context.Request.Headers["X-CompanyDlp-AgentVersion"].ToString()));
        await next(context);
    }

    private static bool CryptographicEquals(string left, string right)
    {
        byte[] leftBytes = [];
        byte[] rightBytes = [];
        try
        {
            leftBytes = Convert.FromHexString(left);
            rightBytes = Convert.FromHexString(right);
            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        catch (FormatException)
        {
            return false;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(leftBytes);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    private static Task WriteUnauthorizedAsync(HttpContext context, string code)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return context.Response.WriteAsJsonAsync(new { error = code });
    }
}
