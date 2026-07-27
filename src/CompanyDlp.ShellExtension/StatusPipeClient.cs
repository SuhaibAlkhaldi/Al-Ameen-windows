using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Web.Script.Serialization;
using CompanyDlp.Contracts;

namespace CompanyDlp.ShellExtension
{
    // Mirrors CompanyDlp.Desktop\Services\PipeClient.cs's connection pattern (same pipe name, same
    // DlpRequest/DlpResponse JSON-line protocol) - deliberately a separate, synchronous-style client
    // rather than a shared reference, because this one runs inside explorer.exe's process (loaded
    // in-proc for the file Properties "DLP" tab, see DlpPropertySheetHandler) and must never risk
    // hanging the host process.
    //
    // An earlier version of this client wrapped the call in Task.Run(...).Wait(timeout) to enforce a
    // deadline, on the theory that giving up on the CALLING thread after a fixed time was enough. It
    // wasn't: if Connect ever took longer than expected, the abandoned background Task kept running
    // orphaned in the thread pool, still blocked on the pipe. That was discovered while diagnosing an
    // unrelated, since-abandoned IQueryInfo/InfoTip approach, but the lesson applies here too -
    // NamedPipeClientStream.Connect already enforces its own bounded wait natively, so trust that
    // directly instead of layering a second, leakier timeout mechanism on top of it.
    //
    // Deliberately uses System.Web.Extensions' JavaScriptSerializer instead of System.Text.Json for
    // the wire JSON, even though CompanyDlp.Contracts (and every other client of this pipe protocol)
    // uses System.Text.Json throughout. This isn't stylistic: a class library loaded directly by
    // mscoree.dll in-proc by explorer.exe (as this one is) runs inside explorer's own default
    // AppDomain, which only ever consults explorer.exe's own config - never a sibling
    // CompanyDlp.ShellExtension.dll.config, no matter how it's wired into the build. A binding
    // redirect placed in that config for System.Text.Json is therefore silently never applied, and
    // the moment any code path here touched System.Text.Json (directly, or transitively through
    // DlpRequest/DlpResponse's JsonElement-typed Data property) explorer.exe's Properties dialog
    // failed with a FileNotFoundException the instant the DLP tab was opened. JavaScriptSerializer
    // ships inside the .NET Framework itself (System.Web.Extensions, GAC-resident on every machine
    // with .NET Framework 4.x) - no NuGet package, no version to redirect, ever. So this client
    // builds/parses the envelope as plain Dictionary<string, object> and never references
    // DlpRequest/DlpResponse (whose Data property is a System.Text.Json.JsonElement) at all.
    internal static class StatusPipeClient
    {
        private const int TimeoutMilliseconds = 400;

        public static FileClassificationStatusResponse? Query(string filePath)
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(".", PipeNames.Policy, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Impersonation))
                {
                    pipe.Connect(TimeoutMilliseconds);

                    var serializer = new JavaScriptSerializer();
                    var envelope = new Dictionary<string, object>
                    {
                        ["protocolVersion"] = "1.0",
                        ["messageId"] = Guid.NewGuid().ToString(),
                        ["sentAtUtc"] = DateTimeOffset.UtcNow.ToString("o"),
                        ["type"] = DlpMessageTypes.GetFileClassificationStatus,
                        ["context"] = BuildContext(),
                        ["data"] = new Dictionary<string, object> { ["filePath"] = filePath }
                    };

                    using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
                    using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true))
                    {
                        writer.WriteLine(serializer.Serialize(envelope));
                        var line = reader.ReadLine();
                        if (string.IsNullOrWhiteSpace(line)) return null;

                        var response = serializer.Deserialize<Dictionary<string, object>>(line);
                        if (response is null || !response.TryGetValue("success", out var successObj) || !(successObj is bool success) || !success)
                        {
                            return null;
                        }

                        if (!response.TryGetValue("data", out var dataObj) || !(dataObj is Dictionary<string, object> data))
                        {
                            return null;
                        }

                        return new FileClassificationStatusResponse
                        {
                            FilePath = GetString(data, "filePath") ?? filePath,
                            Status = GetString(data, "status") ?? FileClassificationStatuses.NotScanned,
                            Classification = GetString(data, "classification") ?? "Unclassified",
                            LastScannedAtUtc = GetDateTimeOffset(data, "lastScannedAtUtc")
                        };
                    }
                }
            }
            catch
            {
                // Never let a pipe/connection failure escape - the caller (GetInfo, a COM entry
                // point loaded in-proc by explorer.exe) must degrade to "no tooltip" rather than
                // throw out of unmanaged code.
                return null;
            }
        }

        private static string? GetString(Dictionary<string, object> data, string key)
        {
            return data.TryGetValue(key, out var value) ? value as string : null;
        }

        private static DateTimeOffset? GetDateTimeOffset(Dictionary<string, object> data, string key)
        {
            var raw = GetString(data, key);
            return raw is not null && DateTimeOffset.TryParse(raw, out var parsed) ? parsed : (DateTimeOffset?)null;
        }

        private static Dictionary<string, object> BuildContext()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                return new Dictionary<string, object>
                {
                    ["userSid"] = identity?.User?.Value ?? "",
                    ["username"] = identity?.Name ?? Environment.UserDomainName + "\\" + Environment.UserName,
                    ["machineName"] = Environment.MachineName,
                    ["windowsSessionId"] = Process.GetCurrentProcess().SessionId,
                    ["clientName"] = "CompanyDlp.ShellExtension",
                    ["clientVersion"] = "1.0.0"
                };
            }
        }
    }
}
