using System;
using System.Collections.Generic;
using System.Linq;

namespace RemotePlayServer.Core
{
    public enum QualityPreset
    {
        Performance,
        Balanced,
        Quality
    }

    /// <summary>
    /// Calculates encoder-friendly resolutions for hardware encoders (NVENC, AMF, QSV).
    /// Standard heights (1080, 720, 900, etc.) are used directly.
    /// Non-standard screens get alignment search (128 > 64 > 32 > 16).
    /// </summary>
    public static class EncoderResolutionCalculator
    {
        private static readonly int[] Alignments = { 128, 64, 32, 16 };
        private const int MinWidth = 640;
        private const float MaxAspectError = 0.02f;
        private const int MaxSearchSteps = 2;
        private const int DefaultMaxQualityHeight = 1440;

        private static readonly HashSet<int> EncoderFriendlyHeights = new()
            { 2160, 1440, 1200, 1080, 900, 720, 540, 480, 360 };

        /// <summary>
        /// Calculate encoder-friendly resolution for a given screen size and quality preset.
        /// </summary>
        /// <param name="screenWidth">Client screen width (landscape: larger dimension)</param>
        /// <param name="screenHeight">Client screen height (landscape: smaller dimension)</param>
        /// <param name="preset">Quality preset</param>
        /// <returns>(width, height) encoder-friendly resolution</returns>
        public static (int width, int height) Calculate(int screenWidth, int screenHeight, QualityPreset preset, int maxQualityHeight = DefaultMaxQualityHeight)
        {
            int w = Math.Max(screenWidth, screenHeight);
            int h = Math.Min(screenWidth, screenHeight);
            float sourceAspect = (float)w / h;

            return preset switch
            {
                QualityPreset.Quality => FindAlignedResolution(w, h,
                    h > maxQualityHeight ? (int)Math.Round(maxQualityHeight * sourceAspect) : w),

                QualityPreset.Performance =>
                    FindAlignedResolution(w, h, Math.Min((int)Math.Round(720 * sourceAspect), w)),

                QualityPreset.Balanced => CalculateBalanced(w, h, sourceAspect, maxQualityHeight),

                _ => FindAlignedResolution(w, h, w)
            };
        }

        private static (int width, int height) CalculateBalanced(int w, int h, float sourceAspect, int maxQualityHeight = DefaultMaxQualityHeight)
        {
            int qualityTargetW = h > maxQualityHeight ? (int)Math.Round(maxQualityHeight * sourceAspect) : w;
            var qualityRes = FindAlignedResolution(w, h, qualityTargetW);
            int perfTargetW = Math.Min((int)Math.Round(720 * sourceAspect), w);
            var perfRes = FindAlignedResolution(w, h, perfTargetW);

            // Pick standard height between Performance and Quality, closest to midpoint
            int midHeight = (qualityRes.height + perfRes.height) / 2;
            int? bestHeight = EncoderFriendlyHeights
                .Where(sh => sh > perfRes.height && sh < qualityRes.height)
                .OrderBy(sh => Math.Abs(sh - midHeight))
                .Cast<int?>()
                .FirstOrDefault();

            if (bestHeight.HasValue)
            {
                return ((int)Math.Round(bestHeight.Value * sourceAspect), bestHeight.Value);
            }

            // Fallback: midpoint width, aligned search
            int midWidth = (qualityRes.width + perfRes.width) / 2;
            return FindAlignedResolution(w, h, midWidth);
        }

        /// <summary>
        /// Find encoder-friendly resolution.
        /// Fast path for standard heights, alignment search for non-standard.
        /// </summary>
        public static (int width, int height) FindAlignedResolution(int screenWidth, int screenHeight, int targetWidth)
        {
            float sourceAspect = (float)screenWidth / screenHeight;

            // Fast path: standard encoder-friendly height
            int idealHeight = (int)Math.Round(targetWidth / sourceAspect);
            if (EncoderFriendlyHeights.Contains(idealHeight) && targetWidth >= MinWidth)
            {
                float aspectError = Math.Abs((float)targetWidth / idealHeight - sourceAspect) / sourceAspect;
                if (aspectError < MaxAspectError)
                {
                    return (targetWidth, idealHeight);
                }
            }

            // Alignment search
            foreach (int alignment in Alignments)
            {
                int candidateWidth = (targetWidth / alignment) * alignment;
                int steps = 0;

                while (candidateWidth >= MinWidth && steps < MaxSearchSteps)
                {
                    float idealH = candidateWidth / sourceAspect;
                    int candidateHeight = (int)((idealH + alignment / 2.0f) / alignment) * alignment;

                    if (candidateHeight > 0)
                    {
                        float aspectError = Math.Abs((float)candidateWidth / candidateHeight - sourceAspect) / sourceAspect;
                        if (aspectError < MaxAspectError)
                        {
                            return (candidateWidth, candidateHeight);
                        }
                    }

                    candidateWidth -= alignment;
                    steps++;
                }
            }

            // Fallback
            int fallbackW = Math.Max((targetWidth / 16) * 16, MinWidth);
            int fallbackH = Math.Max(((int)Math.Round(fallbackW / sourceAspect) / 16) * 16, 16);
            return (fallbackW, fallbackH);
        }
    }
}
