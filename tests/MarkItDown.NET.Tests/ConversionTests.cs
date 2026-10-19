using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using MarkItDown.NET.Worker;
using MarkItDown.NET.Converters;
using UglyToad.PdfPig.Writer;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace MarkItDown.NET.Tests;

[TestFixture]
public sealed class ConversionTests
{
    private string root = null!;

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "markitdown-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(root, recursive: true);

    [TestCase("docx")]
    [TestCase("xlsx")]
    [TestCase("pptx")]
    public async Task OfficeDocumentsConvertToMarkdown(string extension)
    {
        var path = CreateOffice(extension);
        var result = await new DocumentConverter().ConvertAsync(path);
        result.Markdown.ShouldContain("中文内容");
        result.PageCount.ShouldBe(1);
        var output = Path.Combine(root, "converted");
        await ConversionWorker.ConvertAsync(path, output);
        var manifest = JsonSerializer.Deserialize<ConversionManifest>(
            await File.ReadAllTextAsync(Path.Combine(output, "result.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        manifest.Format.ShouldBe(2);
        manifest.MarkdownFile.ShouldBe("document.md");
        manifest.MarkdownSha256.ShouldBe(Convert.ToHexString(SHA256.HashData(
            await File.ReadAllBytesAsync(Path.Combine(output, "document.md")))).ToLowerInvariant());
        manifest.ConverterVersion.ShouldNotBeEmpty();
        manifest.ChunksFile.ShouldBe("chunks.json");
        manifest.ChunksSha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(
            await File.ReadAllBytesAsync(Path.Combine(output, "chunks.json")))));
    }

    [Test]
    public async Task WorkerRunsAsAnIndependentProcess()
    {
        var path = CreateOffice("docx");
        var output = Path.Combine(root, "process-output");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { typeof(ConversionWorker).Assembly.Location, "convert", path, output })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        process.ExitCode.ShouldBe(0, await error);
        File.Exists(Path.Combine(output, "result.json")).ShouldBeTrue();
    }

    [Test]
    public void CloudFormatsAreNotEnabledByInheritedCredentials()
    {
        var converter = new DocumentConverter();
        converter.CanConvert("scan.pdf").ShouldBeTrue();
        converter.CanConvert("scan.png").ShouldBeFalse();
        converter.CanConvert("report.docx").ShouldBeTrue();
    }

