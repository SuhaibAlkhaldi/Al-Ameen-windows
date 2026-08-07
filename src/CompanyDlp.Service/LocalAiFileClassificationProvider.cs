using CompanyDlp.Contracts;
using CompanyDlp.Core;

namespace CompanyDlp.Service;

// Fully local replacement for the old network-based AI classification path: text extraction
// (DocumentTextExtractor), entity detection (LocalEntityExtractor's in-process ONNX model) and rule
// evaluation (DictionaryEvaluator, against the admin-configured DictionaryRuleStore) all happen in
// this same process - no network call, no external process, on the employee's machine.
public sealed class LocalAiFileClassificationProvider(
    LocalEntityExtractor entityExtractor,
    DictionaryRuleStore dictionaryRuleStore) : IFileClassificationProvider
{
    public string Name => FileClassificationProviders.AiApi;

    public async Task<FileClassificationResult> ClassifyAsync(
        FileClassificationRequest request,
        Stream? fileContent,
        CancellationToken cancellationToken)
    {
        if (fileContent is null)
        {
            return new FileClassificationResult
            {
                RequestId = request.RequestId,
                IsAllowed = false,
                IsSensitive = true,
                Classification = "ProviderUnavailable",
                ReasonCode = "NoFileContentAvailableForAiClassification",
                Provider = Name,
                EvaluatedAtUtc = DateTimeOffset.UtcNow
            };
        }

        var extension = string.IsNullOrWhiteSpace(request.Extension)
            ? Path.GetExtension(request.FileName)
            : request.Extension;

        if (!DocumentTextExtractor.IsSupported(extension))
        {
            // A permanent verdict for this file type (matches the old backend's HTTP 400 "extension
            // not allowed" branch) - not a transient failure, so the background scanner must not keep
            // retrying it every tick (see FileClassificationReasonCodes.UnsupportedReasonCodes).
            var nowUtc = DateTimeOffset.UtcNow;
            return new FileClassificationResult
            {
                RequestId = request.RequestId,
                IsAllowed = true,
                IsSensitive = false,
                Classification = "Unclassified",
                ReasonCode = FileClassificationReasonCodes.AiFileTypeRejected,
                Provider = Name,
                EvaluatedAtUtc = nowUtc,
                ValidUntilUtc = nowUtc.AddMinutes(10)
            };
        }

        // Entity extraction runs the ONNX model synchronously (CPU-bound, no I/O) - offloaded onto a
        // thread pool thread so it never blocks the caller's async context.
        var (severity, reasoning) = await Task.Run(() =>
        {
            var text = DocumentTextExtractor.ExtractText(fileContent, extension);
            var entities = entityExtractor.Extract(text);
            var rules = dictionaryRuleStore.Get().Rules;
            var evaluation = DictionaryEvaluator.Evaluate(entities, rules, text);
            return (evaluation.Severity, evaluation.Reasoning);
        }, cancellationToken);

        var isPublic = severity.Equals(ClassificationTiers.Public, StringComparison.OrdinalIgnoreCase);
        return new FileClassificationResult
        {
            RequestId = request.RequestId,
            IsAllowed = isPublic,
            IsSensitive = !isPublic,
            Classification = severity,
            ReasonCode = reasoning,
            Provider = Name,
            EvaluatedAtUtc = DateTimeOffset.UtcNow
        };
    }
}
