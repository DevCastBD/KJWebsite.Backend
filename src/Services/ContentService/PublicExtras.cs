using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using ContentService.Data;
using ContentService.Data.Entities;
using KJWebsite.BuildingBlocks;
using Microsoft.EntityFrameworkCore;
using static ContentService.ContentHttp;

namespace ContentService;

public sealed record OgMetadata(
    string Title, string Description, string Url, string? Image, string Type, string Locale, List<string> AlternateLocales,
    string SiteName, DateTimeOffset? PublishedTime, DateTimeOffset ModifiedTime, List<string> Tags, List<OgAlternate> Alternates);

public sealed record OgAlternate(string Lang, string Url);

/// <summary>Search, sitemap, per-language feeds and Open Graph metadata (BE-040). See docs/PUBLIC_EXTRAS.md.</summary>
public static class PublicExtras
{
    private static readonly XNamespace Sitemap = "http://www.sitemaps.org/schemas/sitemap/0.9";
    private static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";

    public static void MapPublicExtras(this WebApplication app)
    {
        app.MapGet("/api/v1/content/search", async (HttpRequest request, SearchIndex index, IConfiguration configuration, string? q, string? lang, string? type, int? limit, int? offset) =>
        {
            var language = LanguageResolver.Resolve(lang, request.Headers.AcceptLanguage.ToString());
            var term = q?.Trim() ?? string.Empty;
            if (term.Length is 0 or > 100 || (type is not null && ContentKinds.ByType(type) is null))
            {
                return Invalid(request, "q is required (at most 100 characters) and type must be a content type.");
            }

            if (!TryReadPaging(limit, offset, out var safeLimit, out var safeOffset))
            {
                return Invalid(request, "Invalid paging values.");
            }

            // Fail fast: the frontend falls back to its client-side index, so a slow search must answer 503 quickly, not hang.
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(request.HttpContext.RequestAborted);
            budget.CancelAfter(TimeSpan.FromMilliseconds(configuration.GetValue("Search:TimeoutMs", 2000)));
            try
            {
                return Cached(request, await index.SearchAsync(term, language, type, safeLimit, safeOffset, budget.Token));
            }
            catch (OperationCanceledException) when (!request.HttpContext.RequestAborted.IsCancellationRequested)
            {
                return ApiError.Create(request.HttpContext, StatusCodes.Status503ServiceUnavailable, "SEARCH_UNAVAILABLE", "Search is taking too long; use the client-side index.");
            }
        })
        .WithName("SearchContent").WithSummary("Search published content (Bengali-aware)")
        .WithDescription("Prefix match on every query term (AND), in the requested language, weighted title > summary > body. Bengali conjuncts and mixed bn/en queries are supported. Returns `{ q, lang, items, total, limit, offset }`; 503 SEARCH_UNAVAILABLE after `Search:TimeoutMs` (default 2000).")
        .WithTags("Content").CachePublicContent().AllowAnonymous();

        app.MapGet("/sitemap.xml", async (HttpRequest request, ContentDbContext db, IConfiguration configuration) =>
        {
            if (Site(configuration) is not { } site)
            {
                return NotConfigured(request);
            }

            var types = List(configuration, "Site:SitemapTypes", ["project", "news", "page", "press"]);
            var items = await LiveItems(db, types).ToListAsync();
            var urlset = new XElement(Sitemap + "urlset", new XAttribute(XNamespace.Xmlns + "xhtml", Xhtml));
            foreach (var item in items.OrderBy(i => i.Type, StringComparer.Ordinal).ThenBy(i => i.Slug, StringComparer.Ordinal))
            {
                var langs = item.Translations.Select(t => t.Lang).Order(StringComparer.Ordinal).ToList();
                foreach (var lang in langs)
                {
                    var url = new XElement(Sitemap + "url", new XElement(Sitemap + "loc", site.Url(item, lang)), new XElement(Sitemap + "lastmod", item.UpdatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)));
                    url.Add(langs.Select(alt => new XElement(Xhtml + "link", new XAttribute("rel", "alternate"), new XAttribute("hreflang", alt), new XAttribute("href", site.Url(item, alt)))));
                    urlset.Add(url);
                }
            }

            return Xml(request, new XDocument(new XDeclaration("1.0", "utf-8", null), urlset), "application/xml");
        })
        .WithName("GetSitemap").WithSummary("Sitemap of published content (no drafts), with hreflang alternates")
        .WithDescription("Covers content items only (types from `Site:SitemapTypes`); the frontend keeps its own sitemap for static routes. Needs `Site:BaseUrl`.")
        .WithTags("Content").CachePublicContent().AllowAnonymous();

