using Azure.AI.DocumentIntelligence;
using MarkItDown.NET.Converters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MarkItDown.NET;

/// <summary>
/// Unified entry point for converting documents to Markdown.
/// Automatically selects the appropriate converter based on file extension.
/// </summary>
public class DocumentConverter
{
    private readonly Dictionary<string, IDocumentConverter> _converters;
    private readonly ILogger<DocumentConverter>? _logger;

    public DocumentConverter(DocumentConverterOptions? options = null, ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<DocumentConverter>();
        _converters = BuildConverterMap(options, loggerFactory);

        _logger?.LogInformation("DocumentConverter initialized with {Count} supported extensions: {Extensions}",
            _converters.Count, string.Join(", ", _converters.Keys.Order()));
    }

    /// <summary>All file extensions this converter can handle.</summary>
    public IReadOnlyCollection<string> SupportedExtensions => _converters.Keys;

    /// <summary>Returns true if the given filename has a supported extension.</summary>
    public bool CanConvert(string filename)
    {
        var ext = Path.GetExtension(filename)?.ToLowerInvariant() ?? "";
        return _converters.ContainsKey(ext);
    }

    /// <summary>Convert a local file to Markdown.</summary>
    public async Task<ConvertResult> ConvertAsync(string filePath, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(filePath);
        return await ConvertAsync(stream, Path.GetFileName(filePath), locale: null, ct);
    }

    /// <summary>
    /// Convert a document stream to Markdown.
    /// Throws <see cref="NotSupportedException"/> if the file extension is not supported.
    /// </summary>
    public async Task<ConvertResult> ConvertAsync(
        Stream stream, string filename, string? locale = null, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(filename)?.ToLowerInvariant() ?? "";

        // Some files carry a legacy Office extension (.ppt/.xls/.doc) but actually
        // contain modern OOXML (a ZIP package) — or vice versa. Selecting a converter
        // purely by extension then routes them to the wrong parser. Sniff the magic
        // bytes and correct the effective extension before dispatch.
        var effectiveExt = SniffEffectiveExtension(stream, ext);
        if (!string.Equals(effectiveExt, ext, StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogInformation(
                "File {Filename} has extension '{Ext}' but its content matches '{EffectiveExt}'; routing by content.",
                filename, ext, effectiveExt);
            ext = effectiveExt;
        }

        if (!_converters.TryGetValue(ext, out var converter))
            throw new NotSupportedException(
                $"File extension '{ext}' is not supported. Supported extensions: {string.Join(", ", _converters.Keys.Order())}");

        _logger?.LogInformation("Converting {Filename} with {Converter}", filename, converter.GetType().Name);

        var context = new ConvertContext
        {
            Filename = filename,
            Extension = ext,
            Content = stream,
            Locale = locale,
        };

        return await converter.ConvertAsync(context, ct);
    }

    // Legacy Office (OLE2) extensions and their modern OOXML counterparts.
    private static readonly Dictionary<string, string> OleToOoxml = new(StringComparer.OrdinalIgnoreCase)
    {
        [".ppt"] = ".pptx",
        [".xls"] = ".xlsx",
        [".doc"] = ".docx",
    };
    private static readonly Dictionary<string, string> OoxmlToOle = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pptx"] = ".ppt",
        [".pptm"] = ".ppt",
        [".xlsx"] = ".xls",
        [".xlsm"] = ".xls",
        [".docx"] = ".doc",
        [".docm"] = ".doc",
    };

