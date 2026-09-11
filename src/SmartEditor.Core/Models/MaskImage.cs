namespace SmartEditor.Core.Models;

/// <summary>A freehand-painted mask aligned to the first source image's native resolution.</summary>
public sealed class MaskImage
{
    /// <summary>PNG bytes, same pixel dimensions as the first source image.</summary>
    public byte[] Bytes { get; }

    public MaskImage(byte[] bytes) => Bytes = bytes;
}
