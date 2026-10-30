namespace MembershipService.Intake;

public sealed record AttachmentRule(long MaxBytes, string[] ContentTypes);

public static class AttachmentRules
{
    private const long MB = 1024 * 1024;

    public static readonly IReadOnlyDictionary<string, AttachmentRule> ByKind = new Dictionary<string, AttachmentRule>(StringComparer.OrdinalIgnoreCase)
    {
        ["cv"] = new(5 * MB,
        [
            "application/pdf",
            "application/msword",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
        ]),
        ["photo"] = new(2 * MB, ["image/jpeg", "image/png", "image/webp"])
    };

    /// <summary>
    /// Checks the leading bytes against the declared type: the Content-Type header is client-supplied, so it
    /// proves nothing on its own. A .docx is a zip container; the zip signature is the strongest cheap check.
    /// </summary>
    public static bool MatchesSignature(string contentType, ReadOnlySpan<byte> head) => contentType switch
    {
        "application/pdf" => head.StartsWith("%PDF-"u8),
        "application/msword" => head.StartsWith(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }),
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => head.StartsWith("PK\x03\x04"u8),
        "image/jpeg" => head.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }),
        "image/png" => head.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
        "image/webp" => head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8),
        _ => false
    };

    /// <summary>The name is shown to reviewers only: strip paths and control characters, cap the length.</summary>
    public static string SafeFileName(string? raw)
    {
        var decoded = raw is null ? string.Empty : Uri.UnescapeDataString(raw);
        var name = Path.GetFileName(decoded.Replace('\\', '/'));
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length > 120)
        {
            name = name[^120..];
        }

        return name.Length == 0 ? "attachment" : name;
    }
}

/// <summary>
/// Where attachment bytes live. Local disk today; the media pipeline (#41) replaces this with object
/// storage plus virus scanning and EXIF stripping, which is why every row starts as scan status "pending".
/// </summary>
public interface IAttachmentStore
{
    Task SaveAsync(string key, Stream content, CancellationToken cancellationToken);

    Task DeleteAsync(string key, CancellationToken cancellationToken);

    Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken);
}

public sealed class LocalDiskAttachmentStore(Microsoft.Extensions.Options.IOptions<MembershipOptions> options) : IAttachmentStore
{
    private readonly string _root = string.IsNullOrWhiteSpace(options.Value.AttachmentsPath)
        ? Path.Combine(Path.GetTempPath(), "kj-membership-attachments")
        : options.Value.AttachmentsPath;

    public async Task SaveAsync(string key, Stream content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_root);
        await using var file = File.Create(PathFor(key));
        await content.CopyToAsync(file, cancellationToken);
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        var path = PathFor(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        var path = PathFor(key);
        return Task.FromResult<Stream?>(File.Exists(path) ? File.OpenRead(path) : null);
    }

    // Keys are generated server-side (hex only); refuse anything else so a key can never leave the folder.
    private string PathFor(string key) =>
        key.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ? Path.Combine(_root, key) : throw new ArgumentException("Invalid attachment key.", nameof(key));
}
