using System.Text.Json;
using System.Text.RegularExpressions;
using ContentService.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ContentService.Data;

public sealed record Bilingual(string? Bn, string? En);

public sealed record NavItem(string? Id, Bilingual? Label, string? Url, List<NavItem>? Children);

public sealed record ContactSettings(Bilingual? Address, string? Email, string? Phone, string? MapUrl);

public sealed record SocialLink(string? Network, string? Url);

public sealed record MaintenanceSettings(bool Enabled, Bilingual? Message);

public sealed record SettingView(string Key, JsonElement? Value, bool IsSecret, DateTimeOffset UpdatedAt);

public sealed record SettingWrite(JsonElement Value, bool? IsSecret);

public sealed record FlagWrite(bool Enabled, string? Description);

public sealed record FlagView(string Key, bool Enabled, string Description, DateTimeOffset UpdatedAt);

public sealed record PublicSettings(Dictionary<string, JsonElement> Settings, Dictionary<string, bool> Flags);

/// <summary>
/// Site settings and runtime flags. A catalog key is validated as a typed shape in which every visible text must exist in both languages;
/// `custom.*` keys hold one bilingual text and may be secret. Public reads return only non-secret values, already resolved to one language.
/// </summary>
public sealed partial class SettingsStore(ContentDbContext db, TimeProvider time)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public static readonly string[] CatalogKeys = ["navigation.main", "navigation.footer", "contact", "social", "footer.text", "homepage.sections", "maintenance"];

    [GeneratedRegex(@"^custom\.[a-z0-9][a-z0-9._-]{0,60}$")]
    private static partial Regex CustomKey();

    [GeneratedRegex(@"^[a-z][a-z0-9._-]{1,99}$")]
    private static partial Regex FlagKey();

    public static bool IsKnownKey(string key) => CatalogKeys.Contains(key) || CustomKey().IsMatch(key);

    // ---- settings

    public async Task<List<SettingView>> ListAsync() =>
        [.. (await db.Settings.AsNoTracking().ToListAsync()).OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => ToView(s, includeSecret: false))];

    public async Task<SettingView?> GetAsync(string key)
    {
        var setting = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key);
        return setting is null ? null : ToView(setting, includeSecret: true);
    }

    public async Task<UpsertResult<SettingView>> SetAsync(string key, SettingWrite write, IReadOnlyCollection<string>? ifMatch)
    {
        if (!IsKnownKey(key))
        {
            return new UpsertResult<SettingView>(UpsertOutcome.Invalid, Error: $"Unknown setting '{key}'. Known keys: {string.Join(", ", CatalogKeys)}, or custom.<name>.");
        }

        var secret = write.IsSecret ?? false;
        if (secret && !key.StartsWith("custom.", StringComparison.Ordinal))
        {
            return new UpsertResult<SettingView>(UpsertOutcome.Invalid, Error: "Only custom.* settings can be secret; catalog settings are public by design.");
        }

        var (normalized, error) = Normalize(key, write.Value);
        if (error is not null)
        {
            return new UpsertResult<SettingView>(UpsertOutcome.Invalid, Error: error);
        }

        var existing = await db.Settings.FirstOrDefaultAsync(s => s.Key == key);
        if (existing is not null && ifMatch is not null && !ContentETag.Satisfies(ifMatch, existing.UpdatedAt))
        {
            return new UpsertResult<SettingView>(UpsertOutcome.Conflict);
        }

        var created = existing is null;
        existing ??= db.Settings.Add(new SettingEntity { Key = key }).Entity;
        existing.ValueJson = normalized!;
        existing.IsSecret = write.IsSecret ?? existing.IsSecret;
        existing.UpdatedAt = Now();
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return new UpsertResult<SettingView>(UpsertOutcome.Conflict);
        }

        return new UpsertResult<SettingView>(created ? UpsertOutcome.Created : UpsertOutcome.Updated, ToView(existing, includeSecret: true), ETag: ContentETag.For(existing.UpdatedAt));
    }

    public async Task<bool> DeleteAsync(string key)
    {
        var setting = await db.Settings.FirstOrDefaultAsync(s => s.Key == key);
        if (setting is null)
        {
            return false;
        }

        db.Settings.Remove(setting);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<string?> ETagAsync(string key)
    {
        var updatedAt = await db.Settings.AsNoTracking().Where(s => s.Key == key).Select(s => (DateTimeOffset?)s.UpdatedAt).FirstOrDefaultAsync();
        return updatedAt is null ? null : ContentETag.For(updatedAt.Value);
    }

    // ---- flags

    public async Task<List<FlagView>> FlagsAsync() =>
        [.. (await db.FeatureFlags.AsNoTracking().ToListAsync()).OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => new FlagView(f.Key, f.Enabled, f.Description, f.UpdatedAt))];

    public async Task<UpsertResult<FlagView>> SetFlagAsync(string key, FlagWrite write)
    {
        if (!FlagKey().IsMatch(key))
        {
            return new UpsertResult<FlagView>(UpsertOutcome.Invalid, Error: "Flag keys are lowercase letters, digits, dots, hyphens and underscores, starting with a letter.");
        }

        if ((write.Description?.Length ?? 0) > 500)
        {
            return new UpsertResult<FlagView>(UpsertOutcome.Invalid, Error: "description is limited to 500 characters.");
        }

        var flag = await db.FeatureFlags.FirstOrDefaultAsync(f => f.Key == key);
        var created = flag is null;
        flag ??= db.FeatureFlags.Add(new FeatureFlagEntity { Key = key }).Entity;
        flag.Enabled = write.Enabled;
        flag.Description = write.Description ?? flag.Description;
        flag.UpdatedAt = Now();
        await db.SaveChangesAsync();
        return new UpsertResult<FlagView>(created ? UpsertOutcome.Created : UpsertOutcome.Updated, new FlagView(flag.Key, flag.Enabled, flag.Description, flag.UpdatedAt));
    }

    public async Task<bool> DeleteFlagAsync(string key)
    {
        var flag = await db.FeatureFlags.FirstOrDefaultAsync(f => f.Key == key);
        if (flag is null)
        {
            return false;
        }

        db.FeatureFlags.Remove(flag);
        await db.SaveChangesAsync();
        return true;
    }

    // ---- public

    /// <summary>Every non-secret setting resolved to <paramref name="lang"/>, plus the flags. Maintenance is always present, off by default.</summary>
    public async Task<PublicSettings> PublicAsync(string lang)
    {
        var settings = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var setting in (await db.Settings.AsNoTracking().Where(s => !s.IsSecret).ToListAsync()).OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            settings[setting.Key] = Localize(JsonDocument.Parse(setting.ValueJson).RootElement, lang);
        }

        if (!settings.ContainsKey("maintenance"))
        {
            settings["maintenance"] = JsonSerializer.SerializeToElement(new { enabled = false }, Json);
        }

        var flags = (await db.FeatureFlags.AsNoTracking().ToListAsync()).OrderBy(f => f.Key, StringComparer.Ordinal).ToDictionary(f => f.Key, f => f.Enabled);
        return new PublicSettings(settings, flags);
    }

    // ---- validation and localisation

    private static (string? Json, string? Error) Normalize(string key, JsonElement value)
    {
        try
        {
            object? typed = key switch
            {
                "navigation.main" or "navigation.footer" => Read<List<NavItem>>(value),
                "contact" => Read<ContactSettings>(value),
                "social" => Read<List<SocialLink>>(value),
                "footer.text" => Read<Bilingual>(value),
                "homepage.sections" => Read<List<string>>(value),
                "maintenance" => Read<MaintenanceSettings>(value),
                _ => Read<Bilingual>(value)
            };
            var error = key switch
            {
                "navigation.main" or "navigation.footer" => ValidateNav((List<NavItem>?)typed, 0, new HashSet<string>(StringComparer.Ordinal)),
                "contact" => ValidateContact((ContactSettings?)typed),
                "social" => ValidateSocial((List<SocialLink>?)typed),
                "homepage.sections" => ValidateSections((List<string>?)typed),
                "maintenance" => ValidateMaintenance((MaintenanceSettings?)typed),
                _ => Text((Bilingual?)typed, key)
            };
            return error is null ? (JsonSerializer.Serialize(typed, Json), null) : (null, error);
        }
        catch (JsonException)
        {
            return (null, $"The value for '{key}' does not have the expected shape.");
        }
    }

    private static T? Read<T>(JsonElement value) => value.Deserialize<T>(Json);

    private static string? Text(Bilingual? text, string what, bool required = true)
    {
        if (text is null)
        {
            return required ? $"{what} is required in both bn and en." : null;
        }

        if (string.IsNullOrWhiteSpace(text.Bn) || string.IsNullOrWhiteSpace(text.En))
        {
            return $"{what} is required in both bn and en.";
        }

        return text.Bn.Length > 500 || text.En.Length > 500 ? $"{what} is limited to 500 characters per language." : null;
    }

    private static bool IsUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) && url.Length <= 512 && (url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal) || url.StartsWith("https://", StringComparison.Ordinal) || url.StartsWith("http://", StringComparison.Ordinal));

    private static string? ValidateNav(List<NavItem>? items, int depth, HashSet<string> ids)
    {
        if (items is null)
        {
            return "A navigation menu is a list of items.";
        }

        if (depth > 1)
        {
            return "Navigation allows one level of sub-items.";
        }

        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Id) || !ids.Add(item.Id))
            {
                return "Every navigation item needs a unique id.";
            }

            if (Text(item.Label, $"Navigation label '{item.Id}'") is { } error)
            {
                return error;
            }

            if (!IsUrl(item.Url))
            {
                return $"Navigation item '{item.Id}' needs a url that starts with /, http:// or https://.";
            }

            if (item.Children is { Count: > 0 } && ValidateNav(item.Children, depth + 1, ids) is { } child)
            {
                return child;
            }
        }

        return null;
    }

    private static string? ValidateContact(ContactSettings? contact)
    {
        if (contact is null)
        {
            return "contact is required.";
        }

        if (Text(contact.Address, "Contact address") is { } error)
        {
            return error;
        }

        if (string.IsNullOrWhiteSpace(contact.Email) || !contact.Email.Contains('@', StringComparison.Ordinal) || contact.Email.Length > 256)
        {
            return "contact.email must be an email address.";
        }

        if (contact.Phone is { Length: > 40 })
        {
            return "contact.phone is limited to 40 characters.";
        }

        return contact.MapUrl is not null && !IsUrl(contact.MapUrl) ? "contact.mapUrl must be an http(s) URL." : null;
    }

    private static string? ValidateSocial(List<SocialLink>? links) =>
        links is null || links.Any(l => string.IsNullOrWhiteSpace(l.Network) || l.Network.Length > 40 || l.Url is null || !l.Url.StartsWith("http", StringComparison.Ordinal) || !IsUrl(l.Url))
            ? "Each social link needs a network name and an http(s) url."
            : null;

    private static string? ValidateSections(List<string>? sections) =>
        sections is null || sections.Any(string.IsNullOrWhiteSpace) || sections.Distinct(StringComparer.Ordinal).Count() != sections.Count
            ? "homepage.sections is an ordered list of distinct section ids."
            : null;

    private static string? ValidateMaintenance(MaintenanceSettings? maintenance) =>
        maintenance is null ? "maintenance is required." : maintenance.Enabled ? Text(maintenance.Message, "The maintenance message") : Text(maintenance.Message, "The maintenance message", required: false);

    /// <summary>Replaces every `{ "bn": ..., "en": ... }` leaf with the text for <paramref name="lang"/>.</summary>
    public static JsonElement Localize(JsonElement element, string lang)
    {
        object? Walk(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.Object when e.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).SequenceEqual(["bn", "en"]) && e.EnumerateObject().All(p => p.Value.ValueKind == JsonValueKind.String)
                => e.GetProperty(lang == "bn" ? "bn" : "en").GetString(),
            JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => Walk(p.Value)),
            JsonValueKind.Array => e.EnumerateArray().Select(Walk).ToList(),
            _ => e.Clone()
        };
        return JsonSerializer.SerializeToElement(Walk(element), Json);
    }

    private static SettingView ToView(SettingEntity setting, bool includeSecret) =>
        new(setting.Key, setting.IsSecret && !includeSecret ? null : JsonDocument.Parse(setting.ValueJson).RootElement.Clone(), setting.IsSecret, setting.UpdatedAt);

    private DateTimeOffset Now() => new(time.GetUtcNow().UtcTicks / 10 * 10, TimeSpan.Zero);
}
