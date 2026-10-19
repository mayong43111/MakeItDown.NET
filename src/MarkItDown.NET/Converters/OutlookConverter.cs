using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ReverseMarkdown;

namespace MarkItDown.NET.Converters;

/// <summary>
/// Converts Outlook .msg files by extracting metadata and body content.
/// Converts HTML body to Markdown. Does not recurse into nested .msg attachments.
/// </summary>
public partial class OutlookConverter : IDocumentConverter
{
    private const long MaxFileSizeBytes = 100 * 1024 * 1024; // 100 MB limit for .msg
    private readonly ILogger<OutlookConverter>? _logger;

    static OutlookConverter()
    {
        // Register additional encodings (e.g. 936 for Chinese GB2312) needed by MsgReader
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public OutlookConverter(ILogger<OutlookConverter>? logger = null) => _logger = logger;

    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".msg"];

    public bool CanConvert(string extension)
        => extension is ".msg";

    public async Task<ConvertResult> ConvertAsync(ConvertContext context, CancellationToken ct = default)
    {
        _logger?.LogInformation("Converting Outlook message {Filename}", context.Filename);

        var msgBytes = await ReadAllBytesAsync(context.Content, ct);

        if (msgBytes.Length > MaxFileSizeBytes)
            throw new InvalidOperationException(
                $"MSG file too large ({msgBytes.Length / 1024 / 1024} MB). Maximum supported: 100 MB.");

        using var stream = new MemoryStream(msgBytes);
        using var message = new MsgReader.Outlook.Storage.Message(stream);

        var sb = new StringBuilder();

        // Header metadata
        var subject = message.Subject?.Trim() ?? "(No Subject)";
        sb.AppendLine($"# {subject}");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(message.Sender?.Email))
            sb.AppendLine($"**From**: {message.Sender.DisplayName} <{message.Sender.Email}>");

        var recipients = message.Recipients;
        if (recipients is { Count: > 0 })
        {
            var toList = string.Join("; ", recipients
                .Where(r => r.Type == MsgReader.Outlook.RecipientType.To)
                .Select(r => string.IsNullOrWhiteSpace(r.Email) ? r.DisplayName : $"{r.DisplayName} <{r.Email}>"));
            if (!string.IsNullOrEmpty(toList))
                sb.AppendLine($"**To**: {toList}");

            var ccList = string.Join("; ", recipients
                .Where(r => r.Type == MsgReader.Outlook.RecipientType.Cc)
                .Select(r => string.IsNullOrWhiteSpace(r.Email) ? r.DisplayName : $"{r.DisplayName} <{r.Email}>"));
            if (!string.IsNullOrEmpty(ccList))
                sb.AppendLine($"**CC**: {ccList}");
        }

        if (message.SentOn.HasValue)
            sb.AppendLine($"**Date**: {message.SentOn.Value:yyyy-MM-dd HH:mm}");

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        // Body content
        var bodyMarkdown = ExtractBody(message);

        // Clean up CID image references (inline images that can't be displayed in Markdown)
        bodyMarkdown = CleanCidReferences(bodyMarkdown);

        sb.Append(bodyMarkdown);

        // Attachments list — exclude inline images (small embedded images used in email body)
        var attachments = message.Attachments;
        if (attachments is { Count: > 0 })
        {
            var fileAttachments = attachments
                .OfType<MsgReader.Outlook.Storage.Attachment>()
                .Where(a => !string.IsNullOrWhiteSpace(a.FileName) && !a.IsInline)
                .ToList();

            if (fileAttachments.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine("## Attachments");
                sb.AppendLine();
                foreach (var att in fileAttachments)
                {
                    var size = att.Data?.Length ?? 0;
                    var sizeStr = size > 1024 * 1024
                        ? $"{size / 1024.0 / 1024.0:F1} MB"
                        : $"{size / 1024.0:F1} KB";
                    sb.AppendLine($"- {att.FileName} ({sizeStr})");
                }
            }
        }

        var contentMarkdown = sb.ToString().TrimEnd() + "\n";
        var md5 = ContentHasher.ComputeMd5(contentMarkdown);

        _logger?.LogInformation("MSG convert complete: subject=\"{Subject}\", {ContentLen} chars, {AttachCount} attachments",
            subject, contentMarkdown.Length, attachments?.Count ?? 0);

