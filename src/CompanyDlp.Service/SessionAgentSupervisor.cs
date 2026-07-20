using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using CompanyDlp.Contracts;

namespace CompanyDlp.Service;

public sealed class SessionAgentSupervisor(
    PolicyStore policyStore,
    AuditLogger auditLogger,
    ILogger<SessionAgentSupervisor> logger)
{
    private DateTimeOffset _nextScanAtUtc;
    private DateTimeOffset _nextMissingExecutableWarningAtUtc;

    public async Task TickAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return;
        var policy = policyStore.Get();
        if (!policy.Runtime.Mode.Equals("Production", StringComparison.OrdinalIgnoreCase)
            || !policy.Runtime.PersistentProtection
            || !policy.Runtime.KeepSessionAgentRunning)
            return;

        var now = DateTimeOffset.UtcNow;
        if (now < _nextScanAtUtc) return;
        _nextScanAtUtc = now.AddSeconds(Math.Clamp(policy.Runtime.SessionAgentPollSeconds, 2, 60));

        var executablePath = ResolveSessionAgentPath();
        if (!File.Exists(executablePath))
        {
            if (now >= _nextMissingExecutableWarningAtUtc)
            {
                logger.LogError("Company DLP session agent executable was not found at {Path}.", executablePath);
                _nextMissingExecutableWarningAtUtc = now.AddMinutes(5);
            }
            return;
        }

        var runningSessions = GetRunningAgentSessionIds(executablePath);
        foreach (var session in EnumerateActiveSessions())
        {
            if (runningSessions.Contains(session.SessionId)) continue;
            try
            {
                var user = LaunchInSession(session.SessionId, executablePath);
                await auditLogger.WriteAsync(new AuditEvent
                {
                    ActionKey = ActionKeys.AgentSession,
                    EventType = "SessionAgentStarted",
                    Action = "start-session-agent",
                    Method = "WindowsServiceSupervisor",
                    Result = "succeeded",
                    ReasonCode = "SessionAgentMissingOrTerminated",
                    Details = $"sessionId={session.SessionId}; station={Sanitize(session.StationName)}",
                    SourceProcessName = Path.GetFileName(executablePath),
                    SourceProcessPath = executablePath
                }, new ClientContext
                {
                    UserSid = user.UserSid,
                    Username = user.Username,
                    MachineName = Environment.MachineName,
                    WindowsSessionId = session.SessionId,
                    ClientName = "CompanyDlp.Service"
                }, cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Could not start Company DLP session agent in Windows session {SessionId}.", session.SessionId);
                await auditLogger.WriteAsync(new AuditEvent
                {
                    ActionKey = ActionKeys.AgentSession,
                    EventType = "SessionAgentStartFailed",
                    Action = "start-session-agent",
                    Method = "WindowsServiceSupervisor",
                    Result = "failed",
                    ReasonCode = exception.GetType().Name,
                    Details = $"sessionId={session.SessionId}"
                }, new ClientContext
                {
                    MachineName = Environment.MachineName,
                    WindowsSessionId = session.SessionId,
                    ClientName = "CompanyDlp.Service"
                }, cancellationToken);
            }
        }
    }

    private static HashSet<int> GetRunningAgentSessionIds(string executablePath)
    {
        var expectedName = Path.GetFileNameWithoutExtension(executablePath);
        var result = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName(expectedName))
        {
            using (process)
            {
                try { result.Add(process.SessionId); } catch { }
            }
        }
        return result;
    }

    private static IReadOnlyList<ActiveSession> EnumerateActiveSessions()
    {
        var result = new List<ActiveSession>();
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var buffer, out var count))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enumerate Windows sessions.");

        try
        {
            var itemSize = Marshal.SizeOf<WtsSessionInfo>();
            for (var index = 0; index < count; index++)
            {
                var itemPointer = IntPtr.Add(buffer, index * itemSize);
                var item = Marshal.PtrToStructure<WtsSessionInfo>(itemPointer);
                if (item.State != WtsConnectState.Active) continue;
                var station = Marshal.PtrToStringUni(item.StationName) ?? "";
                result.Add(new ActiveSession(item.SessionId, station));
            }
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
        return result;
    }

    private static SessionUser LaunchInSession(int sessionId, string executablePath)
    {
        if (!WTSQueryUserToken((uint)sessionId, out var userToken))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not obtain the interactive user token.");

        IntPtr primaryToken = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;
        try
        {
            using var identity = new WindowsIdentity(userToken);
            var sessionUser = new SessionUser(identity.User?.Value ?? "", identity.Name ?? "");

            if (!DuplicateTokenEx(
                    userToken,
                    TokenAllAccess,
                    IntPtr.Zero,
                    SecurityImpersonationLevel.Impersonation,
                    TokenType.Primary,
                    out primaryToken))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create a primary session token.");

            if (!CreateEnvironmentBlock(out environment, primaryToken, false))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the session environment.");

            var startup = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>(),
                Desktop = "winsta0\\default"
            };
            var commandLine = new StringBuilder($"\"{executablePath}\"");
            var currentDirectory = Path.GetDirectoryName(executablePath);
            if (!CreateProcessAsUser(
                    primaryToken,
                    executablePath,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    CreateUnicodeEnvironment | CreateNewProcessGroup,
                    environment,
                    currentDirectory,
                    ref startup,
                    out var processInformation))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not start the session agent.");

            CloseHandle(processInformation.Thread);
            CloseHandle(processInformation.Process);
            return sessionUser;
        }
        finally
        {
            if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
            if (primaryToken != IntPtr.Zero) CloseHandle(primaryToken);
            CloseHandle(userToken);
        }
    }

    private static string ResolveSessionAgentPath()
    {
        var configured = Environment.GetEnvironmentVariable("COMPANY_DLP_SESSION_AGENT_EXE");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        var serviceDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(serviceDirectory, "..", "Desktop", "CompanyDlp.Desktop.exe"));
    }

    private static string Sanitize(string value)
    {
        var cleaned = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return cleaned.Length <= 100 ? cleaned : cleaned[..100];
    }

    private const uint TokenAllAccess = 0x000F01FF;
    private const uint CreateNewProcessGroup = 0x00000200;
    private const uint CreateUnicodeEnvironment = 0x00000400;

    private sealed record ActiveSession(int SessionId, string StationName);
    private sealed record SessionUser(string UserSid, string Username);

    private enum WtsConnectState
    {
        Active,
        Connected,
        ConnectQuery,
        Shadow,
        Disconnected,
        Idle,
        Listen,
        Reset,
        Down,
        Init
    }

    private enum SecurityImpersonationLevel
    {
        Anonymous,
        Identification,
        Impersonation,
        Delegation
    }

    private enum TokenType
    {
        Primary = 1,
        Impersonation = 2
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsSessionInfo
    {
        public int SessionId;
        public IntPtr StationName;
        public WtsConnectState State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr Reserved2Pointer;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessions(
        IntPtr server,
        int reserved,
        int version,
        out IntPtr sessionInfo,
        out int count);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(
        IntPtr existingToken,
        uint desiredAccess,
        IntPtr tokenAttributes,
        SecurityImpersonationLevel impersonationLevel,
        TokenType tokenType,
        out IntPtr newToken);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(
        IntPtr token,
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
