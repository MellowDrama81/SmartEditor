using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartEditor.App.Services;
using SmartEditor.Core.Abstractions;
using SmartEditor.Core.Models;

namespace SmartEditor.App.ViewModels;

/// <summary>The single, permanent "Assets" tab: browses, uploads to, and downloads from whatever
/// ComfyUI backend is currently configured in Settings, plus purely-local tagging on top. There is
/// only ever one instance of this (registered as a DI singleton), unlike <see cref="EditorViewModel"/>
/// which gets a fresh instance per tab.</summary>
public partial class AssetsViewModel : TabViewModelBase
{
    private readonly IEditSessionFactory _sessionFactory;
    private readonly IFilePickerService _filePicker;
    private readonly AssetTagsStore _tagsStore;
    private readonly AssetThumbnailCache _thumbnailCache;

    private CancellationTokenSource? _refreshCts;
    private string? _nextCursor;
    private bool _hasMore = true;

    /// <summary>Backend output-asset filename &#8596; the filename of our own reupload of that exact
    /// same result (see <see cref="AddGeneratedResultAsync"/>), populated in both directions as
    /// soon as a result is generated. On Comfy Cloud, listing (<see cref="LoadPageAsync"/>)
    /// surfaces the workflow run's own "output"-tagged asset as its own entry, in addition to the
    /// "input"-tagged reupload we make for immediate browsing/reuse &mdash; without this, both show
    /// up as separate, visually identical rows. The two are separately-created, separately-paged
    /// assets, so either one can be fetched before the other (or the other might never be fetched
    /// at all, if it lands on a page the user never scrolls to) &mdash; <see cref="TryFindDuplicateAlreadyShown"/>
    /// only ever collapses into a counterpart that's already on screen, so an asset is never simply
    /// hidden on the assumption its replacement is showing when it might not be. Session-scoped
    /// only (not persisted): it exists to collapse a duplicate at the moment it's created, not to
    /// clean up ones already sitting in the account from before.</summary>
    private readonly Dictionary<string, string> _outputToReupload = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _reuploadToOutput = new(StringComparer.Ordinal);

    [ObservableProperty] public partial bool IsImageViewerOpen { get; set; }
    [ObservableProperty] public partial AssetItemViewModel? ViewedImage { get; set; }
    [ObservableProperty] public partial bool IsActualSize { get; set; }
    [ObservableProperty] public partial string ViewerStatus { get; set; } = "";
    private CancellationTokenSource? _viewerCts;

