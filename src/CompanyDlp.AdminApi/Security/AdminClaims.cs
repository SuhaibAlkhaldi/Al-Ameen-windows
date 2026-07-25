using System.Security.Claims;

namespace CompanyDlp.AdminApi.Security;

public static class AdminClaims
{
    public static Guid GetTenantId(ClaimsPrincipal user) => ParseRequired(user.FindFirstValue("tenant_id"), "tenant_id");
    public static Guid GetAdminUserId(ClaimsPrincipal user) => ParseRequired(user.FindFirstValue(ClaimTypes.NameIdentifier), "sub");
    public static string GetEmail(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.Email) ?? "unknown";

    private static Guid ParseRequired(string? value, string claimName) =>
        Guid.TryParse(value, out var id) && id != Guid.Empty
            ? id
            : throw new InvalidOperationException($"Authenticated token is missing a valid {claimName} claim.");
}
