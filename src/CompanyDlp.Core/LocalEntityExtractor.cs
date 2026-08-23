using System.Text.RegularExpressions;
using ExtensionsML = Microsoft.ML.Tokenizers;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CompanyDlp.Core;

// In-process C# port of GLiNER's (urchade/gliner_multi-v2.1) inference pipeline, running the ONNX
// export of its span-classification model (see docs/plan and the exported gliner_model.onnx) instead
// of the original Python/PyTorch model_service. Faithfully mirrors three stages of the original
// Python code (gliner.GLiNER.prepare_model_inputs / gliner.data_processing.processor.SpanProcessor /
// gliner.decoding.decoder.SpanDecoder, all read directly from the installed package during porting):
//   1. Split text into words (a simple regex word splitter - GLiNER's own WhitespaceTokenSplitter).
//   2. Build the zero-shot "<<ENT>> label1 <<ENT>> label2 ... <<SEP>> word1 word2 ..." token sequence
//      via the model's real SentencePiece tokenizer (spm.model, loaded as-is - not reimplemented).
//   3. Run the ONNX model, then decode raw span logits into entities (sigmoid + threshold + greedy
//      non-overlap resolution, same as the original flat_ner=True decode path).
public sealed class LocalEntityExtractor : IDisposable
{
    private const int MaxWidth = 12;
    private const int MaxWords = 384;
    private const int MaxTextChars = 4_096;
    // 0.4 (the original Python reference value) missed real entities that scored just under it (e.g.
    // a bare email address at 0.398) - 0.38 catches those near-misses without introducing false
    // positives on plain, non-sensitive text (verified against several sanity-check sentences).
    private const float Threshold = 0.38f;

    private const string EntToken = "<<ENT>>";
    private const string SepToken = "<<SEP>>";
    private const int ClsId = 1;
    private const int SepId = 2;
    private const int EntTokenId = 250103;
    private const int SepTokenId = 250104;

    // Verbatim from model_service/main.py's EN_LABELS/AR_LABELS/LABEL_MAP - the bilingual zero-shot
    // label set the model was prompted with, and the mapping back to canonical DLP entity types.
    private static readonly string[] EnLabels =
    [
        "person name", "phone number", "email address", "passport number",
        "credit card number", "national ID number", "organization", "location"
    ];

    private static readonly string[] ArLabels =
    [
        "اسم شخص", "رقم هاتف", "بريد إلكتروني", "رقم جواز سفر",
        "رقم بطاقة ائتمان", "رقم هوية وطنية", "مؤسسة", "موقع جغرافي"
    ];

    private static readonly string[] AllLabels = [.. EnLabels, .. ArLabels];

