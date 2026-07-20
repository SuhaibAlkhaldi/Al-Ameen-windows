namespace CompanyDlp.Contracts;

public sealed class ClientContext
{
    public string UserSid { get; set; } = "";
    public string Username { get; set; } = "";
    public string MachineName { get; set; } = Environment.MachineName;
    public int WindowsSessionId { get; set; }
    public string ClientName { get; set; } = "unknown";
    public string ClientVersion { get; set; } = "1.0.0";
    public int? CallerProcessId { get; set; }
    public string CallerProcessName { get; set; } = "";
    public string CallerProcessPath { get; set; } = "";
}

public sealed class AgentIdentity
{
    public Guid TenantId { get; set; }
    public Guid DeviceId { get; set; }
    public string MachineName { get; set; } = Environment.MachineName;
    public string AgentVersion { get; set; } = "1.0.0";
}
