using System.Globalization;
using Avalonia.Data.Converters;

namespace Spindle.ViewModels;

/// <summary>Renders a filled (★) or empty (☆) star for star slot N (ConverterParameter) given the bound rating.</summary>
public class StarConverter : IValueConverter
{
    public static readonly StarConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var rating = value is int v ? v : 0;
        var slot = parameter is string s && int.TryParse(s, out var p) ? p : 0;
        return rating >= slot ? "★" : "☆";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
