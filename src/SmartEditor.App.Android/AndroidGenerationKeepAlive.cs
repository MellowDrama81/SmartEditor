using Android.App;
using SmartEditor.App.Services;

namespace SmartEditor.App.Android;

public sealed class AndroidGenerationKeepAlive : IGenerationKeepAlive
{
    private readonly Application _application;

    public AndroidGenerationKeepAlive(Application application) => _application = application;

    public void Start(string status) => GenerationForegroundService.Start(_application, status);
    public void Update(string status) => GenerationForegroundService.Update(_application, status);
    public void Stop() => GenerationForegroundService.Stop(_application);
}
