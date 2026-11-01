using System.Globalization;
using FinanceService.Data.Entities;
using KJWebsite.BuildingBlocks.Finance;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace FinanceService.Receipts;

public sealed record ReceiptDocument(
    string Number,
    DateOnly IssuedOn,
    DateOnly ReceivedOn,
    string DonorName,
    Money Amount,
    Money Fee,
    string? Method,
    string DesignationType,
    string? DesignationId,
    bool IsInKind,
    string? ItemDescription,
    string? ValuationBasis,
    OrganisationDetails Organisation,
    int Copies,
    bool Void,
    string Fingerprint);

public sealed record StatementLine(string Number, DateOnly ReceivedOn, Money Amount, bool InKind, string Designation);

public sealed record StatementTotal(string Currency, Money Cash, Money InKind);

public sealed record StatementDocument(OrganisationDetails Organisation, string DonorName, string FiscalYear, IReadOnlyList<StatementLine> Lines, IReadOnlyList<StatementTotal> Totals);

/// <summary>
/// Bilingual receipts and statements. The PDF uses an embedded Noto Sans Bengali (SIL OFL) so Bengali conjuncts shape properly on a server with no system fonts, and amounts
/// and numbers are printed in Bengali numerals on the Bengali line as well as in Latin digits. QuestPDF is used under its Community licence.
/// </summary>
public static class ReceiptPdf
{
    private const string FontFamily = "Noto Sans Bengali";
    private static readonly Lock Gate = new();
    private static bool _ready;

    /// <summary>"1250.50" becomes "১২৫০.৫০".</summary>
    public static string BengaliDigits(string text) => string.Create(text.Length, text, (span, source) =>
    {
        for (var i = 0; i < source.Length; i++)
        {
            span[i] = source[i] is >= '0' and <= '9' ? (char)('০' + (source[i] - '0')) : source[i];
        }
    });

    public static string MethodLabel(string? method) => method switch
    {
        "bank" => "Bank transfer / ব্যাংক ট্রান্সফার",
        "cash" => "Cash / নগদ",
        "cheque" => "Cheque / চেক",
        "in_kind" => "In-kind gift / সামগ্রী উপহার",
        "bkash" => "bKash / বিকাশ",
        "nagad" => "Nagad / নগদ (মোবাইল)",
        "sslcommerz" => "Online payment / অনলাইন পেমেন্ট",
        "stripe" => "Card / কার্ড",
        "paypal" => "PayPal / পেপাল",
        _ => "-"
    };

    public static string DesignationLabel(string type, string? id) => type == Designations.General ? "General fund / সাধারণ তহবিল" : $"{(type == Designations.Campaign ? "Campaign / ক্যাম্পেইন" : "Project / প্রকল্প")}: {id}";

    public static byte[] Render(ReceiptDocument d)
    {
        EnsureReady();
        var org = d.Organisation;
        return Document.Create(document => document.Page(page =>
        {
            page.Size(PageSizes.A5);
            page.Margin(14, Unit.Millimetre);
            page.DefaultTextStyle(style => style.FontFamily(FontFamily).FontSize(10));
            page.Content().Column(column =>
            {
                column.Spacing(4);
                column.Item().Text(org.NameBn).FontSize(18).Bold();
                column.Item().Text(org.NameEn).FontSize(12);
                if (org.RegistrationNumber.Length > 0)
                {
                    column.Item().Text($"Registration no. / নিবন্ধন নং: {org.RegistrationNumber}").FontSize(8);
                }

                if (org.Address.Length > 0)
                {
                    column.Item().Text(org.Address).FontSize(8);
                }

                column.Item().PaddingTop(8).Text("Donation receipt · অনুদানের রসিদ").FontSize(14).SemiBold();
                if (d.Void)
                {
                    column.Item().Background(Colors.Red.Lighten4).Padding(3).Text("VOID · বাতিল: the donation was reversed / অনুদান ফেরত দেওয়া হয়েছে").FontColor(Colors.Red.Darken3).Bold();
                }
                else if (d.Copies > 0)
                {
                    column.Item().Background(Colors.Grey.Lighten3).Padding(3).Text($"DUPLICATE COPY {d.Copies} · অনুলিপি {BengaliDigits(d.Copies.ToString(CultureInfo.InvariantCulture))}: the original keeps this number").Bold();
                }

                Line(column, "Receipt no. · রসিদ নং", $"{d.Number}  ({BengaliDigits(d.Number)})");
                Line(column, "Date issued · প্রদানের তারিখ", Date(d.IssuedOn));
                Line(column, "Date received · প্রাপ্তির তারিখ", Date(d.ReceivedOn));
                Line(column, "Received from · দাতা", d.DonorName);
                var amount = d.Amount.ToDecimalString();
                Line(column, d.IsInKind ? "Value of gift · মূল্য" : "Amount · পরিমাণ", $"{d.Amount.Currency} {amount}  ({d.Amount.Currency} {BengaliDigits(amount)})");
                if (d.Fee.IsPositive)
                {
                    Line(column, "Includes fee covered · ফি সহ", $"{d.Amount.Currency} {d.Fee.ToDecimalString()} (not part of the gift / অনুদানের অংশ নয়)");
                }

                Line(column, "Method · মাধ্যম", MethodLabel(d.Method));
                Line(column, "Designation · খাত", DesignationLabel(d.DesignationType, d.DesignationId));
                if (d.IsInKind)
                {
                    Line(column, "Item · সামগ্রী", d.ItemDescription ?? "-");
                    Line(column, "Valuation basis · মূল্যায়ন", d.ValuationBasis ?? "-");
                }

                column.Item().PaddingTop(8).Text("Thank you for your support. ধন্যবাদ।").FontSize(9);
                if (org.Footer.Length > 0)
                {
                    column.Item().Text(org.Footer).FontSize(7);
                }

                column.Item().Text($"Verification code: {d.Fingerprint}").FontSize(7).FontColor(Colors.Grey.Darken1);
            });
        })).GeneratePdf();
    }

