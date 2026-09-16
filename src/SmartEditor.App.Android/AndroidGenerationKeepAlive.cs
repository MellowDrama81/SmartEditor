using Android.App;
using SmartEditor.App.Services;

namespace SmartEditor.App.Android;

public sealed class AndroidGenerationKeepAlive : IGenerationKeepAlive
{
    private readonly Application _application;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, string> _operations = [];

    public AndroidGenerationKeepAlive(Application application) => _application = application;

    public Guid Start(string status)
    {
        lock (_gate)
        {
            var operationId = Guid.NewGuid();
            var wasEmpty = _operations.Count == 0;
            _operations[operationId] = status;
            if (wasEmpty) GenerationForegroundService.Start(_application, status);
            else GenerationForegroundService.Update(_application, status);
            return operationId;
        }
    }

    public void Update(Guid operationId, string status)
    {
        lock (_gate)
        {
            if (!_operations.ContainsKey(operationId)) return;
            _operations[operationId] = status;
            GenerationForegroundService.Update(_application, status);
        }
    }

    public void Stop(Guid operationId)
    {
        lock (_gate)
        {
            if (!_operations.Remove(operationId)) return;
            if (_operations.Count == 0) GenerationForegroundService.Stop(_application);
            else GenerationForegroundService.Update(_application, _operations.Values.Last());
        }
    }
}
