using System.Security.Cryptography;
using System.Text;

namespace MarkItDown.NET;

public static class ContentHasher
{
    /// <summary>
    /// Computes SHA-256 hash of a stream. Resets stream position if the stream is seekable.
    /// </summary>
    public static async Task<string> ComputeSha256Async(Stream stream, CancellationToken ct = default)
    {
        if (stream.CanSeek)
            stream.Position = 0;
        var hash = await SHA256.HashDataAsync(stream, ct);
        if (stream.CanSeek)
            stream.Position = 0;
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Computes MD5 hash of a string (used for content change detection).
    /// </summary>
    public static string ComputeMd5(string content)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(hash);
    }
}
