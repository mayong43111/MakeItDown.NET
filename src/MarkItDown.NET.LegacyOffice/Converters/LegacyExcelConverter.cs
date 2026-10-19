using System.Text;
using Microsoft.Extensions.Logging;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;

namespace MarkItDown.NET.Converters;

/// <summary>
/// Converts legacy .xls (BIFF/OLE2) files using NPOI HSSF → Markdown tables.
/// Each worksheet becomes a separate Markdown section with a heading.
/// </summary>
public class LegacyExcelConverter : IDocumentConverter
{
    private readonly ILogger<LegacyExcelConverter>? _logger;

    public LegacyExcelConverter(ILogger<LegacyExcelConverter>? logger = null) => _logger = logger;

    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".xls"];

    public bool CanConvert(string extension)
        => extension is ".xls";

    public Task<ConvertResult> ConvertAsync(ConvertContext context, CancellationToken ct = default)
    {
        _logger?.LogInformation("Converting legacy .xls file {Filename}", context.Filename);

        HSSFWorkbook workbook;
        try
        {
            workbook = new HSSFWorkbook(context.Content);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to open .xls file {Filename}", context.Filename);
            throw new InvalidOperationException($"Cannot parse .xls file '{context.Filename}': {ex.Message}", ex);
        }

        try
        {
            var sb = new StringBuilder();
            var sheetCount = workbook.NumberOfSheets;

            for (var s = 0; s < sheetCount; s++)
            {
                var sheet = workbook.GetSheetAt(s);
                var sheetName = sheet.SheetName ?? $"Sheet{s + 1}";

                if (sheet.LastRowNum < 0) continue;

                var maxCols = 0;
                for (var r = sheet.FirstRowNum; r <= sheet.LastRowNum; r++)
                {
                    var row = sheet.GetRow(r);
                    if (row is not null && row.LastCellNum > maxCols)
                        maxCols = row.LastCellNum;
                }

                if (maxCols == 0) continue;

                sb.AppendLine($"## {sheetName}");
                sb.AppendLine();

                var isFirstRow = true;
                for (var r = sheet.FirstRowNum; r <= sheet.LastRowNum; r++)
                {
                    var row = sheet.GetRow(r);
                    var values = new string[maxCols];

                    if (row is not null)
                    {
                        for (var c = 0; c < maxCols; c++)
                        {
                            var cell = row.GetCell(c);
                            values[c] = GetCellValue(cell);
                        }
                    }

                    sb.Append("| ");
                    sb.Append(string.Join(" | ", values.Select(v => v ?? "")));
                    sb.AppendLine(" |");

                    if (isFirstRow)
                    {
                        sb.Append("| ");
                        sb.Append(string.Join(" | ", Enumerable.Range(0, maxCols).Select(_ => "---")));
                        sb.AppendLine(" |");
                        isFirstRow = false;
                    }
                }

                sb.AppendLine();
            }

            var contentMarkdown = sb.ToString().TrimEnd();
            var md5 = ContentHasher.ComputeMd5(contentMarkdown);

            _logger?.LogInformation("Legacy .xls convert complete: {Sheets} sheets, {ContentLen} chars",
                sheetCount, contentMarkdown.Length);

            return Task.FromResult(new ConvertResult
            {
                Markdown = contentMarkdown,
                ContentMd5 = md5,
                PageCount = sheetCount,
            });
        }
        finally
        {
            workbook.Close();
        }
    }

    private static string GetCellValue(ICell? cell)
    {
        if (cell is null) return "";

        return cell.CellType switch
        {
            CellType.String => cell.StringCellValue ?? "",
            CellType.Numeric => DateUtil.IsCellDateFormatted(cell)
                ? cell.DateCellValue?.ToString("yyyy-MM-dd") ?? ""
                : cell.NumericCellValue.ToString("G"),
            CellType.Boolean => cell.BooleanCellValue.ToString(),
            CellType.Formula => GetFormulaCellValue(cell),
            CellType.Blank => "",
            _ => cell.ToString() ?? "",
        };
    }

    private static string GetFormulaCellValue(ICell cell)
    {
        try
        {
            return cell.CachedFormulaResultType switch
            {
                CellType.String => cell.StringCellValue ?? "",
                CellType.Numeric => cell.NumericCellValue.ToString("G"),
                CellType.Boolean => cell.BooleanCellValue.ToString(),
                _ => cell.ToString() ?? "",
            };
        }
        catch
        {
            return cell.CellFormula ?? "";
        }
    }
}
