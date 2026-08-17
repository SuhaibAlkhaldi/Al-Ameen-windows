using CompanyDlp.Contracts;

namespace CompanyDlp.Service;

// Single source of truth for the "<commit> built <timestamp UTC>" build-identity string, so the
// --version switch (Program.cs) and the Information-level startup log line (DlpWorker) can never say
// different things. Values come from policy.json's Backend section (BuildCommit/BuildTimestampUtc),
// stamped there at build/deploy time by scripts\build-portable-agent-package.ps1 (portable installs)
// and scripts\install-production.ps1 (from-source installs) via `git rev-parse --short HEAD` - the
// same commit/timestamp also written to VERSION.txt at the root of a portable package, so all three
// places (log, --version, VERSION.txt) always agree for a given build.
public static class BuildIdentity
{
    public static string Describe(DlpPolicy policy)
    {
        var commit = string.IsNullOrWhiteSpace(policy.Backend.BuildCommit) ? "unknown-commit" : policy.Backend.BuildCommit;
        var timestamp = string.IsNullOrWhiteSpace(policy.Backend.BuildTimestampUtc) ? "unknown-time" : policy.Backend.BuildTimestampUtc;
        return $"{commit} built {timestamp}";
    }
}
