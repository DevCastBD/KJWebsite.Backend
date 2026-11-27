using System.Globalization;
using KJWebsite.BuildingBlocks.Exports;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ReportingService.Reports;

/// <summary>Report PDF. It uses the embedded Noto Sans Bengali (SIL OFL) so Bengali shapes correctly on a server with no system fonts. QuestPDF is used under its Community licence.</summary>
public static class ReportPdf
{
    private const string FontFamily = "Noto Sans Bengali";
    private static readonly Lock Gate = new();
    private static bool _ready;

    public static byte[] Render(string title, IReadOnlyList<ExportTable> tables)
    {
        EnsureReady();
        return Document.Create(document => document.Page(page =>
        {
            page.Size(PageSizes.A4.Landscape());
            page.Margin(24);
            page.DefaultTextStyle(style => style.FontFamily(FontFamily).FontSize(8));
            page.Header().Text(title).FontSize(14).Bold();
            page.Content().Column(column =>
            {
                foreach (var table in tables)
                {
                    column.Item().PaddingTop(10).Text(table.Name).FontSize(11).SemiBold();
                    if (table.Rows.Count == 0)
                    {
                        column.Item().Text("No rows.");
                        continue;
                    }

                    column.Item().Table(grid =>
                    {
                        grid.ColumnsDefinition(columns =>
                        {
                            foreach (var _ in table.Headers)
                            {
                                columns.RelativeColumn();
                            }
                        });
                        grid.Header(header =>
                        {
                            foreach (var name in table.Headers)
                            {
                                header.Cell().Background(Colors.Grey.Lighten3).Padding(2).Text(name).Bold();
                            }
                        });
                        foreach (var row in table.Rows)
                        {
                            foreach (var cell in row)
                            {
                                grid.Cell().BorderBottom(0.25f).Padding(2).Text(Format(cell));
                            }
                        }
                    });
                }
            });
            page.Footer().AlignCenter().Text(text =>
            {
                text.CurrentPageNumber();
                text.Span(" / ");
                text.TotalPages();
            });
        })).GeneratePdf();
    }

    private static string Format(object? cell) => cell switch
    {
        null => string.Empty,
        decimal d => d.ToString("0.00", CultureInfo.InvariantCulture),
        _ => Convert.ToString(cell, CultureInfo.InvariantCulture) ?? string.Empty
    };

    private static void EnsureReady()
    {
        lock (Gate)
        {
            if (_ready)
            {
                return;
            }

            QuestPDF.Settings.License = LicenseType.Community;
            using var font = typeof(ReportPdf).Assembly.GetManifestResourceStream("Fonts.NotoSansBengali.ttf")
                ?? throw new InvalidOperationException("The embedded Bengali font is missing.");
            QuestPDF.Drawing.FontManager.RegisterFontFromStream(font);
            _ready = true;
        }
    }
}
