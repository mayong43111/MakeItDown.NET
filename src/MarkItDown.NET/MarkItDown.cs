using Microsoft.Extensions.Logging;

namespace MarkItDown.NET;

/// <summary>
/// Main entry point for converting documents to Markdown.
/// Register converters, then call ConvertAsync().
/// </summary>
public class MarkItDown
{
    private readonly List<(IDocumentConverter Converter, float Priority)> _converters = [];
    private readonly ILogger<MarkItDown>? _logger;

    public MarkItDown(ILogger<MarkItDown>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>Register a converter (lower priority value = tried first, default = 0).</summary>
    public MarkItDown RegisterConverter(IDocumentConverter converter, float priority = 0f)
    {
        _converters.Add((converter, priority));
        return this;
    }

    /// <summary>Convert a local file to Markdown.</summary>
    public async Task<ConvertResult> ConvertAsync(string filePath, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(filePath);
        return await ConvertAsync(stream, Path.GetFileName(filePath), locale: null, ct);
    }

    /// <summary>Convert a stream to Markdown.</summary>
    public async Task<ConvertResult> ConvertAsync(
        Stream stream, string filename, string? locale = null,
        CancellationToken ct = default)
    {
        var extension = Path.GetExtension(filename)?.ToLowerInvariant() ?? "";
        var context = new ConvertContext
        {
            Filename = filename,
            Extension = extension,
            Content = stream,
            Locale = locale,
        };
        return await ConvertAsync(context, ct);
    }

    /// <summary>Convert with a pre-built context.</summary>
    public async Task<ConvertResult> ConvertAsync(ConvertContext context, CancellationToken ct = default)
    {
        foreach (var (converter, _) in _converters.OrderBy(c => c.Priority))
        {
            if (converter.CanConvert(context.Extension))
            {
                _logger?.LogInformation("Converting {Filename} ({Extension}) with {Converter}",
                    context.Filename, context.Extension, converter.GetType().Name);
                return await converter.ConvertAsync(context, ct);
            }
        }

        throw new NotSupportedException(
            $"No converter registered for extension '{context.Extension}' (file: {context.Filename})");
    }
}
