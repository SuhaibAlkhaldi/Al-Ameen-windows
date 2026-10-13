using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CompanyDlp.Contracts;

namespace CompanyDlp.Service;

// Phase 8: shared machinery for every desktop recipient observer that attaches to a WebView2-hosted app's real DOM
// over the Chrome DevTools Protocol (CDP), instead of Windows UI Automation (UIA was tried first, for WhatsApp
// Desktop, and found to expose nothing - see WhatsAppDesktopObserver's header comment for that investigation).
//
// Unlike WhatsApp Desktop (always exactly one page target, confirmed live), Microsoft Teams can have several CDP
// page targets open at once under one process - one per signed-in tenant's main window (teams.microsoft.com for
// work/school, teams.live.com for personal - confirmed live 2026-10-13, both open simultaneously under the same
// account) plus one per pop-out chat window the user has floated into its own window. A send can happen in any of
// them, so this base class discovers and attaches to every matching target concurrently, not just the first one
// found, and periodically re-discovers so a newly opened pop-out or tenant window is picked up without a restart.
//
// One reconciliation tick (every TickInterval): check the policy gate, make sure the shared dynamic-CDP-port
// environment variable is set (WebView2DebugPort - shared machine-wide, not per-app, see its header comment for
// why a fixed port cannot be used once more than one WebView2 app is being observed), find the watched process,
// read its actual CDP port from its DevToolsActivePort file, list its current page targets, start a session for
// every matching target not already attached, and stop sessions whose target disappeared (window closed) or whose
// process is gone.
public abstract class WebViewDesktopObserver(
    PolicyStore policyStore,
    FileTransferOutbox fileTransferOutbox,
    ILogger logger) : BackgroundService
{
    private const string BindingName = "dlpNotify";
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(3);
    private static readonly HttpClient DiscoveryClient = new() { Timeout = TimeSpan.FromSeconds(2) };

    private readonly ConcurrentDictionary<string, (CancellationTokenSource Cts, Task Task)> _sessions = new();

    protected abstract string ProcessName { get; }
    protected abstract string PackageFamilyName { get; }
    protected abstract string Channel { get; }
    protected abstract string InjectedScript { get; }
    protected abstract bool IsPolicyEnabled(DlpPolicy policy);
    protected abstract bool MatchesTarget(string url);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("{Channel} observer starting.", Channel);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var policy = policyStore.Get();
                if (!policy.Enabled || !IsPolicyEnabled(policy))
                {
                    await StopAllSessionsAsync();
                    await DelayAsync(stoppingToken);
                    continue;
                }

                WebView2DebugPort.EnsureDynamicPortEnabled(logger);

                using var process = FindProcess();
                if (process is null)
                {
                    await StopAllSessionsAsync();
                    await DelayAsync(stoppingToken);
                    continue;
                }

                var port = WebView2DebugPort.TryGetActivePort(PackageFamilyName, logger);
                logger.LogInformation("{Channel}: process {Pid} found, port {Port}.", Channel, process.Id, port);
                if (port is not null)
                {
                    await ReconcileSessionsAsync(port.Value, stoppingToken);
                    logger.LogInformation("{Channel}: {Count} session(s) attached.", Channel, _sessions.Count);
                }

                await DelayAsync(stoppingToken);
            }
        }
        finally
        {
            await StopAllSessionsAsync();
        }
    }

    private static async Task DelayAsync(CancellationToken token)
    {
        try { await Task.Delay(TickInterval, token); } catch (OperationCanceledException) { }
    }

    private async Task ReconcileSessionsAsync(int port, CancellationToken stoppingToken)
    {
        var targets = await DiscoverMatchingTargetsAsync(port, stoppingToken);

        foreach (var targetId in _sessions.Keys.ToArray())
        {
            if (targets.ContainsKey(targetId)) continue;
            if (_sessions.TryRemove(targetId, out var stale))
            {
                stale.Cts.Cancel();
                await stale.Task;
                stale.Cts.Dispose();
            }
        }

        foreach (var (targetId, webSocketUrl) in targets)
        {
            if (_sessions.ContainsKey(targetId)) continue;
            var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _sessions[targetId] = (cts, RunSessionAsync(webSocketUrl, cts.Token));
        }
    }

    private async Task StopAllSessionsAsync()
    {
        foreach (var targetId in _sessions.Keys.ToArray())
        {
            if (_sessions.TryRemove(targetId, out var session))
            {
                session.Cts.Cancel();
                await session.Task;
                session.Cts.Dispose();
            }
        }
    }

    private async Task<Dictionary<string, string>> DiscoverMatchingTargetsAsync(int port, CancellationToken cancellationToken)
    {
        var matches = new Dictionary<string, string>();
        try
        {
            var json = await DiscoveryClient.GetStringAsync($"http://127.0.0.1:{port}/json", cancellationToken);
            using var document = JsonDocument.Parse(json);
            foreach (var target in document.RootElement.EnumerateArray())
            {
                var type = target.TryGetProperty("type", out var typeProperty) ? typeProperty.GetString() : null;
                var url = target.TryGetProperty("url", out var urlProperty) ? urlProperty.GetString() : null;
                if (type != "page" || url is null || !MatchesTarget(url)) continue;

                var id = target.TryGetProperty("id", out var idProperty) ? idProperty.GetString() : null;
                var webSocketUrl = target.TryGetProperty("webSocketDebuggerUrl", out var wsProperty) ? wsProperty.GetString() : null;
                if (id is not null && webSocketUrl is not null) matches[id] = webSocketUrl;
            }
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "{Channel}'s CDP endpoint on port {Port} is not reachable yet.", Channel, port);
        }
        return matches;
    }

    // One full connect -> inject -> listen cycle for a single CDP target. Returns normally (without throwing) once
    // the session is cancelled (its target closed, or its process gone - decided by the reconciliation loop above).
    private async Task RunSessionAsync(string webSocketUrl, CancellationToken sessionToken)
    {
        try
        {
            using var client = new ClientWebSocket();
            await client.ConnectAsync(new Uri(webSocketUrl), sessionToken);
            logger.LogInformation("{Channel}: CDP connected to {Url}.", Channel, webSocketUrl);

            await SendCommandAsync(client, 1, "Runtime.enable", sessionToken);
            await SendCommandAsync(client, 2, "Runtime.addBinding", new { name = BindingName }, sessionToken);
            await SendCommandAsync(client, 3, "Page.addScriptToEvaluateOnNewDocument", new { source = InjectedScript }, sessionToken);
            await SendCommandAsync(client, 4, "Runtime.evaluate", new { expression = InjectedScript }, sessionToken);
            logger.LogInformation("{Channel}: injection commands sent.", Channel);

            var buffer = new byte[65536];
            while (!sessionToken.IsCancellationRequested && client.State == WebSocketState.Open)
            {
                using var messageStream = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await client.ReceiveAsync(buffer, sessionToken);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    messageStream.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                var text = Encoding.UTF8.GetString(messageStream.ToArray());
                await HandleMessageAsync(text, sessionToken);
            }
        }
        catch (OperationCanceledException) when (sessionToken.IsCancellationRequested)
        {
            // Expected: the reconciliation loop cancels this session once its target disappears.
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "{Channel} CDP session ended.", Channel);
        }
    }

    private async Task HandleMessageAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (!root.TryGetProperty("method", out var methodProperty) || methodProperty.GetString() != "Runtime.bindingCalled") return;
            if (!root.TryGetProperty("params", out var paramsProperty)) return;
            if (!paramsProperty.TryGetProperty("name", out var nameProperty) || nameProperty.GetString() != BindingName) return;
            if (!paramsProperty.TryGetProperty("payload", out var payloadProperty)) return;

            var payload = payloadProperty.GetString();
            if (string.IsNullOrWhiteSpace(payload)) return;

            var notice = JsonSerializer.Deserialize<FileTransferObservationNotice>(payload, JsonDefaults.Options);
            if (notice is null || notice.Channel != Channel) return;

            await fileTransferOutbox.EnqueueAsync(notice, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not process a {Channel} CDP message.", Channel);
        }
    }

    private Process? FindProcess()
    {
        var processes = Process.GetProcessesByName(ProcessName);
        if (processes.Length == 0) return null;

        for (var i = 1; i < processes.Length; i++) processes[i].Dispose();
        return processes[0];
    }

    private static Task SendCommandAsync(ClientWebSocket client, int id, string method, CancellationToken cancellationToken) =>
        SendJsonAsync(client, new { id, method }, cancellationToken);

    private static Task SendCommandAsync<TParams>(ClientWebSocket client, int id, string method, TParams parameters, CancellationToken cancellationToken) =>
        SendJsonAsync(client, new { id, method, @params = parameters }, cancellationToken);

    private static async Task SendJsonAsync<T>(ClientWebSocket client, T payload, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        await client.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }
}
