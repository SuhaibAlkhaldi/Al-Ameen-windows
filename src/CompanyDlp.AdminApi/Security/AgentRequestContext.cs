namespace CompanyDlp.AdminApi.Security;

public sealed record AgentRequestContext(Guid TenantId, Guid DeviceId, string AgentVersion);

public static class AgentRequestContextExtensions
{
    private const string ItemKey = "CompanyDlp.AgentRequestContext";

    public static void SetAgentContext(this HttpContext context, AgentRequestContext value) => context.Items[ItemKey] = value;

    public static AgentRequestContext GetAgentContext(this HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var value) && value is AgentRequestContext agent
            ? agent
            : throw new InvalidOperationException("The request was not authenticated as a Company DLP agent.");
}
