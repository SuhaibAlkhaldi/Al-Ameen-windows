# AiModel/ - local-only, not committed

This directory holds two large binary files `LocalEntityExtractor` (`src/CompanyDlp.Core/LocalEntityExtractor.cs`) loads directly by path:

- `gliner_model.onnx` (~1.1GB) - ONNX export of the GLiNER span-classification model (`urchade/gliner_multi-v2.1` base, fine-tuned checkpoint).
- `spm.model` (~4.3MB) - the raw SentencePiece tokenizer for `microsoft/mdeberta-v3-base` (the encoder this GLiNER checkpoint was fine-tuned on).

Both are excluded from git by extension (see `.gitignore` - `AiModel/*.onnx` and `AiModel/*.model`), not by ignoring this whole directory, specifically so this README stays tracked.

**Any build machine that runs `dotnet publish`, `scripts/publish.ps1`, or `scripts/build-portable-agent-package.ps1` needs both files sitting in this exact directory first.** `CompanyDlp.Service.csproj`'s existing `CopyToOutputDirectory` rule for this folder then handles the rest automatically - no script changes needed, but the files will not appear from a fresh `git clone` on their own.

## Where the current copies came from

- `gliner_model.onnx` was exported (2026-08-23) from the PyTorch checkpoint at `C:\Users\User\Downloads\ai_dlp_replica\ai_dlp_replica\models\dlp-ner-model` using GLiNER's own `GLiNER.export_to_onnx()` (opset 19). Verified against `src/CompanyDlp.Core/LocalEntityExtractor.cs`'s expected contract: exactly 6 named inputs (`input_ids`, `attention_mask`, `words_mask`, `text_lengths` all int64; `span_idx` int64; `span_mask` bool) and one `logits` output, all with dynamic batch/sequence/span axes - confirmed directly via `onnx.load()` + `onnx.checker.check_model()`, not assumed.
- `spm.model` is the unmodified raw SentencePiece vocab file bundled with `microsoft/mdeberta-v3-base` on Hugging Face (`AutoTokenizer.from_pretrained("microsoft/mdeberta-v3-base").vocab_file`) - this is a different file from `tokenizer.json` in the source checkpoint folder (that one is the fast-tokenizer JSON; this is the raw SentencePiece binary the C# `SentencePieceTokenizer` needs). Verified with `sentencepiece.SentencePieceProcessor().Load()` and a real encode/decode round-trip.

## Accuracy findings from verifying this export (2026-08-23/24) and what was fixed

Three real accuracy issues were found by comparing GLiNER's real Python behavior against the C# port,
and root-caused (not assumed) before fixing anything - see `StructuredEntityCorrectionTests.cs` for the
resulting regression tests. Fixes live in `LocalEntityExtractor.cs` / `StructuredEntityValidator.cs`, not
here - this section exists so a future re-export doesn't need to rediscover any of this.

- **"`+1-555-234-9876` gets missed" turned out NOT to be a word-splitting/tokenization/export bug at all.**
  Verified exhaustively against the real installed `gliner` PyPI package (not assumed): the C# word-
  splitting regex is byte-for-byte identical to `gliner.data_processing.tokenizer.WhitespaceTokenSplitter`
  (confirmed by running both against the same sentence and diffing token boundaries - both produce "+" and
  "1-555-234-9876" as two separate words at the same offsets); the SentencePiece sub-token IDs for those
  words matched exactly; the full 107-token `input_ids`/`words_mask` sequence C# builds is byte-for-byte
  identical to GLiNER's own `prepare_batch()` output; and a raw `session.run()` call in Python (same
  onnxruntime version, bypassing GLiNER's Python wrapper entirely) reproduced the exact same raw logit C#
  computes. The actual explanation: `LocalEntityExtractor` always prompts the model with all 16 labels
  (English + Arabic combined, see `AllLabels`), but the earlier investigation's "ground truth" Python
  comparison used only the 8 English labels for that sentence - a materially different prompt. Re-run with
  the real 16-label prompt, the original PyTorch model *also* scores this span low for every label (its top
  guess is "location" at 0.30, still below threshold) - i.e. C# was already faithfully reproducing the
  model's real behavior; the earlier "0.60, should have been caught" number came from testing with a prompt
  the C# code doesn't actually send. Nothing needed changing for this one. Whether combining EN+AR labels
  into one prompt measurably costs recall (as this example suggests) is a real, separate question worth
  investigating later (e.g. two smaller per-language calls instead of one 16-label call) - deliberately not
  done here, since it changes inference cost/architecture and wasn't what was asked.
- **Full email addresses got split** - the local part before `@` tagged `PERSON`/`اسم شخص` instead of the
  whole address coming back as one `EMAIL` entity. Reproduced identically on the original PyTorch model, so
  a model fine-tuning characteristic, not the export or the port. Fixed with an independent regex email
  detector (`StructuredEntityValidator.FindEmails`) that runs against the raw text and overrides any
  model-guessed span it overlaps - not a model-quality fix, just recognizing that email shape doesn't need
  a neural model's judgment call at all.
- **Arabic national-ID-shaped digit strings sometimes tagged `CREDIT_CARD` instead of `NATIONAL_ID`** - the
  two labels are close in the model's embedding space for a bare digit sequence with weak context. Fixed by
  reclassifying (never newly detecting) any model-tagged `CREDIT_CARD`/`NATIONAL_ID` entity via a real Luhn
  checksum (`StructuredEntityValidator.ReclassifyDigitEntityType`) - Luhn-valid digit sequences of
  plausible card length stay `CREDIT_CARD`, everything else becomes `NATIONAL_ID`.

## 2026-08-25 update: gliner_model.onnx replaced with a new delivery from the AI team

The AI team sent a second export attempt (`Copy of gliner_model.onnx` / `Copy of spm.model` in
Downloads). Verified before swapping in (via `onnxruntime.InferenceSession`, not assumed):

- `spm.model` is byte-for-byte identical (sha256 `13c8d666d62a7bc4ac8f040aab68e942c861f93303156cc28f5c7e885d86d6e3`)
  to the one already here - no change, left in place.
- `gliner_model.onnx` is a genuinely different file (sha256 `2030df6c45651ecbcc8034d5a1b3df8bc3d5775f218a27152c3aeb2a10337ade`,
  vs the previous `db955a08458f71a57bb03c0a88cb4e9b81bb8185a12efc031784b573661faf10`). The 6 required
  inputs (`input_ids`, `attention_mask`, `words_mask`, `span_idx`, `span_mask`, `text_lengths`) match
  the spec exactly in name and dtype, and a synthetic forward pass produced a correctly-shaped
  `[1, num_words, 12, num_classes]` `logits` tensor with no NaNs.
- **Deviation from spec, not blocking but worth flagging back to the AI team**: this export was not
  trimmed to the single `logits` output we asked for - it still has 5 graph outputs (`logits`,
  `prompts_embedding`, and three unnamed intermediate cast/shape tensors), i.e. the export wrapper
  returned its full internal tuple instead of just `logits`. ONNX Runtime happens to list `logits`
  first, so `results.First()` would have worked by luck, but `LocalEntityExtractor.cs` was hardened
  the same day to call `_session.Run(inputs, new[] { "logits" })` and select by name instead, so this
  (or any future export with the same issue) can't silently break or waste compute on the extra
  outputs regardless of graph output order.
- The previous file was backed up to `../AiModel-backup-2026-08-23-preSwap/gliner_model.onnx`
  (sibling of this folder, deliberately outside `AiModel/` so the csproj's
  `<None Include="AiModel\**" .../>` rule doesn't bundle it into publish output) in case this new
  export needs to be rolled back after real-world testing.

## Backup (outside git)

A copy of both files as placed here is saved at:

`C:\Users\User\AiModelBackup\gliner_model.onnx`
`C:\Users\User\AiModelBackup\spm.model`

Keep that folder (or move it somewhere more permanent/shared) - it is the only copy of this exact export
outside this one machine's working tree, and re-running the export requires the original PyTorch checkpoint
under `ai_dlp_replica` plus a Python environment with `torch`/`gliner`/`onnx`/`sentencepiece`/`transformers`
installed.
