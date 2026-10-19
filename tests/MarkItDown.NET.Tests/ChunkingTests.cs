using MarkItDown.NET.Chunking;

namespace MarkItDown.NET.Tests;

[TestFixture]
public sealed class ChunkingTests
{
    [TestCase("\n")]
    [TestCase("\r\n")]
    public void StructuralChunksHaveExactSourceRangesIncludingRepeatedHeadingText(string newline)
    {
        var markdown = string.Join(newline, new[]
        {
            "# repeated", "", "  repeated  ", "", "| Column | Value |", "| --- | --- |", "| a | b |",
            "", "```text", "repeated", "```", "", "## repeated", "", "repeated"
        });
        var chunks = MarkdownChunker.Chunk(markdown, "source.md", "source", new() { ContextMode = ContextMode.Metadata });
        chunks.Count.ShouldBe(4);
        chunks[0].Metadata.StartLine.ShouldBe(3);
        chunks[1].Metadata.StartLine.ShouldBe(5);
        chunks[2].Metadata.StartLine.ShouldBe(9);
        chunks[3].Metadata.StartLine.ShouldBe(15);
        foreach (var chunk in chunks)
        {
            var metadata = chunk.Metadata;
            markdown.Substring(metadata.SourceOffset, metadata.SourceLength).ShouldBe(chunk.Content);
            (1 + markdown[..metadata.SourceOffset].Count(character => character == '\n')).ShouldBe(metadata.StartLine);
            (metadata.StartLine + chunk.Content.Count(character => character == '\n')).ShouldBe(metadata.EndLine);
        }
    }

    [Test]
    public void SplittingPreservesSeparatorsAndSurrogatePairsWithoutLosingText()
    {
        var markdown = "# Source\n\n" + string.Concat(Enumerable.Repeat("Alpha, beta. Gamma\n\nUnicode \U0001F680 end; ", 100));
        var chunks = MarkdownChunker.Chunk(markdown, "source.md", "source",
            new() { ChunkSize = 100, ChunkOverlap = 20, ContextMode = ContextMode.Metadata });
        var covered = new bool[markdown.Length];
        foreach (var chunk in chunks)
        {
            markdown.Substring(chunk.Metadata.SourceOffset, chunk.Metadata.SourceLength).ShouldBe(chunk.Content);
            chunk.Content.Length.ShouldBeLessThanOrEqualTo(100);
            char.IsLowSurrogate(chunk.Content[0]).ShouldBeFalse();
            char.IsHighSurrogate(chunk.Content[^1]).ShouldBeFalse();
            for (var index = chunk.Metadata.SourceOffset; index < chunk.Metadata.SourceOffset + chunk.Metadata.SourceLength; index++)
                covered[index] = true;
        }
        for (var index = markdown.IndexOf("Alpha", StringComparison.Ordinal); index < markdown.TrimEnd().Length; index++)
            covered[index].ShouldBeTrue($"Source character {index} was dropped.");
    }

    [Test]
    public void SourceArtifactsCanRetainFactsThatAppearOnlyInHeadings()
    {
        var chunks = MarkdownChunker.Chunk("# Response is 17 minutes\n\n![image](image.png)", "source.md", "source",
            new() { ContextMode = ContextMode.Metadata, IncludeHeadingLines = true });
        chunks.Count.ShouldBe(1);
        chunks[0].Content.ShouldContain("# Response is 17 minutes");
        chunks[0].Metadata.StartLine.ShouldBe(1);
    }

    [Test]
    public void CodeAndTableAtomicFlagsAreIndependent()
    {
        var markdown = "```\n" + new string('c', 200) + "\n```\n\n| " + new string('t', 200) + " |\n";
        var chunks = MarkdownChunker.Chunk(markdown, "source.md", "source", new()
        { ChunkSize = 100, ChunkOverlap = 10, RespectCodeBlocks = false, RespectTables = true, ContextMode = ContextMode.Metadata });
        chunks.Count.ShouldBeGreaterThan(2);
        chunks[^1].Metadata.Oversized.ShouldBeTrue();
        chunks.Take(chunks.Count - 1).All(chunk => !chunk.Metadata.Oversized).ShouldBeTrue();
    }

    [TestCase(0, 0)]
    [TestCase(100, 100)]
    [TestCase(100, -1)]
    public void InvalidConfigurationFailsExplicitly(int size, int overlap) =>
        Should.Throw<ArgumentException>(() => MarkdownChunker.Chunk("text", "a.md", "a", new() { ChunkSize = size, ChunkOverlap = overlap }));
}
