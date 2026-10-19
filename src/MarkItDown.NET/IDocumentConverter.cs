namespace MarkItDown.NET;

/// <summary>
/// Converts a document stream to Markdown.
/// Implementations should be stateless and thread-safe.
/// </summary>
public interface IDocumentConverter
{
    /// <summary>File extensions this converter supports (e.g. ".pdf", ".docx").</summary>
    IReadOnlyCollection<string> SupportedExtensions { get; }

    /// <summary>Returns true if this converter can handle the given file extension.</summary>
    bool CanConvert(string extension);

    /// <summary>Converts the document to Markdown.</summary>
    Task<ConvertResult> ConvertAsync(ConvertContext context, CancellationToken ct = default);
}
