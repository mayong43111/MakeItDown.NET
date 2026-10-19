using System.Text;
using Microsoft.Extensions.Logging;
using NPOI.POIFS.FileSystem;

namespace MarkItDown.NET.Converters;

/// <summary>
/// Converts legacy .doc (OLE2 Binary) files using NPOI POIFS → Markdown.
/// Extracts text from the WordDocument stream by parsing the FIB header.
/// </summary>
public class LegacyWordConverter : IDocumentConverter
{
    private readonly ILogger<LegacyWordConverter>? _logger;

    public LegacyWordConverter(ILogger<LegacyWordConverter>? logger = null) => _logger = logger;

    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".doc"];

    public bool CanConvert(string extension)
        => extension is ".doc";

    public Task<ConvertResult> ConvertAsync(ConvertContext context, CancellationToken ct = default)
    {
        _logger?.LogInformation("Converting legacy .doc file {Filename}", context.Filename);

        POIFSFileSystem poifs;
        try
        {
            poifs = new POIFSFileSystem(context.Content);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to open .doc file {Filename} as OLE2", context.Filename);
            throw new InvalidOperationException($"Cannot parse .doc file '{context.Filename}': {ex.Message}", ex);
        }

        try
        {
            var text = ExtractTextFromWordDocument(poifs);
            var images = ExtractImages(poifs);

            var sb = new StringBuilder();

            var lines = text.Split('\r', '\n');
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed))
                {
                    sb.AppendLine();
                    continue;
                }
                sb.AppendLine(trimmed);
                sb.AppendLine();
            }

            foreach (var img in images)
            {
                sb.AppendLine($"![{img.Name}](assets/images/{img.Name})");
                sb.AppendLine();
            }

            var contentMarkdown = sb.ToString().TrimEnd();
            var md5 = ContentHasher.ComputeMd5(contentMarkdown);

            _logger?.LogInformation("Legacy .doc convert complete: {ContentLen} chars, {ImgCount} images",
                contentMarkdown.Length, images.Count);

            return Task.FromResult(new ConvertResult
            {
                Markdown = contentMarkdown,
                ContentMd5 = md5,
                PageCount = 1,
                Images = images,
            });
        }
        finally
        {
            poifs.Close();
        }
    }

    private string ExtractTextFromWordDocument(POIFSFileSystem poifs)
    {
        var root = poifs.Root;

        var wordDocEntry = root.GetEntry("WordDocument") as DocumentEntry
            ?? throw new InvalidOperationException("No WordDocument stream found in .doc file");

        byte[] wordDocData;
        using (var stream = new DocumentInputStream(wordDocEntry))
        {
            wordDocData = new byte[wordDocEntry.Size];
            stream.ReadFully(wordDocData);
        }

        if (wordDocData.Length < 68)
            throw new InvalidOperationException("WordDocument stream too small");

        var wIdent = BitConverter.ToUInt16(wordDocData, 0);
        if (wIdent != 0xA5EC && wIdent != 0xA5DC)
            throw new InvalidOperationException($"Invalid Word magic: 0x{wIdent:X4}");

        var ccpText = BitConverter.ToInt32(wordDocData, 0x004C);

        var flags = BitConverter.ToUInt16(wordDocData, 0x000A);
        var fWhichTblStm = (flags & 0x0200) != 0;

        var tableStreamName = fWhichTblStm ? "1Table" : "0Table";
        DocumentEntry? tableEntry = null;
        try
        {
            tableEntry = root.GetEntry(tableStreamName) as DocumentEntry;
        }
        catch
        {
            try { tableEntry = root.GetEntry(fWhichTblStm ? "0Table" : "1Table") as DocumentEntry; }
            catch { /* ignored */ }
        }

        if (tableEntry is null)
            return ExtractRawText(wordDocData);

        byte[] tableData;
        using (var stream = new DocumentInputStream(tableEntry))
        {
            tableData = new byte[tableEntry.Size];
            stream.ReadFully(tableData);
        }

        int fcClx, lcbClx;
        if (wordDocData.Length >= 0x01AA)
        {
            fcClx = BitConverter.ToInt32(wordDocData, 0x01A2);
            lcbClx = BitConverter.ToInt32(wordDocData, 0x01A6);
        }
        else
        {
            return ExtractRawText(wordDocData);
        }

        if (fcClx < 0 || fcClx + lcbClx > tableData.Length || lcbClx < 4)
            return ExtractRawText(wordDocData);

        return ExtractTextFromPieceTable(wordDocData, tableData, fcClx, lcbClx, ccpText);
    }

    private static string ExtractTextFromPieceTable(byte[] wordDocData, byte[] tableData,
        int fcClx, int lcbClx, int ccpText)
    {
        var sb = new StringBuilder();
        var pos = fcClx;
        var clxEnd = fcClx + lcbClx;

        while (pos < clxEnd && tableData[pos] == 0x01)
        {
            if (pos + 3 > clxEnd) break;
            var cbGrpprl = BitConverter.ToUInt16(tableData, pos + 1);
            pos += 3 + cbGrpprl;
        }

        if (pos >= clxEnd || tableData[pos] != 0x02)
            return ExtractRawText(wordDocData);

        pos++;
        var cb = BitConverter.ToInt32(tableData, pos);
        pos += 4;

        var nPieces = (cb - 4) / 12;
        if (nPieces <= 0)
            return ExtractRawText(wordDocData);

        var cpBase = pos;
        var pcdBase = pos + (nPieces + 1) * 4;

        var totalRead = 0;
        for (var i = 0; i < nPieces && totalRead < ccpText; i++)
        {
            var cpStart = BitConverter.ToInt32(tableData, cpBase + i * 4);
            var cpEnd = BitConverter.ToInt32(tableData, cpBase + (i + 1) * 4);
            var charCount = Math.Min(cpEnd - cpStart, ccpText - totalRead);

            var pcdOffset = pcdBase + i * 8;
            var fc = BitConverter.ToInt32(tableData, pcdOffset + 2);

            var isUnicode = (fc & 0x40000000) == 0;
            var actualFc = isUnicode ? fc : (fc & ~0x40000000) / 2;

            if (isUnicode)
            {
                var byteOffset = actualFc;
                var byteLen = charCount * 2;
                if (byteOffset + byteLen <= wordDocData.Length)
                    sb.Append(Encoding.Unicode.GetString(wordDocData, byteOffset, byteLen));
            }
            else
            {
                var byteOffset = actualFc;
                if (byteOffset + charCount <= wordDocData.Length)
                    sb.Append(Encoding.GetEncoding(1252).GetString(wordDocData, byteOffset, charCount));
            }

            totalRead += charCount;
        }

        return CleanDocText(sb.ToString());
    }

    private static string ExtractRawText(byte[] data)
    {
        var sb = new StringBuilder();

        for (var i = 0; i < data.Length - 1; i += 2)
        {
            var c = (char)(data[i] | (data[i + 1] << 8));
            if (char.IsLetterOrDigit(c) || char.IsPunctuation(c) || char.IsWhiteSpace(c)
                || c is >= (char)0x4E00 and <= (char)0x9FFF
                || c is >= (char)0x3000 and <= (char)0x303F
                || c is >= (char)0xFF00 and <= (char)0xFFEF)
            {
                sb.Append(c);
            }
            else if (sb.Length > 0 && sb[^1] != '\n')
            {
                sb.Append('\n');
            }
        }

        return CleanDocText(sb.ToString());
    }

    private static string CleanDocText(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        return text
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Replace("\u0007", "")
            .Replace("\u0013", "")
            .Replace("\u0014", "")
            .Replace("\u0015", "")
            .Replace("\u0001", "")
            .Replace("\u0008", "")
            .Trim();
    }

    private static List<ExtractedImage> ExtractImages(POIFSFileSystem poifs)
    {
        var images = new List<ExtractedImage>();
        var root = poifs.Root;

        DocumentEntry? dataEntry = null;
        try { dataEntry = root.GetEntry("Data") as DocumentEntry; }
        catch { /* no embedded images */ }

        if (dataEntry is null || dataEntry.Size == 0)
            return images;

        return images;
    }
}
