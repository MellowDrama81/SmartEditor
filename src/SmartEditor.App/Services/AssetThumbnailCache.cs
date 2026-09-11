using System.Security.Cryptography;
using System.Text;

namespace SmartEditor.App.Services;

/// <summary>Caches downloaded asset bytes (used as thumbnails, and reused as the full "Download"
/// payload) on disk under the platform's application-data folder, keyed by filename, so reopening
/// or refreshing the Assets browser doesn't re-download every image over the network every time.
/// Purely a performance cache, never a source of truth against ComfyUI &mdash; a stale local copy
/// after an asset genuinely changed under the same filename is an accepted tradeoff (Comfy Cloud's
/// own filenames are content-hash-derived and therefore immutable in practice; this mostly matters
/// for self-hosted ComfyUI's plain, reusable filenames).</summary>
public sealed class AssetThumbnailCache
{
    private readonly string _directory;

    public AssetThumbnailCache()
    {
        _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SmartEditor", "thumbnail-cache");
        Directory.CreateDirectory(_directory);
    }

    public async Task<byte[]?> TryGetAsync(string filename, CancellationToken ct)
    {
        var path = PathFor(filename);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return await File.ReadAllBytesAsync(path, ct);
        }
        catch (IOException)
        {
            return null; // best-effort cache; a read failure just means "treat as not cached"
        }
    }

    public async Task SaveAsync(string filename, byte[] bytes, CancellationToken ct)
    {
        try
        {
            await File.WriteAllBytesAsync(PathFor(filename), bytes, ct);
        }
        catch (IOException)
        {
            // best-effort cache; a failed write just means it gets re-downloaded next time
        }
    }

    // Filenames aren't guaranteed filesystem-safe (self-hosted ComfyUI imposes no character
    // restrictions), so the cache key is a hash of the filename rather than the filename itself.
    private string PathFor(string filename) =>
        Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(filename))) + ".bin");
}
