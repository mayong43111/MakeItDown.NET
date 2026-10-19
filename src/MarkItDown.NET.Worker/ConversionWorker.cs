using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarkItDown.NET.Chunking;

namespace MarkItDown.NET.Worker;

public sealed record ConversionAsset(string Path, string ContentType, string Sha256);
public sealed record ConversionManifest(int Format, string ConverterVersion, string MarkdownFile,
    string MarkdownSha256, int PageCount, ConversionAsset[] Assets, string ChunksFile, string ChunksSha256);
public sealed record ConversionChunks(int Format, string Algorithm, string MarkdownSha256, ConversionChunk[] Chunks);
public sealed record ConversionChunk(int Offset, int Length, string Heading, int StartLine, int EndLine, int? PageNumber);

public static class ConversionWorker
{
    public const long MaxInputBytes = 25 * 1024 * 1024;
    public const long MaxOutputBytes = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task ConvertAsync(string inputPath, string outputDirectory, string? filename = null,
        DocumentConverterOptions? options = null, CancellationToken token = default)
    {
        if (!Path.IsPathFullyQualified(inputPath) || !Path.IsPathFullyQualified(outputDirectory))
            throw new ArgumentException("Input and output paths must be absolute.");
        if (Path.Exists(outputDirectory)) throw new IOException("Output directory already exists.");
        filename ??= Path.GetFileName(inputPath);
        if (filename != Path.GetFileName(filename) || filename.IndexOfAny(['/', '\\', '\0']) >= 0)
            throw new ArgumentException("Filename must not contain a path.");
        await using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is <= 0 or > MaxInputBytes) throw new InvalidDataException("Input size is outside the supported limit.");
        ValidateZipExpansion(input);
        var converter = new DocumentConverter(options);
        token.ThrowIfCancellationRequested();
        var result = await converter.ConvertAsync(input, filename, ct: token);
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(result.Markdown))
            throw new InvalidDataException("Conversion produced no readable Markdown.");
        var markdown = result.Markdown.ReplaceLineEndings("\n");
        var markdownBytes = Encoding.UTF8.GetBytes(markdown);
        long total = markdownBytes.Length;
        if (total > MaxOutputBytes) throw new InvalidDataException("Conversion output is too large.");
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllBytesAsync(Path.Combine(outputDirectory, "document.md"), markdownBytes, token);
        var assets = new List<ConversionAsset>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        async Task SaveAsset(string folder, string name, byte[] bytes, string contentType)
        {
            if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || name.IndexOfAny(['/', '\\', ':', '\0']) >= 0 ||
                name is "." or ".." || name.Any(char.IsControl))
                throw new InvalidDataException("Converter returned an invalid asset name.");
            var relative = $"assets/{folder}/{name}";
            if (!seen.Add(relative)) throw new InvalidDataException("Converter returned duplicate asset names.");
            total += bytes.LongLength;
            if (total > MaxOutputBytes) throw new InvalidDataException("Conversion output is too large.");
            var directory = Path.Combine(outputDirectory, "assets", folder);
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(Path.Combine(directory, name), bytes, token);
            assets.Add(new(relative, contentType, Hash(bytes)));
        }
        foreach (var asset in result.Images) await SaveAsset("images", asset.Name, asset.Data, asset.ContentType);
        foreach (var asset in result.Tables) await SaveAsset("tables", asset.Name, asset.ImageData, "image/png");
        foreach (var asset in result.Figures) await SaveAsset("figures", asset.Name, asset.ImageData, "image/png");
        var version = typeof(DocumentConverter).Assembly.GetName().Version?.ToString()
            ?? throw new InvalidOperationException("Converter version is unavailable.");
        var chunks = MarkdownChunker.Chunk(markdown, filename, "document", new ChunkerConfig
        { ContextMode = ContextMode.Metadata, IncludeHeadingLines = true });
        if (chunks.Count is 0 or > 100000) throw new InvalidDataException("Conversion must produce 1-100000 source chunks.");
        static string HeadingPreview(string[] chain)
        {
            var heading = string.Concat(string.Join(" > ", chain).Select(character => char.IsControl(character) ? ' ' : character));
            if (heading.Length <= 100) return heading;
            var length = char.IsHighSurrogate(heading[99]) ? 99 : 100;
            return heading[..length] + "...";
        }
        var ranges = chunks.Select(chunk => new ConversionChunk(chunk.Metadata.SourceOffset, chunk.Metadata.SourceLength,
            HeadingPreview(chunk.Metadata.HeadingChain), chunk.Metadata.StartLine, chunk.Metadata.EndLine,
            result.SourcePages.FirstOrDefault(page => page.StartLine <= chunk.Metadata.StartLine && page.EndLine >= chunk.Metadata.EndLine)?.PageNumber)).ToArray();
        var chunkBytes = JsonSerializer.SerializeToUtf8Bytes(new ConversionChunks(1, "markitdown-structure-v1", Hash(markdownBytes), ranges), JsonOptions);
        if (chunkBytes.Length > 16 * 1024 * 1024 || total + chunkBytes.Length > MaxOutputBytes)
            throw new InvalidDataException("Conversion chunk output is too large.");
        await File.WriteAllBytesAsync(Path.Combine(outputDirectory, "chunks.json"), chunkBytes, token);
        var manifest = new ConversionManifest(2, version, "document.md", Hash(markdownBytes), result.PageCount, assets.ToArray(),
            "chunks.json", Hash(chunkBytes));
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "result.json"),
            JsonSerializer.Serialize(manifest, JsonOptions), token);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void ValidateZipExpansion(Stream input)
    {
        Span<byte> header = stackalloc byte[4];
        var read = input.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        input.Position = 0;
        if (read < 4 || header[0] != 0x50 || header[1] != 0x4b || header[2] != 0x03 || header[3] != 0x04) return;
        using (var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true))
        {
            if (archive.Entries.Count > 10000 || archive.Entries.Sum(entry => entry.Length) > 256L * 1024 * 1024 ||
                archive.Entries.Any(entry => entry.Length > 64L * 1024 * 1024))
                throw new InvalidDataException("Document ZIP expansion exceeds supported limits.");
        }
        input.Position = 0;
    }
}
