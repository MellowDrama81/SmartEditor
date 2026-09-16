using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using SmartEditor.App.Services;
using SmartEditor.Core.Models;

namespace SmartEditor.App.ViewModels;

/// <summary>One image in ComfyUI's input asset store, as shown in the Assets tab's grid: a
/// thumbnail (loaded lazily/best-effort), and a user-editable, locally-persisted set of tags.</summary>
public partial class AssetItemViewModel : ViewModelBase
{
    private readonly AssetTagsStore _tagsStore;
    private readonly bool _loaded;

    public string DisplayName { get; }

    /// <summary>The storage filename to pass to <c>DownloadInputAssetAsync</c> — not necessarily
    /// human-readable; see <see cref="AssetInfo.Name"/>.</summary>
    public string Filename { get; }

    [ObservableProperty]
    public partial Bitmap? Thumbnail { get; set; }

    /// <summary>Only populated while this asset is open in the full-size viewer. Keeping it off
    /// the grid items prevents a large asset library from retaining every original-sized bitmap.</summary>
    [ObservableProperty]
    public partial Bitmap? FullImage { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingThumbnail { get; set; } = true;

    [ObservableProperty]
    public partial string TagsText { get; set; }

    public IReadOnlyList<string> Tags =>
        TagsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public AssetItemViewModel(AssetInfo asset, AssetTagsStore tagsStore)
    {
        DisplayName = asset.DisplayName;
        Filename = asset.Name;
        _tagsStore = tagsStore;
        // Tags are keyed by the storage filename — the one value that actually, uniquely refers
        // to this asset anywhere in ComfyUI (there's no separate asset id used for that purpose).
        TagsText = string.Join(", ", tagsStore.GetTags(asset.Name));
        _loaded = true; // don't let the assignment above write the just-loaded tags straight back out
    }

    partial void OnTagsTextChanged(string value)
    {
        if (_loaded)
        {
            _tagsStore.SetTags(Filename, Tags);
        }
    }

    public void SetThumbnailBytes(byte[] bytes)
    {
        Thumbnail?.Dispose();
        Thumbnail = null;
        try
        {
            using var stream = new MemoryStream(bytes);
            Thumbnail = new Bitmap(stream);
        }
        catch (Exception)
        {
            // Not every asset in the store is necessarily a still-decodable image (e.g. a format
            // Avalonia's Bitmap can't load) — leave the thumbnail blank rather than fail the grid.
        }
        finally
        {
            IsLoadingThumbnail = false;
        }
    }

    public void SetFullImageBytes(byte[] bytes)
    {
        ClearFullImage();
        try
        {
            using var stream = new MemoryStream(bytes);
            FullImage = new Bitmap(stream);
        }
        catch (Exception)
        {
            // The thumbnail may still be usable if this full-size decode fails.
        }
    }

    public void ClearFullImage()
    {
        FullImage?.Dispose();
        FullImage = null;
    }

    public void DisposeImages()
    {
        Thumbnail?.Dispose();
        Thumbnail = null;
        ClearFullImage();
    }
}
