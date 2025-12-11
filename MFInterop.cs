#nullable enable
using System;
using System.Runtime.InteropServices;

/// <summary>
/// Media Foundation P/Invoke declarations and COM interfaces
/// </summary>
public static class MFInterop
{
    #region Constants

    public const uint MF_VERSION = 0x00020070;
    public const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    public const int MF_E_NOTACCEPTING = unchecked((int)0xC00D36B5);

    // MFT Enum Flags
    public const uint MFT_ENUM_FLAG_SYNCMFT = 0x00000001;
    public const uint MFT_ENUM_FLAG_ASYNCMFT = 0x00000002;
    public const uint MFT_ENUM_FLAG_HARDWARE = 0x00000004;
    public const uint MFT_ENUM_FLAG_FIELDOFUSE = 0x00000008;
    public const uint MFT_ENUM_FLAG_LOCALMFT = 0x00000010;
    public const uint MFT_ENUM_FLAG_TRANSCODE_ONLY = 0x00000020;
    public const uint MFT_ENUM_FLAG_SORTANDFILTER = 0x00000040;
    public const uint MFT_ENUM_FLAG_ALL = 0x0000003F;

    // MFT Message Type
    public const uint MFT_MESSAGE_COMMAND_FLUSH = 0;
    public const uint MFT_MESSAGE_COMMAND_DRAIN = 1;
    public const uint MFT_MESSAGE_SET_D3D_MANAGER = 2;
    public const uint MFT_MESSAGE_DROP_SAMPLES = 3;
    public const uint MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000;
    public const uint MFT_MESSAGE_NOTIFY_END_STREAMING = 0x10000001;
    public const uint MFT_MESSAGE_NOTIFY_END_OF_STREAM = 0x10000002;
    public const uint MFT_MESSAGE_NOTIFY_START_OF_STREAM = 0x10000003;

    // MFT Output Status Flags
    public const uint MFT_OUTPUT_STATUS_SAMPLE_READY = 0x00000001;

    // MFT Output Data Buffer Flags  
    public const uint MFT_OUTPUT_DATA_BUFFER_INCOMPLETE = 0x01000000;
    public const uint MFT_OUTPUT_DATA_BUFFER_FORMAT_CHANGE = 0x00000100;
    public const uint MFT_OUTPUT_DATA_BUFFER_STREAM_END = 0x00000200;
    public const uint MFT_OUTPUT_DATA_BUFFER_NO_SAMPLE = 0x00000300;

    #endregion

    #region GUIDs

