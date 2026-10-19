using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Extensions.Logging;

namespace MarkItDown.NET.Converters;

/// <summary>
/// Converts .xlsx files using Open XML SDK → Markdown tables.
/// Each worksheet becomes a separate Markdown section with a heading.
/// </summary>
public class ExcelConverter : IDocumentConverter
{
    private readonly ILogger<ExcelConverter>? _logger;

    public ExcelConverter(ILogger<ExcelConverter>? logger = null) => _logger = logger;

    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".xlsx"];

    public bool CanConvert(string extension)
        => extension is ".xlsx";

    public Task<ConvertResult> ConvertAsync(ConvertContext context, CancellationToken ct = default)
    {
        _logger?.LogInformation("Converting Excel document {Filename}", context.Filename);

        using var doc = SpreadsheetDocument.Open(context.Content, false);
        var workbookPart = doc.WorkbookPart;
        if (workbookPart?.Workbook?.Sheets is null)
            return Task.FromResult(EmptyResult());

        var sb = new StringBuilder();
        var sheets = workbookPart!.Workbook.Sheets!.Elements<Sheet>().ToList();
        var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable;

        foreach (var sheet in sheets)
        {
            var sheetName = sheet.Name?.Value ?? "Sheet";
            var relationshipId = sheet.Id?.Value;
            if (relationshipId is null) continue;

            var worksheetPart = workbookPart.GetPartById(relationshipId) as WorksheetPart;
            if (worksheetPart?.Worksheet is null) continue;
            var sheetData = worksheetPart.Worksheet.GetFirstChild<SheetData>();
            if (sheetData is null) continue;

            var rows = sheetData.Elements<Row>().ToList();
            if (rows.Count == 0) continue;

            // Parse rows into sparse value maps keyed by real column index, dropping
            // entirely-empty rows and trimming to the actual used column range. This
            // guards against the "ghost" rows/columns Excel persists far beyond the
            // real data (e.g. a declared dimension of A1:R1048534 with only ~136
            // populated rows), which would otherwise explode into a multi-million-
            // character Markdown table and break downstream chunking/embedding.
            var parsedRows = new List<Dictionary<int, string>>();
            var maxColIndex = -1;

            foreach (var row in rows)
            {
                var map = new Dictionary<int, string>();
                foreach (var cell in row.Elements<Cell>())
                {
                    var colIndex = GetColumnIndex(cell.CellReference?.Value);
                    if (colIndex < 0) continue;

                    var value = GetCellValue(cell, sharedStrings);
                    if (string.IsNullOrEmpty(value)) continue;

                    map[colIndex] = value;
                    if (colIndex > maxColIndex) maxColIndex = colIndex;
                }

                if (map.Count > 0)
                    parsedRows.Add(map);
            }

            if (parsedRows.Count == 0 || maxColIndex < 0) continue;

            var maxCols = maxColIndex + 1;

            sb.AppendLine($"## {sheetName}");
            sb.AppendLine();

            for (var i = 0; i < parsedRows.Count; i++)
            {
                var map = parsedRows[i];
                var values = new string[maxCols];

                foreach (var kv in map)
                {
                    if (kv.Key < maxCols)
                        values[kv.Key] = kv.Value;
                }

                sb.Append("| ");
                sb.Append(string.Join(" | ", values.Select(v => v ?? "")));
                sb.AppendLine(" |");

                if (i == 0)
                {
                    sb.Append("| ");
                    sb.Append(string.Join(" | ", Enumerable.Range(0, maxCols).Select(_ => "---")));
                    sb.AppendLine(" |");
                }
            }

            sb.AppendLine();
        }

        var contentMarkdown = sb.ToString().TrimEnd();
        var md5 = ContentHasher.ComputeMd5(contentMarkdown);

        _logger?.LogInformation("Excel convert complete: {Sheets} sheets, {ContentLen} chars",
            sheets.Count, contentMarkdown.Length);

        return Task.FromResult(new ConvertResult
        {
            Markdown = contentMarkdown,
            ContentMd5 = md5,
            PageCount = sheets.Count,
        });
    }

    private static string GetCellValue(Cell cell, SharedStringTable? sharedStrings)
    {
        var value = cell.CellValue?.InnerText ?? "";

        if (cell.DataType?.Value == CellValues.SharedString &&
            sharedStrings is not null &&
            int.TryParse(value, out var index))
        {
            var item = sharedStrings.ElementAtOrDefault(index);
            return item?.InnerText ?? value;
        }

        return value;
    }

    private static int GetColumnIndex(string? cellReference)
    {
        if (string.IsNullOrEmpty(cellReference)) return -1;

        var col = 0;
        foreach (var c in cellReference)
        {
            if (char.IsLetter(c))
                col = col * 26 + (char.ToUpper(c) - 'A' + 1);
            else
                break;
        }
        return col - 1;
    }

    private static ConvertResult EmptyResult() => new() { Markdown = "", ContentMd5 = ContentHasher.ComputeMd5(""), PageCount = 0 };
}
