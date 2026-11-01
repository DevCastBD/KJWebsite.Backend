using Microsoft.Extensions.Options;

namespace FinanceService.Offline;

public sealed class ProofOptions
{
    public const string SectionName = "Finance:Proofs";

    /// <summary>Where proof files are kept (local disk until object storage replaces it, ADR-006). Empty means a folder under the system temp path: set it in any real deployment.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>How long a proof is kept, counted from upload. Financial records outlive ordinary media; the exact period waits on the retention decision (#110), so seven years is the cautious default.</summary>
    public int RetentionYears { get; set; } = 7;
}

public sealed record ProofRule(long MaxBytes, string[] ContentTypes);

public static class ProofRules
{
    public const long MaxBytes = 5L * 1024 * 1024;
    public const int MaxPerRecord = 10;

    public static readonly string[] ContentTypes = ["application/pdf", "image/jpeg", "image/png", "image/webp"];

    /// <summary>The declared Content-Type is client-supplied and proves nothing: the file's leading bytes must match it.</summary>
    public static bool MatchesSignature(string contentType, ReadOnlySpan<byte> head) => contentType switch
    {
        "application/pdf" => head.StartsWith("%PDF-"u8),
        "image/jpeg" => head.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }),
        "image/png" => head.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
        "image/webp" => head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8),
        _ => false
    };

    /// <summary>The name is only ever shown back to finance staff: strip paths and control characters and cap the length.</summary>
    public static string SafeFileName(string? raw)
    {
        var decoded = raw is null ? string.Empty : Uri.UnescapeDataString(raw);
        var name = System.IO.Path.GetFileName(decoded.Replace('\\', '/'));
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length > 120)
        {
            name = name[^120..];
        }

        return name.Length == 0 ? "proof" : name;
    }
}

/// <summary>Where proof bytes live. There is deliberately no delete: a proof is a financial record, and nothing in the API can remove one.</summary>
public interface IProofStore
{
    Task SaveAsync(string key, Stream content, CancellationToken cancellationToken);

    Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken);
}

public sealed class LocalDiskProofStore(IOptions<ProofOptions> options) : IProofStore
{
    private readonly string _root = string.IsNullOrWhiteSpace(options.Value.Path) ? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kj-finance-proofs") : options.Value.Path;

    public async Task SaveAsync(string key, Stream content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_root);
        await using var file = File.Create(PathFor(key));
        await content.CopyToAsync(file, cancellationToken);
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        var path = PathFor(key);
        return Task.FromResult<Stream?>(File.Exists(path) ? File.OpenRead(path) : null);
    }

    // Keys are generated here (hex only); anything else is refused so a key can never leave the folder.
    private string PathFor(string key) =>
        key.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ? System.IO.Path.Combine(_root, key) : throw new ArgumentException("Invalid proof key.", nameof(key));
}

/// <summary>A proof file that passed every check, ready to store.</summary>
public sealed record PreparedProof(string ContentType, byte[] Bytes, string Sha256, string FileName);

/// <summary>The checks every uploaded proof or receipt goes through, in one place: type, size, content against declared type, hash and a safe name.</summary>
public static class ProofIntake
{
    public static async Task<KJWebsite.BuildingBlocks.Result<PreparedProof>> PrepareAsync(Stream content, string? contentType, string? fileName, CancellationToken cancellationToken)
    {
        var type = contentType?.Split(';')[0].Trim().ToLowerInvariant();
        if (type is null || !ProofRules.ContentTypes.Contains(type))
        {
            return KJWebsite.BuildingBlocks.Result.Failure<PreparedProof>("UNSUPPORTED_TYPE", "A proof is a PDF, JPEG, PNG or WebP file.");
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > ProofRules.MaxBytes)
            {
                return KJWebsite.BuildingBlocks.Result.Failure<PreparedProof>("FILE_TOO_LARGE", $"A proof is at most {ProofRules.MaxBytes / (1024 * 1024)} MB.");
            }

            buffer.Write(chunk, 0, read);
        }

        var bytes = buffer.ToArray();
        if (bytes.Length == 0 || !ProofRules.MatchesSignature(type, bytes.AsSpan(0, Math.Min(bytes.Length, 16))))
        {
            return KJWebsite.BuildingBlocks.Result.Failure<PreparedProof>("CONTENT_MISMATCH", "The file's content does not match its declared type.");
        }

        return KJWebsite.BuildingBlocks.Result.Success(new PreparedProof(type, bytes, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(), ProofRules.SafeFileName(fileName)));
    }
}
