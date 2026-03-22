// NalUtils.h - Shared NAL unit utilities for H.264/H.265 keyframe detection
// Used by AmfWrapper, NvencWrapper, QsvWrapper

#pragma once
#include <stdint.h>
#include <stddef.h>

// Detect keyframe for H.264: SPS (NAL type 7) or IDR (NAL type 5)
static inline int DetectKeyframeH264(const uint8_t* data, size_t size) {
    for (size_t i = 0; i + 4 < size; i++) {
        if (data[i] == 0 && data[i+1] == 0 && data[i+2] == 0 && data[i+3] == 1) {
            int nalType = data[i+4] & 0x1F;
            if (nalType == 7 || nalType == 5) {
                return 1;
            }
        }
    }
    return 0;
}

// Detect keyframe for H.265: VPS (32), SPS (33), IDR_W_RADL (19), IDR_N_LP (20), CRA (21)
static inline int DetectKeyframeHEVC(const uint8_t* data, size_t size) {
    for (size_t i = 0; i + 5 < size; i++) {
        if (data[i] == 0 && data[i+1] == 0 && data[i+2] == 0 && data[i+3] == 1) {
            int nalType = (data[i+4] >> 1) & 0x3F;
            // VPS=32, SPS=33, IDR_W_RADL=19, IDR_N_LP=20, CRA=21
            if (nalType == 32 || nalType == 33 || nalType == 19 || nalType == 20 || nalType == 21) {
                return 1;
            }
        }
    }
    return 0;
}
