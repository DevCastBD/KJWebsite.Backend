using System.Globalization;
using KJWebsite.BuildingBlocks;
using MembershipService.Data;
using MembershipService.Intake;
using Microsoft.Extensions.Options;
using QRCoder;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MembershipService.Self;

/// <summary>
/// The membership card: member code, name in both scripts and a QR code. PDF output uses an embedded Noto Sans Bengali (SIL OFL)
/// so conjuncts such as "ক্ষ" and "ন্দ" are shaped properly; a system font would not be there on a server.
/// QuestPDF is used under its Community licence.
/// </summary>
public static class MembershipCard
{
    private const string FontFamily = "Noto Sans Bengali";
    private static readonly Lock Gate = new();
    private static bool _ready;

    public static void MapMemberCard(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/members/me/card", async (HttpContext context, MembershipDbContext db, IOptions<MembershipOptions> options) =>
            await SelfEndpoints.WithMember(context, db, member => Task.FromResult(Results.Ok(View(member, options.Value)))))
        .WithName("GetMyCard").WithSummary("Membership card data with its QR code (SVG)").WithTags("Member portal").Own();

        app.MapGet("/api/v1/members/me/card.pdf", async (HttpContext context, MembershipDbContext db, IOptions<MembershipOptions> options) =>
            await SelfEndpoints.WithMember(context, db, member =>
            {
                var bytes = Render(View(member, options.Value));
                context.Response.Headers.ContentDisposition = $"inline; filename=\"membership-card-{member.MemberCode}.pdf\"";
                return Task.FromResult(Results.File(bytes, "application/pdf"));
            }))
        .WithName("GetMyCardPdf").WithSummary("Membership card as a printable PDF (Bengali name rendered with an embedded font)").WithTags("Member portal").Own();
    }

    public static CardView View(Data.Entities.MemberEntity member, MembershipOptions options)
    {
        var payload = $"{options.CardVerifyUrl.TrimEnd('/')}/{member.MemberCode}";
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        var svg = new SvgQRCode(data).GetGraphic(4);
        return new CardView(
            member.MemberCode,
            new CardName($"{member.FirstName} {member.LastName}".Trim(), string.IsNullOrWhiteSpace(member.NameBn) ? $"{member.FirstName} {member.LastName}".Trim() : member.NameBn!),
            member.JoinedAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            payload,
            svg);
    }

    public static byte[] Render(CardView card)
    {
        EnsureReady();
        var png = new PngByteQRCode(new QRCodeGenerator().CreateQrCode(card.QrPayload, QRCodeGenerator.ECCLevel.M)).GetGraphic(10);
        return Document.Create(document => document.Page(page =>
        {
            page.Size(new PageSize(148, 105, Unit.Millimetre));
            page.Margin(8, Unit.Millimetre);
            page.DefaultTextStyle(style => style.FontFamily(FontFamily).FontSize(11));
            page.Content().Row(row =>
            {
                row.RelativeItem().Column(column =>
                {
                    column.Item().Text("কল্পজন্ত্র · Kolpojontro").FontSize(13).Bold();
                    column.Item().PaddingTop(10).Text(card.Name.Bn).FontSize(20);
                    column.Item().Text(card.Name.En).FontSize(12);
                    column.Item().PaddingTop(12).Text(card.MemberCode).FontSize(16).SemiBold();
                    column.Item().Text($"সদস্য · Member since {card.MemberSince}").FontSize(9);
                });
                row.ConstantItem(34, Unit.Millimetre).AlignMiddle().Image(png);
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
            using var font = typeof(MembershipCard).Assembly.GetManifestResourceStream("Fonts.NotoSansBengali.ttf")
                ?? throw new InvalidOperationException("The embedded Bengali font is missing.");
            QuestPDF.Drawing.FontManager.RegisterFontFromStream(font);
            _ready = true;
        }
    }
}
