using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace SmartEditor.App.Services;

/// <summary>Creates a compact PNG for the asset grid without retaining the original image.
/// Full-resolution bytes are deliberately handled by the separate full-image cache only.</summary>
public static class AssetThumbnailRenderer
{
    public const int MaxDimension = 320;

    public static byte[] Create(byte[] sourceBytes)
    {
        using var image = Image.Load(sourceBytes);
        image.Mutate(context => context.Resize(new ResizeOptions
        {
            Size = new Size(MaxDimension, MaxDimension),
            Mode = ResizeMode.Max,
        }));
        using var output = new MemoryStream();
        image.SaveAsPng(output);
        return output.ToArray();
    }

    public static byte[]? TryCreate(byte[] sourceBytes)
    {
        try { return Create(sourceBytes); }
        catch (Exception) { return null; }
    }
}
