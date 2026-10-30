using ContentService.Data.Entities;

namespace ContentService;

/// <summary>The eight content types on the one model (plan §3.2): their type value, the public path segment and the API route segment.</summary>
public sealed record ContentKind(string Type, string Segment, string Pascal);

public static class ContentKinds
{
    public static readonly string[] Statuses = ["draft", "published", "archived"];

    public static readonly ContentKind[] All =
    [
        new(ContentTypes.Project, "projects", "Project"),
        new(ContentTypes.News, "news", "News"),
        new(ContentTypes.Page, "pages", "Page"),
        new(ContentTypes.Faq, "faqs", "Faq"),
        new(ContentTypes.TeamMember, "team-members", "TeamMember"),
        new(ContentTypes.Testimonial, "testimonials", "Testimonial"),
        new(ContentTypes.Partner, "partners", "Partner"),
        new(ContentTypes.Press, "press", "Press")
    ];

    /// <summary>The types served by the generic API; projects and news keep their dedicated endpoints.</summary>
    public static IEnumerable<ContentKind> Generic => All.Where(k => k.Type is not (ContentTypes.Project or ContentTypes.News));

    public static ContentKind? ByType(string? type) => All.FirstOrDefault(k => k.Type == type);

    /// <summary>The public site path of an item, used as the redirect source/target when a slug changes.</summary>
    public static string PublicPath(string type, string slug) => $"/{ByType(type)?.Segment ?? type}/{slug}";
}
