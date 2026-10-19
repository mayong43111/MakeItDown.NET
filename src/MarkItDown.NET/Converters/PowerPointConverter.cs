using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using Microsoft.Extensions.Logging;
using A = DocumentFormat.OpenXml.Drawing;

namespace MarkItDown.NET.Converters;

/// <summary>
/// Converts .pptx / .pptm files using Open XML SDK → Markdown.
/// Each slide becomes a section with heading (slide title) + body text + extracted images.
/// </summary>
public class PowerPointConverter : IDocumentConverter
{
    private readonly ILogger<PowerPointConverter>? _logger;

    public PowerPointConverter(ILogger<PowerPointConverter>? logger = null) => _logger = logger;

    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".pptx", ".pptm"];

    public bool CanConvert(string extension)
        => extension is ".pptx" or ".pptm";

    public Task<ConvertResult> ConvertAsync(ConvertContext context, CancellationToken ct = default)
    {
        _logger?.LogInformation("Converting PowerPoint {Filename}", context.Filename);

        using var doc = PresentationDocument.Open(context.Content, false);
        var presentationPart = doc.PresentationPart;
        if (presentationPart?.Presentation?.SlideIdList is null)
            return Task.FromResult(EmptyResult());

        var sb = new StringBuilder();
        var images = new List<ExtractedImage>();
        var slideIndex = 0;
        var imageIndex = 0;

        foreach (var slideId in presentationPart.Presentation.SlideIdList.Elements<SlideId>())
        {
            var relationshipId = slideId.RelationshipId?.Value;
            if (relationshipId is null) continue;

            var slidePart = presentationPart.GetPartById(relationshipId) as SlidePart;
            if (slidePart?.Slide?.CommonSlideData?.ShapeTree is null) continue;

            slideIndex++;
            var title = ExtractSlideTitle(slidePart);
            var body = ExtractSlideBody(slidePart);

            sb.AppendLine($"## Slide {slideIndex}{(string.IsNullOrWhiteSpace(title) ? "" : $": {title}")}");
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(body))
            {
                sb.AppendLine(body);
                sb.AppendLine();
            }

            foreach (var imagePart in slidePart.ImageParts)
            {
                imageIndex++;
                var ext = GetImageExtension(imagePart.ContentType);
                var name = $"slide{slideIndex:D2}_img_{imageIndex:D3}{ext}";

                using var stream = imagePart.GetStream();
                using var ms = new MemoryStream();
                stream.CopyTo(ms);

                images.Add(new ExtractedImage(name, ms.ToArray(), imagePart.ContentType));
                sb.AppendLine($"![{name}](assets/images/{name})");
            }

            foreach (var table in slidePart.Slide.Descendants<A.Table>())
            {
                ConvertTable(table, sb);
            }

            sb.AppendLine();
        }

        var contentMarkdown = sb.ToString().TrimEnd();
        var md5 = ContentHasher.ComputeMd5(contentMarkdown);

        _logger?.LogInformation("PowerPoint convert complete: {Slides} slides, {ImgCount} images, {ContentLen} chars",
            slideIndex, images.Count, contentMarkdown.Length);

        return Task.FromResult(new ConvertResult
        {
            Markdown = contentMarkdown,
            ContentMd5 = md5,
            PageCount = slideIndex,
            Images = images,
        });
    }

    private static bool IsTitleShape(PlaceholderShape? ph)
    {
        if (ph?.Type is null) return false;
        var t = ph.Type.Value;
        return t == PlaceholderValues.Title || t == PlaceholderValues.CenteredTitle;
    }

    private static string ExtractSlideTitle(SlidePart slidePart)
    {
        var shapes = slidePart.Slide?.CommonSlideData?.ShapeTree?.Elements<Shape>() ?? [];
        foreach (var shape in shapes)
        {
            var ph = shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?
                .GetFirstChild<PlaceholderShape>();
            if (IsTitleShape(ph))
                return GetShapeText(shape);
        }
        return "";
    }

    private static string ExtractSlideBody(SlidePart slidePart)
    {
        var sb = new StringBuilder();
        var shapes = slidePart.Slide?.CommonSlideData?.ShapeTree?.Elements<Shape>() ?? [];

        foreach (var shape in shapes)
        {
            var ph = shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?
                .GetFirstChild<PlaceholderShape>();

            if (IsTitleShape(ph))
                continue;

            var text = GetShapeText(shape);
            if (!string.IsNullOrWhiteSpace(text))
            {
                var paras = shape.TextBody?.Elements<A.Paragraph>()
                    .Select(p => p.InnerText.Trim())
                    .Where(t => !string.IsNullOrEmpty(t))
                    .ToList() ?? [];

                if (paras.Count > 1)
                {
                    foreach (var para in paras)
                        sb.AppendLine($"- {para}");
                }
                else if (paras.Count == 1)
                {
                    sb.AppendLine(paras[0]);
                }
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string GetShapeText(Shape shape)
        => shape.TextBody?.InnerText?.Trim() ?? "";

    private static void ConvertTable(A.Table table, StringBuilder sb)
    {
        var rows = table.Elements<A.TableRow>().ToList();
        if (rows.Count == 0) return;

        sb.AppendLine();
        for (var i = 0; i < rows.Count; i++)
        {
            var cells = rows[i].Elements<A.TableCell>().ToList();
            sb.Append("| ");
            sb.Append(string.Join(" | ", cells.Select(c => c.InnerText.Trim())));
            sb.AppendLine(" |");

            if (i == 0)
            {
                sb.Append("| ");
                sb.Append(string.Join(" | ", cells.Select(_ => "---")));
                sb.AppendLine(" |");
            }
        }
    }

    private static string GetImageExtension(string contentType) => contentType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/bmp" => ".bmp",
        "image/tiff" => ".tiff",
        "image/svg+xml" => ".svg",
        _ => ".png",
    };

    private static ConvertResult EmptyResult() => new() { Markdown = "", ContentMd5 = ContentHasher.ComputeMd5(""), PageCount = 0 };
}
