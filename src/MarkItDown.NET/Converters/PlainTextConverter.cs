using System.Text;
using Microsoft.Extensions.Logging;

namespace MarkItDown.NET.Converters;

/// <summary>
/// Converts plain text and CSV files to Markdown.
/// CSV is rendered as a Markdown table; plain text is wrapped in a code block.
/// </summary>
public class PlainTextConverter : IDocumentConverter
{
    private readonly ILogger<PlainTextConverter>? _logger;

    public PlainTextConverter(ILogger<PlainTextConverter>? logger = null) => _logger = logger;

    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".txt", ".csv"];

    public bool CanConvert(string extension)
        => extension is ".txt" or ".csv";

    public async Task<ConvertResult> ConvertAsync(ConvertContext context, CancellationToken ct = default)
    {
        _logger?.LogInformation("Converting plain text {Filename}", context.Filename);

        using var reader = new StreamReader(context.Content, Encoding.UTF8);
        var rawText = await reader.ReadToEndAsync(ct);

        var isCsv = context.Extension is ".csv"
                    || context.Filename.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);

        var contentMarkdown = isCsv
            ? ConvertCsvToMarkdownTable(rawText)
            : $"```\n{rawText}\n```";

        var md5 = ContentHasher.ComputeMd5(contentMarkdown);

        _logger?.LogInformation("Plain text convert complete: {ContentLen} chars, MD5={Md5}",
            contentMarkdown.Length, md5);

        return new ConvertResult
        {
            Markdown = contentMarkdown,
            ContentMd5 = md5,
            PageCount = 1,
        };
    }

    private static string ConvertCsvToMarkdownTable(string csv)
    {
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return csv;

        var sb = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            var cells = lines[i].TrimEnd('\r').Split(',');
            sb.AppendLine("| " + string.Join(" | ", cells) + " |");
            if (i == 0)
                sb.AppendLine("| " + string.Join(" | ", cells.Select(_ => "---")) + " |");
        }
        return sb.ToString();
    }
}
