#nullable enable
namespace RemotePlayServer.Core
{
    public enum GpuTier
    {
        Low,    // 720p  — software encoder, iGPU
        Mid,    // 1080p — entry dGPU, Intel Iris/Arc
        High    // 1440p — mid+ dGPU (NVENC/AMF ≥4GB)
    }

    public static class GpuTierClassifier
    {
        /// <summary>
        /// Classify GPU tier from encoder type and dedicated VRAM.
        /// Used to determine max quality resolution for streaming presets.
        /// </summary>
        public static GpuTier Classify(string? encoderType, long vramMB)
        {
            // Software encoder → always Low
            if (string.IsNullOrEmpty(encoderType) || encoderType == "Software")
                return GpuTier.Low;

            // Intel QSV: iGPU with shared memory typically reports low dedicated VRAM
            if (encoderType.Contains("qsv", System.StringComparison.OrdinalIgnoreCase)
                || encoderType.Contains("QSV", System.StringComparison.Ordinal))
                return vramMB < 2048 ? GpuTier.Low : GpuTier.Mid;

            // NVENC / AMF (discrete GPU)
            return vramMB < 4096 ? GpuTier.Mid : GpuTier.High;
        }

        /// <summary>
        /// Get maximum quality height for a GPU tier.
        /// </summary>
        public static int GetMaxQualityHeight(GpuTier tier) => tier switch
        {
            GpuTier.Low  => 720,
            GpuTier.Mid  => 1080,
            GpuTier.High => 1440,
            _            => 1440
        };
    }
}
