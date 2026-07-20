using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Security.Principal;
using CompanyDlp.Contracts;

namespace CompanyDlp.Desktop.Services;

public sealed class PipeClient
{
    public async Task<DlpResponse> SendAsync(string type, object? data = null, int timeoutMilliseconds = 4000, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", PipeNames.Policy, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(timeoutMilliseconds);
            await pipe.ConnectAsync(timeout.Token);

            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            var request = new DlpRequest
            {
                Type = type,
                Context = CreateContext(),
                Data = data is null ? null : JsonSerializer.SerializeToElement(data, JsonDefaults.Options)
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonDefaults.Options));
            var line = await reader.ReadLineAsync(timeout.Token);
            return string.IsNullOrWhiteSpace(line)
                ? DlpResponse.Fail("The DLP service returned an empty response.")
                : JsonSerializer.Deserialize<DlpResponse>(line, JsonDefaults.Options) ?? DlpResponse.Fail("Invalid DLP service response.");
        }
        catch (Exception exception)
        {
            return DlpResponse.Fail(exception.Message);
        }
    }

    private static ClientContext CreateContext()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new ClientContext
        {
            UserSid = identity.User?.Value ?? "",
            Username = identity.Name ?? $"{Environment.UserDomainName}\\{Environment.UserName}",
            MachineName = Environment.MachineName,
            WindowsSessionId = Process.GetCurrentProcess().SessionId,
            ClientName = "CompanyDlp.Desktop",
            ClientVersion = "1.0.0"
        };
    }
}