    /// <summary>
    /// Returns the extension that matches the stream's actual content for Office
    /// formats, correcting a mislabeled file. OOXML files start with the ZIP magic
    /// (<c>PK\x03\x04</c>); legacy OLE2 files start with <c>D0 CF 11 E0</c>.
    /// Only Office extensions are affected; everything else (and non-seekable or
    /// unrecognized content) is returned unchanged. The stream position is restored.
    /// </summary>
    internal static string SniffEffectiveExtension(Stream stream, string ext)
    {
        var isOle = OleToOoxml.ContainsKey(ext);
        var isOoxml = OoxmlToOle.ContainsKey(ext);
        if (!isOle && !isOoxml)
            return ext;
        if (!stream.CanSeek)
            return ext;

        Span<byte> header = stackalloc byte[4];
        var origin = stream.Position;
        try
        {
            stream.Position = 0;
            // Stream.Read may return fewer bytes than requested even when more are
            // available, so use ReadAtLeast to fill the 4-byte magic without throwing
            // on a short (e.g. empty/truncated) stream.
            var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            if (read < header.Length)
                return ext;
        }
        finally
        {
            stream.Position = origin;
        }

        var isZip = header[0] == 0x50 && header[1] == 0x4B && header[2] == 0x03 && header[3] == 0x04;
        var isOle2 = header[0] == 0xD0 && header[1] == 0xCF && header[2] == 0x11 && header[3] == 0xE0;

        if (isOle && isZip)
            return OleToOoxml[ext];     // .ppt/.xls/.doc that is really OOXML
        if (isOoxml && isOle2)
            return OoxmlToOle[ext];     // .pptx/.xlsx/.docx that is really OLE2

        return ext;
    }

    private static Dictionary<string, IDocumentConverter> BuildConverterMap(
        DocumentConverterOptions? options, ILoggerFactory? lf)
    {
        var map = new Dictionary<string, IDocumentConverter>(StringComparer.OrdinalIgnoreCase);

        // Office formats
        Register(map, new WordConverter(lf?.CreateLogger<WordConverter>()));
        Register(map, new ExcelConverter(lf?.CreateLogger<ExcelConverter>()));
        Register(map, new PowerPointConverter(lf?.CreateLogger<PowerPointConverter>()));
        Register(map, new OutlookConverter(lf?.CreateLogger<OutlookConverter>()));

        // Text formats
        Register(map, new PlainTextConverter(lf?.CreateLogger<PlainTextConverter>()));
        Register(map, new MarkdownConverter(lf?.CreateLogger<MarkdownConverter>()));
        Register(map, new LocalPdfConverter());

        // PDF & Image — require Azure Document Intelligence
        if (!string.IsNullOrEmpty(options?.AzureDiEndpoint))
        {
            DocumentIntelligenceClient diClient;
            if (!string.IsNullOrEmpty(options.AzureDiApiKey))
            {
                diClient = new DocumentIntelligenceClient(
                    new Uri(options.AzureDiEndpoint), new Azure.AzureKeyCredential(options.AzureDiApiKey));
            }
            else
            {
                diClient = new DocumentIntelligenceClient(
                    new Uri(options.AzureDiEndpoint), new Azure.Identity.DefaultAzureCredential());
            }
            Register(map, new PdfConverter(diClient, lf?.CreateLogger<PdfConverter>(), options.PdfRenderWorkerPath));
            Register(map, new ImageConverter(diClient, lf?.CreateLogger<ImageConverter>()));
        }

        foreach (var converter in options?.AdditionalConverters ?? [])
            Register(map, converter);
        return map;
    }

    private static void Register(Dictionary<string, IDocumentConverter> map, IDocumentConverter converter)
    {
        foreach (var ext in converter.SupportedExtensions)
            map[ext] = converter;
    }
}

/// <summary>Configuration for <see cref="DocumentConverter"/>.</summary>
public class DocumentConverterOptions
{
    /// <summary>Azure Document Intelligence endpoint URL.</summary>
    public string? AzureDiEndpoint { get; set; }

    /// <summary>Azure Document Intelligence API key.</summary>
    public string? AzureDiApiKey { get; set; }

    /// <summary>Explicit path to the standalone worker used for PDF asset rendering.</summary>
    public string? PdfRenderWorkerPath { get; set; }

    /// <summary>Optional converters, such as the separately licensed legacy Office module.</summary>
    public IReadOnlyCollection<IDocumentConverter> AdditionalConverters { get; set; } = [];
}

/// <summary>DI registration extension.</summary>
public static class DocumentConverterServiceExtensions
{
    public static IServiceCollection AddDocumentConverter(
        this IServiceCollection services,
        string? azureDiEndpoint = null,
        string? azureDiApiKey = null)
    {
        services.AddSingleton(sp =>
        {
            var options = new DocumentConverterOptions
            {
                AzureDiEndpoint = azureDiEndpoint,
                AzureDiApiKey = azureDiApiKey,
            };
            return new DocumentConverter(options, sp.GetService<ILoggerFactory>());
        });
        return services;
    }
}