        app.MapGet("/feed.rss", async (HttpRequest request, ContentDbContext db, IConfiguration configuration, string? lang) =>
            await Feed(request, db, configuration, lang, atom: false))
        .WithName("GetRssFeed").WithSummary("RSS 2.0 feed of the newest published items, one language")
        .WithDescription("`?lang=bn|en`. Items without a translation in that language are left out (no fallback: a subscriber wants one language). Types from `Site:FeedTypes` (default news).")
        .WithTags("Content").CachePublicContent().AllowAnonymous();

        app.MapGet("/feed.atom", async (HttpRequest request, ContentDbContext db, IConfiguration configuration, string? lang) =>
            await Feed(request, db, configuration, lang, atom: true))
        .WithName("GetAtomFeed").WithSummary("Atom feed of the newest published items, one language")
        .WithTags("Content").CachePublicContent().AllowAnonymous();

        app.MapGet("/api/v1/content/og/{type}/{slug}", async (HttpRequest request, ContentDbContext db, IConfiguration configuration, string type, string slug, string? lang) =>
        {
            if (Site(configuration) is not { } site)
            {
                return NotConfigured(request);
            }

            var language = LanguageResolver.Resolve(lang, request.Headers.AcceptLanguage.ToString());
            var item = ContentKinds.ByType(type) is null ? null : await LiveItems(db, [type]).FirstOrDefaultAsync(i => i.Slug == slug);
            var translation = item?.Translations.FirstOrDefault(t => t.Lang == language);
            if (item is null || translation is null)
            {
                return NotFound(request, "Item not found in this language.");
            }

            var tags = await db.ItemTags.AsNoTracking().Where(l => l.ContentItemId == item.Id).Select(l => l.Tag!.Names.Where(n => n.Lang == language).Select(n => n.Name).FirstOrDefault() ?? l.Tag.Slug).ToListAsync();
            var image = item.Project?.ImageUrl is { Length: > 0 } p ? p : item.News?.ImageUrl is { Length: > 0 } n ? n : item.ImageUrl;
            var langs = item.Translations.Select(t => t.Lang).Order(StringComparer.Ordinal).ToList();
            return Cached(request, new OgMetadata(translation.Title, Description(translation), site.Url(item, language), image.Length > 0 ? site.Absolute(image) : null,
                item.Type == ContentTypes.News || item.Type == ContentTypes.Press ? "article" : "website", Locale(language), [.. langs.Where(l => l != language).Select(Locale)],
                configuration["Site:Name"] ?? "Site", item.PublishedAt, item.UpdatedAt, tags, [.. langs.Select(l => new OgAlternate(l, site.Url(item, l)))]));
        })
        .WithName("GetOpenGraph").WithSummary("Open Graph metadata for one item, for the frontend to render")
        .WithDescription("`title`, `description`, canonical `url`, absolute `image`, `type` (article/website), `locale` (bn_BD/en_US) and alternates. Needs `Site:BaseUrl`.")
        .WithTags("Content").CachePublicContent().AllowAnonymous();
    }

    private static async Task<IResult> Feed(HttpRequest request, ContentDbContext db, IConfiguration configuration, string? lang, bool atom)
    {
        if (Site(configuration) is not { } site)
        {
            return NotConfigured(request);
        }

        var language = LanguageResolver.Resolve(lang, request.Headers.AcceptLanguage.ToString());
        var types = List(configuration, "Site:FeedTypes", ["news"]);
        var rows = (await LiveItems(db, types).ToListAsync()).Where(i => i.Translations.Any(t => t.Lang == language))
            .OrderByDescending(i => i.PublishedAt ?? i.CreatedAt).ThenBy(i => i.Slug, StringComparer.Ordinal).Take(30).ToList();
        var title = $"{configuration["Site:Name"] ?? "Site"} ({language})";
        var newest = rows.Select(i => i.UpdatedAt).DefaultIfEmpty(DateTimeOffset.UnixEpoch).Max();

        XDocument document;
        if (atom)
        {
            var feed = new XElement(Atom + "feed", new XAttribute(XNamespace.Xml + "lang", language),
                new XElement(Atom + "title", title), new XElement(Atom + "id", $"{site.Base}/feed.atom?lang={language}"),
                new XElement(Atom + "updated", newest.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)),
                new XElement(Atom + "link", new XAttribute("rel", "self"), new XAttribute("href", $"{site.Base}/feed.atom?lang={language}")),
                new XElement(Atom + "link", new XAttribute("rel", "alternate"), new XAttribute("href", site.Base)));
            foreach (var item in rows)
            {
                var translation = item.Translations.First(t => t.Lang == language);
                feed.Add(new XElement(Atom + "entry", new XElement(Atom + "title", Clean(translation.Title)), new XElement(Atom + "id", site.Url(item, language)),
                    new XElement(Atom + "link", new XAttribute("rel", "alternate"), new XAttribute("href", site.Url(item, language))),
                    new XElement(Atom + "updated", item.UpdatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)),
                    new XElement(Atom + "published", (item.PublishedAt ?? item.CreatedAt).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)),
                    new XElement(Atom + "summary", Clean(Description(translation)))));
            }

            document = new XDocument(new XDeclaration("1.0", "utf-8", null), feed);
        }
        else
        {
            var channel = new XElement("channel", new XElement("title", title), new XElement("link", site.Base),
                new XElement("description", Clean($"{title} — latest")), new XElement("language", language),
                new XElement("lastBuildDate", newest.UtcDateTime.ToString("R", CultureInfo.InvariantCulture)));
            foreach (var item in rows)
            {
                var translation = item.Translations.First(t => t.Lang == language);
                var link = site.Url(item, language);
                channel.Add(new XElement("item", new XElement("title", Clean(translation.Title)), new XElement("link", link),
                    new XElement("guid", new XAttribute("isPermaLink", "true"), link),
                    new XElement("pubDate", (item.PublishedAt ?? item.CreatedAt).UtcDateTime.ToString("R", CultureInfo.InvariantCulture)),
                    new XElement("description", Clean(Description(translation)))));
            }

            document = new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("rss", new XAttribute("version", "2.0"), channel));
        }

        return Xml(request, document, atom ? "application/atom+xml" : "application/rss+xml");
    }

    private static IQueryable<ContentItemEntity> LiveItems(ContentDbContext db, IEnumerable<string> types)
    {
        var typeList = types.ToList();
        return db.Items.AsNoTracking().Include(i => i.Translations).Include(i => i.Project).Include(i => i.News)
            .Where(i => typeList.Contains(i.Type) && i.DeletedAt == null && (i.Status == "published" || i.Status == "active"));
    }

    private static IResult NotConfigured(HttpRequest request) =>
        ApiError.Create(request.HttpContext, StatusCodes.Status503ServiceUnavailable, "SITE_URL_NOT_CONFIGURED", "Site:BaseUrl is not configured.");

    private static IResult Xml(HttpRequest request, XDocument document, string contentType)
    {
        request.HttpContext.Response.Headers.CacheControl = "public, max-age=300";
        return Results.Text(document.Declaration + Environment.NewLine + document.Root, $"{contentType}; charset=utf-8");
    }

    private static SiteUrls? Site(IConfiguration configuration) =>
        configuration["Site:BaseUrl"] is { Length: > 0 } baseUrl && Uri.TryCreate(baseUrl, UriKind.Absolute, out _)
            ? new SiteUrls(baseUrl.TrimEnd('/'), configuration["Site:ItemUrlTemplate"] ?? "{base}/{lang}/{segment}/{slug}")
            : null;

    private static string[] List(IConfiguration configuration, string key, string[] fallback) =>
        configuration[key] is { Length: > 0 } raw ? [.. raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)] : fallback;

    private static string Locale(string lang) => lang == "bn" ? "bn_BD" : "en_US";

    private static string Description(ContentTranslationEntity translation)
    {
        var text = SearchIndex.Snippet(translation);
        return text.Length > 0 ? text : translation.Title;
    }

    /// <summary>Drops characters XML 1.0 forbids, which would otherwise make the whole document invalid.</summary>
    private static string Clean(string text) => new([.. text.Where(XmlConvert.IsXmlChar)]);

    private sealed record SiteUrls(string Base, string Template)
    {
        public string Url(ContentItemEntity item, string lang) =>
            Template.Replace("{base}", Base, StringComparison.Ordinal).Replace("{lang}", lang, StringComparison.Ordinal)
                .Replace("{segment}", ContentKinds.ByType(item.Type)?.Segment ?? item.Type, StringComparison.Ordinal).Replace("{slug}", item.Slug, StringComparison.Ordinal);

        public string Absolute(string path) => path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : Base + "/" + path.TrimStart('/');
    }
}
