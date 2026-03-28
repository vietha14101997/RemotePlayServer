#nullable enable
using System;
using System.Globalization;
using System.Windows.Data;

namespace RemotePlayServer.Converters;

public class BoolToLabelConverter : IValueConverter
{
    public string TrueLabel { get; set; } = "True";
    public string FalseLabel { get; set; } = "False";

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? TrueLabel : FalseLabel;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
