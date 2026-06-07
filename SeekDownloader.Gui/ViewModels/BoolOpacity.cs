using System.Globalization;
using Avalonia.Data.Converters;

namespace SeekDownloader.Gui.ViewModels;

/// <summary>true → fully opaque, false → dimmed (used to grey out not-found albums).</summary>
public class BoolOpacity : IValueConverter
{
    public static readonly BoolOpacity Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? 1.0 : 0.45;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