    public static byte[] RenderStatement(StatementDocument s)
    {
        EnsureReady();
        return Document.Create(document => document.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(16, Unit.Millimetre);
            page.DefaultTextStyle(style => style.FontFamily(FontFamily).FontSize(10));
            page.Content().Column(column =>
            {
                column.Spacing(4);
                column.Item().Text(s.Organisation.NameBn).FontSize(18).Bold();
                column.Item().Text(s.Organisation.NameEn).FontSize(12);
                if (s.Organisation.RegistrationNumber.Length > 0)
                {
                    column.Item().Text($"Registration no. / নিবন্ধন নং: {s.Organisation.RegistrationNumber}").FontSize(8);
                }

                column.Item().PaddingTop(8).Text($"Annual donation statement · বার্ষিক অনুদান বিবরণী {s.FiscalYear} ({BengaliDigits(s.FiscalYear)})").FontSize(14).SemiBold();
                column.Item().Text($"Donor · দাতা: {s.DonorName}");
                column.Item().PaddingTop(6).Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        c.RelativeColumn(3);
                        c.RelativeColumn(2);
                        c.RelativeColumn(3);
                        c.RelativeColumn(2);
                    });
                    table.Header(h =>
                    {
                        h.Cell().Text("Receipt · রসিদ").SemiBold();
                        h.Cell().Text("Date · তারিখ").SemiBold();
                        h.Cell().Text("Designation · খাত").SemiBold();
                        h.Cell().AlignRight().Text("Amount · পরিমাণ").SemiBold();
                    });
                    foreach (var line in s.Lines)
                    {
                        table.Cell().Text(line.Number);
                        table.Cell().Text(Date(line.ReceivedOn));
                        table.Cell().Text(line.Designation);
                        table.Cell().AlignRight().Text($"{line.Amount.Currency} {line.Amount.ToDecimalString()}{(line.InKind ? " (in kind)" : string.Empty)}");
                    }
                });
                column.Item().PaddingTop(8).Text("Totals · মোট").SemiBold();
                foreach (var total in s.Totals)
                {
                    column.Item().Text($"{total.Currency}: {total.Cash.ToDecimalString()} ({BengaliDigits(total.Cash.ToDecimalString())}) money; {total.InKind.ToDecimalString()} in-kind value");
                }

                if (s.Organisation.Footer.Length > 0)
                {
                    column.Item().PaddingTop(8).Text(s.Organisation.Footer).FontSize(7);
                }
            });
        })).GeneratePdf();
    }

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static void Line(ColumnDescriptor column, string label, string value) =>
        column.Item().Row(row =>
        {
            row.ConstantItem(150).Text(label).FontSize(9).FontColor(Colors.Grey.Darken2);
            row.RelativeItem().Text(value);
        });

    private static void EnsureReady()
    {
        lock (Gate)
        {
            if (_ready)
            {
                return;
            }

            QuestPDF.Settings.License = LicenseType.Community;
            using var font = typeof(ReceiptPdf).Assembly.GetManifestResourceStream("Fonts.NotoSansBengali.ttf")
                ?? throw new InvalidOperationException("The embedded Bengali font is missing.");
            QuestPDF.Drawing.FontManager.RegisterFontFromStream(font);
            _ready = true;
        }
    }
}