        return new ConvertResult
        {
            Markdown = contentMarkdown,
            ContentMd5 = md5,
            PageCount = 1,
        };
    }

    private string ExtractBody(MsgReader.Outlook.Storage.Message message)
    {
        // Prefer HTML body → convert to Markdown
        var htmlBody = message.BodyHtml;
        if (!string.IsNullOrWhiteSpace(htmlBody))
        {
            try
            {
                var converter = new Converter(new Config
                {
                    UnknownTags = Config.UnknownTagsOption.Bypass,
                    GithubFlavored = true,
                    RemoveComments = true,
                    SmartHrefHandling = true,
                });
                var md = converter.Convert(htmlBody);
                if (!string.IsNullOrWhiteSpace(md))
                    return FixMojibake(md.Trim());
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to convert HTML body to Markdown, falling back to plain text");
            }
        }

        // Fall back to plain text body
        var text = message.BodyText?.Trim() ?? "";
        return FixMojibake(text);
    }

    /// <summary>
    /// Detects and fixes mojibake caused by GBK/GB2312 bytes being read as Latin-1.
    /// </summary>
    private string FixMojibake(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length < 10)
            return text;

        bool hasHighBytes = false;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] >= 0x80 && text[i] <= 0xFF)
            {
                hasHighBytes = true;
                break;
            }
        }
        if (!hasHighBytes)
            return text;

        _logger?.LogInformation("Detected high Latin-1 bytes in text, attempting segment-based GBK re-decode");

        var latin1 = Encoding.Latin1;
        var gbk = Encoding.GetEncoding(936);
        var result = new StringBuilder(text.Length);
        var garbledBuf = new StringBuilder();

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (c >= 0x80 && c <= 0xFF)
            {
                garbledBuf.Append(c);
            }
            else
            {
                if (garbledBuf.Length > 0)
                {
                    result.Append(DecodeGarbledSegment(garbledBuf.ToString(), latin1, gbk));
                    garbledBuf.Clear();
                }
                result.Append(c);
            }
        }

        if (garbledBuf.Length > 0)
            result.Append(DecodeGarbledSegment(garbledBuf.ToString(), latin1, gbk));

        var resultStr = result.ToString();
        _logger?.LogInformation("Mojibake segment fix complete, {OrigLen} → {NewLen} chars", text.Length, resultStr.Length);
        return resultStr;
    }

    private static string DecodeGarbledSegment(string segment, Encoding latin1, Encoding gbk)
    {
        try
        {
            var rawBytes = latin1.GetBytes(segment);
            var decoded = gbk.GetString(rawBytes);

            if (decoded.Length > 0 && !decoded.Contains('\uFFFD'))
                return decoded;
        }
        catch
        {
            // Fall through to return original
        }
        return segment;
    }

    // Regex patterns for CID image references in Markdown

    // Linked escaped CID: [!\[alt\](cid:xxx)?label](url) → [label](url)
    private static readonly Regex LinkedEscapedCidRegex = new(
        @"\[!\\\[[^\\]*\\\]\(cid:[^)]+\)([^\]]*)]\(([^)]+)\)",
        RegexOptions.Compiled);

    // Linked non-escaped CID: [![alt](cid:xxx)](url) → [alt](url)
    private static readonly Regex LinkedCidImageRegex = new(
        @"\[!\[([^\]]*)\]\(cid:[^)]+\)([^\]]*)\]\(([^)]+)\)",
        RegexOptions.Compiled);

    // Standalone escaped CID: !\[alt\](cid:xxx) → remove
    private static readonly Regex StandaloneEscapedCidRegex = new(
        @"!\\\[[^\\]*\\\]\(cid:[^)]+\)",
        RegexOptions.Compiled);

    // Standalone non-escaped CID: ![alt](cid:xxx) → remove
    private static readonly Regex StandaloneCidImageRegex = new(
        @"!\[[^\]]*\]\(cid:[^)]+\)",
        RegexOptions.Compiled);

    // Empty table rows that only contained CID images
    private static readonly Regex EmptyTableRowRegex = new(
        @"^\|[\s|]*\|\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static string CleanCidReferences(string markdown)
    {
        if (string.IsNullOrEmpty(markdown) || !markdown.Contains("cid:"))
            return markdown;

        var result = LinkedEscapedCidRegex.Replace(markdown, m =>
        {
            var label = m.Groups[1].Value.TrimStart('?').Trim();
            var url = m.Groups[2].Value;
            var linkText = string.IsNullOrEmpty(label) ? "link" : label;
            return $"[{linkText}]({url})";
        });

        result = LinkedCidImageRegex.Replace(result, m =>
        {
            var alt = m.Groups[1].Value.Trim();
            var label = m.Groups[2].Value.TrimStart('?').Trim();
            var url = m.Groups[3].Value;
            var linkText = !string.IsNullOrEmpty(label) ? label
                         : !string.IsNullOrEmpty(alt) ? alt
                         : "link";
            return $"[{linkText}]({url})";
        });

        result = StandaloneEscapedCidRegex.Replace(result, "");
        result = StandaloneCidImageRegex.Replace(result, "");
        result = EmptyTableRowRegex.Replace(result, "");
        result = Regex.Replace(result, @"\n{4,}", "\n\n\n");

        return result.Trim();
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }
}
