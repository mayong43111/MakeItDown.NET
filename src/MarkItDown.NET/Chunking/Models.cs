namespace MarkItDown.NET.Chunking;

public enum BlockType { Paragraph, CodeBlock, Table, Hr }
public enum ContextMode { Metadata, Prefix }

public record StructuralBlock(
    string Content,
    BlockType BlockType,
    IReadOnlyList<string> HeadingChain,
    int SourceLine);

public record ChunkResult(
    string ChunkId,
    string Content,
    ChunkMetadata Metadata);

public record ChunkMetadata
{
    public string Id { get; init; } = "";
    public string Filename { get; init; } = "";
    public int SectionIndex { get; init; }
    public string[] HeadingChain { get; init; } = [];
    public int CharLength { get; init; }
    public bool Oversized { get; init; }
    public int SourceOffset { get; init; }
    public int SourceLength { get; init; }
    public int StartLine { get; init; }
    public int EndLine { get; init; }
}
