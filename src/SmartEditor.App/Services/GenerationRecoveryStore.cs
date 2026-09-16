using System.Text.Json;

namespace SmartEditor.App.Services;

/// <summary>Durable list of Cloud jobs whose output has not yet been safely received by the app.</summary>
public sealed class GenerationRecoveryStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SmartEditor", "pending-generations.json");

    public async Task<IReadOnlyList<string>> GetPendingAsync()
    {
        await _gate.WaitAsync();
        try { return await ReadAsync(); }
        finally { _gate.Release(); }
    }

    public async Task TrackAsync(string jobId)
    {
        await _gate.WaitAsync();
        try
        {
            var jobs = await ReadAsync();
            if (!jobs.Contains(jobId, StringComparer.Ordinal))
            {
                await WriteAsync([.. jobs, jobId]);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAsync(string jobId)
    {
        await _gate.WaitAsync();
        try { await WriteAsync([.. (await ReadAsync()).Where(id => id != jobId)]); }
        finally { _gate.Release(); }
    }

    private async Task<List<string>> ReadAsync()
    {
        if (!File.Exists(_path)) return [];
        try
        {
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<List<string>>(stream) ?? [];
        }
        catch (JsonException) { return []; }
    }

    private async Task WriteAsync(IReadOnlyList<string> jobs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(jobs));
    }
}
