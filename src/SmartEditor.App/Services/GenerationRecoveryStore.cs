using System.Text.Json;

namespace SmartEditor.App.Services;

/// <summary>Durable list of Cloud jobs whose output has not yet been safely received by the app.</summary>
public sealed class GenerationRecoveryStore
{
    private readonly object _gate = new();
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SmartEditor", "pending-generations.json");

    public IReadOnlyList<string> GetPending()
    {
        lock (_gate) return Read();
    }

    public void Track(string jobId)
    {
        lock (_gate)
        {
            var jobs = Read();
            if (!jobs.Contains(jobId, StringComparer.Ordinal))
            {
                Write([.. jobs, jobId]);
            }
        }
    }

    public void Remove(string jobId)
    {
        lock (_gate) Write([.. Read().Where(id => id != jobId)]);
    }

    private List<string> Read()
    {
        if (!File.Exists(_path)) return [];
        try
        {
            using var stream = File.OpenRead(_path);
            return JsonSerializer.Deserialize<List<string>>(stream) ?? [];
        }
        catch (JsonException ex) { throw new InvalidDataException("The pending-generation journal is corrupt.", ex); }
    }

    private void Write(IReadOnlyList<string> jobs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (!AtomicFile.TryWriteAllText(_path, JsonSerializer.Serialize(jobs)))
        {
            throw new IOException("Could not save the pending-generation journal.");
        }
    }
}
