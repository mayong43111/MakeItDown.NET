using Microsoft.Extensions.Logging;

namespace MarkItDown.NET.Chunking;

/// <summary>
/// Markdown structure-aware chunker. Two-stage pipeline:
/// L1 splits by Markdown structure (headings, code blocks, tables, HR),
/// L2 splits oversized blocks by prioritized separators with overlap.
/// </summary>
public static class MarkdownChunker
{
    /// <summary>
    /// Split Markdown text into chunks.
    /// </summary>
    /// <param name="markdown">Raw Markdown text</param>
    /// <param name="filename">Source filename (written into metadata)</param>
    /// <param name="documentId">Document ID (used for chunk_id generation)</param>
    /// <param name="config">Chunking configuration (null uses defaults)</param>
    /// <param name="logger">Optional logger for oversized block warnings</param>
    /// <returns>List of chunk results</returns>
    public static List<ChunkResult> Chunk(
        string markdown,
        string filename,
        string documentId,
        ChunkerConfig? config = null,
        ILogger? logger = null)
    {
        config ??= new ChunkerConfig();
        if (config.ChunkSize < 2 || config.ChunkOverlap < 0 || config.ChunkOverlap >= config.ChunkSize ||
            config.MaxHeadingDepth is < 1 or > 6 || config.Separators is null || config.Separators.Any(separator => separator is null))
            throw new ArgumentException("Invalid Markdown chunking configuration.");

        // L1: Structure-aware split
        var blocks = MarkdownSplitter.Split(markdown, config.MaxHeadingDepth, config.IncludeHeadingLines);

        // L2: Size-controlled split + assemble chunks
        var results = new List<ChunkResult>();
        int index = 0;
        var sourceCursor = 0;
        var lineOffsets = new List<int> { 0 };
        for (var offset = 0; offset < markdown.Length; offset++)
            if (markdown[offset] == '\n') lineOffsets.Add(offset + 1);
        int LineAt(int offset)
        {
            var found = lineOffsets.BinarySearch(offset);
            return found >= 0 ? found + 1 : ~found;
        }

        foreach (var block in blocks)
        {
            if (block.BlockType == BlockType.Hr)
                continue; // HR blocks carry no content
            var blockOffset = markdown.IndexOf(block.Content, lineOffsets[block.SourceLine], StringComparison.Ordinal);
            if (blockOffset < sourceCursor) throw new InvalidDataException("Markdown block has no exact source range.");
            sourceCursor = blockOffset + block.Content.Length;
            void Add(string content, int relativeOffset, bool oversizedBlock)
            {
                var offset = blockOffset + relativeOffset;
                if (string.IsNullOrWhiteSpace(content)) return;
                var chunk = BuildChunk(content, block.HeadingChain, documentId, filename, ref index,
                    config.ContextMode, oversizedBlock);
                results.Add(chunk with
                {
                    Metadata = chunk.Metadata with
                    {
                        SourceOffset = offset, SourceLength = content.Length,
                        StartLine = LineAt(offset), EndLine = LineAt(offset + content.Length - 1)
                    }
                });
            }

            bool isAtomic = block.BlockType == BlockType.CodeBlock && config.RespectCodeBlocks ||
                block.BlockType == BlockType.Table && config.RespectTables;
            bool oversized = block.Content.Length > config.ChunkSize;

            if (isAtomic)
            {
                // Atomic block: keep whole even if oversized
                if (oversized)
                    logger?.LogWarning(
                        "Oversized {BlockType} block ({Length} chars) in {Filename} at line {Line}, kept as single chunk",
                        block.BlockType, block.Content.Length, filename, block.SourceLine);

                Add(block.Content, 0, oversized);
            }
            else if (block.Content.Length <= config.ChunkSize)
            {
                // Fits in one chunk
                Add(block.Content, 0, false);
            }
            else
            {
                // L2: recursive split
                var subChunks = RecursiveSplitter.SplitRanges(
                    block.Content, config.ChunkSize, config.ChunkOverlap, config.Separators);

                foreach (var sub in subChunks)
                {
                    Add(block.Content.Substring(sub.Offset, sub.Length), sub.Offset, false);
                }
            }
        }

        return results;
    }

    private static ChunkResult BuildChunk(
        string content,
        IReadOnlyList<string> headingChain,
        string documentId,
        string filename,
        ref int index,
        ContextMode contextMode,
        bool oversized)
    {
        var chainArray = headingChain.Count > 0
            ? headingChain.ToArray()
            : [];

        string finalContent = contextMode == ContextMode.Prefix && chainArray.Length > 0
            ? string.Join(" > ", chainArray) + "\n\n" + content
            : content;

        var chunkId = $"{documentId}_section_{index}";
        var result = new ChunkResult(
            ChunkId: chunkId,
            Content: finalContent,
            Metadata: new ChunkMetadata
            {
                Id = chunkId,
                Filename = filename,
                SectionIndex = index,
                HeadingChain = chainArray,
                CharLength = finalContent.Length,
                Oversized = oversized,
            });

        index++;
        return result;
    }
}
