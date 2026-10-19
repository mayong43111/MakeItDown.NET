# MarkItDown.NET

Standalone .NET 10 document-to-Markdown library and conversion worker. Extracted
from `AZ\mvr\04_PIKE_RAG\src\pike-agent`, commit
`7f07dda50608effb11882678dbe1c9f894f36732`. The original repository is unchanged.
This extraction does not assign a new license or grant publication rights.
Dependency licenses still apply.

## Scope

- Modern Office, text, Markdown, Outlook converters and Markdown chunking from the original implementation.
- Legacy DOC/XLS/PPT converters are preserved in `src\MarkItDown.NET.LegacyOffice`,
  outside the default solution and worker. They require a separate NPOI/ImageSharp
  license review. After approval, reference that project and pass
  `LegacyOfficeConverters.Create()` as `DocumentConverterOptions.AdditionalConverters`.
  They are not silently substituted for modern formats.
- Text PDFs use PdfPig locally by default, with page-to-Markdown ranges. A page
  without extractable text explicitly fails with OCR required (Worker exit 6),
  including blank or mixed scanned/text PDFs. There is no automatic cloud fallback.
- PDF/image OCR remains available through explicitly enabled Azure Document Intelligence.
- No PIKE application, database, workflow, storage or `Pike.JobRunner` dependency.
- Self-contained build settings and pinned direct package versions.

```powershell
dotnet build .\MarkItDown.NET.slnx
dotnet test .\tests\MarkItDown.NET.Tests
dotnet publish .\src\MarkItDown.NET.Worker -c Release -o .\artifacts\worker
dotnet .\artifacts\worker\MarkItDown.NET.Worker.dll convert C:\input\sample.docx C:\output\sample
```

The output directory must not exist. The caller retains ownership of the source
file. Successful output contains `document.md`, `chunks.json`, extracted assets, and `result.json`.
The worker rejects empty Markdown, oversized ZIP expansion, and invalid asset names.
It never executes Office macros or follows document links.

### Worker protocol v2

`convert <input-path> <new-output-directory> [--filename <original-name>] [--azure]`

`--protocol` prints `2` for deployment compatibility checks.

`result.json` uses camelCase: `format` (2), `converterVersion`, `markdownFile`
(`document.md`), `markdownSha256`, `pageCount`, and `assets` (relative `path`,
`contentType`, `sha256`), `chunksFile` (`chunks.json`) and `chunksSha256`.
The manifest is written last. A zero exit code and a valid
manifest are both required; an output directory without a manifest is not success.
Errors go to stderr with a nonzero exit code. Markdown and source content never
appear in stdout.

`chunks.json` has `format` (1), `algorithm` (`markitdown-structure-v1`),
`markdownSha256` and `chunks`. Each entry has UTF-16 `offset`/`length` into the
normalized LF Markdown, exact 1-based `startLine`/`endLine`, `heading`, and optional
`pageNumber` (local PDFs only). There is no synthesized text in source ranges.
Display headings normalize control characters to spaces and abbreviate after
100 characters with `...`; original source text is never abbreviated.
The existing structural chunker uses 1000 characters/100 overlap, prioritized
separators, surrogate-safe boundaries, metadata heading context, preserved heading
lines and atomic tables/code. Separator text is retained. This algorithm/config
identity must change when persisted source segmentation changes.
Artifacts contain at most 100,000 chunks/16 MiB of chunk metadata and count toward
the 64 MiB output limit. Vector and transport limits belong to the consuming host,
not the converter; oversized atomic blocks are not silently truncated.

Cloud conversion requires the explicit `--azure` flag and
`MARKITDOWN_AZURE_DI_ENDPOINT`; `MARKITDOWN_AZURE_DI_API_KEY` is optional when an
explicitly configured Azure identity is used. Without the flag, inherited Azure
credentials do not enable cloud conversion. Never upload confidential files to an
external service without approval. Cloud integration tests are not run by default.

PDF asset rendering invokes the standalone worker, never the caller's entry
assembly in cloud PDF mode only. Local PdfPig conversion does not use
PDFium/Skia, render images, recover complex layouts or perform OCR.
Native PDFium/Skia support depends on the deployed RID and must be
verified separately, particularly on Windows ARM64. Merely building does not
certify native PDF rendering or OCR.

The worker is a separate process, not an OS sandbox. The embedding application
must apply timeout/resource/permission policy. Office page coordinates and
high-fidelity layout recovery remain unsupported; chunking is not a vector index.
