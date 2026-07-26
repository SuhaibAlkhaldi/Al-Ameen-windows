using CompanyDlp.Contracts;

namespace CompanyDlp.Service;

public interface IFileClassificationProvider
{
    string Name { get; }
    Task<FileClassificationResult> ClassifyAsync(
        FileClassificationRequest request,
        Stream? fileContent,
        CancellationToken cancellationToken);
}

public sealed class BlockAllFileClassificationProvider : IFileClassificationProvider
{
    public string Name => FileClassificationProviders.BlockAll;

    public Task<FileClassificationResult> ClassifyAsync(
        FileClassificationRequest request,
        Stream? fileContent,
        CancellationToken cancellationToken) =>
        Task.FromResult(new FileClassificationResult
        {
            RequestId = request.RequestId,
            IsAllowed = false,
            IsSensitive = true,
            Classification = "Sensitive",
            ReasonCode = "BlockAllUntilAiProviderAvailable",
            Provider = Name,
            EvaluatedAtUtc = DateTimeOffset.UtcNow
        });
}

public sealed class AiApiFileClassificationProvider(BackendApiClient backendApiClient) : IFileClassificationProvider
{
    public string Name => FileClassificationProviders.AiApi;

    // Real, content-based classification needs the file's actual bytes - without them there is
    // nothing to send the AI API, so this fails closed immediately rather than guessing from
    // metadata alone (which is exactly the gap BlockAll already covers for that case).
    public Task<FileClassificationResult> ClassifyAsync(
        FileClassificationRequest request,
        Stream? fileContent,
        CancellationToken cancellationToken) =>
        fileContent is null
            ? Task.FromResult(new FileClassificationResult
            {
                RequestId = request.RequestId,
                IsAllowed = false,
                IsSensitive = true,
                Classification = "ProviderUnavailable",
                ReasonCode = "NoFileContentAvailableForAiClassification",
                Provider = Name,
                EvaluatedAtUtc = DateTimeOffset.UtcNow
            })
            : backendApiClient.ClassifyFileAsync(request, fileContent, cancellationToken);
}

public sealed class FileClassificationService(
    PolicyStore policyStore,
    AgentIdentityProvider identityProvider,
    BlockAllFileClassificationProvider blockAllProvider,
    AiApiFileClassificationProvider aiApiProvider,
    ILogger<FileClassificationService> logger)
{
    public async Task<FileClassificationResult> ClassifyAsync(
        FileClassificationRequest request,
        ClientContext context,
        CancellationToken cancellationToken,
        Stream? fileContent = null)
    {
        var policy = policyStore.Get().FileClassification;
        var identity = identityProvider.Get();
        request.TenantId = identity.TenantId;
        request.DeviceId = identity.DeviceId;
        request.UserSid = context.UserSid;
        request.Extension = string.IsNullOrWhiteSpace(request.Extension)
            ? Path.GetExtension(request.FileName)
            : request.Extension;

        if (!policy.Enabled)
        {
            return new FileClassificationResult
            {
                RequestId = request.RequestId,
                IsAllowed = true,
                IsSensitive = false,
                Classification = "NotEvaluated",
                ReasonCode = "FileClassificationDisabled",
                Provider = "Disabled"
            };
        }

        if (request.SizeBytes < 0 || request.SizeBytes > policy.MaximumFileSizeBytes)
        {
            return new FileClassificationResult
            {
                RequestId = request.RequestId,
                IsAllowed = false,
                IsSensitive = true,
                Classification = "PolicyBlocked",
                ReasonCode = "FileSizeOutsideClassificationLimit",
                Provider = policy.Provider
            };
        }

        IFileClassificationProvider provider = policy.Provider.Equals(
            FileClassificationProviders.AiApi,
            StringComparison.OrdinalIgnoreCase)
            ? aiApiProvider
            : blockAllProvider;

        try
        {
            return await provider.ClassifyAsync(request, fileContent, cancellationToken);
        }
        catch (OperationCanceledException exception) when (policy.FailClosed && !cancellationToken.IsCancellationRequested)
        {
            return CreateFailClosedResult(request, provider, exception);
        }
        catch (Exception exception) when (policy.FailClosed)
        {
            return CreateFailClosedResult(request, provider, exception);
        }
    }

    private FileClassificationResult CreateFailClosedResult(
        FileClassificationRequest request,
        IFileClassificationProvider provider,
        Exception exception)
    {
        logger.LogWarning(exception, "File classification provider {Provider} failed; fail-closed decision applied.", provider.Name);
        return new FileClassificationResult
        {
            RequestId = request.RequestId,
            IsAllowed = false,
            IsSensitive = true,
            Classification = "ProviderUnavailable",
            ReasonCode = "ClassificationProviderUnavailableFailClosed",
            Provider = provider.Name,
            EvaluatedAtUtc = DateTimeOffset.UtcNow
        };
    }
}
