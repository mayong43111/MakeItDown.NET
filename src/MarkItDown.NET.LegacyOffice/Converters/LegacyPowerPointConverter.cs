using System.Text;
using Microsoft.Extensions.Logging;
using NPOI.POIFS.FileSystem;

namespace MarkItDown.NET.Converters;

/// <summary>
/// Converts legacy .ppt (OLE2 Binary) files to Markdown.
/// Extracts text by parsing the "PowerPoint Document" stream records.
/// </summary>
public class LegacyPowerPointConverter : IDocumentConverter
{
    // PPT record types for text extraction
    private const ushort RT_TextBytesAtom = 4008;  // ANSI text
    private const ushort RT_TextCharsAtom = 4000;  // Unicode text
    private const ushort RT_SlidePersistAtom = 1011;

    private readonly ILogger<LegacyPowerPointConverter>? _logger;

    public LegacyPowerPointConverter(ILogger<LegacyPowerPointConverter>? logger = null) => _logger = logger;

    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".ppt"];

    public bool CanConvert(string extension)
        => extension is ".ppt";

    public Task<ConvertResult> ConvertAsync(ConvertContext context, CancellationToken ct = default)
    {
        _logger?.LogInformation("Converting legacy .ppt file {Filename}", context.Filename);

        POIFSFileSystem poifs;
        try
        {
            poifs = new POIFSFileSystem(context.Content);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to open .ppt file {Filename} as OLE2", context.Filename);
            throw new InvalidOperationException($"Cannot parse .ppt file '{context.Filename}': {ex.Message}", ex);
        }

        try
        {
            var slides = ExtractSlides(poifs);

            var sb = new StringBuilder();
            for (var i = 0; i < slides.Count; i++)
            {
                var slideTexts = slides[i];
                if (slideTexts.Count == 0) continue;

                sb.AppendLine($"## Slide {i + 1}");
                sb.AppendLine();

                foreach (var text in slideTexts)
                {
                    var trimmed = text.Trim();
                    if (string.IsNullOrEmpty(trimmed)) continue;

                    sb.AppendLine(trimmed);
                    sb.AppendLine();
                }
            }

            var contentMarkdown = sb.ToString().TrimEnd();
            var md5 = ContentHasher.ComputeMd5(contentMarkdown);

            _logger?.LogInformation("Legacy .ppt convert complete: {SlideCount} slides, {ContentLen} chars",
                slides.Count, contentMarkdown.Length);

            return Task.FromResult(new ConvertResult
            {
                Markdown = contentMarkdown,
                ContentMd5 = md5,
                PageCount = slides.Count,
            });
        }
        finally
        {
            poifs.Close();
        }
    }

    /// <summary>
    /// Extracts text from the PowerPoint Document stream, grouped by slide.
    /// Returns a list of slides, each containing a list of text blocks.
    /// </summary>
    private List<List<string>> ExtractSlides(POIFSFileSystem poifs)
    {
        var root = poifs.Root;

        var pptDocEntry = root.GetEntry("PowerPoint Document") as DocumentEntry
            ?? throw new InvalidOperationException("No 'PowerPoint Document' stream found in .ppt file");

        byte[] data;
        using (var stream = new DocumentInputStream(pptDocEntry))
        {
            data = new byte[pptDocEntry.Size];
            stream.ReadFully(data);
        }

        // Parse records: each record has 8-byte header (2 ver/inst, 2 type, 4 length)
        var slides = new List<List<string>>();
        var currentSlide = new List<string>();

        var pos = 0;
        while (pos + 8 <= data.Length)
        {
            var recType = BitConverter.ToUInt16(data, pos + 2);
            var recLen = BitConverter.ToInt32(data, pos + 4);

            if (recLen < 0 || pos + 8 + recLen > data.Length)
                break;

            var recDataOffset = pos + 8;

            switch (recType)
            {
                case RT_SlidePersistAtom:
                    // New slide boundary — save previous and start a new one
                    if (currentSlide.Count > 0)
                        slides.Add(currentSlide);
                    currentSlide = [];
                    break;

                case RT_TextCharsAtom:
                    // Unicode text (UTF-16LE)
                    if (recLen >= 2)
                    {
                        var text = Encoding.Unicode.GetString(data, recDataOffset, recLen);
                        if (!string.IsNullOrWhiteSpace(text))
                            currentSlide.Add(text);
                    }
                    break;

                case RT_TextBytesAtom:
                    // ANSI text (Windows-1252)
                    if (recLen >= 1)
                    {
                        var text = Encoding.GetEncoding(1252).GetString(data, recDataOffset, recLen);
                        if (!string.IsNullOrWhiteSpace(text))
                            currentSlide.Add(text);
                    }
                    break;
            }

            // Container records (high nibble of ver/inst has 0xF) contain nested records,
            // so we step into them. Leaf records we skip over.
            var verInst = BitConverter.ToUInt16(data, pos);
            var isContainer = (verInst & 0x000F) == 0x000F;
            pos = isContainer ? recDataOffset : recDataOffset + recLen;
        }

        // Don't forget the last slide
        if (currentSlide.Count > 0)
            slides.Add(currentSlide);

        return slides;
    }
}
