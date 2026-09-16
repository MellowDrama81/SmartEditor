namespace SmartEditor.App.Services;

/// <summary>Keeps a user-started generation alive while the app is backgrounded where the
/// platform supports it. Desktop uses the no-op implementation.</summary>
public interface IGenerationKeepAlive
{
    Guid Start(string status);
    void Update(Guid operationId, string status);
    void Stop(Guid operationId);
}

public sealed class NoOpGenerationKeepAlive : IGenerationKeepAlive
{
    public Guid Start(string status) => Guid.NewGuid();
    public void Update(Guid operationId, string status) { }
    public void Stop(Guid operationId) { }
}
