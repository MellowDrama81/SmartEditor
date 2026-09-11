using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace SmartEditor.App.Services;

/// <summary>Implements <see cref="IFilePickerService"/> against whichever <see cref="TopLevel"/>
/// is currently hosting the UI (the desktop window, or the Android single-view host). The host is
/// set once from the view's code-behind, since it isn't known at DI composition time.</summary>
public sealed class AvaloniaFilePickerService : IFilePickerService
{
    private TopLevel? _host;

    public void SetHost(TopLevel? host) => _host = host;

    public async Task<IReadOnlyList<PickedFile>> PickImagesAsync()
    {
        if (_host?.StorageProvider is not { } storageProvider)
        {
            return [];
        }

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose source images",
            AllowMultiple = true,
            FileTypeFilter = [FilePickerFileTypes.ImageAll],
        });

        var result = new List<PickedFile>();
        foreach (var file in files)
        {
            await using var stream = await file.OpenReadAsync();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            result.Add(new PickedFile(file.Name, memory.ToArray()));
        }

        return result;
    }

    public async Task<bool> SaveFileAsync(byte[] bytes, string suggestedName)
    {
        if (_host?.StorageProvider is not { } storageProvider)
        {
            return false;
        }

        var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save result image",
            SuggestedFileName = suggestedName,
            FileTypeChoices = [FilePickerFileTypes.ImagePng],
        });

        if (file is null)
        {
            return false;
        }

        await using var stream = await file.OpenWriteAsync();
        await stream.WriteAsync(bytes);
        return true;
    }
}
