namespace SmartEditor.Core.Models;

/// <summary>A user-supplied source image, kept at original resolution for ComfyUI and lazily
/// downscaled for LLM vision calls.</summary>
public sealed class SourceImage
{
    public Guid Id { get; }
    public string FileName { get; }
    public byte[] OriginalBytes { get; }

    private byte[]? _llmPreviewBytes;

    public SourceImage(string fileName, byte[] originalBytes)
    {
        Id = Guid.NewGuid();
        FileName = fileName;
        OriginalBytes = originalBytes;
    }

    /// <summary>Downscaled (long edge &#8804; 1536px) JPEG bytes suitable for sending to a vision LLM.
    /// Computed once and cached.</summary>
    public byte[] GetLlmPreviewBytes() => _llmPreviewBytes ??= ImagePreview.CreatePreview(OriginalBytes);
}
