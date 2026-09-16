namespace SmartEditor.App.Services;

/// <summary>Keeps a user-started generation alive while the app is backgrounded where the
/// platform supports it. Desktop uses the no-op implementation.</summary>
public interface IGenerationKeepAlive
{
    void Start(string status);
    void Update(string status);
    void Stop();
}

public sealed class NoOpGenerationKeepAlive : IGenerationKeepAlive
{
    public void Start(string status) { }
    public void Update(string status) { }
    public void Stop() { }
}
