#nullable enable
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace RemotePlayServer.Converters;

public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        bool hasValue = value != null && (value is not string s || !string.IsNullOrEmpty(s));
        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
