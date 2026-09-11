namespace SmartEditor.Core.Models;

/// <summary>The user's original ask: 0-8 source images, an optional mask on the first image, and
/// a prompt describing the desired edit.</summary>
public sealed class EditRequest
{
    public const int MaxImages = 8;

    public IReadOnlyList<SourceImage> Images { get; }
    public string Prompt { get; }
    public MaskImage? Mask { get; }

    public EditRequest(IReadOnlyList<SourceImage> images, string prompt, MaskImage? mask = null)
    {
        if (images.Count > MaxImages)
        {
            throw new ArgumentException($"At most {MaxImages} source images are supported.", nameof(images));
        }

        if (mask is not null && images.Count == 0)
        {
            throw new ArgumentException("A mask requires at least one source image.", nameof(mask));
        }

        Images = images;
        Prompt = prompt;
        Mask = mask;
    }
}
