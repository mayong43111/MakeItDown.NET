using System.Diagnostics;
using System.Text;
using Azure.AI.DocumentIntelligence;
using Docnet.Core;
using Docnet.Core.Models;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace MarkItDown.NET.Converters;

public class PdfConverter : IDocumentConverter
{
    private const int RenderDpi = 144; // 2× zoom like Python's fitz
    private const double ScalingFactor = RenderDpi / 72.0; // Docnet uses pixels-per-point, not DPI
    private readonly DocumentIntelligenceClient _diClient;
    private readonly ILogger<PdfConverter>? _logger;
    private readonly string? _renderWorkerPath;

    public PdfConverter(DocumentIntelligenceClient diClient, ILogger<PdfConverter>? logger = null,
        string? renderWorkerPath = null)
    {
        _diClient = diClient;
        _logger = logger;
        _renderWorkerPath = renderWorkerPath;
    }

    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".pdf"];

    public bool CanConvert(string extension)
        => extension is ".pdf";

    public async Task<ConvertResult> ConvertAsync(ConvertContext context, CancellationToken ct = default)
    {
        _logger?.LogInformation("Converting PDF {Filename} via Azure Document Intelligence", context.Filename);

        var pdfBytes = await ReadAllBytesAsync(context.Content, ct);
        var binaryData = BinaryData.FromBytes(pdfBytes);

        // ① Call Azure DI
        var options = new AnalyzeDocumentOptions("prebuilt-layout", binaryData)
        {
            OutputContentFormat = DocumentContentFormat.Markdown,
        };
        if (!string.IsNullOrEmpty(context.Locale))
        {
            options.Locale = context.Locale;
        }

        var operation = await _diClient.AnalyzeDocumentAsync(
            Azure.WaitUntil.Completed, options, ct);

        var analyzeResult = operation.Value;
        var contentMarkdown = analyzeResult.Content ?? "";

        var pageCount = analyzeResult.Pages?.Count ?? 0;
        var diTables = analyzeResult.Tables ?? [];
        var diFigures = analyzeResult.Figures ?? [];

        _logger?.LogInformation(
            "PDF convert complete: {Pages} pages, {Tables} tables, {Figures} figures, {ContentLen} chars",
            pageCount, diTables.Count, diFigures.Count, contentMarkdown.Length);

        // ② Render PDF pages + crop tables/figures
        var tables = new List<ExtractedTable>();
        var figures = new List<ExtractedFigure>();

        if (diTables.Count > 0 || diFigures.Count > 0)
        {
            // Collect which pages we need to render
            var neededPages = new HashSet<int>();
            int tablesWithBR = 0, figuresWithBR = 0;
            foreach (var t in diTables)
            {
                if (t.BoundingRegions?.Count > 0)
                {
                    tablesWithBR++;
                    neededPages.Add(t.BoundingRegions[0].PageNumber);
                }
            }
            foreach (var f in diFigures)
            {
                if (f.BoundingRegions?.Count > 0)
                {
                    figuresWithBR++;
                    neededPages.Add(f.BoundingRegions[0].PageNumber);
                }
            }

            _logger?.LogInformation(
                "BoundingRegion stats: {TablesWithBR}/{TotalTables} tables, {FiguresWithBR}/{TotalFigures} figures have bounding regions. Need {PageCount} pages.",
                tablesWithBR, diTables.Count, figuresWithBR, diFigures.Count, neededPages.Count);

            // Build page dimension map from DI (inches)
            var pageInchDims = new Dictionary<int, (float Width, float Height)>();
            if (analyzeResult.Pages is not null)
            {
                foreach (var p in analyzeResult.Pages)
                {
                    if (p.Width.HasValue && p.Height.HasValue)
                        pageInchDims[p.PageNumber] = (p.Width.Value, p.Height.Value);
                }
            }

            _logger?.LogInformation("Page dimensions from DI: {Count} pages have width/height", pageInchDims.Count);

            // Render needed pages via Docnet (in a subprocess to survive native SEGV crashes)
            var pageImages = await RenderPagesInSubprocessAsync(pdfBytes, neededPages, ct);

            // Crop tables
            for (int i = 0; i < diTables.Count; i++)
            {
                var table = diTables[i];
                var cropped = CropElement(table.BoundingRegions, pageImages, pageInchDims, $"table_{i + 1:D3}");
                if (cropped is not null)
                {
                    tables.Add(new ExtractedTable(cropped.Value.Name, cropped.Value.Data, null, null));
                }
            }

            // Crop figures
            for (int i = 0; i < diFigures.Count; i++)
            {
                var figure = diFigures[i];
                var cropped = CropElement(figure.BoundingRegions, pageImages, pageInchDims, $"figure_{i + 1:D3}");
                if (cropped is not null)
                {
                    figures.Add(new ExtractedFigure(cropped.Value.Name, cropped.Value.Data, null, null));
                }
            }

            // Dispose page images
            foreach (var img in pageImages.Values) img.Dispose();
        }

        _logger?.LogInformation("Cropped {Tables} table images, {Figures} figure images",
            tables.Count, figures.Count);

        // ③ Embed image references into Markdown
        var enrichedContent = EmbedImageReferences(contentMarkdown, diTables, diFigures);

        var md5 = ContentHasher.ComputeMd5(enrichedContent);

        return new ConvertResult
        {
            Markdown = enrichedContent,
            ContentMd5 = md5,
            PageCount = pageCount,
            Images = [],
            Tables = tables,
            Figures = figures,
        };
    }

    /// <summary>
    /// Subprocess entry point: render PDF pages to PNG files.
    /// Called via the standalone MarkItDown.NET.Worker render command.
    /// </summary>
    public static int RunRenderSubprocess(string[] args)
    {
        if (args.Length != 5) throw new ArgumentException("Invalid PDF render arguments.");
        var pdfPath = args[1];
        var outputDir = args[2];
        var scalingFactor = double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
        var pages = args[4].Split(',').Select(int.Parse).ToHashSet();

        var pdfBytes = File.ReadAllBytes(pdfPath);
        using var docReader = DocLib.Instance.GetDocReader(pdfBytes, new PageDimensions(scalingFactor));

        foreach (var pageNum in pages)
        {
            var pageIndex = pageNum - 1;
            if (pageIndex < 0) throw new InvalidDataException("Invalid PDF page number.");

            using var pageReader = docReader.GetPageReader(pageIndex);
            var rawBytes = pageReader.GetImage();
            var width = pageReader.GetPageWidth();
            var height = pageReader.GetPageHeight();

            if (rawBytes is null || width <= 0 || height <= 0 || rawBytes.Length != checked(width * height * 4))
                throw new InvalidDataException("PDF page rendering failed.");

            using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            System.Runtime.InteropServices.Marshal.Copy(rawBytes, 0, bitmap.GetPixels(), rawBytes.Length);

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            using var stream = new FileStream(Path.Combine(outputDir, $"{pageNum}.png"), FileMode.CreateNew);
            data.SaveTo(stream);
        }

        return 0;
    }

    /// <summary>Render specific PDF pages to SKBitmap via an isolated subprocess (survives native SEGV).</summary>
    private async Task<Dictionary<int, SKBitmap>> RenderPagesInSubprocessAsync(
        byte[] pdfBytes, HashSet<int> pageNumbers, CancellationToken token)
    {
        var result = new Dictionary<int, SKBitmap>();
        if (pageNumbers.Count == 0) return result;
        if (string.IsNullOrEmpty(_renderWorkerPath) || !Path.IsPathFullyQualified(_renderWorkerPath) || !File.Exists(_renderWorkerPath))
            throw new InvalidOperationException("PDF asset rendering requires an explicit standalone worker path.");
        var tempDir = Path.Combine(Path.GetTempPath(), $"pdfrender_{Guid.NewGuid():N}");
        var tempPdf = Path.Combine(tempDir, "input.pdf");

        try
        {
            Directory.CreateDirectory(tempDir);
            await File.WriteAllBytesAsync(tempPdf, pdfBytes, token);

            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(_renderWorkerPath);
            psi.ArgumentList.Add("--render-pdf");
            psi.ArgumentList.Add(tempPdf);
            psi.ArgumentList.Add(tempDir);
            psi.ArgumentList.Add(ScalingFactor.ToString(System.Globalization.CultureInfo.InvariantCulture));
            psi.ArgumentList.Add(string.Join(",", pageNumbers));

            using var process = Process.Start(psi) ?? throw new IOException("PDF renderer did not start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(1));
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                throw;
            }
            await stderr;

            if (process.ExitCode != 0)
            {
                throw new IOException($"PDF renderer failed with exit code {process.ExitCode}.");
            }

            // Load rendered PNGs
            foreach (var pageNum in pageNumbers)
            {
                var pngPath = Path.Combine(tempDir, $"{pageNum}.png");
                result[pageNum] = SKBitmap.Decode(pngPath)
                    ?? throw new InvalidDataException("PDF renderer returned an invalid page image.");
            }
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }

        return result;
    }

    /// <summary>Crop an element (table/figure) from its page image using BoundingRegion polygon.</summary>
    private static (string Name, byte[] Data)? CropElement(
        IReadOnlyList<BoundingRegion>? boundingRegions,
        Dictionary<int, SKBitmap> pageImages,
        Dictionary<int, (float Width, float Height)> pageInchDims,
        string baseName)
    {
        if (boundingRegions is null || boundingRegions.Count == 0) return null;

        var region = boundingRegions[0];
        if (!pageImages.TryGetValue(region.PageNumber, out var pageImage)) return null;
        if (region.Polygon is null || region.Polygon.Count < 8) return null;

        var polygon = region.Polygon;
        var imgW = pageImage.Width;
        var imgH = pageImage.Height;

        // Get page dimensions in inches from DI
        float pageWidthIn, pageHeightIn;
        if (pageInchDims.TryGetValue(region.PageNumber, out var dims))
        {
            pageWidthIn = dims.Width;
            pageHeightIn = dims.Height;
        }
        else
        {
            // Fallback: derive from render DPI
            pageWidthIn = imgW / (float)RenderDpi;
            pageHeightIn = imgH / (float)RenderDpi;
        }

        var rect = PolygonToPixelRect(polygon, imgW, imgH, pageWidthIn, pageHeightIn);

        // Clamp to image bounds
        rect = ClampRect(rect, imgW, imgH);
        if (rect.Width <= 0 || rect.Height <= 0) return null;

        using var cropped = new SKBitmap(rect.Width, rect.Height);
        using (var canvas = new SKCanvas(cropped))
        {
            canvas.DrawBitmap(pageImage, SKRect.Create(rect.Left, rect.Top, rect.Width, rect.Height),
                SKRect.Create(0, 0, rect.Width, rect.Height));
        }
        using var image = SKImage.FromBitmap(cropped);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return ($"{baseName}.png", data.ToArray());
    }

    private static SKRectI PolygonToPixelRect(
        IReadOnlyList<float> polygon, int imgWidthPx, int imgHeightPx,
        float pageWidthInches, float pageHeightInches)
    {
        float scaleX = imgWidthPx / pageWidthInches;
        float scaleY = imgHeightPx / pageHeightInches;

        int x = (int)(Math.Min(polygon[0], polygon[6]) * scaleX);
        int y = (int)(Math.Min(polygon[1], polygon[3]) * scaleY);
        int x2 = (int)(Math.Max(polygon[2], polygon[4]) * scaleX);
        int y2 = (int)(Math.Max(polygon[5], polygon[7]) * scaleY);

        return new SKRectI(x, y, x + Math.Max(1, x2 - x), y + Math.Max(1, y2 - y));
    }

    private static SKRectI ClampRect(SKRectI rect, int imgWidth, int imgHeight)
    {
        int x = Math.Max(0, rect.Left);
        int y = Math.Max(0, rect.Top);
        int w = Math.Min(rect.Width, imgWidth - x);
        int h = Math.Min(rect.Height, imgHeight - y);
        return new SKRectI(x, y, x + Math.Max(0, w), y + Math.Max(0, h));
    }

    private static string EmbedImageReferences(
        string content,
        IReadOnlyList<DocumentTable> tables,
        IReadOnlyList<DocumentFigure> figures)
    {
        if (tables.Count == 0 && figures.Count == 0) return content;

        var insertions = new List<(int Offset, string Text)>();

        for (int i = 0; i < tables.Count; i++)
        {
            var table = tables[i];
            if (table.Spans is null || table.Spans.Count == 0) continue;
            if (table.BoundingRegions is null || table.BoundingRegions.Count == 0) continue;

            var offset = table.Spans[0].Offset;
            insertions.Add((offset, $"\n\n![table_{i + 1:D3}](assets/tables/table_{i + 1:D3}.png)\n\n"));
        }

        for (int i = 0; i < figures.Count; i++)
        {
            var figure = figures[i];
            if (figure.Spans is null || figure.Spans.Count == 0) continue;
            if (figure.BoundingRegions is null || figure.BoundingRegions.Count == 0) continue;

            var offset = figure.Spans[0].Offset;
            insertions.Add((offset, $"\n\n![figure_{i + 1:D3}](assets/figures/figure_{i + 1:D3}.png)\n\n"));
        }

        if (insertions.Count == 0) return content;

        // Sort descending by offset to avoid position drift
        insertions.Sort((a, b) => b.Offset.CompareTo(a.Offset));

        var sb = new StringBuilder(content);
        foreach (var (offset, text) in insertions)
        {
            if (offset >= 0 && offset <= sb.Length)
            {
                sb.Insert(offset, text);
            }
        }

        return sb.ToString().Trim();
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }
}
