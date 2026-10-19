namespace MarkItDown.NET;

/// <summary>Conversion output: Markdown text + optional extracted assets.</summary>
public record ConvertResult
{
    /// <summary>The document content converted to Markdown.</summary>
    public required string Markdown { get; init; }

    /// <summary>MD5 hash of the Markdown content.</summary>
    public string ContentMd5 { get; init; } = "";

    /// <summary>Number of pages in the source document (1 for non-paginated formats).</summary>
    public int PageCount { get; init; } = 1;
    public IReadOnlyList<SourcePage> SourcePages { get; init; } = [];

    /// <summary>Extracted inline images.</summary>
    public IReadOnlyList<ExtractedImage> Images { get; init; } = [];

    /// <summary>Extracted tables (image crop + optional Markdown representation).</summary>
    public IReadOnlyList<ExtractedTable> Tables { get; init; } = [];

    /// <summary>Extracted figures (image crop + optional caption).</summary>
    public IReadOnlyList<ExtractedFigure> Figures { get; init; } = [];
}

public sealed record SourcePage(int PageNumber, int StartLine, int EndLine);

public record ExtractedImage(string Name, byte[] Data, string ContentType);
public record ExtractedTable(string Name, byte[] ImageData, string? MarkdownContent, string? LlmDescription);
public record ExtractedFigure(string Name, byte[] ImageData, string? Caption, string? LlmDescription);
