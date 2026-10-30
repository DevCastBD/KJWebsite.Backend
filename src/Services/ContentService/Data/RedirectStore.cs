using ContentService.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ContentService.Data;

/// <summary>
/// Old public path to new public path. Rows are flattened on write, so resolving is one lookup and a chain of renames never needs
/// several hops. Legacy `.html`/`.php` URLs and the frontend's language-prefix moves are plain manual rows.
/// </summary>
public sealed class RedirectStore(ContentDbContext db, TimeProvider time)
{
    private const int MaxChain = 16;

    /// <summary>Lower case, leading slash, no query/fragment, no trailing slash. External targets (http/https) are left as given.</summary>
    public static string NormalizePath(string path)
    {
        var value = path.Trim();
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        var cut = value.IndexOfAny(['?', '#']);
        if (cut >= 0)
        {
            value = value[..cut];
        }

        try
        {
            value = Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
        }

        value = "/" + value.Trim().TrimStart('/');
        return value.Length > 1 ? value.TrimEnd('/').ToLowerInvariant() : value;
    }

    public async Task<RedirectEntity?> ResolveAsync(string path)
    {
        var from = NormalizePath(path);
        return await db.Redirects.AsNoTracking().FirstOrDefaultAsync(r => r.FromPath == from);
    }

    public async Task<List<RedirectEntity>> ListAsync() =>
        [.. (await db.Redirects.AsNoTracking().ToListAsync()).OrderBy(r => r.FromPath, StringComparer.Ordinal)];

    public async Task<UpsertResult<RedirectEntity>> CreateAsync(string from, string to, int? statusCode)
    {
        var code = statusCode ?? 301;
        if (code is not (301 or 302 or 307 or 308))
        {
            return new UpsertResult<RedirectEntity>(UpsertOutcome.Invalid, Error: "statusCode must be 301, 302, 307 or 308.");
        }

        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to) || from.Length > 512 || to.Length > 512)
        {
            return new UpsertResult<RedirectEntity>(UpsertOutcome.Invalid, Error: "from and to are required (at most 512 characters).");
        }

        var error = await StageAsync(from, to, code, "manual");
        if (error is not null)
        {
            return new UpsertResult<RedirectEntity>(UpsertOutcome.Invalid, Error: error);
        }

        await db.SaveChangesAsync();
        return new UpsertResult<RedirectEntity>(UpsertOutcome.Created, await ResolveAsync(from));
    }

    public async Task<bool> DeleteAsync(string id)
    {
        var row = await db.Redirects.FirstOrDefaultAsync(r => r.Id == id);
        if (row is null)
        {
            return false;
        }

        db.Redirects.Remove(row);
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>Queues `from` -> `to` on the context without saving, so a rename and its redirect commit together. Returns an error for loops.</summary>
    public async Task<string?> StageAsync(string from, string to, int statusCode, string source)
    {
        var fromPath = NormalizePath(from);
        var target = NormalizePath(to);
        if (fromPath == target)
        {
            return "from and to are the same path.";
        }

        for (var hops = 0; ; hops++)
        {
            var next = await db.Redirects.FirstOrDefaultAsync(r => r.FromPath == target);
            if (next is null)
            {
                break;
            }

            target = next.ToPath;
            if (target == fromPath || hops >= MaxChain)
            {
                return "That redirect would create a loop.";
            }
        }

        foreach (var pointingHere in await db.Redirects.Where(r => r.ToPath == fromPath).ToListAsync())
        {
            pointingHere.ToPath = target;
        }

        var row = await db.Redirects.FirstOrDefaultAsync(r => r.FromPath == fromPath);
        if (row is null)
        {
            db.Redirects.Add(new RedirectEntity { Id = $"rdr_{Guid.NewGuid():N}", FromPath = fromPath, ToPath = target, StatusCode = statusCode, Source = source, CreatedAt = time.GetUtcNow() });
        }
        else
        {
            row.ToPath = target;
            row.StatusCode = statusCode;
            row.Source = source;
        }

        return null;
    }

    /// <summary>Drops a redirect whose source is about to become a live path again (an item renamed back to its old slug).</summary>
    public async Task RemoveFromAsync(string path)
    {
        var from = NormalizePath(path);
        // Tracked (not ExecuteDelete) so the audit log sees it; saved at once so the chain walk that follows no longer finds the row.
        db.Redirects.RemoveRange(await db.Redirects.Where(r => r.FromPath == from).ToListAsync());
        await db.SaveChangesAsync();
    }
}
