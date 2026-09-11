using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace SmartEditor.Core.Services;

/// <summary>Composites a user-painted mask (opaque = "edit this region", per
/// <c>SmartEditor.App.Controls.MaskCanvas</c>'s own painting convention) into a source image's
/// alpha channel using the convention every bundled masked ComfyUI workflow expects: alpha=0
/// (transparent) at the region to edit, alpha=255 (opaque) everywhere else.</summary>
internal static class MaskCompositor
{
    public static byte[] ApplyMask(byte[] sourceImageBytes, byte[] maskPngBytes)
    {
        using var source = Image.Load<Rgba32>(sourceImageBytes);
        using var mask = Image.Load<Rgba32>(maskPngBytes);

        if (mask.Width != source.Width || mask.Height != source.Height)
        {
            throw new InvalidOperationException(
                $"Mask size ({mask.Width}x{mask.Height}) does not match the source image size ({source.Width}x{source.Height}).");
        }

        source.ProcessPixelRows(mask, (sourceAccessor, maskAccessor) =>
        {
            for (var y = 0; y < sourceAccessor.Height; y++)
            {
                var sourceRow = sourceAccessor.GetRowSpan(y);
                var maskRow = maskAccessor.GetRowSpan(y);
                for (var x = 0; x < sourceRow.Length; x++)
                {
                    ref var pixel = ref sourceRow[x];
                    pixel.A = (byte)(255 - maskRow[x].A);
                }
            }
        });

        using var output = new MemoryStream();
        source.Save(output, new PngEncoder());
        return output.ToArray();
    }
}
