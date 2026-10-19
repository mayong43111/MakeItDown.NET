namespace MarkItDown.NET;

/// <summary>Input context for document conversion.</summary>
public record ConvertContext
{
    /// <summary>Original filename.</summary>
    public required string Filename { get; init; }

    /// <summary>Lowercase file extension including dot (e.g. ".pdf").</summary>
    public required string Extension { get; init; }

    /// <summary>Document content stream. Caller is responsible for disposing.</summary>
    public required Stream Content { get; init; }

    /// <summary>Optional locale hint for OCR (e.g. "zh-hans", "en-US").</summary>
    public string? Locale { get; init; }
}
