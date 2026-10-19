using System.Text.RegularExpressions;

namespace MarkItDown.NET.Chunking;

/// <summary>
/// L1: Markdown structure-aware splitter.
/// Scans lines to detect headings, fenced code blocks, tables, and horizontal rules,
/// producing <see cref="StructuralBlock"/> items that preserve heading context.
/// </summary>
public static partial class MarkdownSplitter
{
    [GeneratedRegex(@"^(#{1,6})\s+(.+)$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^(`{3,}|~{3,})")]
    private static partial Regex FenceRegex();

    [GeneratedRegex(@"^\|.*\|")]
    private static partial Regex TableRowRegex();

    [GeneratedRegex(@"^(-{3,}|\*{3,}|_{3,})\s*$")]
    private static partial Regex HrRegex();

    public static List<StructuralBlock> Split(string markdown, int maxHeadingDepth = 6, bool includeHeadingLines = false)
    {
        if (string.IsNullOrEmpty(markdown))
            return [];

        var blocks = new List<StructuralBlock>();
        var lines = markdown.Split('\n');
        var headingChain = new List<string>();
        var currentLines = new List<string>();
        int blockStartLine = 0;

        bool inCodeBlock = false;
        string? codeFence = null;
        int codeStartLine = 0;
        var codeLines = new List<string>();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // ── Code block state machine ──
            if (inCodeBlock)
            {
                codeLines.Add(line);
                var fenceMatch = FenceRegex().Match(line);
                if (fenceMatch.Success && line.TrimEnd().StartsWith(codeFence!))
                {
                    // Closing fence
                    inCodeBlock = false;
                    FlushParagraph(blocks, currentLines, headingChain, blockStartLine);
                    currentLines.Clear();
                    blocks.Add(new StructuralBlock(
                        string.Join('\n', codeLines),
                        BlockType.CodeBlock,
                        headingChain.ToArray(),
                        codeStartLine));
                    codeLines.Clear();
                    codeFence = null;
                    blockStartLine = i + 1;
                }
                continue;
            }

            // ── Opening code fence ──
            var openFence = FenceRegex().Match(line);
            if (openFence.Success)
            {
                FlushParagraph(blocks, currentLines, headingChain, blockStartLine);
                currentLines.Clear();
                inCodeBlock = true;
                codeFence = openFence.Groups[1].Value;
                codeStartLine = i;
                codeLines.Clear();
                codeLines.Add(line);
                continue;
            }

            // ── Heading ──
            var headingMatch = HeadingRegex().Match(line);
            if (headingMatch.Success)
            {
                int level = headingMatch.Groups[1].Value.Length;
                if (level <= maxHeadingDepth)
                {
                    FlushParagraph(blocks, currentLines, headingChain, blockStartLine);
                    currentLines.Clear();

                    string title = headingMatch.Groups[2].Value.Trim();
                    // Trim chain to parent level, then push
                    while (headingChain.Count >= level)
                        headingChain.RemoveAt(headingChain.Count - 1);
                    headingChain.Add(title);

                    blockStartLine = i + 1;
                    if (includeHeadingLines)
                    {
                        blockStartLine = i;
                        currentLines.Add(line);
                    }
                    continue;
                }
            }

            // ── Horizontal rule ──
            if (HrRegex().IsMatch(line))
            {
                FlushParagraph(blocks, currentLines, headingChain, blockStartLine);
                currentLines.Clear();
                blocks.Add(new StructuralBlock("", BlockType.Hr, headingChain.ToArray(), i));
                blockStartLine = i + 1;
                continue;
            }

            // ── Table rows (accumulate consecutive) ──
            if (TableRowRegex().IsMatch(line))
            {
                if (currentLines.Count == 0) blockStartLine = i;
                // If we're not already accumulating table rows, flush paragraph first
                if (currentLines.Count > 0 && !TableRowRegex().IsMatch(currentLines[^1]))
                {
                    FlushParagraph(blocks, currentLines, headingChain, blockStartLine);
                    currentLines.Clear();
                    blockStartLine = i;
                }

                currentLines.Add(line);

                // Peek: if next line is not a table row, flush as table block
                bool nextIsTable = i + 1 < lines.Length && TableRowRegex().IsMatch(lines[i + 1]);
                if (!nextIsTable)
                {
                    var tableContent = string.Join('\n', currentLines);
                    blocks.Add(new StructuralBlock(
                        tableContent,
                        BlockType.Table,
                        headingChain.ToArray(),
                        blockStartLine));
                    currentLines.Clear();
                    blockStartLine = i + 1;
                }
                continue;
            }

            // ── Regular line ──
            if (currentLines.Count == 0) blockStartLine = i;
            currentLines.Add(line);
        }

        // Flush remaining
        if (inCodeBlock && codeLines.Count > 0)
        {
            // Unclosed code block — treat as code block anyway
            FlushParagraph(blocks, currentLines, headingChain, blockStartLine);
            blocks.Add(new StructuralBlock(
                string.Join('\n', codeLines),
                BlockType.CodeBlock,
                headingChain.ToArray(),
                codeStartLine));
        }
        else
        {
            FlushParagraph(blocks, currentLines, headingChain, blockStartLine);
        }

        return blocks;
    }

    private static void FlushParagraph(
        List<StructuralBlock> blocks,
        List<string> lines,
        List<string> headingChain,
        int startLine)
    {
        if (lines.Count == 0) return;
        var text = string.Join('\n', lines).Trim();
        if (text.Length == 0) return;
        for (var index = 0; index < lines.Count && string.IsNullOrWhiteSpace(lines[index]); index++)
            startLine++;

        blocks.Add(new StructuralBlock(
            text,
            BlockType.Paragraph,
            headingChain.ToArray(),
            startLine));
    }
}
