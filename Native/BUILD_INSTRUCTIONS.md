# Building Native Encoder Wrappers

This folder contains native C++ wrappers for hardware video encoders:

- **AmfWrapper** - AMD AMF (Advanced Media Framework) for AMD GPUs
- **NvencWrapper** - NVIDIA NVENC for NVIDIA GPUs
- **QsvWrapper** - Intel Quick Sync Video for Intel GPUs

## Prerequisites

### For All Projects
- Visual Studio 2022 with C++ Desktop Development workload
- Windows SDK 10.0 or later

### For NvencWrapper (NVIDIA)
1. Download NVIDIA Video Codec SDK from: https://developer.nvidia.com/nvidia-video-codec-sdk
2. Extract and copy `Interface/nvEncodeAPI.h` to `NvencWrapper/nvenc/` folder
   (A minimal header is already included, but the full SDK header is recommended)

### For AmfWrapper (AMD)
- AMD AMF SDK headers are already included in `AmfWrapper/amf/` folder
- AMD drivers with AMF support required at runtime

### For QsvWrapper (Intel)
- Uses Windows Media Foundation (built into Windows)
- Intel GPU with Quick Sync support required at runtime

## Building

### Option 1: Visual Studio
1. Open `NativeEncoders.sln` in Visual Studio 2022
2. Select **Release | x64** configuration
3. Build Solution (Ctrl+Shift+B)

### Option 2: Command Line (MSBuild)
```cmd
cd Native
msbuild NativeEncoders.sln /p:Configuration=Release /p:Platform=x64
```

### Option 3: Build Individual Projects
```cmd
# AMD AMF
msbuild AmfWrapper\AmfWrapper.vcxproj /p:Configuration=Release /p:Platform=x64

# NVIDIA NVENC
msbuild NvencWrapper\NvencWrapper.vcxproj /p:Configuration=Release /p:Platform=x64

# Intel QSV
msbuild QsvWrapper\QsvWrapper.vcxproj /p:Configuration=Release /p:Platform=x64
```

## Output

Built DLLs are output to:
```
bin\Release\net9.0-windows10.0.26100.0\
  ├── AmfWrapper.dll
  ├── NvencWrapper.dll
  └── QsvWrapper.dll
```

## Runtime Requirements

| Encoder | Required Runtime |
|---------|------------------|
| AMF | AMD GPU + AMD drivers with AMF support |
| NVENC | NVIDIA GPU + nvEncodeAPI64.dll (included with NVIDIA drivers) |
| QSV | Intel GPU with Quick Sync + Windows Media Foundation |

## Encoder Selection

The C# code automatically detects GPU vendor and selects the appropriate encoder:
- AMD GPU → AmfWrapper.dll
- NVIDIA GPU → NvencWrapper.dll
- Intel GPU → QsvWrapper.dll

If the primary encoder fails, it falls back to the next available option.

## Troubleshooting

### NVENC: "Failed to load nvEncodeAPI64.dll"
- Ensure NVIDIA drivers are installed (version 418.81 or later)
- Check that `nvEncodeAPI64.dll` exists in `C:\Windows\System32\`

### AMF: "AMF Factory init failed"
- Ensure AMD drivers are installed
- Check that `amfrt64.dll` exists in `C:\Windows\System32\`

### QSV: "No hardware H.264 encoder found"
- Ensure Intel GPU is present and enabled
- Update Intel graphics drivers
- Check Windows Media Foundation is working: `mfplat.dll`
