using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using CompanyDlp.Contracts;
using CompanyDlp.Core;
using Microsoft.Identity.Client;

namespace CompanyDlp.Service;

// Phase 8: Outlook recipient observation, for any Outlook client (classic, new, web, mobile) - not just
// desktop - via Microsoft Graph, since every other approach tried for "new Outlook for Windows" specifically
// (CDP, UIA) hit a dead end. See OutlookGraphObservationPolicy's header comment for that investigation.
//
// Confirmed live 2026-10-13, via Microsoft Graph Explorer, using a personal Microsoft account with no
// Exchange Online and no tenant admin:
//   - GET /me/mailFolders('sentitems')/messages returns real sent messages with real toRecipients (and, per
//     the Graph message resource, ccRecipients/bccRecipients the same way - Bcc is new: no other channel in
//     this system can see it, since it has no DOM/UI representation to read).
//   - GET /me/messages/{id}/attachments returns real file attachments with a contentBytes property - the
//     actual file content, base64-encoded - enough to hash exactly the way every other channel already does.
//
// This observer polls Sent Items (a message only appears there once Outlook/Exchange has actually accepted
// it for delivery - stronger evidence than every other channel's "the send button was clicked", which is
// why RecipientEvidence_EmailSentConfirmed is its own evidence type on the backend, not a reuse of
// EMAIL_IN_SEND_FORM) rather than intercepting the compose/send action live, so detection lags the real send
// by one poll interval (PollSeconds) instead of being instant - acceptable since Phase 8 is observation, not
// blocking.
//
// Sign-in is delegated OAuth (MSAL device code flow, one time): on first run, or whenever no cached sign-in
// is found, LogWarning prints the verification URL and user code a human must enter at
// https://microsoft.com/devicelogin - there is no UI in this service to show it any other way. The signed-in
// token is cached to disk (OutlookGraphTokenCache, DPAPI-encrypted) so this only happens once per machine
// per account, surviving service restarts.
public sealed class OutlookGraphObserver(
    PolicyStore policyStore,
    FileTransferOutbox fileTransferOutbox,
    IMachineDataProtector protector,
    IHttpClientFactory httpClientFactory,
    ILogger<OutlookGraphObserver> logger) : BackgroundService
{
    private const string Channel = "OUTLOOK_GRAPH";
    private const string RecipientKind = "EMAIL";
    private const string RecipientEvidenceType = "EMAIL_SENT_CONFIRMED";
    private static readonly string[] Scopes = ["Mail.Read"];

    private static string StateDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CompanyDlp", "OutlookGraph");
    private static string WatermarkPath => Path.Combine(StateDirectory, "watermark.txt");

    private IPublicClientApplication? _app;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var policy = policyStore.Get().OutlookGraphObservation;
            if (!policyStore.Get().Enabled || !policy.Enabled)
            {
                await DelayAsync(TimeSpan.FromSeconds(10), stoppingToken);
                continue;
            }

            if (string.IsNullOrWhiteSpace(policy.ClientId))
            {
                logger.LogWarning(
                    "OutlookGraphObservation is enabled but no ClientId is configured (register an Azure AD app " +
                    "with delegated Mail.Read and public client flows allowed); the observer will not run.");
                await DelayAsync(TimeSpan.FromMinutes(5), stoppingToken);
                continue;
            }

            try
            {
                var token = await AcquireTokenAsync(policy, stoppingToken);
                if (token is not null)
                {
                    await PollOnceAsync(token, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Outlook Graph polling failed; will retry.");
            }

            var delaySeconds = Math.Clamp(policyStore.Get().OutlookGraphObservation.PollSeconds, 10, 300);
            await DelayAsync(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
        }
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken token)
    {
        try { await Task.Delay(delay, token); } catch (OperationCanceledException) { }
    }

    private async Task<string?> AcquireTokenAsync(OutlookGraphObservationPolicy policy, CancellationToken cancellationToken)
    {
        if (_app is null)
        {
            var builder = PublicClientApplicationBuilder.Create(policy.ClientId);
            builder = policy.TenantId.Equals("common", StringComparison.OrdinalIgnoreCase)
                ? builder.WithAuthority(AadAuthorityAudience.AzureAdAndPersonalMicrosoftAccount)
                : builder.WithAuthority(AzureCloudInstance.AzurePublic, policy.TenantId);
            _app = builder.Build();
            new OutlookGraphTokenCache(protector, logger).Attach(_app.UserTokenCache);
        }

        var accounts = await _app.GetAccountsAsync();
        var account = accounts.FirstOrDefault();
        if (account is not null)
        {
            try
            {
                var silent = await _app.AcquireTokenSilent(Scopes, account).ExecuteAsync(cancellationToken);
                return silent.AccessToken;
            }
            catch (MsalUiRequiredException)
            {
                // Cached sign-in expired or was revoked - fall through to a fresh device-code sign-in below.
            }
        }

        var result = await _app.AcquireTokenWithDeviceCode(Scopes, callback =>
        {
            logger.LogWarning(
                "Outlook Graph observation needs a one-time sign-in. On any device, go to {Url} and enter " +
                "code {Code} within {Expiry} minutes.",
                callback.VerificationUrl, callback.UserCode, (callback.ExpiresOn - DateTimeOffset.UtcNow).TotalMinutes);
            return Task.CompletedTask;
        }).ExecuteAsync(cancellationToken);
        return result.AccessToken;
    }

    private async Task PollOnceAsync(string accessToken, CancellationToken cancellationToken)
    {
        var watermark = LoadWatermark();
        var client = httpClientFactory.CreateClient("CompanyDlp.Graph");
        client.DefaultRequestHeaders.Authorization = new("Bearer", accessToken);

        var filterValue = watermark.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var url = "https://graph.microsoft.com/v1.0/me/mailFolders('sentitems')/messages" +
                  $"?$filter=sentDateTime gt {filterValue}" +
                  "&$select=subject,hasAttachments,sentDateTime,toRecipients,ccRecipients,bccRecipients" +
                  "&$orderby=sentDateTime asc&$top=50";

        using var response = await client.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("value", out var messages)) return;

        var newWatermark = watermark;
        foreach (var message in messages.EnumerateArray())
        {
            var sentDateTime = message.GetProperty("sentDateTime").GetDateTimeOffset();
            if (sentDateTime > newWatermark) newWatermark = sentDateTime;

            if (!message.TryGetProperty("hasAttachments", out var hasAttachmentsProperty) || !hasAttachmentsProperty.GetBoolean())
                continue;

            var messageId = message.GetProperty("id").GetString();
            if (messageId is null) continue;

            var recipients = CollectRecipients(message);
            if (recipients.Count == 0) continue;

            await ReportMessageAttachmentsAsync(client, messageId, sentDateTime, recipients, cancellationToken);
        }

        if (newWatermark > watermark) SaveWatermark(newWatermark);
    }

    private static List<(string Role, string Value)> CollectRecipients(JsonElement message)
    {
        var recipients = new List<(string Role, string Value)>();
        foreach (var (property, role) in new[] { ("toRecipients", "TO"), ("ccRecipients", "CC"), ("bccRecipients", "BCC") })
        {
            if (!message.TryGetProperty(property, out var list)) continue;
            foreach (var recipient in list.EnumerateArray())
            {
                var address = recipient.GetProperty("emailAddress").GetProperty("address").GetString();
                if (!string.IsNullOrWhiteSpace(address)) recipients.Add((role, address));
            }
        }
        return recipients;
    }

    private async Task ReportMessageAttachmentsAsync(
        HttpClient client, string messageId, DateTimeOffset sentDateTime,
        List<(string Role, string Value)> recipients, CancellationToken cancellationToken)
    {
        var url = $"https://graph.microsoft.com/v1.0/me/messages/{messageId}/attachments";
        using var response = await client.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode) return;

        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("value", out var attachments)) return;

        foreach (var attachment in attachments.EnumerateArray())
        {
            // Only real file attachments carry contentBytes to hash. An itemAttachment (a forwarded message)
            // or a referenceAttachment (a OneDrive/SharePoint share link) is not observed - the same
            // "cloud-sharing link isn't seen" gap every other channel already has, documented in their own
            // adapters.
            var odataType = attachment.TryGetProperty("@odata.type", out var typeProperty) ? typeProperty.GetString() : null;
            if (odataType != "#microsoft.graph.fileAttachment") continue;
            if (!attachment.TryGetProperty("contentBytes", out var contentBytesProperty)) continue;

            var fileName = attachment.GetProperty("name").GetString() ?? "";
            var fileSizeBytes = attachment.TryGetProperty("size", out var sizeProperty) ? sizeProperty.GetInt64() : 0;
            var hash = Convert.ToHexString(SHA256.HashData(contentBytesProperty.GetBytesFromBase64())).ToLowerInvariant();

            var sendActionId = Guid.NewGuid();
            for (var ordinal = 0; ordinal < recipients.Count; ordinal++)
            {
                var (role, value) = recipients[ordinal];
                await fileTransferOutbox.EnqueueAsync(new FileTransferObservationNotice
                {
                    SendActionId = sendActionId,
                    RecipientOrdinal = ordinal,
                    Channel = Channel,
                    RecipientRole = role,
                    RecipientKind = RecipientKind,
                    RecipientValue = value,
                    RecipientEvidenceType = RecipientEvidenceType,
                    FileName = fileName,
                    FileSizeBytes = fileSizeBytes,
                    FileHashBytes = hash,
                    ObservedAtUtc = sentDateTime
                }, cancellationToken);
            }
        }
    }

    private static DateTimeOffset LoadWatermark()
    {
        try
        {
            var text = File.ReadAllText(WatermarkPath);
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                return parsed;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        // First run: start from now, not the dawn of the mailbox - we only want sends from here forward.
        return DateTimeOffset.UtcNow;
    }

    private static void SaveWatermark(DateTimeOffset watermark)
    {
        Directory.CreateDirectory(StateDirectory);
        var temporary = WatermarkPath + ".tmp";
        File.WriteAllText(temporary, watermark.ToString("o", CultureInfo.InvariantCulture));
        File.Move(temporary, WatermarkPath, true);
    }
}
