#nullable enable
using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using RemotePlayServer.Application.Protocol;

namespace RemotePlayServer.Converters;

public class PhaseToColorConverter : IValueConverter
{
    private static readonly SolidColorBrush Green = new(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly SolidColorBrush Yellow = new(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly SolidColorBrush Red = new(Color.FromRgb(0xEF, 0x44, 0x44));
    private static readonly SolidColorBrush Blue = new(Color.FromRgb(0x60, 0xA5, 0xFA));
    private static readonly SolidColorBrush Gray = new(Color.FromRgb(0x90, 0x90, 0xA0));

    static PhaseToColorConverter()
    {
        Green.Freeze(); Yellow.Freeze(); Red.Freeze(); Blue.Freeze(); Gray.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not ConnectionPhase phase) return Gray;
        return phase switch
        {
            ConnectionPhase.Phase3_Streaming => Green,
            ConnectionPhase.Connected or ConnectionPhase.Phase1_HardwareDetect
                or ConnectionPhase.Phase1_SpeedTest or ConnectionPhase.Phase1_WaitingProceed => Blue,
            ConnectionPhase.Phase2_ApplyConfig or ConnectionPhase.Phase2_IceExchange
                or ConnectionPhase.Phase3_WaitingStart => Yellow,
            ConnectionPhase.Disconnecting => Gray,
            ConnectionPhase.Error => Red,
            _ => Gray
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
