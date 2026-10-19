namespace MarkItDown.NET.Chunking;

public record ChunkerConfig
{
    public int ChunkSize { get; init; } = 1000;
    public int ChunkOverlap { get; init; } = 100;
    public ContextMode ContextMode { get; init; } = ContextMode.Prefix;
    public bool RespectCodeBlocks { get; init; } = true;
    public bool RespectTables { get; init; } = true;
    public int MaxHeadingDepth { get; init; } = 6;
    public bool IncludeHeadingLines { get; init; }
    public string[] Separators { get; init; } =
        ["\n\n", "\n", "。", ". ", "；", "; ", "，", ", ", " ", ""];
}
