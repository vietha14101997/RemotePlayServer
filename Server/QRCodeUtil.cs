#nullable enable
using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QRCoder;

namespace RemotePlayServer.Server;

/// <summary>
/// Generates QR codes for easy client connection via phone camera scan.
/// </summary>
public static class QRCodeUtil
{
    public static void PrintQRCodeToConsole(string data)
    {
        try
        {
            using var qrGenerator = new QRCodeGenerator();
            using var qrCodeData = qrGenerator.CreateQrCode(data, QRCodeGenerator.ECCLevel.L);
            using var qrCode = new AsciiQRCode(qrCodeData);
            var qrString = qrCode.GetGraphicSmall();
            Console.WriteLine(qrString);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[QRCode] Failed to generate: {ex.Message}");
            Console.WriteLine($"[QRCode] Raw data: {data}");
        }
    }

    /// <summary>
    /// Generate a WPF-compatible ImageSource from QR data.
    /// Uses PngByteQRCode (no System.Drawing dependency).
    /// </summary>
    public static ImageSource? GenerateImageSource(string data, int pixelsPerModule = 10)
    {
        try
        {
            using var qrGenerator = new QRCodeGenerator();
            using var qrCodeData = qrGenerator.CreateQrCode(data, QRCodeGenerator.ECCLevel.L);
            var qrCode = new PngByteQRCode(qrCodeData);
            var pngBytes = qrCode.GetGraphic(pixelsPerModule);

            var image = new BitmapImage();
            using (var ms = new MemoryStream(pngBytes))
            {
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = ms;
                image.EndInit();
            }
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            Core.Logger.Error($"[QRCode] Failed to generate image: {ex.Message}");
            return null;
        }
    }
}
