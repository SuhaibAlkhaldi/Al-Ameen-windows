using System.Reflection;
using System.Text.Json;
using CompanyDlp.Contracts;

namespace CompanyDlp.Service;

public sealed class AgentIdentityProvider(PolicyStore policyStore, ILogger<AgentIdentityProvider> logger)
{
    private readonly object _sync = new();
    private AgentIdentity? _identity;

    public AgentIdentity Get()
    {
        lock (_sync)
        {
            return _identity ??= LoadOrCreate();
        }
    }

    private AgentIdentity LoadOrCreate()
    {
        var policy = policyStore.Get();
        var root = policy.Runtime.Mode.Equals("Production", StringComparison.OrdinalIgnoreCase)
            ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var directory = Path.Combine(root, "CompanyDlp", "Agent");
        var path = Path.Combine(directory, "identity.json");

        try
        {
            if (File.Exists(path))
            {
                var existing = JsonSerializer.Deserialize<AgentIdentity>(File.ReadAllText(path), JsonDefaults.Options);
                if (existing is not null && existing.DeviceId != Guid.Empty)
                {
                    existing.TenantId = policy.Backend.TenantId != Guid.Empty ? policy.Backend.TenantId : existing.TenantId;
                    existing.MachineName = Environment.MachineName;
                    existing.AgentVersion = GetAgentVersion();
                    return existing;
                }
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not read the existing Company DLP agent identity.");
        }

        Directory.CreateDirectory(directory);
        var created = new AgentIdentity
        {
            TenantId = policy.Backend.TenantId,
            DeviceId = Guid.NewGuid(),
            MachineName = Environment.MachineName,
            AgentVersion = GetAgentVersion()
        };
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(created, JsonDefaults.Options));
        File.Move(temporaryPath, path, true);
        return created;
    }

    private static string GetAgentVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
}
