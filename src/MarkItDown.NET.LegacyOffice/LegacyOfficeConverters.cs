using MarkItDown.NET.Converters;

namespace MarkItDown.NET.LegacyOffice;

public static class LegacyOfficeConverters
{
    public static IReadOnlyCollection<IDocumentConverter> Create() =>
        [new LegacyWordConverter(), new LegacyExcelConverter(), new LegacyPowerPointConverter()];
}
