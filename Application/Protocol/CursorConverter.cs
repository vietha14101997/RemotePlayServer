#nullable enable
using System;
using RemotePlayServer.Core;

namespace RemotePlayServer.Application.Protocol
{
    /// <summary>
    /// Static helper for converting DXGI cursor shapes to RGBA32 format.
    /// </summary>
    internal static class CursorConverter
    {
        internal const int DXGI_POINTER_SHAPE_TYPE_MONOCHROME = 1;
        internal const int DXGI_POINTER_SHAPE_TYPE_COLOR = 2;
        internal const int DXGI_POINTER_SHAPE_TYPE_MASKED_COLOR = 4;

        /// <summary>
        /// Convert DXGI cursor buffer to RGBA32 format for Unity.
        /// Handles Monochrome, Color, and MaskedColor cursor types.
        /// </summary>
        internal static (byte[] rgbaData, int width, int height, int hotspotX, int hotspotY)? ConvertDxgiCursorToRgba(
            byte[] buffer, Vortice.DXGI.OutduplPointerShapeInfo shapeInfo)
        {
            int width = (int)shapeInfo.Width;
            int height = (int)shapeInfo.Height;
            int pitch = (int)shapeInfo.Pitch;
            int hotspotX = shapeInfo.HotSpot.X;
            int hotspotY = shapeInfo.HotSpot.Y;
            uint type = shapeInfo.Type;

            // Debug logging for cursor dimensions
            string typeName = type switch
            {
                DXGI_POINTER_SHAPE_TYPE_MONOCHROME => "MONOCHROME",
                DXGI_POINTER_SHAPE_TYPE_COLOR => "COLOR",
                DXGI_POINTER_SHAPE_TYPE_MASKED_COLOR => "MASKED_COLOR",
                _ => $"UNKNOWN({type})"
            };

            try
            {
                byte[] rgbaData;

                Logger.Info($"[Cursor] Converting shape: type={typeName}, size={width}x{height}, pitch={pitch}, bufLen={buffer.Length}");

                switch (type)
                {
                    case DXGI_POINTER_SHAPE_TYPE_MONOCHROME:
                        rgbaData = ConvertMonochromeCursorToRgba(buffer, width, height, pitch);
                        // Monochrome cursor height is doubled (AND mask + XOR mask)
                        height /= 2;
                        break;

                    case DXGI_POINTER_SHAPE_TYPE_COLOR:
                    case DXGI_POINTER_SHAPE_TYPE_MASKED_COLOR:
                        rgbaData = ConvertBgraCursorToRgba(buffer, width, height, pitch, type == DXGI_POINTER_SHAPE_TYPE_MASKED_COLOR);
                        break;

                    default:
                        Logger.Info($"[Cursor] Unknown cursor type: {type}");
                        return null;
                }

                // Log pixel statistics for debugging cursor rendering issues
                int opaqueCount = 0, transparentCount = 0;
                int totalPixels = width * height;
                for (int i = 0; i < rgbaData.Length; i += 4)
                {
                    if (rgbaData[i + 3] == 0) transparentCount++;
                    else opaqueCount++;
                }
                Logger.Info($"[Cursor] Converted {typeName} {width}x{height}: opaque={opaqueCount}, transparent={transparentCount}/{totalPixels}");

                return (rgbaData, width, height, hotspotX, hotspotY);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Convert Monochrome cursor (1bpp AND/XOR masks) to RGBA32.
        /// Inverse pixels (AND=1, XOR=1) are rendered as black with white outline
        /// for visibility on any background, approximating Windows XOR behavior.
        /// </summary>
        internal static byte[] ConvertMonochromeCursorToRgba(byte[] buffer, int width, int height, int pitch)
        {
            // Monochrome cursor has doubled height: AND mask on top, XOR mask on bottom
            int actualHeight = height / 2;
            byte[] rgbaData = new byte[width * actualHeight * 4];

            // Calculate bytes per row (1 bit per pixel, aligned to pitch)
            int bytesPerRow = pitch;

            // Validate buffer size: need enough for both AND and XOR masks
            int expectedBufferSize = bytesPerRow * height;
            if (buffer.Length < expectedBufferSize)
            {
                return rgbaData;
            }

            // Track inverse pixels for outline pass
            bool hasInversePixels = false;
            bool[] inverseMap = new bool[width * actualHeight];
            int blackCount = 0, whiteCount = 0, inverseCount = 0, transpCount = 0;

            for (int y = 0; y < actualHeight; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int byteIndex = x / 8;
                    int bitIndex = 7 - (x % 8);

                    // AND mask (top half)
                    int andRowOffset = y * bytesPerRow;
                    int andOffset = andRowOffset + byteIndex;
                    if (andOffset >= buffer.Length) continue;
                    byte andByte = buffer[andOffset];
                    int andBit = (andByte >> bitIndex) & 1;

                    // XOR mask (bottom half)
                    int xorRowOffset = (actualHeight + y) * bytesPerRow;
                    int xorOffset = xorRowOffset + byteIndex;
                    if (xorOffset >= buffer.Length) continue;
                    byte xorByte = buffer[xorOffset];
                    int xorBit = (xorByte >> bitIndex) & 1;

                    // Calculate RGBA based on AND/XOR combination
                    // AND=0, XOR=0 -> Black, opaque
                    // AND=0, XOR=1 -> White, opaque
                    // AND=1, XOR=0 -> Transparent
                    // AND=1, XOR=1 -> Inverse (black + white outline for contrast)
                    byte r, g, b, a;

                    if (andBit == 0)
                    {
                        // Opaque pixel
                        a = 255;
                        if (xorBit == 0)
                        {
                            r = g = b = 0; // Black
                            blackCount++;
                        }
                        else
                        {
                            r = g = b = 255; // White
                            whiteCount++;
                        }
                    }
                    else
                    {
                        if (xorBit == 0)
                        {
                            // Transparent
                            r = g = b = a = 0;
                            transpCount++;
                        }
                        else
                        {
                            // Inverse pixel - render as black, will add white outline in second pass
                            r = g = b = 0;
                            a = 255;
                            inverseMap[y * width + x] = true;
                            hasInversePixels = true;
                            inverseCount++;
                        }
                    }

                    // Output RGBA - NO FLIP, keep original Windows row order
                    int dstOffset = (y * width + x) * 4;
                    rgbaData[dstOffset] = r;
                    rgbaData[dstOffset + 1] = g;
                    rgbaData[dstOffset + 2] = b;
                    rgbaData[dstOffset + 3] = a;
                }
            }

            Logger.Info($"[Cursor] Monochrome {width}x{actualHeight}: black={blackCount}, white={whiteCount}, inverse={inverseCount}, transparent={transpCount}");

            // Second pass: add white outline around inverse pixels for visibility on any background.
            // On white bg: white outline blends in, black cursor visible (like native Windows).
            // On dark bg: white outline provides contrast, cursor remains visible.
            if (hasInversePixels)
            {
                byte[] outlined = new byte[rgbaData.Length];
                Array.Copy(rgbaData, outlined, rgbaData.Length);

                for (int y = 0; y < actualHeight; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int idx = (y * width + x) * 4;
                        // Only convert transparent pixels to outline
                        if (rgbaData[idx + 3] != 0) continue;

                        // Check 8-connected neighbors for any inverse pixel
                        bool adjacentToInverse = false;
                        for (int dy = -1; dy <= 1 && !adjacentToInverse; dy++)
                        {
                            for (int dx = -1; dx <= 1 && !adjacentToInverse; dx++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                int nx = x + dx, ny = y + dy;
                                if (nx < 0 || nx >= width || ny < 0 || ny >= actualHeight) continue;
                                if (inverseMap[ny * width + nx])
                                    adjacentToInverse = true;
                            }
                        }

                        if (adjacentToInverse)
                        {
                            outlined[idx] = 255;     // R - white
                            outlined[idx + 1] = 255; // G
                            outlined[idx + 2] = 255; // B
                            outlined[idx + 3] = 255; // A - fully opaque
                        }
                    }
                }

                return outlined;
            }

            return rgbaData;
        }

