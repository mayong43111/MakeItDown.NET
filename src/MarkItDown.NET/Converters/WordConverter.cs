using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Logging;

namespace MarkItDown.NET.Converters;

/// <summary>
/// Converts .docx files using Open XML SDK → Markdown.
/// Supports headings, paragraphs, tables, bold/italic, lists, and embedded images.
/// </summary>
public class WordConverter : IDocumentConverter
{
    private readonly ILogger<WordConverter>? _logger;

    public WordConverter(ILogger<WordConverter>? logger = null) => _logger = logger;

    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".docx"];

    public bool CanConvert(string extension)
        => extension is ".docx";

    public Task<ConvertResult> ConvertAsync(ConvertContext context, CancellationToken ct = default)
    {
        _logger?.LogInformation("Converting Word document {Filename}", context.Filename);

        using var doc = WordprocessingDocument.Open(context.Content, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null || doc.MainDocumentPart is null)
            return Task.FromResult(EmptyResult());

        var mainPart = doc.MainDocumentPart;

        var sb = new StringBuilder();
        var images = new List<ExtractedImage>();
        var imageIndex = 0;

        foreach (var element in body.ChildElements)
        {
            if (element is Paragraph para)
            {
                var heading = GetHeadingLevel(para, mainPart);
                var text = GetParagraphText(para);

                if (string.IsNullOrWhiteSpace(text) && !HasImages(para))
                {
                    sb.AppendLine();
                    continue;
                }

                // Extract inline images
                foreach (var blip in para.Descendants<DocumentFormat.OpenXml.Drawing.Blip>())
                {
                    var img = ExtractImage(mainPart, blip, ref imageIndex);
                    if (img is not null)
                    {
                        images.Add(img);
                        sb.AppendLine($"![{img.Name}](assets/images/{img.Name})");
                    }
                }

                if (heading > 0)
                    sb.AppendLine($"{new string('#', heading)} {text}");
                else if (IsListItem(para))
                    sb.AppendLine($"- {text}");
                else if (!string.IsNullOrWhiteSpace(text))
                    sb.AppendLine(text);

                sb.AppendLine();
            }
            else if (element is Table table)
            {
                ConvertTable(table, sb);
                sb.AppendLine();
            }
        }

        var contentMarkdown = sb.ToString().TrimEnd();
        var md5 = ContentHasher.ComputeMd5(contentMarkdown);

        _logger?.LogInformation("Word convert complete: {ContentLen} chars, {ImgCount} images",
            contentMarkdown.Length, images.Count);

        return Task.FromResult(new ConvertResult
        {
            Markdown = contentMarkdown,
            ContentMd5 = md5,
            PageCount = 1,
            Images = images,
        });
    }

    private static int GetHeadingLevel(Paragraph para, MainDocumentPart mainPart)
    {
        var styleId = para.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        if (styleId is null) return 0;

        if (styleId.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(styleId.AsSpan(7), out var level))
            return Math.Min(level, 6);

        var stylePart = mainPart.StyleDefinitionsPart;
        if (stylePart is null) return 0;

        var style = stylePart.Styles?.Elements<Style>()
            .FirstOrDefault(s => s.StyleId?.Value == styleId);
        var outlineLevel = style?.StyleParagraphProperties?.OutlineLevel?.Val?.Value;
        return outlineLevel.HasValue ? Math.Min(outlineLevel.Value + 1, 6) : 0;
    }

    private static string GetParagraphText(Paragraph para)
    {
        var sb = new StringBuilder();
        foreach (var run in para.Elements<Run>())
        {
            var text = run.InnerText;
            if (string.IsNullOrEmpty(text)) continue;

            var bold = run.RunProperties?.Bold is not null;
            var italic = run.RunProperties?.Italic is not null;

            if (bold && italic) sb.Append($"***{text}***");
            else if (bold) sb.Append($"**{text}**");
            else if (italic) sb.Append($"*{text}*");
            else sb.Append(text);
        }
        return sb.ToString();
    }

    private static bool IsListItem(Paragraph para)
        => para.ParagraphProperties?.NumberingProperties is not null;

    private static bool HasImages(Paragraph para)
        => para.Descendants<DocumentFormat.OpenXml.Drawing.Blip>().Any();

    private static ExtractedImage? ExtractImage(MainDocumentPart mainPart,
        DocumentFormat.OpenXml.Drawing.Blip blip, ref int index)
    {
        var embed = blip.Embed?.Value;
        if (embed is null) return null;

        var part = mainPart.GetPartById(embed);
        if (part is not ImagePart imagePart) return null;

        using var stream = imagePart.GetStream();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);

        index++;
        var ext = GetImageExtension(imagePart.ContentType);
        var name = $"img_{index:D3}{ext}";

        return new ExtractedImage(name, ms.ToArray(), imagePart.ContentType);
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

    private static void ConvertTable(Table table, StringBuilder sb)
    {
        var rows = table.Elements<TableRow>().ToList();
        if (rows.Count == 0) return;

        for (var i = 0; i < rows.Count; i++)
        {
            var cells = rows[i].Elements<TableCell>().ToList();
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

    private static ConvertResult EmptyResult() => new() { Markdown = "", ContentMd5 = ContentHasher.ComputeMd5(""), PageCount = 0 };
}
