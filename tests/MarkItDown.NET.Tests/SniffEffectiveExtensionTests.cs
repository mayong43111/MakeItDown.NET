using MarkItDown.NET;

namespace MarkItDown.NET.Tests;

/// <summary>
/// Unit tests for the content-sniffing dispatch fix (ISSUE-012): files whose
/// extension disagrees with their actual binary format must be routed by content,
/// while well-labeled files and non-Office files must be left untouched.
/// </summary>
[TestFixture]
public class SniffEffectiveExtensionTests
{
    private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00];   // PK\x03\x04  (OOXML)
    private static readonly byte[] Ole2Magic = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1];  // OLE2 compound file
    private static readonly byte[] PdfMagic = [0x25, 0x50, 0x44, 0x46, 0x2D, 0x31];    // %PDF-1

    private static MemoryStream StreamOf(byte[] header) => new(header, writable: false);

    // --- Mislabeled legacy extension that is actually OOXML → upgrade to OOXML converter ---

    [TestCase(".ppt", ".pptx")]
    [TestCase(".xls", ".xlsx")]
    [TestCase(".doc", ".docx")]
    public void LegacyExtension_WithZipContent_IsCorrectedToOoxml(string ext, string expected)
    {
        var result = DocumentConverter.SniffEffectiveExtension(StreamOf(ZipMagic), ext);
        result.ShouldBe(expected);
    }

    // --- Mislabeled OOXML extension that is actually OLE2 → downgrade to legacy converter ---

    [TestCase(".pptx", ".ppt")]
    [TestCase(".pptm", ".ppt")]
    [TestCase(".xlsx", ".xls")]
    [TestCase(".xlsm", ".xls")]
    [TestCase(".docx", ".doc")]
    [TestCase(".docm", ".doc")]
    public void OoxmlExtension_WithOle2Content_IsCorrectedToLegacy(string ext, string expected)
    {
        var result = DocumentConverter.SniffEffectiveExtension(StreamOf(Ole2Magic), ext);
        result.ShouldBe(expected);
    }

    // --- Correctly-labeled files must NOT be rewritten ---

    [TestCase(".pptx")]
    [TestCase(".xlsx")]
    [TestCase(".docx")]
    public void OoxmlExtension_WithZipContent_IsUnchanged(string ext)
    {
        DocumentConverter.SniffEffectiveExtension(StreamOf(ZipMagic), ext).ShouldBe(ext);
    }

    [TestCase(".ppt")]
    [TestCase(".xls")]
    [TestCase(".doc")]
    public void LegacyExtension_WithOle2Content_IsUnchanged(string ext)
    {
        DocumentConverter.SniffEffectiveExtension(StreamOf(Ole2Magic), ext).ShouldBe(ext);
    }

    // --- Non-Office extensions are never touched, even with ZIP/OLE2 content ---

    [TestCase(".pdf")]
    [TestCase(".txt")]
    [TestCase(".md")]
    [TestCase("")]
    public void NonOfficeExtension_IsAlwaysUnchanged(string ext)
    {
        DocumentConverter.SniffEffectiveExtension(StreamOf(ZipMagic), ext).ShouldBe(ext);
        DocumentConverter.SniffEffectiveExtension(StreamOf(Ole2Magic), ext).ShouldBe(ext);
    }

    // --- Unrecognized content falls back to the original extension ---

    [Test]
    public void OfficeExtension_WithUnknownMagic_IsUnchanged()
    {
        DocumentConverter.SniffEffectiveExtension(StreamOf(PdfMagic), ".ppt").ShouldBe(".ppt");
        DocumentConverter.SniffEffectiveExtension(StreamOf(PdfMagic), ".pptx").ShouldBe(".pptx");
    }

    // --- Edge cases: short stream, empty stream, non-seekable stream ---

    [Test]
    public void ShortStream_IsUnchanged()
    {
        DocumentConverter.SniffEffectiveExtension(StreamOf([0x50, 0x4B]), ".ppt").ShouldBe(".ppt");
    }

    [Test]
    public void EmptyStream_IsUnchanged()
    {
        DocumentConverter.SniffEffectiveExtension(StreamOf([]), ".ppt").ShouldBe(".ppt");
    }

    [Test]
    public void NonSeekableStream_IsUnchanged()
    {
        using var inner = StreamOf(ZipMagic);
        using var nonSeekable = new NonSeekableStream(inner);
        DocumentConverter.SniffEffectiveExtension(nonSeekable, ".ppt").ShouldBe(".ppt");
    }

    // --- The stream position must be restored so downstream converters can read from the start ---

    [Test]
    public void StreamPosition_IsRestoredAfterSniffing()
    {
        using var stream = StreamOf(ZipMagic);
        stream.Position = 3;
        DocumentConverter.SniffEffectiveExtension(stream, ".ppt");
        stream.Position.ShouldBe(3);
    }

    [Test]
    public void ExtensionMatching_IsCaseInsensitive()
    {
        DocumentConverter.SniffEffectiveExtension(StreamOf(ZipMagic), ".PPT").ShouldBe(".pptx");
    }

    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