    private static readonly Dictionary<string, string> LabelMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["person name"] = "PERSON",
        ["phone number"] = "PHONE",
        ["email address"] = "EMAIL",
        ["passport number"] = "PASSPORT",
        ["credit card number"] = "CREDIT_CARD",
        ["national id number"] = "NATIONAL_ID",
        ["organization"] = "ORGANIZATION",
        ["location"] = "LOCATION",
        ["اسم شخص"] = "PERSON",
        ["رقم هاتف"] = "PHONE",
        ["بريد إلكتروني"] = "EMAIL",
        ["رقم جواز سفر"] = "PASSPORT",
        ["رقم بطاقة ائتمان"] = "CREDIT_CARD",
        ["رقم هوية وطنية"] = "NATIONAL_ID",
        ["مؤسسة"] = "ORGANIZATION",
        ["موقع جغرافي"] = "LOCATION"
    };

    // GLiNER's own WhitespaceTokenSplitter regex, unchanged - verified byte-for-byte identical
    // against the real installed `gliner` package's gliner/data_processing/tokenizer.py source, and
    // cross-checked by running both regexes against the same real sentences and diffing the token
    // boundaries (see StructuredEntityCorrectionTests.cs for the pinned regression case). internal
    // (not private) solely so that test can call it directly without needing the ONNX model loaded.
    internal static readonly Regex WordSplitter = new(@"\w+(?:[-_]\w+)*|\S", RegexOptions.Compiled);

    private readonly ExtensionsML.SentencePieceTokenizer _tokenizer;
    private readonly InferenceSession _session;

    public LocalEntityExtractor(string onnxModelPath, string spmModelPath)
    {
        var specialTokens = new Dictionary<string, int>
        {
            [EntToken] = EntTokenId,
            [SepToken] = SepTokenId
        };

        using var spmStream = File.OpenRead(spmModelPath);
        _tokenizer = ExtensionsML.SentencePieceTokenizer.Create(
            spmStream,
            addBeginningOfSentence: false,
            addEndOfSentence: false,
            specialTokens: specialTokens);

        _session = new InferenceSession(onnxModelPath);
    }

    public List<DetectedEntity> Extract(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        if (text.Length > MaxTextChars) text = text[..MaxTextChars];

        var words = WordSplitter.Matches(text)
            .Select(m => (Text: m.Value, Start: m.Index, End: m.Index + m.Length))
            .ToList();
        if (words.Count == 0) return [];
        if (words.Count > MaxWords) words = words[..MaxWords];

        // "<<ENT>> label1 <<ENT>> label2 ... <<SEP>>" - one prompt "word" per list element, matching
        // BaseProcessor.prepare_inputs exactly (ent_token and each label are separate elements).
        var promptItems = new List<string>();
        foreach (var label in AllLabels)
        {
            promptItems.Add(EntToken);
            promptItems.Add(label);
        }
        promptItems.Add(SepToken);
        var promptLength = promptItems.Count;

        var inputIds = new List<long> { ClsId };
        var wordsMask = new List<long> { 0 };

        var wordIndex = 0;
        foreach (var item in promptItems)
        {
            AppendItem(item, wordIndex, promptLength, inputIds, wordsMask);
            wordIndex++;
        }
        foreach (var (wordText, _, _) in words)
        {
            AppendItem(wordText, wordIndex, promptLength, inputIds, wordsMask);
            wordIndex++;
        }

        inputIds.Add(SepId);
        wordsMask.Add(0);

        var seqLength = inputIds.Count;
        var numWords = words.Count;
        var numClasses = AllLabels.Length;
        var numSpans = numWords * MaxWidth;

        var attentionMask = Enumerable.Repeat(1L, seqLength).ToArray();
        var spanIdx = new long[numSpans * 2];
        var spanMask = new bool[numSpans];
        var spanIndex = 0;
        for (var start = 0; start < numWords; start++)
        {
            for (var width = 0; width < MaxWidth; width++)
            {
                var end = start + width;
                spanIdx[spanIndex * 2] = start;
                spanIdx[spanIndex * 2 + 1] = end;
                spanMask[spanIndex] = end <= numWords - 1;
                spanIndex++;
            }
        }

        var inputIdsTensor = new DenseTensor<long>(inputIds.ToArray(), [1, seqLength]);
        var attentionMaskTensor = new DenseTensor<long>(attentionMask, [1, seqLength]);
        var wordsMaskTensor = new DenseTensor<long>(wordsMask.ToArray(), [1, seqLength]);
        var spanIdxTensor = new DenseTensor<long>(spanIdx, [1, numSpans, 2]);
        var spanMaskTensor = new DenseTensor<bool>(spanMask, [1, numSpans]);
        var textLengthsTensor = new DenseTensor<long>(new long[] { numWords }, [1, 1]);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMaskTensor),
            NamedOnnxValue.CreateFromTensor("words_mask", wordsMaskTensor),
            NamedOnnxValue.CreateFromTensor("span_idx", spanIdxTensor),
            NamedOnnxValue.CreateFromTensor("span_mask", spanMaskTensor),
            NamedOnnxValue.CreateFromTensor("text_lengths", textLengthsTensor)
        };

        using var results = _session.Run(inputs);
        var logits = results.First().AsTensor<float>();

        var spans = DecodeSpans(logits, numWords, numClasses);

        // Independent of the model entirely - see StructuredEntityValidator for why a plain regex
        // beats GLiNER specifically for full email addresses, and why this runs against the raw text
        // (not the model's word/span decode) using character offsets available here in Extract()
        // before spans are collapsed into offset-less DetectedEntity values.
        var emailMatches = StructuredEntityValidator.FindEmails(text);

        var entities = new List<DetectedEntity>();
        var seen = new HashSet<(string Type, string Value)>();
        foreach (var span in spans)
        {
            var charStart = words[span.Start].Start;
            var charEnd = words[span.End].End;

            // Any model span touching a real email address is either the address itself (split into
            // fragments) or noise inside it - the regex match below already covers this region more
            // accurately, so drop the model's guess here rather than keep it alongside a duplicate/
            // conflicting EMAIL entity.
            if (emailMatches.Any(e => StructuredEntityValidator.RangesOverlap(charStart, charEnd, e.Start, e.End)))
            {
                continue;
            }

            var value = text[charStart..charEnd].Trim();
            if (value.Length == 0) continue;

            var label = AllLabels[span.ClassIndex];
            var type = LabelMap.GetValueOrDefault(label, label.ToUpperInvariant());

            // Luhn-based reclassification only ever applies to what the model already called one of
            // these two - see StructuredEntityValidator.ReclassifyDigitEntityType.
            if (type is "CREDIT_CARD" or "NATIONAL_ID")
            {
                type = StructuredEntityValidator.ReclassifyDigitEntityType(value);
            }

            if (seen.Add((type, value.ToLowerInvariant())))
            {
                entities.Add(new DetectedEntity(type, value));
            }
        }

        foreach (var (value, _, _) in emailMatches)
        {
            if (seen.Add(("EMAIL", value.ToLowerInvariant())))
            {
                entities.Add(new DetectedEntity("EMAIL", value));
            }
        }

        return entities;
    }

    private void AppendItem(string item, int wordIndex, int promptLength, List<long> inputIds, List<long> wordsMask)
    {
        IReadOnlyList<int> ids = item switch
        {
            EntToken => [EntTokenId],
            SepToken => [SepTokenId],
            _ => _tokenizer.EncodeToIds(item, addBeginningOfSentence: false, addEndOfSentence: false,
                considerPreTokenization: true, considerNormalization: true)
        };

        // Same rule as BaseProcessor.prepare_word_mask: the FIRST sub-token of a prompt word gets
        // mask 0, the first sub-token of a real document word gets its 1-based word index, and every
        // continuation sub-token after the first gets 0.
        var maskValue = wordIndex < promptLength ? 0L : wordIndex - promptLength + 1L;
        var first = true;
        foreach (var id in ids)
        {
            inputIds.Add(id);
            wordsMask.Add(first ? maskValue : 0L);
            first = false;
        }
    }

    // Direct port of SpanDecoder.decode + BaseDecoder.greedy_search/has_overlapping (flat_ner=True,
    // multi_label=False path only - the only mode model_service ever used).
    private static List<(int Start, int End, int ClassIndex, float Score)> DecodeSpans(Tensor<float> logits, int numWords, int numClasses)
    {
        var candidates = new List<(int Start, int End, int ClassIndex, float Score)>();
        for (var start = 0; start < numWords; start++)
        {
            for (var width = 0; width < MaxWidth; width++)
            {
                var end = start + width;
                if (end >= numWords) continue;

                for (var c = 0; c < numClasses; c++)
                {
                    var logit = logits[0, start, width, c];
                    var probability = 1f / (1f + MathF.Exp(-logit));
                    if (probability > Threshold)
                    {
                        candidates.Add((start, end, c, probability));
                    }
                }
            }
        }

        // greedy_search: highest-confidence span wins, later spans overlapping an already-accepted
        // one are dropped (flat_ner - no nested/overlapping entities).
        var accepted = new List<(int Start, int End, int ClassIndex, float Score)>();
        foreach (var candidate in candidates.OrderByDescending(c => c.Score))
        {
            var overlaps = accepted.Any(a => !(candidate.Start > a.End || a.Start > candidate.End));
            if (!overlaps) accepted.Add(candidate);
        }

        return [.. accepted.OrderBy(a => a.Start)];
    }

    public void Dispose() => _session.Dispose();
}
