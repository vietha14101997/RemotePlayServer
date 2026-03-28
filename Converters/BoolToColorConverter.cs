#nullable enable
using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace RemotePlayServer.Converters;

public class BoolToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b && b)
            return new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)); // green
        return new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0xA0)); // gray
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
