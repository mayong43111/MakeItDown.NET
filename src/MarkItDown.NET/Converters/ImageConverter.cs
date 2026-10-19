using Azure.AI.DocumentIntelligence;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace MarkItDown.NET.Converters;

/// <summary>
/// Converts image files (JPEG, PNG, BMP, TIFF, GIF, WebP) to Markdown via Azure Document Intelligence.
/// </summary>
public class ImageConverter : IDocumentConverter
{
    private const long MaxFileSizeBytes = 500 * 1024 * 1024; // 500 MB (DI limit)
    private const int MaxPixelDimension = 10000; // DI max 10000x10000

    private static readonly string[] Extensions =
        [".jpg", ".jpeg", ".png", ".bmp", ".tiff", ".tif", ".gif", ".webp"];

    private static readonly HashSet<string> SupportedExtensionSet = new(Extensions, StringComparer.OrdinalIgnoreCase);

    private readonly DocumentIntelligenceClient _diClient;
    private readonly ILogger<ImageConverter>? _logger;

    public ImageConverter(DocumentIntelligenceClient diClient, ILogger<ImageConverter>? logger = null)
    {
        _diClient = diClient;
        _logger = logger;
    }

    public IReadOnlyCollection<string> SupportedExtensions => Extensions;

    public bool CanConvert(string extension)
        => SupportedExtensionSet.Contains(extension);

    public async Task<ConvertResult> ConvertAsync(ConvertContext context, CancellationToken ct = default)
    {
        _logger?.LogInformation("Converting image {Filename}, Extension={Extension}", context.Filename, context.Extension);

        var imageBytes = await ReadAllBytesAsync(context.Content, ct);

        if (imageBytes.Length > MaxFileSizeBytes)
            throw new InvalidOperationException(
                $"Image file too large ({imageBytes.Length / 1024 / 1024} MB). Maximum supported: 500 MB.");

        byte[] diBytes = imageBytes;

        ValidateImageDimensions(diBytes, context.Extension);

        var binaryData = BinaryData.FromBytes(diBytes);
        var options = new AnalyzeDocumentOptions("prebuilt-layout", binaryData)
        {
            OutputContentFormat = DocumentContentFormat.Markdown,
        };
        if (!string.IsNullOrEmpty(context.Locale))
            options.Locale = context.Locale;

        var operation = await _diClient.AnalyzeDocumentAsync(
            Azure.WaitUntil.Completed, options, ct);

        var analyzeResult = operation.Value;
        var contentMarkdown = analyzeResult.Content ?? "";
        var pageCount = analyzeResult.Pages?.Count ?? 1;

        _logger?.LogInformation("Image convert complete: {Pages} pages, {ContentLen} chars",
            pageCount, contentMarkdown.Length);

        var md5 = ContentHasher.ComputeMd5(contentMarkdown);

        return new ConvertResult
        {
            Markdown = contentMarkdown,
            ContentMd5 = md5,
            PageCount = pageCount,
        };
    }

    private void ValidateImageDimensions(byte[] imageBytes, string extension)
    {
        try
        {
            using var codec = SKCodec.Create(new MemoryStream(imageBytes));
            if (codec is null) return;

            var info = codec.Info;
            if (info.Width > MaxPixelDimension || info.Height > MaxPixelDimension)
                throw new InvalidOperationException(
                    $"Image dimensions ({info.Width}x{info.Height}) exceed maximum {MaxPixelDimension}x{MaxPixelDimension}.");
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not validate image dimensions for {Extension}", extension);
        }
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }
}
