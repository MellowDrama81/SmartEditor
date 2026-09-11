namespace SmartEditor.Core.Configuration;

public sealed class OrchestratorOptions
{
    public const string SectionName = "Orchestrator";
    public const int HardMaxIterations = 10;

    private int _maxIterations = 3;

    public int MaxIterations
    {
        get => _maxIterations;
        set => _maxIterations = Math.Clamp(value, 1, HardMaxIterations);
    }
}