    [Test]
    public async Task TextPdfConvertsLocallyWithPageMappedChunks()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(600, 800).AddText("Page one local evidence.", 12, new PdfPoint(30, 700), font);
        builder.AddPage(600, 800).AddText("P1 response time is 17 minutes.", 12, new PdfPoint(30, 700), font);
        var path = Path.Combine(root, "local.pdf");
        await File.WriteAllBytesAsync(path, builder.Build());
        var result = await new DocumentConverter().ConvertAsync(path);
        result.PageCount.ShouldBe(2);
        result.Markdown.ShouldContain("P1 response time is 17 minutes.");
        result.Images.ShouldBeEmpty();
        var output = Path.Combine(root, "pdf-output");
        await ConversionWorker.ConvertAsync(path, output);
        var chunks = JsonSerializer.Deserialize<ConversionChunks>(await File.ReadAllTextAsync(Path.Combine(output, "chunks.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var markdown = await File.ReadAllTextAsync(Path.Combine(output, "document.md"));
        chunks.Chunks.Length.ShouldBe(2);
        chunks.Chunks[1].PageNumber.ShouldBe(2);
        markdown.Substring(chunks.Chunks[1].Offset, chunks.Chunks[1].Length).ShouldBe("# Page 2\n\nP1 response time is 17 minutes.");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { typeof(ConversionWorker).Assembly.Location, "convert", path, Path.Combine(root, "inherited-output") })
            start.ArgumentList.Add(argument);
        start.Environment["MARKITDOWN_AZURE_DI_ENDPOINT"] = "https://127.0.0.1:1";
        start.Environment["MARKITDOWN_AZURE_DI_API_KEY"] = "test-only";
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        process.ExitCode.ShouldBe(0, await error);
    }

    [Test]
    public async Task HeadingPreviewIsBoundedWithoutTruncatingSourceEvidence()
    {
        var path = Path.Combine(root, "heading.md");
        var text = "# \t" + new string('h', 5000);
        await File.WriteAllTextAsync(path, text);
        var output = Path.Combine(root, "heading-output");
        await ConversionWorker.ConvertAsync(path, output);
        var chunks = JsonSerializer.Deserialize<ConversionChunks>(await File.ReadAllTextAsync(Path.Combine(output, "chunks.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        chunks.Chunks.All(chunk => chunk.Heading.Length == 103 && chunk.Heading.EndsWith("...", StringComparison.Ordinal) &&
            !chunk.Heading.Any(char.IsControl)).ShouldBeTrue();
        chunks.Chunks[0].Offset.ShouldBe(0);
        (chunks.Chunks[^1].Offset + chunks.Chunks[^1].Length).ShouldBe(text.Length);
    }

    [Test]
    public async Task PdfWithoutTextExplicitlyRequiresOcrAndDoesNotPublishSuccess()
    {
        var builder = new PdfDocumentBuilder();
        builder.AddPage(600, 800);
        var path = Path.Combine(root, "scan.pdf");
        await File.WriteAllBytesAsync(path, builder.Build());
        var output = Path.Combine(root, "scan-output");
        await Should.ThrowAsync<PdfTextUnavailableException>(() => ConversionWorker.ConvertAsync(path, output));
        File.Exists(Path.Combine(output, "result.json")).ShouldBeFalse();
        var start = new ProcessStartInfo("dotnet") { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { typeof(ConversionWorker).Assembly.Location, "convert", path, output })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        process.ExitCode.ShouldBe(6);
        (await error).ShouldContain("no cloud conversion was attempted");
    }

    [Test]
    public async Task EmptyOrInvalidDocumentsDoNotPublishManifests()
    {
        var path = Path.Combine(root, "empty.md");
        await File.WriteAllTextAsync(path, " ");
        await Should.ThrowAsync<InvalidDataException>(() => ConversionWorker.ConvertAsync(path, Path.Combine(root, "empty-output")));
        Directory.Exists(Path.Combine(root, "empty-output")).ShouldBeFalse();
        var word = Path.Combine(root, "broken.docx");
        await File.WriteAllTextAsync(word, "not an Office package");
        await Should.ThrowAsync<Exception>(() => ConversionWorker.ConvertAsync(word, Path.Combine(root, "broken-output")));
        File.Exists(Path.Combine(root, "broken-output", "result.json")).ShouldBeFalse();
    }

    [Test]
    public async Task CancellationAndExistingOutputAreExplicitFailures()
    {
        var path = CreateOffice("docx");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() =>
            ConversionWorker.ConvertAsync(path, Path.Combine(root, "cancelled"), token: cancellation.Token));
        await Should.ThrowAsync<IOException>(() => ConversionWorker.ConvertAsync(path, root));
        await Should.ThrowAsync<ArgumentException>(() =>
            ConversionWorker.ConvertAsync(path, Path.Combine(root, "escape"), "../escape.docx"));
    }

    private string CreateOffice(string extension)
    {
        var path = Path.Combine(root, "sample." + extension);
        if (extension == "docx")
        {
            using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
            var main = document.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body(
                new W.Paragraph(new W.ParagraphProperties(new W.ParagraphStyleId { Val = "Heading1" }),
                    new W.Run(new W.Text("中文内容"))),
                new W.Table(new W.TableRow(new W.TableCell(new W.Paragraph(new W.Run(new W.Text("表格内容"))))))));
            main.Document.Save();
        }
        else if (extension == "xlsx")
        {
            using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
            var workbook = document.AddWorkbookPart();
            workbook.Workbook = new S.Workbook();
            var worksheet = workbook.AddNewPart<WorksheetPart>();
            worksheet.Worksheet = new S.Worksheet(new S.SheetData(new S.Row(
                new S.Cell { CellReference = "A1", DataType = S.CellValues.String, CellValue = new S.CellValue("中文内容") })));
            workbook.Workbook.Append(new S.Sheets(new S.Sheet
                { Id = workbook.GetIdOfPart(worksheet), SheetId = 1, Name = "工作表" }));
            worksheet.Worksheet.Save();
            workbook.Workbook.Save();
        }
        else
        {
            using var document = PresentationDocument.Create(path, PresentationDocumentType.Presentation);
            var presentation = document.AddPresentationPart();
            presentation.Presentation = new P.Presentation();
            var slide = presentation.AddNewPart<SlidePart>();
            slide.Slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree(
                new P.NonVisualGroupShapeProperties(new P.NonVisualDrawingProperties { Id = 1, Name = "" },
                    new P.NonVisualGroupShapeDrawingProperties(), new P.ApplicationNonVisualDrawingProperties()),
                new P.GroupShapeProperties(),
                new P.Shape(new P.NonVisualShapeProperties(
                    new P.NonVisualDrawingProperties { Id = 2, Name = "Text" },
                    new P.NonVisualShapeDrawingProperties(), new P.ApplicationNonVisualDrawingProperties()),
                    new P.ShapeProperties(),
                    new P.TextBody(new A.BodyProperties(), new A.ListStyle(),
                        new A.Paragraph(new A.Run(new A.Text("中文内容"))))))));
            presentation.Presentation.Append(new P.SlideIdList(new P.SlideId
                { Id = 256, RelationshipId = presentation.GetIdOfPart(slide) }));
            slide.Slide.Save();
            presentation.Presentation.Save();
        }
        return path;
    }
}
