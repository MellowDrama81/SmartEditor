using System.Globalization;
using Avalonia.Data.Converters;

namespace SmartEditor.App.Converters;

/// <summary>Given the available width of the asset grid's scrolling container, returns the
/// thumbnail card width to use: <see cref="PreferredWidth"/> unchanged whenever that leaves room
/// for at least two cards side by side (the common desktop/tablet case), or just small enough to
/// fit exactly two otherwise (a narrow phone in portrait). Never grows past the preferred size, and
/// only ever shrinks the on-screen card &mdash; the underlying thumbnail image is still requested
/// and cached at its normal resolution and is simply drawn smaller (<c>Stretch="Uniform"</c>
/// already handles that), so nothing about the actual asset data changes.</summary>
public sealed class TwoColumnThumbnailWidthConverter : IValueConverter
{
    public const double PreferredWidth = 176;

    private const double MinWidth = 96;

    /// <summary>Matches the card's own <c>Margin="4"</c> on each side.</summary>
    private const double CardMargin = 4;

    /// <summary>Slack for the container's own padding and an overlay scrollbar that floats on top
    /// of the content rather than reserving its own column.</summary>
    private const double ContainerBuffer = 24;

    public static readonly TwoColumnThumbnailWidthConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double containerWidth || containerWidth <= 0)
        {
            return PreferredWidth;
        }

        var perCardBudget = (containerWidth - ContainerBuffer) / 2 - CardMargin * 2;
        return Math.Clamp(perCardBudget, MinWidth, PreferredWidth);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
