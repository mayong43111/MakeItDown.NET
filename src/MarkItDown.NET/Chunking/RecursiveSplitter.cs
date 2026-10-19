namespace MarkItDown.NET.Chunking;

public static class RecursiveSplitter
{
    public static List<string> Split(string text, int chunkSize, int chunkOverlap, string[] separators) =>
        SplitRanges(text, chunkSize, chunkOverlap, separators).Select(range => text.Substring(range.Offset, range.Length)).ToList();

    internal readonly record struct TextRange(int Offset, int Length);

    internal static List<TextRange> SplitRanges(string text, int chunkSize, int chunkOverlap, string[] separators)
    {
        if (chunkSize < 2 || chunkOverlap < 0 || chunkOverlap >= chunkSize || separators is null ||
            separators.Any(separator => separator is null))
            throw new ArgumentException("Invalid chunk size, overlap or separators.");
        var result = new List<TextRange>();
        for (var start = 0; start < text.Length;)
        {
            var end = Math.Min(text.Length, start + chunkSize);
            if (end < text.Length)
            {
                var minimum = start + Math.Max(chunkOverlap + 1, chunkSize / 2);
                foreach (var separator in separators.Where(separator => separator.Length > 0))
                {
                    var found = text.LastIndexOf(separator, end - 1, end - start, StringComparison.Ordinal);
                    if (found >= minimum && found + separator.Length <= end)
                    {
                        end = found + separator.Length;
                        break;
                    }
                }
                if (char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end])) end--;
            }
            result.Add(new(start, end - start));
            if (end == text.Length) break;
            var next = Math.Max(start + 1, end - chunkOverlap);
            if (char.IsLowSurrogate(text[next]) && char.IsHighSurrogate(text[next - 1])) next++;
            start = next;
        }
        return result;
    }
}
