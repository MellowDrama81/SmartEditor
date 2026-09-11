namespace SmartEditor.App.Services;

public sealed record PickedFile(string Name, byte[] Bytes);

/// <summary>Cross-platform (Desktop + Android) file picking, backed by Avalonia's
/// <c>TopLevel.StorageProvider</c>.</summary>
public interface IFilePickerService
{
    Task<IReadOnlyList<PickedFile>> PickImagesAsync();

    Task<bool> SaveFileAsync(byte[] bytes, string suggestedName);
}
