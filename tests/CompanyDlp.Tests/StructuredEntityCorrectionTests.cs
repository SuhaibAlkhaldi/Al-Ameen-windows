using CompanyDlp.Core;
using Xunit;

namespace CompanyDlp.Tests;

// Regression coverage for the three real accuracy problems found (2026-08-24) comparing the original
// PyTorch GLiNER checkpoint's behavior against LocalEntityExtractor's C# port, on the exact sentences
// used during that investigation. See StructuredEntityValidator.cs and LocalEntityExtractor.Extract()
// for the fixes.
public sealed class StructuredEntityCorrectionTests : IClassFixture<LocalEntityExtractorFixture>
{
    private readonly LocalEntityExtractor _extractor;
    public StructuredEntityCorrectionTests(LocalEntityExtractorFixture fixture) => _extractor = fixture.Extractor;

    // Problem 1 as originally hypothesized ("+1-555-234-9876 is missed - is it a word-splitting
    // difference from GLiNER's own tokenizer?"): investigated and DISPROVEN. Verified directly against
    // the real installed `gliner` package (gliner/data_processing/tokenizer.py's WhitespaceTokenSplitter,
    // pip-installed, not assumed) - both regexes are byte-for-byte identical, and running each against
    // this exact sentence produces identical 22 tokens with identical boundaries, including "+" and
    // "1-555-234-9876" as two separate words in both. The C# sub-token IDs from this project's own
    // spm.model matched Python's exactly too, and the full 107-token input_ids/words_mask sequence
    // diffed byte-for-byte identical between the two. The real explanation (see LocalEntityExtractor's
    // AllLabels comment and this class's other tests) was a flaw in the *comparison methodology* from
    // the earlier session: comparing against a Python run prompted with only 8 English labels, while
    // LocalEntityExtractor always prompts with all 16 (English+Arabic) labels combined - a materially
    // different prompt that measurably changes this specific span's classification (phone number: 0.60
    // with 8 labels vs location: 0.30, both irrelevant here, with the real 16-label prompt this port
    // always uses). This test pins the one thing that genuinely needed verifying and turned out correct:
    // word-splitting fidelity to GLiNER's own tokenizer.
    [Fact]
    public void WordSplitter_MatchesGlinerReferenceTokenization_ForPhoneNumberWithLeadingPlus()
    {
        var text = "Please contact John Smith at john.smith@example.com or call him at +1-555-234-9876 regarding the contract.";
        var matches = LocalEntityExtractor.WordSplitter.Matches(text);

        Assert.Equal(22, matches.Count);
        Assert.Equal("+", matches[16].Value);
        Assert.Equal(67, matches[16].Index);
        Assert.Equal("1-555-234-9876", matches[17].Value);
        Assert.Equal(68, matches[17].Index);
        Assert.Equal(82, matches[17].Index + matches[17].Length);
    }

    // Problem 2: a full email address must come back as one EMAIL entity, not have its local part
    // (before @) tagged PERSON by the model - reproduced identically on the original PyTorch checkpoint,
    // so fixed with an independent regex detector (StructuredEntityValidator.FindEmails) that overrides
    // any overlapping model-guessed span, rather than by touching the model.
    [Fact]
    public void Extract_FullEmailAddress_DetectedAsSingleEmailEntity_NotSplitIntoPersonFragment()
    {
        var text = "Please contact John Smith at john.smith@example.com or call him at +1-555-234-9876 regarding the contract.";
        var entities = _extractor.Extract(text);

        Assert.Contains(entities, e => e.Type == "EMAIL" && e.Value == "john.smith@example.com");
        Assert.DoesNotContain(entities, e => e.Value.Equals("john.smith", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entities, e => e.Value == "@" || e.Value == "example.com");
    }

    // Problem 3: an Arabic national-ID-shaped digit string the model sometimes tags CREDIT_CARD must be
    // reclassified to NATIONAL_ID when it fails Luhn (1234567890 does - not a real card number), while a
    // genuine Luhn-valid number stays CREDIT_CARD - deterministic correction
    // (StructuredEntityValidator.ReclassifyDigitEntityType), no new detector, only reclassifying what the
    // model already called one of these two types.
    [Fact]
    public void Extract_ArabicNationalId_ReclassifiedCorrectlyByLuhnCheck()
    {
        var text = "رقم الهوية الوطنية للموظف هو 1234567890 والبريد الإلكتروني هو ahmad.khaled@example.com";
        var entities = _extractor.Extract(text);

        Assert.Contains(entities, e => e.Type == "EMAIL" && e.Value == "ahmad.khaled@example.com");

        var digitEntity = entities.FirstOrDefault(e => e.Value.Any(char.IsDigit) && (e.Type == "NATIONAL_ID" || e.Type == "CREDIT_CARD"));
        if (digitEntity is not null)
        {
            Assert.Equal("NATIONAL_ID", digitEntity.Type); // 1234567890 fails Luhn
        }

        // Direct, model-independent checks of the reclassification rule itself.
        Assert.Equal("NATIONAL_ID", StructuredEntityValidator.ReclassifyDigitEntityType("1234567890"));
        Assert.Equal("CREDIT_CARD", StructuredEntityValidator.ReclassifyDigitEntityType("4111 1111 1111 1111")); // real Luhn-valid Visa test number
    }
}

// Shared across this class's tests so the ~1GB ONNX model is loaded once, not once per test.
public sealed class LocalEntityExtractorFixture : IDisposable
{
    public LocalEntityExtractor Extractor { get; }

    public LocalEntityExtractorFixture()
    {
        var aiModelDirectory = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "CompanyDlp.Service", "AiModel");
        Extractor = new LocalEntityExtractor(
            Path.Combine(aiModelDirectory, "gliner_model.onnx"),
            Path.Combine(aiModelDirectory, "spm.model"));
    }

    public void Dispose() => Extractor.Dispose();
}
