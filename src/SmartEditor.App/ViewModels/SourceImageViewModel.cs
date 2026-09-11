using Avalonia.Media.Imaging;
using SmartEditor.Core.Models;

namespace SmartEditor.App.ViewModels;

public sealed class SourceImageViewModel
{
    public SourceImage Model { get; }
    public Bitmap Thumbnail { get; }

    public SourceImageViewModel(SourceImage model)
    {
        Model = model;
        using var stream = new MemoryStream(model.OriginalBytes);
        Thumbnail = new Bitmap(stream);
    }
}
