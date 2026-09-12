using System.Security.Cryptography;
using System.Text;

namespace SmartEditor.App.Services;

/// <summary>Best-effort, on-disk LRU cache for asset thumbnails and full-size image files.
/// The two pools have independent limits, so opening high-resolution images cannot evict the
/// thumbnails that make the asset browser responsive.</summary>
public sealed class AssetThumbnailCache
{
    private readonly AppSettingsStore _settingsStore;
    private readonly string _thumbnailDirectory;
    private readonly string _fullImageDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AssetThumbnailCache(AppSettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SmartEditor", "image-cache");
        // v2 contains genuinely downscaled PNGs. The old pool stored original image bytes under
        // thumbnail names, so it must never be read by the grid after this migration.
        _thumbnailDirectory = Path.Combine(root, "thumbnails-v2");
        _fullImageDirectory = Path.Combine(root, "full-images");
        try { Directory.Delete(Path.Combine(root, "thumbnails"), recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public Task<byte[]?> TryGetThumbnailAsync(string filename, CancellationToken ct) =>
        TryGetAsync(_thumbnailDirectory, filename, ct);

    public Task SaveThumbnailAsync(string filename, byte[] bytes, CancellationToken ct) =>
        SaveAsync(_thumbnailDirectory, _settingsStore.Current.ThumbnailCacheLimit, filename, bytes, ct);

    public Task<byte[]?> TryGetFullImageAsync(string filename, CancellationToken ct) =>
        TryGetAsync(_fullImageDirectory, filename, ct);

    public Task SaveFullImageAsync(string filename, byte[] bytes, CancellationToken ct) =>
        SaveAsync(_fullImageDirectory, _settingsStore.Current.FullImageCacheLimit, filename, bytes, ct);

    /// <summary>Applies newly saved limits immediately, removing oldest files from either cache.
    /// This is deliberately best-effort, like all cache operations.</summary>
    public async Task ApplyLimitsAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            TrimLeastRecentlyUsed(_thumbnailDirectory, _settingsStore.Current.ThumbnailCacheLimit, ct);
            TrimLeastRecentlyUsed(_fullImageDirectory, _settingsStore.Current.FullImageCacheLimit, ct);
        }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally { _gate.Release(); }
    }

    private async Task<byte[]?> TryGetAsync(string directory, string filename, CancellationToken ct)
    {
        var path = PathFor(directory, filename);
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(path)) return null;
            var bytes = await File.ReadAllBytesAsync(path, ct);
            if (bytes.Length == 0) return null;
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return bytes;
        }
        catch (IOException)
        {
            return null; // best-effort cache; a read failure just means "treat as not cached"
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SaveAsync(string directory, int limit, string filename, byte[] bytes, CancellationToken ct)
    {
        if (bytes.Length == 0 || limit == 0) return;
        var path = PathFor(directory, filename);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(temporary, bytes, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            TrimLeastRecentlyUsed(directory, limit, ct);
        }
        catch (IOException)
        {
            // best-effort cache; a failed write just means it gets re-downloaded next time
        }
        catch (UnauthorizedAccessException)
        {
            // An unwritable cache must not prevent displaying downloaded thumbnails.
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            _gate.Release();
        }
    }

    private static void TrimLeastRecentlyUsed(string directory, int limit, CancellationToken ct)
    {
        if (!Directory.Exists(directory)) return;
        var files = Directory.EnumerateFiles(directory, "*.bin")
            .Select(path => new FileInfo(path))
            .OrderBy(file => file.LastWriteTimeUtc)
            .ToList();
        foreach (var file in files.Take(Math.Max(0, files.Count - limit)))
        {
            ct.ThrowIfCancellationRequested();
            try { file.Delete(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // Filenames aren't guaranteed filesystem-safe, so cache keys are hashes rather than filenames.
    private static string PathFor(string directory, string filename) =>
        Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(filename))) + ".bin");
}
