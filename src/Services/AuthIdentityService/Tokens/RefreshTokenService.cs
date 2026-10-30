using System.Security.Cryptography;
using System.Text;
using AuthIdentityService.Data;
using AuthIdentityService.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AuthIdentityService.Tokens;

public enum RotationOutcome
{
    Rotated,
    Invalid,
    ReplayDetected
}

public sealed record RotationResult(RotationOutcome Outcome, string? Token = null, RefreshTokenEntity? Row = null);

/// <summary>
/// Refresh-token lifecycle (ADR-003): opaque random tokens stored only as SHA-256 hashes, single-use rotation within a
/// family, and revocation of the whole family when a rotated token is presented again (a sign of theft).
/// </summary>
public sealed class RefreshTokenService(AuthDbContext db, TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan RotatedRetention = TimeSpan.FromDays(7);

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static string NewTokenValue() => "rt_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Starts a new session (family) for a login. The caller saves changes.</summary>
    public (string Token, RefreshTokenEntity Row) StartSession(string userId, string? userAgent)
    {
        var now = time.GetUtcNow();
        return Add(userId, $"ses_{Guid.NewGuid():N}", now, userAgent, now);
    }

    public async Task<RotationResult> RotateAsync(string presented, string? userAgent)
    {
        var row = await db.RefreshTokens.FirstOrDefaultAsync(r => r.TokenHash == Hash(presented));
        if (row is null)
        {
            return new RotationResult(RotationOutcome.Invalid);
        }

        if (row.RevokedAt is not null)
        {
            // A rotated token used again means two parties hold it: end the whole session. A logged-out token is just invalid.
            if (row.ReplacedById is null)
            {
                return new RotationResult(RotationOutcome.Invalid);
            }

            await RevokeSessionAsync(row.FamilyId);
            return new RotationResult(RotationOutcome.ReplayDetected);
        }

        var now = time.GetUtcNow();
        if (row.ExpiresAt <= now)
        {
            return new RotationResult(RotationOutcome.Invalid);
        }

        var (token, next) = Add(row.UserId, row.FamilyId, row.SessionCreatedAt, userAgent, now);
        // Claim the old row atomically: of two concurrent uses of one token, exactly one wins.
        var claimed = await db.RefreshTokens
            .Where(r => r.Id == row.Id && r.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, now).SetProperty(r => r.ReplacedById, next.Id));
        if (claimed == 0)
        {
            db.Entry(next).State = EntityState.Detached;
            await RevokeSessionAsync(row.FamilyId);
            return new RotationResult(RotationOutcome.ReplayDetected);
        }

        await db.SaveChangesAsync();
        await PruneAsync(row.UserId, now);
        return new RotationResult(RotationOutcome.Rotated, token, next);
    }

    public async Task<bool> IsSessionActiveAsync(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return false;
        }

        var now = time.GetUtcNow();
        var expiries = await db.RefreshTokens.Where(r => r.FamilyId == sessionId && r.RevokedAt == null).Select(r => r.ExpiresAt).ToListAsync();
        return expiries.Any(expiresAt => expiresAt > now);
    }

    public async Task<IReadOnlyList<RefreshTokenEntity>> ActiveSessionsAsync(string userId)
    {
        var now = time.GetUtcNow();
        var heads = await db.RefreshTokens.Where(r => r.UserId == userId && r.RevokedAt == null).ToListAsync();
        return heads.Where(r => r.ExpiresAt > now).OrderByDescending(r => r.CreatedAt).ToList();
    }

    public async Task<bool> RevokeSessionAsync(string sessionId, string? userId = null)
    {
        var now = time.GetUtcNow();
        var query = db.RefreshTokens.Where(r => r.FamilyId == sessionId && r.RevokedAt == null);
        if (userId is not null)
        {
            query = query.Where(r => r.UserId == userId);
        }

        return await query.ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, now)) > 0;
    }

    public async Task RevokeAllAsync(string userId)
    {
        var now = time.GetUtcNow();
        await db.RefreshTokens.Where(r => r.UserId == userId && r.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, now));
    }

    public async Task<bool> RevokeByTokenAsync(string presented)
    {
        var row = await db.RefreshTokens.FirstOrDefaultAsync(r => r.TokenHash == Hash(presented) && r.RevokedAt == null);
        return row is not null && await RevokeSessionAsync(row.FamilyId);
    }

    /// <summary>Drops rotated rows once they are too old to matter, so a long-lived session does not grow the table forever.</summary>
    public async Task PruneAsync(string userId, DateTimeOffset now)
    {
        var cutoff = now - RotatedRetention;
        // Filtered in memory: SQLite (the test provider) cannot translate DateTimeOffset comparisons.
        var rotated = await db.RefreshTokens.Where(r => r.UserId == userId && r.ReplacedById != null).Select(r => new { r.Id, r.RevokedAt }).ToListAsync();
        var stale = rotated.Where(r => r.RevokedAt < cutoff).Select(r => r.Id).ToList();
        if (stale.Count > 0)
        {
            await db.RefreshTokens.Where(r => stale.Contains(r.Id)).ExecuteDeleteAsync();
        }
    }

    private (string Token, RefreshTokenEntity Row) Add(string userId, string familyId, DateTimeOffset sessionCreatedAt, string? userAgent, DateTimeOffset now)
    {
        var token = NewTokenValue();
        var row = new RefreshTokenEntity
        {
            Id = $"rft_{Guid.NewGuid():N}",
            TokenHash = Hash(token),
            FamilyId = familyId,
            UserId = userId,
            SessionCreatedAt = sessionCreatedAt,
            CreatedAt = now,
            ExpiresAt = now + Lifetime,
            UserAgent = userAgent is { Length: > 256 } ? userAgent[..256] : userAgent
        };
        db.RefreshTokens.Add(row);
        return (token, row);
    }
}
