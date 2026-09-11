using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace SmartEditor.Core.Models;

/// <summary>Downscales images before they're sent to a vision LLM. Never used on the bytes sent
/// to ComfyUI itself, which always gets the original resolution.</summary>
internal static class ImagePreview
{
    private const int MaxLongEdge = 1536;
    private const int JpegQuality = 85;

    public static byte[] CreatePreview(byte[] originalBytes)
    {
        using var image = Image.Load(originalBytes);

        if (image.Width > MaxLongEdge || image.Height > MaxLongEdge)
        {
            image.Mutate(ctx => ctx.Resize(new ResizeOptions
            {
                Mode = ResizeMode.Max,
                Size = new Size(MaxLongEdge, MaxLongEdge),
            }));
        }

        using var output = new MemoryStream();
        image.Save(output, new JpegEncoder { Quality = JpegQuality });
        return output.ToArray();
    }
}
