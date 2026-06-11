using System.Globalization;
using Avalonia.Data.Converters;

namespace Spindle.ViewModels;

/// <summary>true when the bound int equals the ConverterParameter (used to mark the active sidebar nav item).</summary>
public class IntEquals : IValueConverter
{
    public static readonly IntEquals Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int v && parameter is string s && int.TryParse(s, out var p))
            return v == p;
        return false;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
