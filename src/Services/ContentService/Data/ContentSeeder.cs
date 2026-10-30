using System.Globalization;
using System.Reflection;
using System.Text.Json;
using ContentService.Data.Entities;

namespace ContentService.Data;

/// <summary>
/// Imports the content that is live on the public site today (the frontend's project and news snapshots, embedded under Data/Seed)
/// into an empty database. It never runs against a database that already holds content, so editors' changes are never overwritten.
/// The earlier hard-coded sample entries were placeholders for these same two slugs and are superseded by them.
/// </summary>
public static class ContentSeeder
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<int> SeedAsync(ContentDbContext db, TimeProvider time)
    {
        if (db.Items.Any())
        {
            return 0;
        }

        var now = time.GetUtcNow();
        var added = 0;

        var projects = Load<SeedProject>("projects");
        foreach (var (slug, index) in projects.Select(p => p.Slug).Distinct().Select((slug, i) => (slug, i)))
        {
            var item = NewItem(ContentTypes.Project, slug, "active", index, now);
            item.Project = new ProjectEntity
            {
                ImageUrl = projects.First(p => p.Slug == slug).Image,
                Icon = projects.First(p => p.Slug == slug).Icon
            };
            foreach (var p in projects.Where(p => p.Slug == slug))
            {
                item.Translations.Add(new ContentTranslationEntity
                {
                    ContentItemId = item.Id,
                    Lang = p.Lang,
                    Title = p.Title,
                    Summary = p.ShortDescription,
                    Body = p.FullDescription,
                    AttributesJson = JsonSerializer.Serialize(new { shortTitle = p.ShortTitle, category = p.Category, year = p.Year }, Json)
                });
            }

            db.Items.Add(item);
            added++;
        }

        var news = Load<SeedNews>("news");
        foreach (var (slug, index) in news.Select(n => n.Slug).Distinct().Select((slug, i) => (slug, i)))
        {
            var english = news.First(n => n.Slug == slug && n.Lang == "en");
            var item = NewItem(ContentTypes.News, slug, "published", index, now);
            item.PublishedAt = ParseEnglishDate(english.Date);
            item.News = new NewsArticleEntity { ImageUrl = english.Image };
            foreach (var n in news.Where(n => n.Slug == slug))
            {
                item.Translations.Add(new ContentTranslationEntity
                {
                    ContentItemId = item.Id,
                    Lang = n.Lang,
                    Title = n.Title,
                    Summary = n.Summary,
                    Body = string.Join("\n\n", n.Content),
                    AttributesJson = JsonSerializer.Serialize(new { date = n.Date, tags = n.Tags }, Json)
                });
            }

            db.Items.Add(item);
            added++;
        }

        await db.SaveChangesAsync();
        return added;
    }

    private static ContentItemEntity NewItem(string type, string slug, string status, int order, DateTimeOffset now) => new()
    {
        Id = $"cnt_{Guid.NewGuid():N}",
        Type = type,
        Slug = slug,
        Status = status,
        SortOrder = order,
        CreatedAt = now,
        UpdatedAt = now
    };

    private static DateTimeOffset ParseEnglishDate(string label) =>
        DateTimeOffset.TryParseExact(label, "MMMM d, yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
            ? date
            : throw new InvalidOperationException($"Seed news date '{label}' is not in 'MMMM d, yyyy' form.");

    private static List<T> Load<T>(string name) where T : ISeedRow
    {
        var rows = new List<T>();
        foreach (var lang in new[] { "en", "bn" })
        {
            var resource = $"ContentService.Data.Seed.{name}.{lang}.json";
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Seed resource {resource} is missing.");
            foreach (var row in JsonSerializer.Deserialize<List<T>>(stream, Json) ?? [])
            {
                row.Lang = lang;
                rows.Add(row);
            }
        }

        return rows;
    }

    private interface ISeedRow
    {
        string Lang { get; set; }
    }

    private sealed class SeedProject : ISeedRow
    {
        public string Lang { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string Slug => Id;
        public string Title { get; set; } = string.Empty;
        public string ShortTitle { get; set; } = string.Empty;
        public string Image { get; set; } = string.Empty;
        public string ShortDescription { get; set; } = string.Empty;
        public string FullDescription { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Year { get; set; } = string.Empty;
        public string Icon { get; set; } = string.Empty;
    }

    private sealed class SeedNews : ISeedRow
    {
        public string Lang { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string Slug => Id;
        public string Title { get; set; } = string.Empty;
        public string Date { get; set; } = string.Empty;
        public string Image { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public List<string> Content { get; set; } = [];
        public List<string> Tags { get; set; } = [];
    }
}
