using Avalonia.Media.Imaging;
using SmartEditor.Core.Models;

namespace SmartEditor.App.ViewModels;

public sealed class IterationDisplayViewModel
{
    public int Index { get; }
    public string WorkflowId { get; }
    public string RefinedPrompt { get; }
    public bool Satisfied { get; }
    public string JudgeFeedback { get; }
    public Bitmap? ResultImage { get; }

    public IterationDisplayViewModel(EditIteration iteration)
    {
        Index = iteration.Index;
        WorkflowId = iteration.WorkflowId;
        RefinedPrompt = iteration.RefinedPrompt;
        Satisfied = iteration.Satisfied;
        JudgeFeedback = iteration.JudgeFeedback;
        if (iteration.ResultImageBytes is { } bytes)
        {
            using var stream = new MemoryStream(bytes);
            ResultImage = new Bitmap(stream);
        }
    }
}
