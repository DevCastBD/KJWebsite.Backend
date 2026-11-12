using Microsoft.Extensions.Options;

namespace SponsorshipService.Students;

public sealed class SponsorshipOptions
{
    public const string SectionName = "Sponsorship";

    /// <summary>Folder for photos and signed forms. Empty means a temporary folder, fine for development only.</summary>
    public string FilesPath { get; set; } = string.Empty;
}

/// <summary>Private storage for photos and signed consent forms. Nothing here is ever served without a staff token.</summary>
public interface IStudentFileStore
{
    Task SaveAsync(string key, byte[] content, CancellationToken cancellationToken);

    Task DeleteAsync(string key, CancellationToken cancellationToken);

    Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken);
}

public sealed class LocalDiskStudentFileStore(IOptions<SponsorshipOptions> options) : IStudentFileStore
{
    private readonly string _root = string.IsNullOrWhiteSpace(options.Value.FilesPath)
        ? Path.Combine(Path.GetTempPath(), "kj-sponsorship-files")
        : options.Value.FilesPath;

    public async Task SaveAsync(string key, byte[] content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllBytesAsync(PathFor(key), content, cancellationToken);
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

    // Keys are generated here (letters, digits and dashes only); anything else is refused so a key can never leave the folder.
    private string PathFor(string key) =>
        key.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ? Path.Combine(_root, key) : throw new ArgumentException("Invalid file key.", nameof(key));
}
