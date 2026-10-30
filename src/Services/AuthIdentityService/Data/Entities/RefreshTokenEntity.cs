namespace AuthIdentityService.Data.Entities;

/// <summary>
/// One refresh token. Only a SHA-256 hash is stored, so a database read cannot yield a usable token.
/// Rotation links rows into a family (a login session): each use revokes the row and issues the next one.
/// </summary>
public sealed class RefreshTokenEntity
{
    public string Id { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public string FamilyId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public DateTimeOffset SessionCreatedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? ReplacedById { get; set; }
    public string? UserAgent { get; set; }
}
