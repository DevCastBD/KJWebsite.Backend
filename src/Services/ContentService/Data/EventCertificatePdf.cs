using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ContentService.Data;

/// <summary>
/// Attendance certificates. The PDF embeds Noto Sans Bengali (SIL OFL) so Bengali conjuncts shape correctly on a server with no system
/// fonts. QuestPDF is used under its Community licence, as in the Finance receipts.
/// </summary>
public static class EventCertificatePdf
{
    private const string FontFamily = "Noto Sans Bengali";
    private static readonly Lock Gate = new();
    private static bool _ready;

    public static byte[] Render(string number, string recipientName, string eventTitle, string dateLine, string organisationEn, string organisationBn)
    {
        EnsureReady();
        return Document.Create(document => document.Page(page =>
        {
            page.Size(PageSizes.A4.Landscape());
            page.Margin(48);
            page.DefaultTextStyle(style => style.FontFamily(FontFamily).FontSize(14));
            page.Content().Border(2).BorderColor(Colors.Grey.Darken2).Padding(32).Column(column =>
            {
                column.Spacing(14);
                column.Item().AlignCenter().Text($"{organisationBn} · {organisationEn}").FontSize(16).SemiBold();
                column.Item().AlignCenter().Text("Certificate of Participation / অংশগ্রহণের সনদ").FontSize(26).SemiBold();
                column.Item().AlignCenter().PaddingTop(12).Text("This is to certify that / এই মর্মে প্রত্যয়ন করা যাচ্ছে যে").FontColor(Colors.Grey.Darken2);
                column.Item().AlignCenter().Text(recipientName).FontSize(32).SemiBold();
                column.Item().AlignCenter().Text("took part in / অংশগ্রহণ করেছেন").FontColor(Colors.Grey.Darken2);
                column.Item().AlignCenter().Text(eventTitle).FontSize(22).SemiBold();
                column.Item().AlignCenter().Text(dateLine);
                column.Item().PaddingTop(24).AlignCenter().Text($"No. {number}").FontSize(10).FontColor(Colors.Grey.Darken1);
            });
        })).GeneratePdf();
    }

    private static void EnsureReady()
    {
        lock (Gate)
        {
            if (_ready)
            {
                return;
            }

            QuestPDF.Settings.License = LicenseType.Community;
            using var font = typeof(EventCertificatePdf).Assembly.GetManifestResourceStream("Fonts.NotoSansBengali.ttf")
                ?? throw new InvalidOperationException("The embedded Bengali font is missing.");
            QuestPDF.Drawing.FontManager.RegisterFontFromStream(font);
            _ready = true;
        }
    }
}
