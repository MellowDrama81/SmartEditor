using System.Text.Json;

namespace SmartEditor.App.Services;

/// <summary>Loads/saves a filename &#8594; tags map to a local JSON file under the platform's
/// application-data folder. Tags are purely a local convenience for finding assets again later
/// &mdash; ComfyUI itself has no concept of tags, so nothing here is ever sent to it.</summary>
public sealed class AssetTagsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly Dictionary<string, List<string>> _tagsByFilename;

    /// <summary>Raised when an edit is retained for this session but could not be made durable.</summary>
    public event Action<string>? SaveFailed;

    public AssetTagsStore()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SmartEditor");
        _filePath = Path.Combine(directory, "asset-tags.json");
        _tagsByFilename = Load();
    }

    private Dictionary<string, List<string>> Load()
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(_filePath), JsonOptions)
                   ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public IReadOnlyList<string> GetTags(string filename) =>
        _tagsByFilename.TryGetValue(filename, out var tags) ? tags : [];

    public void SetTags(string filename, IReadOnlyList<string> tags)
    {
        if (tags.Count == 0)
        {
            _tagsByFilename.Remove(filename);
        }
        else
        {
            _tagsByFilename[filename] = [.. tags];
        }

        if (!AtomicFile.TryWriteAllText(_filePath, JsonSerializer.Serialize(_tagsByFilename, JsonOptions)))
        {
            SaveFailed?.Invoke("Could not save asset tags. Check available storage and try again.");
        }
    }
}
