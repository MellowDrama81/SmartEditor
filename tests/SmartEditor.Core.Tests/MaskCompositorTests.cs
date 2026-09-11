using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SmartEditor.Core.Services;
using Xunit;

namespace SmartEditor.Core.Tests;

public class MaskCompositorTests
{
    private static byte[] MakeImage(int width, int height, Action<Image<Rgba32>> paint)
    {
        using var image = new Image<Rgba32>(width, height);
        paint(image);
        using var stream = new MemoryStream();
        image.Save(stream, new PngEncoder());
        return stream.ToArray();
    }

    [Fact]
    public void Painted_region_becomes_transparent_and_the_rest_stays_opaque()
    {
        var source = MakeImage(2, 2, image =>
        {
            for (var y = 0; y < 2; y++)
            for (var x = 0; x < 2; x++)
                image[x, y] = new Rgba32(255, 0, 0, 255);
        });

        // Top-left pixel painted (opaque white = "edit here"); the rest untouched (transparent).
        var mask = MakeImage(2, 2, image =>
        {
            image[0, 0] = new Rgba32(255, 255, 255, 255);
            image[1, 0] = new Rgba32(0, 0, 0, 0);
            image[0, 1] = new Rgba32(0, 0, 0, 0);
            image[1, 1] = new Rgba32(0, 0, 0, 0);
        });

        var resultBytes = MaskCompositor.ApplyMask(source, mask);

        using var result = Image.Load<Rgba32>(resultBytes);
        Assert.Equal(0, result[0, 0].A);
        Assert.Equal(255, result[1, 0].A);
        Assert.Equal(255, result[0, 1].A);
        Assert.Equal(255, result[1, 1].A);

        // Color channels are preserved regardless of the new alpha.
        Assert.Equal((byte)255, result[0, 0].R);
    }

    [Fact]
    public void Throws_when_mask_and_source_dimensions_differ()
    {
        var source = MakeImage(4, 4, _ => { });
        var mask = MakeImage(2, 2, _ => { });

        Assert.Throws<InvalidOperationException>(() => MaskCompositor.ApplyMask(source, mask));
    }
}
