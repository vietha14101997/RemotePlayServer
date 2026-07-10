#nullable enable
using System;
using System.Globalization;
using System.Windows.Data;

namespace RemotePlayServer.Converters;

/// <summary>
/// True when the bound int equals the ConverterParameter.
/// ConvertBack (checked = true) returns the parameter, so a group of
/// RadioButtons can two-way bind a single int "selected index" property.
/// </summary>
public class IntEqualsToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int intValue && int.TryParse(parameter?.ToString(), out var target) && intValue == target;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is true && int.TryParse(parameter?.ToString(), out var target))
            return target;
        return System.Windows.Data.Binding.DoNothing;
    }
}
