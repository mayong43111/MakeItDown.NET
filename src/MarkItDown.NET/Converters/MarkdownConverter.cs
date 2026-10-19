using System.Text;
using Microsoft.Extensions.Logging;

namespace MarkItDown.NET.Converters;

/// <summary>
/// Pass-through converter for Markdown files.
/// Reads the file content and computes a hash; no transformation needed.
/// </summary>
public class MarkdownConverter : IDocumentConverter
{
    private readonly ILogger<MarkdownConverter>? _logger;

    public MarkdownConverter(ILogger<MarkdownConverter>? logger = null) => _logger = logger;

    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".md", ".markdown"];

    public bool CanConvert(string extension)
        => extension is ".md" or ".markdown";

    public async Task<ConvertResult> ConvertAsync(ConvertContext context, CancellationToken ct = default)
    {
        _logger?.LogInformation("Converting Markdown {Filename}", context.Filename);

        using var reader = new StreamReader(context.Content, Encoding.UTF8);
        var contentMarkdown = await reader.ReadToEndAsync(ct);

        var md5 = ContentHasher.ComputeMd5(contentMarkdown);

        _logger?.LogInformation("Markdown convert complete: {ContentLen} chars, MD5={Md5}",
            contentMarkdown.Length, md5);

        return new ConvertResult
        {
            Markdown = contentMarkdown,
            ContentMd5 = md5,
            PageCount = 1,
        };
    }
}
