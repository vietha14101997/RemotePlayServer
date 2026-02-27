#nullable enable
using System;
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
}
