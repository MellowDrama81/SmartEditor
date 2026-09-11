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

    /// <summary>The asset's raw bytes, once fetched — cached so "Download" doesn't need a second
    /// round trip after the thumbnail has already loaded them.</summary>
    public byte[]? Bytes { get; private set; }

    [ObservableProperty]
    public partial Bitmap? Thumbnail { get; set; }

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

    public void SetBytes(byte[] bytes)
    {
        Bytes = bytes;
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
}