    // Media Type GUIDs
    public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormat_NV12 = new("3231564E-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormat_ARGB32 = new("00000015-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00AA00389B71");

    // MFT Category
    public static readonly Guid MFT_CATEGORY_VIDEO_ENCODER = new("f79eac7d-e545-4387-bdee-d647d7bde42a");

    // Attribute GUIDs
    public static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static readonly Guid MF_MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    public static readonly Guid MF_MT_MPEG2_LEVEL = new("96f66574-11c5-4015-8666-bff516436da7");

    // H.264 Encoder Attributes
    public static readonly Guid CODECAPI_AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid CODECAPI_AVEncCommonQuality = new("fcbf57a3-7ea5-4b0c-9644-69b40c39c391");
    public static readonly Guid CODECAPI_AVEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static readonly Guid CODECAPI_AVEncMPVGOPSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static readonly Guid CODECAPI_AVLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid CODECAPI_AVEncVideoForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");
    public static readonly Guid CODECAPI_AVEncH264CABACEnable = new("ee6cad62-d305-4248-a50e-e1b255f7caf8");

    // MFT Attributes
    public static readonly Guid MFT_FRIENDLY_NAME_Attribute = new("314ffbae-5b41-4c95-9c19-4e7d586face3");
    public static readonly Guid MF_TRANSFORM_ASYNC_UNLOCK = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");

    // Sample Attributes
    public static readonly Guid MFSampleExtension_CleanPoint = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");

    // Interface GUIDs
    public static readonly Guid IID_IMFTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");
    public static readonly Guid IID_IMFMediaType = new("44ae0fa8-ea31-4109-8d2e-4cae4997c555");
    public static readonly Guid IID_IMFAttributes = new("2cd2d921-c447-44a7-a13c-4adabfc247e3");
    public static readonly Guid IID_IMFSample = new("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4");
    public static readonly Guid IID_IMFMediaBuffer = new("045fa593-8799-42b8-bc8d-8968c6453507");
    public static readonly Guid IID_IMFDXGIBuffer = new("e7174cfa-1c9e-48b1-8866-626226bfc258");
    public static readonly Guid IID_IMFActivate = new("7fee9e9a-4a89-47a6-899c-b6a53a70fb67");
    public static readonly Guid IID_ICodecAPI = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");

    #endregion

    #region Structures

    [StructLayout(LayoutKind.Sequential)]
    public struct MFT_REGISTER_TYPE_INFO
    {
        public Guid guidMajorType;
        public Guid guidSubtype;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MFT_OUTPUT_STREAM_INFO
    {
        public uint dwFlags;
        public uint cbSize;
        public uint cbAlignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MFT_INPUT_STREAM_INFO
    {
        public long hnsMaxLatency;
        public uint dwFlags;
        public uint cbSize;
        public uint cbMaxLookahead;
        public uint cbAlignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MFT_OUTPUT_DATA_BUFFER
    {
        public uint dwStreamID;
        public IntPtr pSample;      // IMFSample*
        public uint dwStatus;
        public IntPtr pEvents;      // IMFCollection*
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROPVARIANT
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr data1;
        public IntPtr data2;
    }

    #endregion

    #region MFPlat Functions

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFStartup(uint version, uint dwFlags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMediaType(out IntPtr ppMFType);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateSample(out IntPtr ppIMFSample);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMemoryBuffer(int cbMaxLength, out IntPtr ppBuffer);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateDXGIDeviceManager(out uint pResetToken, out IntPtr ppDeviceManager);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateDXGISurfaceBuffer(
        ref Guid riid,
        IntPtr punkSurface,
        uint uSubresourceIndex,
        [MarshalAs(UnmanagedType.Bool)] bool fBottomUpWhenLinear,
        out IntPtr ppBuffer);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreate2DMediaBuffer(
        uint dwWidth,
        uint dwHeight,
        uint dwFourCC,
        [MarshalAs(UnmanagedType.Bool)] bool fBottomUp,
        out IntPtr ppBuffer);

    #endregion

    #region MF Functions

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFTEnumEx(
        ref Guid guidCategory,
        uint flags,
        IntPtr pInputType,      // MFT_REGISTER_TYPE_INFO*
        IntPtr pOutputType,     // MFT_REGISTER_TYPE_INFO*
        out IntPtr pppMFTActivate,  // IMFActivate***
        out uint pnumMFTActivate);

    #endregion

    #region Ole32 Functions

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int CoTaskMemFree(IntPtr pv);

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int PropVariantClear(ref PROPVARIANT pvar);

    #endregion

    #region Helper Methods

    public static long PackSize(int width, int height)
    {
        return ((long)width << 32) | (uint)height;
    }

    public static void UnpackSize(long packed, out int width, out int height)
    {
        width = (int)(packed >> 32);
        height = (int)(packed & 0xFFFFFFFF);
    }

    public static uint MakeFourCC(char a, char b, char c, char d)
    {
        return (uint)a | ((uint)b << 8) | ((uint)c << 16) | ((uint)d << 24);
    }

    #endregion
}

#region COM Interfaces

/// <summary>
/// IMFAttributes interface
/// </summary>
[ComImport]
[Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFAttributes
{
    [PreserveSig]
    int GetItem(ref Guid guidKey, IntPtr pValue);

    [PreserveSig]
    int GetItemType(ref Guid guidKey, out uint pType);

    [PreserveSig]
    int CompareItem(ref Guid guidKey, IntPtr Value, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);

    [PreserveSig]
    int Compare(IMFAttributes pTheirs, uint MatchType, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);

    [PreserveSig]
    int GetUINT32(ref Guid guidKey, out uint punValue);

    [PreserveSig]
    int GetUINT64(ref Guid guidKey, out ulong punValue);

    [PreserveSig]
    int GetDouble(ref Guid guidKey, out double pfValue);

    [PreserveSig]
    int GetGUID(ref Guid guidKey, out Guid pguidValue);

    [PreserveSig]
    int GetStringLength(ref Guid guidKey, out uint pcchLength);

    [PreserveSig]
    int GetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pwszValue, uint cchBufSize, out uint pcchLength);

    [PreserveSig]
    int GetAllocatedString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string ppwszValue, out uint pcchLength);

    [PreserveSig]
    int GetBlobSize(ref Guid guidKey, out uint pcbBlobSize);

    [PreserveSig]
    int GetBlob(ref Guid guidKey, [Out, MarshalAs(UnmanagedType.LPArray)] byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);

    [PreserveSig]
    int GetAllocatedBlob(ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);

    [PreserveSig]
    int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);

    [PreserveSig]
    int SetItem(ref Guid guidKey, IntPtr Value);

    [PreserveSig]
    int DeleteItem(ref Guid guidKey);

    [PreserveSig]
    int DeleteAllItems();

    [PreserveSig]
    int SetUINT32(ref Guid guidKey, uint unValue);

    [PreserveSig]
    int SetUINT64(ref Guid guidKey, ulong unValue);

    [PreserveSig]
    int SetDouble(ref Guid guidKey, double fValue);

    [PreserveSig]
    int SetGUID(ref Guid guidKey, ref Guid guidValue);

    [PreserveSig]
    int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);

    [PreserveSig]
    int SetBlob(ref Guid guidKey, [MarshalAs(UnmanagedType.LPArray)] byte[] pBuf, uint cbBufSize);

    [PreserveSig]
    int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);

    [PreserveSig]
    int LockStore();

    [PreserveSig]
    int UnlockStore();

    [PreserveSig]
    int GetCount(out uint pcItems);

    [PreserveSig]
    int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);

