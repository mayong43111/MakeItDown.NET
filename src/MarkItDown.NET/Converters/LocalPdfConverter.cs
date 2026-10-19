using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace MarkItDown.NET.Converters;

public sealed class LocalPdfConverter : IDocumentConverter
{
    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".pdf"];
    public bool CanConvert(string extension) => extension is ".pdf";

    public Task<ConvertResult> ConvertAsync(ConvertContext context, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var document = PdfDocument.Open(context.Content);
        if (document.NumberOfPages is < 1 or > 2000)
            throw new InvalidDataException("Local PDF conversion supports 1-2000 pages.");
        var markdown = new StringBuilder();
        var pages = new List<SourcePage>();
        var line = 1;
        foreach (var page in document.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            var text = ContentOrderTextExtractor.GetText(page).ReplaceLineEndings("\n").Trim();
            if (string.IsNullOrWhiteSpace(text))
                throw new PdfTextUnavailableException();
            var section = $"# Page {page.Number}\n\n{text}\n\n";
            var end = line + section.Count(character => character == '\n');
            pages.Add(new(page.Number, line, end - 1));
            markdown.Append(section);
            if (markdown.Length > 32 * 1024 * 1024)
                throw new InvalidDataException("Local PDF text exceeds its size limit.");
            line = end;
        }
        var content = markdown.ToString();
        return Task.FromResult(new ConvertResult
        {
            Markdown = content, ContentMd5 = ContentHasher.ComputeMd5(content),
            PageCount = document.NumberOfPages, SourcePages = pages
        });
    }
}

public sealed class PdfTextUnavailableException() : NotSupportedException(
    "PDF contains a page without extractable text. OCR is required; no cloud conversion was attempted.");
