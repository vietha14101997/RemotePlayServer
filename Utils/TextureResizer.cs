#nullable enable
using System;
using System.Collections.Generic;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace RemotePlayServer.Utils
{
    /// <summary>
    /// GPU-accelerated texture resizer using Compute Shader.
    /// Resizes textures to max resolution (1440x810) while maintaining aspect ratio.
    /// Uses bilinear filtering for high-quality scaling.
    /// Supports multiple D3D11 devices (one scaler per device).
    /// </summary>
    public class TextureResizer : IDisposable
    {
        private readonly int _monitorCount;

        // Max resolution after resize
        public const int MaxWidth = 1440;
        public const int MaxHeight = 810;

        // Per-device GPU scalers (key = device pointer)
        private readonly Dictionary<IntPtr, GpuTextureScaler> _scalers = new();
        private readonly HashSet<IntPtr> _failedDevices = new();
        private readonly object _lock = new();

        private bool _disposed;

        public TextureResizer(int monitorCount)
        {
            _monitorCount = monitorCount;
            Console.WriteLine($"[TextureResizer] Initialized for {monitorCount} monitors (per-device GPU scaling)");
        }

        /// <summary>
        /// Check if resize is needed for the given dimensions.
        /// </summary>
        public static bool NeedsResize(int width, int height)
        {
            return width > MaxWidth || height > MaxHeight;
        }

        /// <summary>
        /// Calculate target dimensions that fit within MaxWidth x MaxHeight while maintaining aspect ratio.
        /// </summary>
        public static (int targetWidth, int targetHeight) CalculateTargetSize(int width, int height)
        {
            if (width <= MaxWidth && height <= MaxHeight)
                return (width, height); // No resize needed

            double aspectRatio = (double)width / height;

            int targetWidth, targetHeight;

            if (aspectRatio > (double)MaxWidth / MaxHeight)
            {
                // Width-limited: fit to MaxWidth
                targetWidth = MaxWidth;
                targetHeight = (int)(MaxWidth / aspectRatio);
            }
            else
            {
                // Height-limited: fit to MaxHeight
                targetHeight = MaxHeight;
                targetWidth = (int)(MaxHeight * aspectRatio);
            }

            // Ensure even dimensions (required by many encoders)
            targetWidth = (targetWidth + 1) & ~1;
            targetHeight = (targetHeight + 1) & ~1;

            return (targetWidth, targetHeight);
        }

        /// <summary>
        /// Get or create a GPU scaler for the specified device.
        /// </summary>
        private GpuTextureScaler? GetOrCreateScaler(ID3D11Device device, int monitorIndex)
        {
            var devicePtr = device.NativePointer;
            
            lock (_lock)
            {
                // Check if already failed for this device
                if (_failedDevices.Contains(devicePtr))
                    return null;
                
                // Check if scaler already exists
                if (_scalers.TryGetValue(devicePtr, out var existingScaler))
                    return existingScaler;
                
                // Create new scaler for this device
                try
                {
                    var scaler = new GpuTextureScaler(device, monitorIndex);
                    _scalers[devicePtr] = scaler;
                    Console.WriteLine($"[TextureResizer] Created GPU scaler for monitor {monitorIndex} (device={devicePtr:X})");
                    return scaler;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[TextureResizer] Failed to create scaler for monitor {monitorIndex}: {ex.Message}");
                    _failedDevices.Add(devicePtr);
                    return null;
                }
            }
        }

        /// <summary>
        /// Resize a BGRA texture to fit within MaxWidth x MaxHeight.
        /// Returns (resizedTexture, targetWidth, targetHeight).
        /// If no resize needed, returns (originalTexture, originalWidth, originalHeight).
        /// Uses GPU Compute Shader for high-quality bilinear scaling.
        /// </summary>
        public (ID3D11Texture2D texture, int width, int height) ResizeBgraTexture(
            ID3D11Device device, ID3D11Texture2D sourceTexture, int sourceWidth, int sourceHeight, int monitorIndex)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TextureResizer));

            // Check if resize is needed
            if (!NeedsResize(sourceWidth, sourceHeight))
            {
                return (sourceTexture, sourceWidth, sourceHeight);
            }

            // Calculate target size
            (int targetWidth, int targetHeight) = CalculateTargetSize(sourceWidth, sourceHeight);

            // Ensure target size is different from source
            if (targetWidth == sourceWidth && targetHeight == sourceHeight)
            {
                return (sourceTexture, sourceWidth, sourceHeight);
            }

            // Get or create scaler for this device
            var scaler = GetOrCreateScaler(device, monitorIndex);
            if (scaler == null)
            {
                return (sourceTexture, sourceWidth, sourceHeight);
            }

            // Perform GPU scaling
            var scaled = scaler.Scale(sourceTexture, sourceWidth, sourceHeight, targetWidth, targetHeight, monitorIndex);
            
            if (scaled != null)
            {
                return (scaled, targetWidth, targetHeight);
            }

            // Fallback to original if scaling failed
            return (sourceTexture, sourceWidth, sourceHeight);
        }

        /// <summary>
        /// Resize an NV12 texture to fit within MaxWidth x MaxHeight.
        /// Note: NV12 requires special handling - Y and UV planes must be scaled together.
        /// For simplicity, we recommend converting to BGRA first, then resize.
        /// </summary>
        public (ID3D11Texture2D texture, int width, int height) ResizeNv12Texture(
            ID3D11Texture2D sourceTexture, int sourceWidth, int sourceHeight, int monitorIndex)
        {
            // NV12 resize is complex - Y and UV planes have different resolutions
            // For now, we don't support NV12 resize directly
            // Color converter should convert to BGRA first, then resize
            Console.WriteLine($"[TextureResizer] NV12 resize requested but not supported. Use BGRA path instead.");
            return (sourceTexture, sourceWidth, sourceHeight);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            
            lock (_lock)
            {
                foreach (var scaler in _scalers.Values)
                {
                    try { scaler.Dispose(); } catch { }
                }
                _scalers.Clear();
                _failedDevices.Clear();
            }
        }
    }
}
