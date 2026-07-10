#nullable enable
using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using RemotePlayServer.Core;

namespace RemotePlayServer.Converters;

/// <summary>
/// Maps a LogLevel to the text brush used for that line in the log viewer.
/// </summary>
public class LogLevelToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush DebugGray = new(Color.FromRgb(0x6B, 0x6B, 0x7B));
    private static readonly SolidColorBrush InfoDim = new(Color.FromRgb(0xA0, 0xA0, 0xB0));
    private static readonly SolidColorBrush WarnYellow = new(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly SolidColorBrush ErrorRed = new(Color.FromRgb(0xEF, 0x44, 0x44));

    static LogLevelToBrushConverter()
    {
        DebugGray.Freeze(); InfoDim.Freeze(); WarnYellow.Freeze(); ErrorRed.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not LogLevel level) return InfoDim;
        return level switch
        {
            LogLevel.Debug => DebugGray,
            LogLevel.Warning => WarnYellow,
            LogLevel.Error => ErrorRed,
            _ => InfoDim
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
