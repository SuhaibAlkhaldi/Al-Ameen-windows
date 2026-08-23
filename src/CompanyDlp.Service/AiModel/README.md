# AiModel/ - local-only, not committed

This directory holds two large binary files `LocalEntityExtractor` (`src/CompanyDlp.Core/LocalEntityExtractor.cs`) loads directly by path:

- `gliner_model.onnx` (~1.1GB) - ONNX export of the GLiNER span-classification model (`urchade/gliner_multi-v2.1` base, fine-tuned checkpoint).
- `spm.model` (~4.3MB) - the raw SentencePiece tokenizer for `microsoft/mdeberta-v3-base` (the encoder this GLiNER checkpoint was fine-tuned on).

Both are excluded from git by extension (see `.gitignore` - `AiModel/*.onnx` and `AiModel/*.model`), not by ignoring this whole directory, specifically so this README stays tracked.

**Any build machine that runs `dotnet publish`, `scripts/publish.ps1`, or `scripts/build-portable-agent-package.ps1` needs both files sitting in this exact directory first.** `CompanyDlp.Service.csproj`'s existing `CopyToOutputDirectory` rule for this folder then handles the rest automatically - no script changes needed, but the files will not appear from a fresh `git clone` on their own.

## Where the current copies came from

- `gliner_model.onnx` was exported (2026-08-23) from the PyTorch checkpoint at `C:\Users\User\Downloads\ai_dlp_replica\ai_dlp_replica\models\dlp-ner-model` using GLiNER's own `GLiNER.export_to_onnx()` (opset 19). Verified against `src/CompanyDlp.Core/LocalEntityExtractor.cs`'s expected contract: exactly 6 named inputs (`input_ids`, `attention_mask`, `words_mask`, `text_lengths` all int64; `span_idx` int64; `span_mask` bool) and one `logits` output, all with dynamic batch/sequence/span axes - confirmed directly via `onnx.load()` + `onnx.checker.check_model()`, not assumed.
- `spm.model` is the unmodified raw SentencePiece vocab file bundled with `microsoft/mdeberta-v3-base` on Hugging Face (`AutoTokenizer.from_pretrained("microsoft/mdeberta-v3-base").vocab_file`) - this is a different file from `tokenizer.json` in the source checkpoint folder (that one is the fast-tokenizer JSON; this is the raw SentencePiece binary the C# `SentencePieceTokenizer` needs). Verified with `sentencepiece.SentencePieceProcessor().Load()` and a real encode/decode round-trip.

## Known limitation found while verifying (not fixed here - out of scope for a model-conversion task)

Manual extraction tests against both the original PyTorch model and this ONNX export (same results on both,
confirming the ONNX export is faithful) showed real recall gaps in `LocalEntityExtractor`'s C# port
specifically:
- A phone number formatted with a leading `+` and hyphens (e.g. `+1-555-234-9876`) was missed entirely in
  C#, even though the underlying model scores it at 0.60 (well above the 0.38 threshold) - a plainer
  format (`555-867-5309`) was caught correctly. Likely a word-splitting boundary difference between the C#
  port's regex splitter and GLiNER's own, not an export problem.
- Full email addresses are frequently split at prediction time - the local part before `@` gets tagged
  `PERSON`/`اسم شخص` instead of the whole address getting tagged `EMAIL`/`بريد إلكتروني`. Reproduced
  identically on the original PyTorch model, so this is a model fine-tuning characteristic, not specific
  to the export or the C# port.
- Arabic national-ID-shaped digit strings were sometimes tagged `CREDIT_CARD` instead of `NATIONAL_ID` -
  the two labels are close in the model's embedding space for a bare digit sequence with weak surrounding
  context.

Worth a follow-up (prompt/threshold tuning, or reconciling the C# word splitter against GLiNER's own), but
deliberately not touched in this change.

## Backup (outside git)

A copy of both files as placed here is saved at:

`C:\Users\User\AiModelBackup\gliner_model.onnx`
`C:\Users\User\AiModelBackup\spm.model`

Keep that folder (or move it somewhere more permanent/shared) - it is the only copy of this exact export
outside this one machine's working tree, and re-running the export requires the original PyTorch checkpoint
under `ai_dlp_replica` plus a Python environment with `torch`/`gliner`/`onnx`/`sentencepiece`/`transformers`
installed.
