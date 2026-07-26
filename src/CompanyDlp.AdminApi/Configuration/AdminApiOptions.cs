namespace CompanyDlp.AdminApi.Configuration;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public string Issuer { get; set; } = "CompanyDlp.AdminApi";
    public string Audience { get; set; } = "CompanyDlp.AdminPortal";
    public string SigningKey { get; set; } = "";
    public int AccessTokenMinutes { get; set; } = 60;
}

public sealed class OnboardingOptions
{
    public const string SectionName = "Onboarding";
    public bool AllowPublicOnboarding { get; set; }
}

public sealed class AgentSecurityOptions
{
    public const string SectionName = "AgentSecurity";
    public int DeviceTokenDays { get; set; } = 90;
    public int EnrollmentCodeMinutes { get; set; } = 30;
}

public sealed class PolicyDeliveryOptions
{
    public const string SectionName = "PolicyDelivery";
    public string Mode { get; set; } = "Production";
    public string PublicBaseUrl { get; set; } = "https://dlp-api.example.com";
    public int SnapshotLifetimeMinutes { get; set; } = 1440;
    public bool AllowUnsignedDevelopmentSnapshots { get; set; }
    public string SigningPrivateKeyPem { get; set; } = "";
    public string SigningPrivateKeyPath { get; set; } = "";
}

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";
    public bool AutoMigrate { get; set; }
}