    [RelayCommand]
    private async Task ViewImageAsync(AssetItemViewModel item)
    {
        _viewerCts?.Cancel();
        ViewedImage?.ClearFullImage();
        var cts = _viewerCts = new CancellationTokenSource();
        ViewedImage = item;
        IsActualSize = false;
        ViewerStatus = "Loading image…";
        IsImageViewerOpen = true;
        try
        {
            var bytes = await _thumbnailCache.TryGetFullImageAsync(item.Filename, cts.Token)
                ?? await _sessionFactory.CreateComfyClient().DownloadInputAssetAsync(item.Filename, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            item.SetFullImageBytes(bytes);
            if (item.Thumbnail is null)
            {
                var thumbnailBytes = await Task.Run(() => AssetThumbnailRenderer.TryCreate(bytes), cts.Token);
                if (thumbnailBytes is not null)
                {
                    item.SetThumbnailBytes(thumbnailBytes);
                    await _thumbnailCache.SaveThumbnailAsync(item.Filename, thumbnailBytes, cts.Token);
                }
            }
            await _thumbnailCache.SaveFullImageAsync(item.Filename, bytes, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            ViewerStatus = item.FullImage is { } bitmap
                ? $"{bitmap.PixelSize.Width} × {bitmap.PixelSize.Height} pixels"
                : "This image format cannot be previewed. Use Download to save it.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!cts.IsCancellationRequested) ViewerStatus = $"Could not open image: {ex.Message}"; }
        finally
        {
            if (ReferenceEquals(_viewerCts, cts)) _viewerCts = null;
            cts.Dispose();
        }
    }

    [RelayCommand]
    private void CloseImageViewer()
    {
        _viewerCts?.Cancel();
        ViewedImage?.ClearFullImage();
        IsImageViewerOpen = false;
        ViewedImage = null;
    }

    /// <summary>Every asset loaded so far, unfiltered.</summary>
    public ObservableCollection<AssetItemViewModel> Assets { get; } = [];

    /// <summary><see cref="Assets"/> narrowed by <see cref="TagFilter"/>; this is what the grid
    /// actually binds to.</summary>
    public ObservableCollection<AssetItemViewModel> FilteredAssets { get; } = [];

    public bool HasAssets => FilteredAssets.Count > 0;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "";

    [ObservableProperty]
    public partial string TagFilter { get; set; } = "";

    // The folder-index emoji prefix is purely cosmetic: it makes the one permanent tab read as
    // visually distinct from the numbered, closable "Tab N" editor tabs at a glance.
    public AssetsViewModel(
        IEditSessionFactory sessionFactory, IFilePickerService filePicker, AssetTagsStore tagsStore, AssetThumbnailCache thumbnailCache)
        : base("\U0001F5C2 Assets", isClosable: false)
    {
        _sessionFactory = sessionFactory;
        _filePicker = filePicker;
        _tagsStore = tagsStore;
        _thumbnailCache = thumbnailCache;
        _tagsStore.SaveFailed += message => StatusMessage = message;
    }

    partial void OnTagFilterChanged(string value) => ApplyFilter();

    /// <summary>Restarts from the first page, discarding whatever was loaded before.</summary>
    [RelayCommand]
    private Task RefreshAsync() => LoadPageAsync(reset: true);

    /// <summary>Fetches the next page onto the end of what's already loaded. Called by the view
    /// when the user scrolls near the bottom of the asset grid, so a large account is only ever
    /// fetched as far as the user has actually scrolled &mdash; not all at once up front.</summary>
    [RelayCommand]
    private Task LoadMoreAsync() => IsLoading || !_hasMore ? Task.CompletedTask : LoadPageAsync(reset: false);

    private async Task LoadPageAsync(bool reset)
    {
        // Pagination shares the refresh lifetime so earlier pages finish caching their thumbnails.
        if (reset)
        {
            _refreshCts?.Cancel();
            _refreshCts?.Dispose();
            _refreshCts = null;
        }
        _refreshCts ??= new CancellationTokenSource();
        var ct = _refreshCts.Token;

        if (reset)
        {
            CloseImageViewer();
            foreach (var item in Assets) item.DisposeImages();
            Assets.Clear();
            FilteredAssets.Clear();
            OnPropertyChanged(nameof(HasAssets));
            _nextCursor = null;
            _hasMore = true;
        }

        if (!_hasMore)
        {
            return;
        }

        IsLoading = true;
        StatusMessage = reset ? "Loading assets…" : $"Loading more… ({Assets.Count} so far)";
        IComfyUiClient comfy;
        AssetPage page;
        try
        {
            comfy = _sessionFactory.CreateComfyClient();
            page = await comfy.ListInputAssetsPageAsync(_nextCursor, ct);
        }
        catch (OperationCanceledException)
        {
            return; // superseded by a newer refresh; leave whatever that one reports
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not load assets: {ex.Message}";
            return;
        }
        finally
        {
            IsLoading = false;
        }

        _nextCursor = page.NextCursor;
        _hasMore = _nextCursor is not null;

        var filter = TagFilter.Trim();
        var newItems = new List<AssetItemViewModel>(page.Assets.Count);
        foreach (var asset in page.Assets)
        {
            if (TryFindDuplicateAlreadyShown(asset.Name, out var survivorFilename))
            {
                // Comfy's own native copy of a result we already show under our reupload's
                // filename (or vice versa) — collapse into that already-shown row instead of
                // adding a second, rather than hiding this one outright.
                MergeOrphanedTags(fromFilename: asset.Name, intoFilename: survivorFilename);
                continue;
            }

            var item = new AssetItemViewModel(asset, _tagsStore);
            Assets.Add(item);
            newItems.Add(item);
            if (filter.Length == 0 || item.Tags.Any(t => t.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            {
                FilteredAssets.Add(item);
            }
        }

        OnPropertyChanged(nameof(HasAssets));
        StatusMessage = _hasMore
            ? $"{Assets.Count} assets loaded — scroll for more."
            : Assets.Count == 1 ? "1 asset." : $"{Assets.Count} assets.";

        // Thumbnails for just this page load in the background after the page itself is up, so the
        // grid (with per-item spinners) appears immediately rather than waiting on every fetch.
        // Deliberately NOT awaited: RefreshCommand/LoadMoreCommand only stay "busy" (disabling the
        // button) for as long as this method's own Task is running, and thumbnails can take a while
        // to trickle in one at a time — awaiting them here left Refresh looking permanently disabled
        // long after the page's asset list had actually finished loading.
        _ = LoadThumbnailsAsync(comfy, newItems, ct);
    }

    private async Task LoadThumbnailsAsync(IComfyUiClient comfy, IReadOnlyList<AssetItemViewModel> items, CancellationToken ct)
    {
        // Sequential, not parallel: a self-hosted ComfyUI box is typically a single GPU machine —
        // don't hammer it with a request storm just to populate a thumbnail grid. Each item appears
        // in the grid immediately with a spinner and fills in as its own fetch completes. Scoped to
        // just the page that was loaded, not the whole running Assets list.
        foreach (var item in items)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            try
            {
                var cached = await _thumbnailCache.TryGetThumbnailAsync(item.Filename, ct);
                if (cached is not null)
                {
                    item.SetThumbnailBytes(cached);
                    continue;
                }

                var fullBytes = await comfy.DownloadInputAssetAsync(item.Filename, ct);
                var thumbnailBytes = await Task.Run(() => AssetThumbnailRenderer.TryCreate(fullBytes), ct);
                if (thumbnailBytes is null)
                {
                    item.IsLoadingThumbnail = false;
                    continue;
                }
                item.SetThumbnailBytes(thumbnailBytes);
                await _thumbnailCache.SaveThumbnailAsync(item.Filename, thumbnailBytes, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                item.IsLoadingThumbnail = false; // best-effort thumbnails; move on if one fetch fails
            }
        }
    }

    [RelayCommand]
    private async Task AddFilesAsync()
    {
        var picked = await _filePicker.PickImagesAsync();
        if (picked.Count == 0)
        {
            return;
        }

        StatusMessage = $"Uploading {picked.Count} file(s)…";
        try
        {
            var comfy = _sessionFactory.CreateComfyClient();
            foreach (var file in picked)
            {
                var name = await comfy.UploadInputAssetAsync(file.Bytes, file.Name, CancellationToken.None);
                // Comfy's upload response only carries the storage filename, not a full asset
                // record — this stands in for the display name until the user hits Refresh, which
                // picks up the real one from the listing API on Cloud.
                var item = new AssetItemViewModel(new AssetInfo(name, file.Name), _tagsStore);
                var thumbnailBytes = AssetThumbnailRenderer.TryCreate(file.Bytes);
                if (thumbnailBytes is not null) item.SetThumbnailBytes(thumbnailBytes);
                else item.IsLoadingThumbnail = false;
                Assets.Insert(0, item);
                if (thumbnailBytes is not null) _ = _thumbnailCache.SaveThumbnailAsync(name, thumbnailBytes, CancellationToken.None);
                _ = _thumbnailCache.SaveFullImageAsync(name, file.Bytes, CancellationToken.None);
            }
            ApplyFilter();
            StatusMessage = $"Uploaded {picked.Count} file(s).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Upload failed: {ex.Message}";
        }
    }

    /// <summary>Uploads a freshly generated result into Comfy's input asset store and inserts it at
    /// the front of the browsable list, so it's available immediately &mdash; both to look at and
    /// to reuse as a new source image &mdash; without waiting for a Refresh. Uploading it for real
    /// (rather than just inserting a locally-labelled placeholder) matters: <see cref="EditorViewModel.AddAssetToSourcesAsync"/>
    /// trusts an asset's <see cref="AssetItemViewModel.Filename"/> to already exist on the backend
    /// and skips re-uploading it, so a fabricated filename would break a later run that reused this
    /// result as a source image.</summary>
    /// <param name="outputFilename">The same result's own identity as produced by the workflow run
    /// itself (<see cref="EditIteration.ResultOutputFilename"/>) &mdash; recorded so a later listing
    /// refresh can recognize Comfy's native copy of this same result as a duplicate of the reupload
    /// below and collapse the two into one row instead of showing both.</param>
    public async Task<bool> AddGeneratedResultAsync(byte[] bytes, string displayName, string? outputFilename)
    {
        try
        {
            var comfy = _sessionFactory.CreateComfyClient();
            var name = await comfy.UploadInputAssetAsync(bytes, displayName, CancellationToken.None);
            if (!string.IsNullOrEmpty(outputFilename))
            {
                _outputToReupload[outputFilename] = name;
                _reuploadToOutput[name] = outputFilename;
            }

            InsertAtFront(name, displayName, bytes);
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not add the generated result to the asset library: {ex.Message}";
            return false;
        }
    }

    /// <summary>True only if <paramref name="filename"/> is a known duplicate of some other
    /// filename representing the exact same generated result AND that counterpart is already
    /// shown in <see cref="Assets"/> &mdash; in which case <paramref name="survivorFilename"/> is
    /// that counterpart. Deliberately does NOT report a duplicate just because a mapping exists:
    /// the two sides of a pair are fetched independently (possibly on different pages, possibly
    /// one not at all), so assuming the counterpart is showing without checking would hide an
    /// asset that has nothing already on screen to collapse into.</summary>
    private bool TryFindDuplicateAlreadyShown(string filename, out string survivorFilename)
    {
        if (_outputToReupload.TryGetValue(filename, out var reupload) && Assets.Any(a => a.Filename == reupload))
        {
            survivorFilename = reupload;
            return true;
        }

        if (_reuploadToOutput.TryGetValue(filename, out var output) && Assets.Any(a => a.Filename == output))
        {
            survivorFilename = output;
            return true;
        }

        survivorFilename = "";
        return false;
    }

    /// <summary>Moves any tags stored under a duplicate entry we're about to collapse away onto
    /// the entry that survives instead of silently dropping them.</summary>
    private void MergeOrphanedTags(string fromFilename, string intoFilename)
    {
        var orphaned = _tagsStore.GetTags(fromFilename);
        if (orphaned.Count == 0)
        {
            return;
        }

        var survivor = Assets.FirstOrDefault(a => a.Filename == intoFilename);
        var merged = (survivor?.Tags ?? _tagsStore.GetTags(intoFilename))
            .Concat(orphaned)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (survivor is not null)
        {
            survivor.TagsText = string.Join(", ", merged); // also persists, via OnTagsTextChanged
        }
        else
        {
            _tagsStore.SetTags(intoFilename, merged);
        }

        _tagsStore.SetTags(fromFilename, []); // clears the now-merged, never-shown-again entry
    }

    /// <summary>Inserts an asset that's already been uploaded elsewhere (e.g. a source image
    /// added to an editor tab, which uploads it itself right away) at the front of the browsable
    /// list, so it's visible immediately without waiting for a Refresh &mdash; without re-uploading
    /// it under a second name.</summary>
    public void AddAlreadyUploaded(string filename, string displayName, byte[] bytes) =>
        InsertAtFront(filename, displayName, bytes);

    private void InsertAtFront(string filename, string displayName, byte[] bytes)
    {
        var item = new AssetItemViewModel(new AssetInfo(filename, displayName), _tagsStore);
        var thumbnailBytes = AssetThumbnailRenderer.TryCreate(bytes);
        if (thumbnailBytes is not null) item.SetThumbnailBytes(thumbnailBytes);
        else item.IsLoadingThumbnail = false;
        Assets.Insert(0, item);
        ApplyFilter();

        // Already have the bytes right here — seed the cache so a later Refresh (a fresh
        // AssetItemViewModel instance for the same filename) doesn't re-download them.
        if (thumbnailBytes is not null) _ = _thumbnailCache.SaveThumbnailAsync(filename, thumbnailBytes, CancellationToken.None);
        _ = _thumbnailCache.SaveFullImageAsync(filename, bytes, CancellationToken.None);
    }

    [RelayCommand]
    private async Task DownloadAsync(AssetItemViewModel item)
    {
        try
        {
            var bytes = await _thumbnailCache.TryGetFullImageAsync(item.Filename, CancellationToken.None)
                ?? await _sessionFactory.CreateComfyClient().DownloadInputAssetAsync(item.Filename, CancellationToken.None);
            await _thumbnailCache.SaveFullImageAsync(item.Filename, bytes, CancellationToken.None);
            var saved = await _filePicker.SaveFileAsync(bytes, item.Filename);
            StatusMessage = saved ? $"Saved {item.Filename}." : StatusMessage;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not download {item.Filename}: {ex.Message}";
        }
    }

    private void ApplyFilter()
    {
        var filter = TagFilter.Trim();
        FilteredAssets.Clear();
        foreach (var item in Assets)
        {
            if (filter.Length == 0 || item.Tags.Any(t => t.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            {
                FilteredAssets.Add(item);
            }
        }
        OnPropertyChanged(nameof(HasAssets));
    }
}