        /// <summary>
        /// Convert BGRA cursor (32bpp) to RGBA32.
        /// Keeps original Windows row order (row 0 = top).
        /// Unity client will flip when loading into Texture2D if needed.
        /// </summary>
        internal static byte[] ConvertBgraCursorToRgba(byte[] buffer, int width, int height, int pitch, bool isMaskedColor)
        {
            byte[] rgbaData = new byte[width * height * 4];

            // Validate and auto-correct pitch if needed
            int expectedPitchMin = width * 4; // Minimum pitch for BGRA (32bpp)
            int expectedBufferSize = pitch * height;

            // Auto-detect pitch from buffer size if reported pitch seems wrong
            if (buffer.Length != expectedBufferSize && height > 0)
            {
                int detectedPitch = buffer.Length / height;
                if (detectedPitch >= expectedPitchMin && (buffer.Length % height) == 0)
                {
                    pitch = detectedPitch;
                    expectedBufferSize = pitch * height;
                }
            }

            if (buffer.Length < expectedBufferSize)
            {
                // Try with minimum pitch as fallback
                if (buffer.Length >= expectedPitchMin * height)
                {
                    pitch = expectedPitchMin;
                }
                else
                {
                    // Fill with transparent pixels
                    return rgbaData;
                }
            }

            // Track XOR pixels for outline pass (masked color only)
            bool hasXorPixels = false;
            bool[]? xorMap = isMaskedColor ? new bool[width * height] : null;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int srcOffset = y * pitch + x * 4;

                    // Bounds check
                    if (srcOffset + 3 >= buffer.Length)
                    {
                        continue;
                    }

                    // BGRA format
                    byte b = buffer[srcOffset];
                    byte g = buffer[srcOffset + 1];
                    byte r = buffer[srcOffset + 2];
                    byte a = buffer[srcOffset + 3];

                    // For masked color cursors, alpha has special meaning:
                    // 0x00 = use cursor color directly (OPAQUE, not transparent!)
                    // 0xFF = XOR with background (screen inversion not possible in overlay)
                    if (isMaskedColor)
                    {
                        if (a == 0x00)
                        {
                            // Opaque pixel - use cursor color directly
                            a = 255;
                        }
                        else if (a == 0xFF)
                        {
                            // XOR pixel: screen XOR cursor_color
                            // Black (R=G=B=0) XOR'd means no change → transparent
                            // Non-black: can't do real XOR in overlay, so render as
                            // inverted color (best approximation) + white outline
                            if (r == 0 && g == 0 && b == 0)
                            {
                                a = 0; // Transparent
                            }
                            else
                            {
                                // Invert the XOR color: white→black, etc.
                                // This approximates XOR on a light background (most common)
                                r = (byte)(255 - r);
                                g = (byte)(255 - g);
                                b = (byte)(255 - b);
                                a = 255;
                                xorMap![y * width + x] = true;
                                hasXorPixels = true;
                            }
                        }
                    }

                    // Output RGBA - NO FLIP, keep original Windows row order (row 0 = top)
                    int dstOffset = (y * width + x) * 4;
                    rgbaData[dstOffset] = r;
                    rgbaData[dstOffset + 1] = g;
                    rgbaData[dstOffset + 2] = b;
                    rgbaData[dstOffset + 3] = a;
                }
            }

            // Second pass: add contrasting outline around XOR pixels for masked color cursors
            if (hasXorPixels)
            {
                byte[] outlined = new byte[rgbaData.Length];
                Array.Copy(rgbaData, outlined, rgbaData.Length);

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int idx = (y * width + x) * 4;
                        if (rgbaData[idx + 3] != 0) continue;

                        bool adjacentToXor = false;
                        for (int dy = -1; dy <= 1 && !adjacentToXor; dy++)
                        {
                            for (int dx = -1; dx <= 1 && !adjacentToXor; dx++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                int nx = x + dx, ny = y + dy;
                                if (nx < 0 || nx >= width || ny < 0 || ny >= height) continue;
                                if (xorMap![ny * width + nx])
                                    adjacentToXor = true;
                            }
                        }

                        if (adjacentToXor)
                        {
                            outlined[idx] = 255;     // White outline
                            outlined[idx + 1] = 255;
                            outlined[idx + 2] = 255;
                            outlined[idx + 3] = 255;
                        }
                    }
                }

                return outlined;
            }

            return rgbaData;
        }
    }
}
