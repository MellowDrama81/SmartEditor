namespace SmartEditor.App.Services;

/// <summary>Delivers critical lifecycle updates inline instead of posting them to the UI queue.</summary>
public sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
