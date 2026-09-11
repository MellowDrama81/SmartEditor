using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace SmartEditor.App.Controls;

/// <summary>Freehand brush mask painting over a source image. The control is always sized to the
/// source image's <em>native pixel resolution</em> (see <see cref="MaskSize"/>) and left to a
/// parent <c>Viewbox</c> for on-screen scaling, so pointer coordinates captured here are already
/// in source-image pixel space &mdash; no manual scale mapping needed. Painting/erasing writes
/// directly into a backing <see cref="WriteableBitmap"/> pixel buffer so erasing is a true pixel
/// clear, not a layered vector draw.</summary>
public sealed class MaskCanvas : Control
{
    public static readonly StyledProperty<PixelSize> MaskSizeProperty =
        AvaloniaProperty.Register<MaskCanvas, PixelSize>(nameof(MaskSize));

    public PixelSize MaskSize
    {
        get => GetValue(MaskSizeProperty);
        set => SetValue(MaskSizeProperty, value);
    }

    public static readonly StyledProperty<double> BrushRadiusProperty =
        AvaloniaProperty.Register<MaskCanvas, double>(nameof(BrushRadius), 32.0);

    public double BrushRadius
    {
        get => GetValue(BrushRadiusProperty);
        set => SetValue(BrushRadiusProperty, value);
    }

    public static readonly StyledProperty<bool> IsErasingProperty =
        AvaloniaProperty.Register<MaskCanvas, bool>(nameof(IsErasing));

    public bool IsErasing
    {
        get => GetValue(IsErasingProperty);
        set => SetValue(IsErasingProperty, value);
    }

    private WriteableBitmap? _bitmap;
    private Point? _lastPoint;

    public MaskCanvas()
    {
        ClipToBounds = true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MaskSizeProperty)
        {
            EnsureBitmap();
        }
    }

    private void EnsureBitmap()
    {
        var size = MaskSize;
        _bitmap?.Dispose();
        _bitmap = size.Width > 0 && size.Height > 0
            ? new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul)
            : null;
        Clear();
    }

    /// <summary>Clears every painted stroke back to fully transparent.</summary>
    public void Clear()
    {
        if (_bitmap is null)
        {
            return;
        }

        using (var buffer = _bitmap.Lock())
        {
            unsafe
            {
                var totalBytes = buffer.RowBytes * buffer.Size.Height;
                var ptr = (byte*)buffer.Address;
                for (var i = 0; i < totalBytes; i++)
                {
                    ptr[i] = 0;
                }
            }
        }

        InvalidateVisual();
    }

    /// <summary>True if at least one pixel has been painted.</summary>
    public bool HasContent { get; private set; }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_bitmap is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Pointer.Capture(this);
        var point = e.GetPosition(this);
        StampAt(point);
        _lastPoint = point;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_bitmap is null || _lastPoint is not { } last)
        {
            return;
        }

        var point = e.GetPosition(this);
        StampLine(last, point);
        _lastPoint = point;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _lastPoint = null;
        e.Pointer.Capture(null);
    }

    private void StampLine(Point from, Point to)
    {
        var distance = Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Y - from.Y, 2));
        var step = Math.Max(1.0, BrushRadius / 3);
        var steps = Math.Max(1, (int)(distance / step));
        for (var i = 1; i <= steps; i++)
        {
            var t = (double)i / steps;
            StampAt(new Point(from.X + ((to.X - from.X) * t), from.Y + ((to.Y - from.Y) * t)));
        }
    }

    private void StampAt(Point center)
    {
        if (_bitmap is null)
        {
            return;
        }

        var radius = BrushRadius;
        var minX = Math.Max(0, (int)(center.X - radius));
        var maxX = Math.Min(_bitmap.PixelSize.Width - 1, (int)(center.X + radius));
        var minY = Math.Max(0, (int)(center.Y - radius));
        var maxY = Math.Min(_bitmap.PixelSize.Height - 1, (int)(center.Y + radius));
        if (minX > maxX || minY > maxY)
        {
            return;
        }

        var radiusSq = radius * radius;
        var erasing = IsErasing;

        using (var buffer = _bitmap.Lock())
        {
            unsafe
            {
                var basePtr = (byte*)buffer.Address;
                for (var y = minY; y <= maxY; y++)
                {
                    var dy = y + 0.5 - center.Y;
                    var rowPtr = basePtr + (y * buffer.RowBytes);
                    for (var x = minX; x <= maxX; x++)
                    {
                        var dx = x + 0.5 - center.X;
                        if ((dx * dx) + (dy * dy) > radiusSq)
                        {
                            continue;
                        }

                        var pixel = rowPtr + (x * 4);
                        if (erasing)
                        {
                            pixel[0] = 0;
                            pixel[1] = 0;
                            pixel[2] = 0;
                            pixel[3] = 0;
                        }
                        else
                        {
                            pixel[0] = 255;
                            pixel[1] = 255;
                            pixel[2] = 255;
                            pixel[3] = 255;
                        }
                    }
                }
            }
        }

        if (!erasing)
        {
            HasContent = true;
        }

        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_bitmap is not null)
        {
            context.DrawImage(_bitmap, new Rect(Bounds.Size));
        }
    }

    /// <summary>Exports the painted mask as PNG bytes, at the canvas's native pixel resolution.
    /// White/opaque marks the region to edit (the app's canonical mask convention).</summary>
    public byte[] ExportPng()
    {
        if (_bitmap is null)
        {
            throw new InvalidOperationException("Mask canvas has no backing bitmap yet.");
        }

        using var stream = new MemoryStream();
        _bitmap.Save(stream, new PngBitmapEncoderOptions());
        return stream.ToArray();
    }
}