    [PreserveSig]
    int CopyAllItems(IMFAttributes pDest);
}

/// <summary>
/// IMFMediaType interface (extends IMFAttributes)
/// </summary>
[ComImport]
[Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFMediaType : IMFAttributes
{
    // IMFAttributes methods (inherited)
    #region IMFAttributes
    [PreserveSig]
    new int GetItem(ref Guid guidKey, IntPtr pValue);
    [PreserveSig]
    new int GetItemType(ref Guid guidKey, out uint pType);
    [PreserveSig]
    new int CompareItem(ref Guid guidKey, IntPtr Value, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
    [PreserveSig]
    new int Compare(IMFAttributes pTheirs, uint MatchType, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
    [PreserveSig]
    new int GetUINT32(ref Guid guidKey, out uint punValue);
    [PreserveSig]
    new int GetUINT64(ref Guid guidKey, out ulong punValue);
    [PreserveSig]
    new int GetDouble(ref Guid guidKey, out double pfValue);
    [PreserveSig]
    new int GetGUID(ref Guid guidKey, out Guid pguidValue);
    [PreserveSig]
    new int GetStringLength(ref Guid guidKey, out uint pcchLength);
    [PreserveSig]
    new int GetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pwszValue, uint cchBufSize, out uint pcchLength);
    [PreserveSig]
    new int GetAllocatedString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string ppwszValue, out uint pcchLength);
    [PreserveSig]
    new int GetBlobSize(ref Guid guidKey, out uint pcbBlobSize);
    [PreserveSig]
    new int GetBlob(ref Guid guidKey, [Out, MarshalAs(UnmanagedType.LPArray)] byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);
    [PreserveSig]
    new int GetAllocatedBlob(ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
    [PreserveSig]
    new int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);
    [PreserveSig]
    new int SetItem(ref Guid guidKey, IntPtr Value);
    [PreserveSig]
    new int DeleteItem(ref Guid guidKey);
    [PreserveSig]
    new int DeleteAllItems();
    [PreserveSig]
    new int SetUINT32(ref Guid guidKey, uint unValue);
    [PreserveSig]
    new int SetUINT64(ref Guid guidKey, ulong unValue);
    [PreserveSig]
    new int SetDouble(ref Guid guidKey, double fValue);
    [PreserveSig]
    new int SetGUID(ref Guid guidKey, ref Guid guidValue);
    [PreserveSig]
    new int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    [PreserveSig]
    new int SetBlob(ref Guid guidKey, [MarshalAs(UnmanagedType.LPArray)] byte[] pBuf, uint cbBufSize);
    [PreserveSig]
    new int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
    [PreserveSig]
    new int LockStore();
    [PreserveSig]
    new int UnlockStore();
    [PreserveSig]
    new int GetCount(out uint pcItems);
    [PreserveSig]
    new int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
    [PreserveSig]
    new int CopyAllItems(IMFAttributes pDest);
    #endregion

    // IMFMediaType methods
    [PreserveSig]
    int GetMajorType(out Guid pguidMajorType);

    [PreserveSig]
    int IsCompressedFormat([MarshalAs(UnmanagedType.Bool)] out bool pfCompressed);

    [PreserveSig]
    int IsEqual(IMFMediaType pIMediaType, out uint pdwFlags);

    [PreserveSig]
    int GetRepresentation(Guid guidRepresentation, out IntPtr ppvRepresentation);

    [PreserveSig]
    int FreeRepresentation(Guid guidRepresentation, IntPtr pvRepresentation);
}

/// <summary>
/// IMFMediaBuffer interface
/// </summary>
[ComImport]
[Guid("045fa593-8799-42b8-bc8d-8968c6453507")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFMediaBuffer
{
    [PreserveSig]
    int Lock(out IntPtr ppbBuffer, out int pcbMaxLength, out int pcbCurrentLength);

    [PreserveSig]
    int Unlock();

    [PreserveSig]
    int GetCurrentLength(out int pcbCurrentLength);

    [PreserveSig]
    int SetCurrentLength(int cbCurrentLength);

    [PreserveSig]
    int GetMaxLength(out int pcbMaxLength);
}

/// <summary>
/// IMFSample interface
/// </summary>
[ComImport]
[Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFSample : IMFAttributes
{
    // IMFAttributes methods (inherited)
    #region IMFAttributes
    [PreserveSig]
    new int GetItem(ref Guid guidKey, IntPtr pValue);
    [PreserveSig]
    new int GetItemType(ref Guid guidKey, out uint pType);
    [PreserveSig]
    new int CompareItem(ref Guid guidKey, IntPtr Value, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
    [PreserveSig]
    new int Compare(IMFAttributes pTheirs, uint MatchType, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
    [PreserveSig]
    new int GetUINT32(ref Guid guidKey, out uint punValue);
    [PreserveSig]
    new int GetUINT64(ref Guid guidKey, out ulong punValue);
    [PreserveSig]
    new int GetDouble(ref Guid guidKey, out double pfValue);
    [PreserveSig]
    new int GetGUID(ref Guid guidKey, out Guid pguidValue);
    [PreserveSig]
    new int GetStringLength(ref Guid guidKey, out uint pcchLength);
    [PreserveSig]
    new int GetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pwszValue, uint cchBufSize, out uint pcchLength);
    [PreserveSig]
    new int GetAllocatedString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string ppwszValue, out uint pcchLength);
    [PreserveSig]
    new int GetBlobSize(ref Guid guidKey, out uint pcbBlobSize);
    [PreserveSig]
    new int GetBlob(ref Guid guidKey, [Out, MarshalAs(UnmanagedType.LPArray)] byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);
    [PreserveSig]
    new int GetAllocatedBlob(ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
    [PreserveSig]
    new int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);
    [PreserveSig]
    new int SetItem(ref Guid guidKey, IntPtr Value);
    [PreserveSig]
    new int DeleteItem(ref Guid guidKey);
    [PreserveSig]
    new int DeleteAllItems();
    [PreserveSig]
    new int SetUINT32(ref Guid guidKey, uint unValue);
    [PreserveSig]
    new int SetUINT64(ref Guid guidKey, ulong unValue);
    [PreserveSig]
    new int SetDouble(ref Guid guidKey, double fValue);
    [PreserveSig]
    new int SetGUID(ref Guid guidKey, ref Guid guidValue);
    [PreserveSig]
    new int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    [PreserveSig]
    new int SetBlob(ref Guid guidKey, [MarshalAs(UnmanagedType.LPArray)] byte[] pBuf, uint cbBufSize);
    [PreserveSig]
    new int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
    [PreserveSig]
    new int LockStore();
    [PreserveSig]
    new int UnlockStore();
    [PreserveSig]
    new int GetCount(out uint pcItems);
    [PreserveSig]
    new int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
    [PreserveSig]
    new int CopyAllItems(IMFAttributes pDest);
    #endregion

    // IMFSample methods
    [PreserveSig]
    int GetSampleFlags(out uint pdwSampleFlags);

    [PreserveSig]
    int SetSampleFlags(uint dwSampleFlags);

    [PreserveSig]
    int GetSampleTime(out long phnsSampleTime);

    [PreserveSig]
    int SetSampleTime(long hnsSampleTime);

    [PreserveSig]
    int GetSampleDuration(out long phnsSampleDuration);

    [PreserveSig]
    int SetSampleDuration(long hnsSampleDuration);

    [PreserveSig]
    int GetBufferCount(out uint pdwBufferCount);

    [PreserveSig]
    int GetBufferByIndex(uint dwIndex, out IMFMediaBuffer ppBuffer);

    [PreserveSig]
    int ConvertToContiguousBuffer(out IMFMediaBuffer ppBuffer);

    [PreserveSig]
    int AddBuffer(IMFMediaBuffer pBuffer);

    [PreserveSig]
    int RemoveBufferByIndex(uint dwIndex);

    [PreserveSig]
    int RemoveAllBuffers();

    [PreserveSig]
    int GetTotalLength(out uint pcbTotalLength);

    [PreserveSig]
    int CopyToBuffer(IMFMediaBuffer pBuffer);
}

/// <summary>
/// IMFTransform interface
/// </summary>
[ComImport]
[Guid("bf94c121-5b05-4e6f-8000-ba598961414d")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFTransform
{
    [PreserveSig]
    int GetStreamLimits(out uint pdwInputMinimum, out uint pdwInputMaximum, out uint pdwOutputMinimum, out uint pdwOutputMaximum);

    [PreserveSig]
    int GetStreamCount(out uint pcInputStreams, out uint pcOutputStreams);

    [PreserveSig]
    int GetStreamIDs(uint dwInputIDArraySize, [Out, MarshalAs(UnmanagedType.LPArray)] uint[] pdwInputIDs,
        uint dwOutputIDArraySize, [Out, MarshalAs(UnmanagedType.LPArray)] uint[] pdwOutputIDs);

    [PreserveSig]
    int GetInputStreamInfo(uint dwInputStreamID, out MFInterop.MFT_INPUT_STREAM_INFO pStreamInfo);

    [PreserveSig]
    int GetOutputStreamInfo(uint dwOutputStreamID, out MFInterop.MFT_OUTPUT_STREAM_INFO pStreamInfo);

    [PreserveSig]
    int GetAttributes(out IMFAttributes pAttributes);

    [PreserveSig]
    int GetInputStreamAttributes(uint dwInputStreamID, out IMFAttributes pAttributes);

    [PreserveSig]
    int GetOutputStreamAttributes(uint dwOutputStreamID, out IMFAttributes pAttributes);

    [PreserveSig]
    int DeleteInputStream(uint dwStreamID);

    [PreserveSig]
    int AddInputStreams(uint cStreams, [MarshalAs(UnmanagedType.LPArray)] uint[] adwStreamIDs);

    [PreserveSig]
    int GetInputAvailableType(uint dwInputStreamID, uint dwTypeIndex, out IMFMediaType ppType);

    [PreserveSig]
    int GetOutputAvailableType(uint dwOutputStreamID, uint dwTypeIndex, out IMFMediaType ppType);

    [PreserveSig]
    int SetInputType(uint dwInputStreamID, IMFMediaType pType, uint dwFlags);

    [PreserveSig]
    int SetOutputType(uint dwOutputStreamID, IMFMediaType pType, uint dwFlags);

    [PreserveSig]
    int GetInputCurrentType(uint dwInputStreamID, out IMFMediaType ppType);

    [PreserveSig]
    int GetOutputCurrentType(uint dwOutputStreamID, out IMFMediaType ppType);

    [PreserveSig]
    int GetInputStatus(uint dwInputStreamID, out uint pdwFlags);

    [PreserveSig]
    int GetOutputStatus(out uint pdwFlags);

    [PreserveSig]
    int SetOutputBounds(long hnsLowerBound, long hnsUpperBound);

    [PreserveSig]
    int ProcessEvent(uint dwInputStreamID, IntPtr pEvent);

    [PreserveSig]
    int ProcessMessage(uint eMessage, IntPtr ulParam);

    [PreserveSig]
    int ProcessInput(uint dwInputStreamID, IMFSample pSample, uint dwFlags);

    [PreserveSig]
    int ProcessOutput(uint dwFlags, uint cOutputBufferCount,
        [In, Out, MarshalAs(UnmanagedType.LPArray)] MFInterop.MFT_OUTPUT_DATA_BUFFER[] pOutputSamples,
        out uint pdwStatus);
}

/// <summary>
/// IMFActivate interface
/// </summary>
[ComImport]
[Guid("7fee9e9a-4a89-47a6-899c-b6a53a70fb67")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFActivate : IMFAttributes
{
    // IMFAttributes methods (inherited)
    #region IMFAttributes
    [PreserveSig]
    new int GetItem(ref Guid guidKey, IntPtr pValue);
    [PreserveSig]
    new int GetItemType(ref Guid guidKey, out uint pType);
    [PreserveSig]
    new int CompareItem(ref Guid guidKey, IntPtr Value, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
    [PreserveSig]
    new int Compare(IMFAttributes pTheirs, uint MatchType, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
    [PreserveSig]
    new int GetUINT32(ref Guid guidKey, out uint punValue);
    [PreserveSig]
    new int GetUINT64(ref Guid guidKey, out ulong punValue);
    [PreserveSig]
    new int GetDouble(ref Guid guidKey, out double pfValue);
    [PreserveSig]
    new int GetGUID(ref Guid guidKey, out Guid pguidValue);
    [PreserveSig]
    new int GetStringLength(ref Guid guidKey, out uint pcchLength);
    [PreserveSig]
    new int GetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pwszValue, uint cchBufSize, out uint pcchLength);
    [PreserveSig]
    new int GetAllocatedString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string ppwszValue, out uint pcchLength);
    [PreserveSig]
    new int GetBlobSize(ref Guid guidKey, out uint pcbBlobSize);
    [PreserveSig]
    new int GetBlob(ref Guid guidKey, [Out, MarshalAs(UnmanagedType.LPArray)] byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);
    [PreserveSig]
    new int GetAllocatedBlob(ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
    [PreserveSig]
    new int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);
    [PreserveSig]
    new int SetItem(ref Guid guidKey, IntPtr Value);
    [PreserveSig]
    new int DeleteItem(ref Guid guidKey);
    [PreserveSig]
    new int DeleteAllItems();
    [PreserveSig]
    new int SetUINT32(ref Guid guidKey, uint unValue);
    [PreserveSig]
    new int SetUINT64(ref Guid guidKey, ulong unValue);
    [PreserveSig]
    new int SetDouble(ref Guid guidKey, double fValue);
    [PreserveSig]
    new int SetGUID(ref Guid guidKey, ref Guid guidValue);
    [PreserveSig]
    new int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    [PreserveSig]
    new int SetBlob(ref Guid guidKey, [MarshalAs(UnmanagedType.LPArray)] byte[] pBuf, uint cbBufSize);
    [PreserveSig]
    new int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
    [PreserveSig]
    new int LockStore();
    [PreserveSig]
    new int UnlockStore();
    [PreserveSig]
    new int GetCount(out uint pcItems);
    [PreserveSig]
    new int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
    [PreserveSig]
    new int CopyAllItems(IMFAttributes pDest);
    #endregion

    // IMFActivate methods
    [PreserveSig]
    int ActivateObject(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);

    [PreserveSig]
    int ShutdownObject();

    [PreserveSig]
    int DetachObject();
}

/// <summary>
/// IMFDXGIDeviceManager interface
/// </summary>
[ComImport]
[Guid("eb533d5d-2db6-40f8-97a9-494692014f07")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFDXGIDeviceManager
{
    [PreserveSig]
    int CloseDeviceHandle(IntPtr hDevice);

    [PreserveSig]
    int GetVideoService(IntPtr hDevice, ref Guid riid, out IntPtr ppService);

    [PreserveSig]
    int LockDevice(IntPtr hDevice, ref Guid riid, out IntPtr ppUnkDevice, [MarshalAs(UnmanagedType.Bool)] bool fBlock);

    [PreserveSig]
    int OpenDeviceHandle(out IntPtr phDevice);

    [PreserveSig]
    int ResetDevice([MarshalAs(UnmanagedType.IUnknown)] object pUnkDevice, uint resetToken);

    [PreserveSig]
    int TestDevice(IntPtr hDevice);

    [PreserveSig]
    int UnlockDevice(IntPtr hDevice, [MarshalAs(UnmanagedType.Bool)] bool fSaveState);
}

/// <summary>
/// ICodecAPI interface for encoder configuration
/// </summary>
[ComImport]
[Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ICodecAPI
{
    [PreserveSig]
    int IsSupported(ref Guid Api);

    [PreserveSig]
    int IsModifiable(ref Guid Api);

    [PreserveSig]
    int GetParameterRange(ref Guid Api, out IntPtr ValueMin, out IntPtr ValueMax, out IntPtr SteppingDelta);

    [PreserveSig]
    int GetParameterValues(ref Guid Api, out IntPtr Values, out uint ValuesCount);

    [PreserveSig]
    int GetDefaultValue(ref Guid Api, out IntPtr Value);

    [PreserveSig]
    int GetValue(ref Guid Api, out IntPtr Value);

    [PreserveSig]
    int SetValue(ref Guid Api, ref MFInterop.PROPVARIANT Value);

    [PreserveSig]
    int RegisterForEvent(ref Guid Api, IntPtr userData);

    [PreserveSig]
    int UnregisterForEvent(ref Guid Api);

    [PreserveSig]
    int SetAllDefaults();

    [PreserveSig]
    int SetValueWithNotify(ref Guid Api, ref MFInterop.PROPVARIANT Value, out IntPtr ChangedParam, out uint ChangedParamCount);

    [PreserveSig]
    int SetAllDefaultsWithNotify(out IntPtr ChangedParam, out uint ChangedParamCount);

    [PreserveSig]
    int GetAllSettings(IntPtr __MIDL__ICodecAPI0000);

    [PreserveSig]
    int SetAllSettings(IntPtr __MIDL__ICodecAPI0001);

    [PreserveSig]
    int SetAllSettingsWithNotify(IntPtr __MIDL__ICodecAPI0002, out IntPtr ChangedParam, out uint ChangedParamCount);
}

#endregion
