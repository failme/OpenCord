using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeScord;

// Minimal Media Foundation interop: everything the camera capture and H.264 encode/decode paths
// need. The COM interfaces are declared with their FULL vtables (method order IS the vtable order —
// skipping a method would shift every one after it and crash). MF ships in every Windows 10/11,
// so this adds no packaged dependency. [PreserveSig] keeps HRESULTs as ints so callers compare
// against the MF_E_* constants without a thrown COMException on every probe.
static class Mf
{
    // ── GUIDs ──────────────────────────────────────────────────────────────────────────────────
    public static readonly Guid
        // MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE and MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID.
        // These were previously two fabricated GUIDs: SetGUID/GetGUID round-tripped (both used the
        // same wrong key) so the probe looked fine, but MFEnumDeviceSources looks up the REAL
        // SOURCE_TYPE key and failed with MF_E_ATTRIBUTENOTFOUND (0xC00D36E6) — the camera button
        // silently died. Values verified against the Windows SDK (mfidl.h), Wine, and mingw-w64.
        DevSourceType = new("C60AC5FE-252A-478F-A0EF-BC8FA5F7CAD3"),
        DevSourceTypeVidCap = new("8AC3587A-4AE7-42D8-99E0-0A6013EEF90F"),
        DevSourceFriendlyName = new("60D0E559-52F8-4FA2-BBCE-ACDB34A8EC01"),
        // MF_DEVSOURCE_ATTRIBUTE_SHARE_CAPTURE_DEVICE: when TRUE, the capture device is opened in
        // shared (frame-server) mode so OTHER applications — e.g. the real Discord client — can
        // use the same webcam at the same time. Without it, opening the camera while Discord has
        // it active fails with MF_E_DEVICE_IN_USE / the device source won't start.
        DevSourceShareCapture = new("598A2F4F-702E-4345-8724-A5DD2541586D"),
        MtMajorType = new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F"),
        MtSubtype = new("F7E34C9A-42E8-4714-B74B-CB29D72C35E5"),
        MtFrameSize = new("1652C33D-D6B2-4012-B834-72030849A37D"),
        MtFrameRate = new("C459A2E8-3D2C-4E44-B132-FEE5156C7BB0"),
        MtInterlaceMode = new("E2724BB8-E676-4806-B4B2-A8D6EFB44CCD"),
        MtPixelAspectRatio = new("C6376A1E-8D0A-4027-BE45-6D9A0AD39BB6"),
        MtAvgBitrate = new("20332624-FB0D-4D9E-BD0D-CBF6786C102E"),
        MtMpeg2Profile = new("AD76A80B-2D5C-4E0B-B375-64E520137036"),
        MtMpeg2Level = new("96F66574-11C5-4015-8666-BFF516436DA7"),
        // MF_MT_MAX_KEYFRAME_SPACING (UINT32): max frames from one IDR to the next. The MS H.264
        // encoder otherwise emits exactly ONE keyframe — its very first frame — for the life of
        // the MFT, so any receiver that joins late or drops a packet stays black forever.
        MtMaxKeyframeSpacing = new("C16EB52B-73A1-476F-8D62-839D6A020652"),
        // MF_MT_MPEG_SEQUENCE_HEADER: the encoder may carry SPS/PPS here instead of in-band.
        MtMpegSeqHeader = new("1A6C95C9-FF0A-4B43-A2E5-2F20A35B8C2C"),
        MediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71"),
        VideoFormatNv12 = new("3231564E-0000-0010-8000-00AA00389B71"),
        // D3DFMT_X8R8G8B8 (22) in the MF subtype namespace — BGRA bytes, which is exactly what
        // GDI+ wants for a 32bpp Bitmap, so a decoded frame copies straight in.
        VideoFormatRgb32 = new("00000016-0000-0010-8000-00AA00389B71"),
        // MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING: lets the reader insert the video processor so
        // it can hand back RGB32 regardless of what the file actually stores.
        SourceReaderEnableVideoProcessing = new("FB394F3D-CCF1-42EE-BBB3-F9B845D5681D"),
        VideoFormatH264 = new("34363248-0000-0010-8000-00AA00389B71"),
        VideoFormatIyuv = new("56555949-0000-0010-8000-00AA00389B71"),
        VideoFormatYuy2 = new("59555932-0000-0010-8000-00AA00389B71"),
        VideoFormatYv12 = new("59563132-0000-0010-8000-00AA00389B71"),
        CategoryVideoEncoder = new("F79EAC7D-E545-4387-BDEE-D647D7BDE42A"),
        CategoryVideoDecoder = new("D6C02D4B-6833-45B4-971A-05A4B04BAB91"),
        ClsidH264Encoder = new("6CA50344-051A-4DED-9779-A43305165E35"),
        ClsidH264Decoder = new("62CE7E72-4C71-4D20-B15D-452831A87D9D"),
        // MF_TRANSFORM_ASYNC: set TRUE on an MFT's attribute store when it is asynchronous —
        // output is signalled by events, not by polling ProcessOutput. The MS H.264 encoder sets
        // it on some Windows builds; a sync-style drive then never yields output there.
        MfTransformAsync = new("f81a699a-649a-497d-8c73-29f8d6da5634");

    // ── HRESULTs / flags ───────────────────────────────────────────────────────────────────────
    public const int S_OK = 0;
    public const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    public const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D76);
    // "Drain my output before giving me more input." Not an error — the correct response is to
    // ProcessOutput and re-submit the same sample. Treating it as a failure wedges the MFT for
    // good: it never accepts another frame, so it never produces output to unblock itself.
    public const int MF_E_NOTACCEPTING = unchecked((int)0xC00D36B5);
    // The MFT dropped its output type — the H.264 DECODER does this the moment it parses a real
    // SPS whose resolution differs from the one the caller guessed. It is a renegotiation request,
    // not a failure: re-enumerate the output types and set one.
    public const int MF_E_TRANSFORM_TYPE_NOT_SET = unchecked((int)0xC00D6D61);
    public const uint MF_VERSION = 0x00020070;
    // 0xFFFFFFFC per mfreadwrite.h. This was 0xFFFFFFFB, which is not a sentinel at all — it just
    // resolved to "no such stream", so every call using it failed. Only the --mft diagnostic used
    // it (CameraCapture deliberately passes index 0), so the wrong value never surfaced.
    public const uint MF_SOURCE_READER_FIRST_VIDEO_STREAM = 0xFFFFFFFC;
    public const uint MF_SOURCE_READER_FIRST_AUDIO_STREAM = 0xFFFFFFFD;
    public const uint MF_SOURCE_READERF_CURRENTMEDIATYPE_CHANGED = 0x2;
    public const uint MFT_ENUM_FLAG_SYNCMFT = 0x1, MFT_ENUM_FLAG_ASYNCMFT = 0x2,
                        MFT_ENUM_FLAG_HARDWARE = 0x4, MFT_ENUM_FLAG_ALL = 0x1F;
    public const int MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0;
    public const int MFT_MESSAGE_NOTIFY_START_OF_STREAM = 1;
    public const int MFT_MESSAGE_COMMAND_FLUSH = 2;
    public const int MFT_MESSAGE_COMMAND_DRAIN = 3;
    public const uint MFVideoInterlace_Progressive = 2;
    public const int MFT_OUTPUT_DATA_BUFFER_NO_SAMPLE = 0x1;

    // ── interfaces ─────────────────────────────────────────────────────────────────────────────
    [ComImport, Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFAttributes
    {
        [PreserveSig] int GetItem(Guid key, IntPtr pValue);
        [PreserveSig] int GetItemType(Guid key, out int pType);
        [PreserveSig] int CompareItem(Guid key, IntPtr value, out int pbResult);
        [PreserveSig] int Compare(IMFAttributes pTheirs, int match, out int pbResult);
        [PreserveSig] int GetUINT32(Guid key, out uint pValue);
        [PreserveSig] int GetUINT64(Guid key, out ulong pValue);
        [PreserveSig] int GetDouble(Guid key, out double pValue);
        [PreserveSig] int GetGUID(Guid key, out Guid pValue);
        [PreserveSig] int GetStringLength(Guid key, out uint pcchLength);
        [PreserveSig] int GetString(Guid key, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszValue, uint cchBufSize, out uint pcchLength);
        [PreserveSig] int GetAllocatedString(Guid key, out IntPtr ppwszValue, out uint pcchLength);
        [PreserveSig] int GetBlobSize(Guid key, out uint pcbBlobSize);
        [PreserveSig] int GetBlob(Guid key, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);
        [PreserveSig] int GetAllocatedBlob(Guid key, out IntPtr ppBuf, out uint pcbSize);
        [PreserveSig] int GetUnknown(Guid key, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
        [PreserveSig] int SetItem(Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(Guid key, uint value);
        [PreserveSig] int SetUINT64(Guid key, ulong value);
        [PreserveSig] int SetDouble(Guid key, double value);
        [PreserveSig] int SetGUID(Guid key, Guid value);
        [PreserveSig] int SetString(Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(Guid key, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] pBuf, uint cbBufSize);
        [PreserveSig] int SetUnknown(Guid key, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint pcItems);
        [PreserveSig] int GetItemByIndex(uint index, out Guid pKey, IntPtr pValue);
        [PreserveSig] int CopyAllItems(IMFAttributes pDest);
    }

    // NOTE: .NET's COM interop computes vtable slots unreliably when a [ComImport] interface
    // inherits another one (IMFSample's derived methods landed on wrong slots — AddBuffer returned
    // MF_E_ATTRIBUTENOTFOUND and SetSampleTime AV'd), so every interface below is declared FLAT:
    // IUnknown's 3 methods are implicit, then the full parent method list, then its own, in exact
    // vtable order. Flattened interfaces are proven reliable (IMFTransform / IMFMediaBuffer work).
    [ComImport, Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFMediaType
    {
        [PreserveSig] int GetItem(Guid key, IntPtr pValue);
        [PreserveSig] int GetItemType(Guid key, out int pType);
        [PreserveSig] int CompareItem(Guid key, IntPtr value, out int pbResult);
        [PreserveSig] int Compare(IMFAttributes pTheirs, int match, out int pbResult);
        [PreserveSig] int GetUINT32(Guid key, out uint pValue);
        [PreserveSig] int GetUINT64(Guid key, out ulong pValue);
        [PreserveSig] int GetDouble(Guid key, out double pValue);
        [PreserveSig] int GetGUID(Guid key, out Guid pValue);
        [PreserveSig] int GetStringLength(Guid key, out uint pcchLength);
        [PreserveSig] int GetString(Guid key, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszValue, uint cchBufSize, out uint pcchLength);
        [PreserveSig] int GetAllocatedString(Guid key, out IntPtr ppwszValue, out uint pcchLength);
        [PreserveSig] int GetBlobSize(Guid key, out uint pcbBlobSize);
        [PreserveSig] int GetBlob(Guid key, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);
        [PreserveSig] int GetAllocatedBlob(Guid key, out IntPtr ppBuf, out uint pcbSize);
        [PreserveSig] int GetUnknown(Guid key, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
        [PreserveSig] int SetItem(Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(Guid key, uint value);
        [PreserveSig] int SetUINT64(Guid key, ulong value);
        [PreserveSig] int SetDouble(Guid key, double value);
        [PreserveSig] int SetGUID(Guid key, Guid value);
        [PreserveSig] int SetString(Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(Guid key, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] pBuf, uint cbBufSize);
        [PreserveSig] int SetUnknown(Guid key, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint pcItems);
        [PreserveSig] int GetItemByIndex(uint index, out Guid pKey, IntPtr pValue);
        [PreserveSig] int CopyAllItems(IMFAttributes pDest);
        [PreserveSig] int GetMajorType(out Guid pguidMajorType);
        [PreserveSig] int IsCompressedFormat(out int pfCompressed);
        [PreserveSig] int IsEqual(IMFMediaType pIAttributes, out int pfEqual);
        [PreserveSig] int GetRepresentation(Guid guidRepresentation, out IntPtr ppvRepresentation);
        [PreserveSig] int FreeRepresentation(Guid guidRepresentation, IntPtr pvRepresentation);
    }

    [ComImport, Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFSample
    {
        [PreserveSig] int GetItem(Guid key, IntPtr pValue);
        [PreserveSig] int GetItemType(Guid key, out int pType);
        [PreserveSig] int CompareItem(Guid key, IntPtr value, out int pbResult);
        [PreserveSig] int Compare(IMFAttributes pTheirs, int match, out int pbResult);
        [PreserveSig] int GetUINT32(Guid key, out uint pValue);
        [PreserveSig] int GetUINT64(Guid key, out ulong pValue);
        [PreserveSig] int GetDouble(Guid key, out double pValue);
        [PreserveSig] int GetGUID(Guid key, out Guid pValue);
        [PreserveSig] int GetStringLength(Guid key, out uint pcchLength);
        [PreserveSig] int GetString(Guid key, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszValue, uint cchBufSize, out uint pcchLength);
        [PreserveSig] int GetAllocatedString(Guid key, out IntPtr ppwszValue, out uint pcchLength);
        [PreserveSig] int GetBlobSize(Guid key, out uint pcbBlobSize);
        [PreserveSig] int GetBlob(Guid key, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);
        [PreserveSig] int GetAllocatedBlob(Guid key, out IntPtr ppBuf, out uint pcbSize);
        [PreserveSig] int GetUnknown(Guid key, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
        [PreserveSig] int SetItem(Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(Guid key, uint value);
        [PreserveSig] int SetUINT64(Guid key, ulong value);
        [PreserveSig] int SetDouble(Guid key, double value);
        [PreserveSig] int SetGUID(Guid key, Guid value);
        [PreserveSig] int SetString(Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(Guid key, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] pBuf, uint cbBufSize);
        [PreserveSig] int SetUnknown(Guid key, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint pcItems);
        [PreserveSig] int GetItemByIndex(uint index, out Guid pKey, IntPtr pValue);
        [PreserveSig] int CopyAllItems(IMFAttributes pDest);
        [PreserveSig] int GetSampleFlags(out uint pdwFlags);
        [PreserveSig] int SetSampleFlags(uint dwSampleFlags);
        [PreserveSig] int GetSampleTime(out long phnsSampleTime);
        [PreserveSig] int SetSampleTime(long hnsSampleTime);
        [PreserveSig] int GetSampleDuration(out long phnsSampleDuration);
        [PreserveSig] int SetSampleDuration(long hnsSampleDuration);
        [PreserveSig] int GetBufferCount(out uint pdwBufferCount);
        [PreserveSig] int GetBufferByIndex(uint dwIndex, out IMFMediaBuffer ppBuffer);
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer ppBuffer);
        [PreserveSig] int AddBuffer(IMFMediaBuffer pBuffer);
        [PreserveSig] int RemoveBufferByIndex(uint dwIndex);
        [PreserveSig] int RemoveAllBuffers();
        [PreserveSig] int GetTotalLength(out uint pcbTotalLength);
        [PreserveSig] int CopyToBuffer(IMFMediaBuffer pBuffer);
    }

    [ComImport, Guid("7FEE9E9A-4A89-47A6-899C-B6A53A70FB67"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFActivate
    {
        [PreserveSig] int GetItem(Guid key, IntPtr pValue);
        [PreserveSig] int GetItemType(Guid key, out int pType);
        [PreserveSig] int CompareItem(Guid key, IntPtr value, out int pbResult);
        [PreserveSig] int Compare(IMFAttributes pTheirs, int match, out int pbResult);
        [PreserveSig] int GetUINT32(Guid key, out uint pValue);
        [PreserveSig] int GetUINT64(Guid key, out ulong pValue);
        [PreserveSig] int GetDouble(Guid key, out double pValue);
        [PreserveSig] int GetGUID(Guid key, out Guid pValue);
        [PreserveSig] int GetStringLength(Guid key, out uint pcchLength);
        [PreserveSig] int GetString(Guid key, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszValue, uint cchBufSize, out uint pcchLength);
        [PreserveSig] int GetAllocatedString(Guid key, out IntPtr ppwszValue, out uint pcchLength);
        [PreserveSig] int GetBlobSize(Guid key, out uint pcbBlobSize);
        [PreserveSig] int GetBlob(Guid key, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);
        [PreserveSig] int GetAllocatedBlob(Guid key, out IntPtr ppBuf, out uint pcbSize);
        [PreserveSig] int GetUnknown(Guid key, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
        [PreserveSig] int SetItem(Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(Guid key, uint value);
        [PreserveSig] int SetUINT64(Guid key, ulong value);
        [PreserveSig] int SetDouble(Guid key, double value);
        [PreserveSig] int SetGUID(Guid key, Guid value);
        [PreserveSig] int SetString(Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(Guid key, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] pBuf, uint cbBufSize);
        [PreserveSig] int SetUnknown(Guid key, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint pcItems);
        [PreserveSig] int GetItemByIndex(uint index, out Guid pKey, IntPtr pValue);
        [PreserveSig] int CopyAllItems(IMFAttributes pDest);
        [PreserveSig] int ActivateObject([MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
        [PreserveSig] int ShutdownObject();
        [PreserveSig] int DetachObject();
    }

    [ComImport, Guid("045FA593-8799-42B8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr ppbBuffer, out uint pcbMaxLength, out uint pcbCurrentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out uint pcbCurrentLength);
        [PreserveSig] int SetCurrentLength(uint cbCurrentLength);
        [PreserveSig] int GetMaxLength(out uint pcbMaxLength);
    }

    // IMFPresentationDescriptor / IMFStreamDescriptor (diagnostics only): both derive from
    // IMFAttributes, so they are declared FLAT with the same 28 attribute methods first, then
    // their own (IMFPresentationDescriptor: count/by-index/select/deselect; IMFStreamDescriptor:
    // stream id + media type handler).
    [ComImport, Guid("03CB2711-24D7-4DB6-A17F-F3A7A479A536"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFPresentationDescriptor
    {
        [PreserveSig] int GetItem(Guid key, IntPtr pValue);
        [PreserveSig] int GetItemType(Guid key, out int pType);
        [PreserveSig] int CompareItem(Guid key, IntPtr value, out int pbResult);
        [PreserveSig] int Compare(IMFAttributes pTheirs, int match, out int pbResult);
        [PreserveSig] int GetUINT32(Guid key, out uint pValue);
        [PreserveSig] int GetUINT64(Guid key, out ulong pValue);
        [PreserveSig] int GetDouble(Guid key, out double pValue);
        [PreserveSig] int GetGUID(Guid key, out Guid pValue);
        [PreserveSig] int GetStringLength(Guid key, out uint pcchLength);
        [PreserveSig] int GetString(Guid key, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszValue, uint cchBufSize, out uint pcchLength);
        [PreserveSig] int GetAllocatedString(Guid key, out IntPtr ppwszValue, out uint pcchLength);
        [PreserveSig] int GetBlobSize(Guid key, out uint pcbBlobSize);
        [PreserveSig] int GetBlob(Guid key, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);
        [PreserveSig] int GetAllocatedBlob(Guid key, out IntPtr ppBuf, out uint pcbSize);
        [PreserveSig] int GetUnknown(Guid key, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
        [PreserveSig] int SetItem(Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(Guid key, uint value);
        [PreserveSig] int SetUINT64(Guid key, ulong value);
        [PreserveSig] int SetDouble(Guid key, double value);
        [PreserveSig] int SetGUID(Guid key, Guid value);
        [PreserveSig] int SetString(Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(Guid key, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] pBuf, uint cbBufSize);
        [PreserveSig] int SetUnknown(Guid key, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint pcItems);
        [PreserveSig] int GetItemByIndex(uint index, out Guid pKey, IntPtr pValue);
        [PreserveSig] int CopyAllItems(IMFAttributes pDest);
        [PreserveSig] int GetStreamDescriptorCount(out uint pcDescriptors);
        [PreserveSig] int GetStreamDescriptorByIndex(uint dwIndex, out int pfSelected, out IntPtr ppDescriptor);
        [PreserveSig] int SelectStream(uint dwDescriptorIndex);
        [PreserveSig] int DeselectStream(uint dwDescriptorIndex);
    }

    [ComImport, Guid("56C03D9C-9DBB-45F5-AB4B-D80F47C05938"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFStreamDescriptor
    {
        [PreserveSig] int GetItem(Guid key, IntPtr pValue);
        [PreserveSig] int GetItemType(Guid key, out int pType);
        [PreserveSig] int CompareItem(Guid key, IntPtr value, out int pbResult);
        [PreserveSig] int Compare(IMFAttributes pTheirs, int match, out int pbResult);
        [PreserveSig] int GetUINT32(Guid key, out uint pValue);
        [PreserveSig] int GetUINT64(Guid key, out ulong pValue);
        [PreserveSig] int GetDouble(Guid key, out double pValue);
        [PreserveSig] int GetGUID(Guid key, out Guid pValue);
        [PreserveSig] int GetStringLength(Guid key, out uint pcchLength);
        [PreserveSig] int GetString(Guid key, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszValue, uint cchBufSize, out uint pcchLength);
        [PreserveSig] int GetAllocatedString(Guid key, out IntPtr ppwszValue, out uint pcchLength);
        [PreserveSig] int GetBlobSize(Guid key, out uint pcbBlobSize);
        [PreserveSig] int GetBlob(Guid key, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);
        [PreserveSig] int GetAllocatedBlob(Guid key, out IntPtr ppBuf, out uint pcbSize);
        [PreserveSig] int GetUnknown(Guid key, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
        [PreserveSig] int SetItem(Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(Guid key, uint value);
        [PreserveSig] int SetUINT64(Guid key, ulong value);
        [PreserveSig] int SetDouble(Guid key, double value);
        [PreserveSig] int SetGUID(Guid key, Guid value);
        [PreserveSig] int SetString(Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(Guid key, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] pBuf, uint cbBufSize);
        [PreserveSig] int SetUnknown(Guid key, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint pcItems);
        [PreserveSig] int GetItemByIndex(uint index, out Guid pKey, IntPtr pValue);
        [PreserveSig] int CopyAllItems(IMFAttributes pDest);
        [PreserveSig] int GetStreamIdentifier(out uint pdwStreamIdentifier);
        [PreserveSig] int GetMediaTypeHandler(out IntPtr ppHandler);
    }

    // IMFMediaTypeHandler (diagnostics): the media type store on a stream descriptor.
    [ComImport, Guid("E93DCF6C-4B07-4E1E-8123-AA16ED6EADF5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFMediaTypeHandler
    {
        [PreserveSig] int IsMediaTypeSupported(IMFMediaType pMediaType, out IMFMediaType ppMediaType);
        [PreserveSig] int GetMediaTypeCount(out uint pcMediaTypes);
        [PreserveSig] int GetMediaTypeByIndex(uint dwIndex, out IMFMediaType ppType);
        [PreserveSig] int SetCurrentMediaType(IMFMediaType pMediaType);
        [PreserveSig] int GetCurrentMediaType(out IMFMediaType ppMediaType);
        [PreserveSig] int GetMajorType(out Guid pguidMajorType);
    }

    // MFTEnumEx type filter: two GUIDs (major + subtype). The P/Invoke takes a pointer to this.
    [StructLayout(LayoutKind.Sequential)]
    public struct MFT_REGISTER_TYPE_INFO
    {
        public Guid guidMajorType;
        public Guid guidSubtype;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MFT_INPUT_STREAM_INFO
    {
        public long hnsMaxLatency;
        public uint dwFlags;
        public uint cbSize;
        public uint cbMaxLookahead;
        public uint cbAlignment;
        public IntPtr pguidMajorType;
    }

    /// The OUTPUT counterpart — three DWORDs, no latency field. `cbSize` is the minimum output
    /// buffer the MFT needs for the CURRENT output type; it changes when the type does (a 720p
    /// peer needs 1.32MB of NV12 where 640x360 needs 0.34MB), so it must be re-read after every
    /// stream change rather than guessed.
    [StructLayout(LayoutKind.Sequential)]
    public struct MFT_OUTPUT_STREAM_INFO
    {
        public uint dwFlags;
        public uint cbSize;
        public uint cbAlignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MFT_OUTPUT_DATA_BUFFER
    {
        public uint dwStreamID;
        public IntPtr pSample;
        public uint dwStatus;
        public IntPtr pEvents;
    }

    [ComImport, Guid("BF94C121-5B05-4E6F-8000-BA598961414D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFTransform
    {
        [PreserveSig] int GetStreamLimits(out uint pdwInputMinimum, out uint pdwInputMaximum,
                                          out uint pdwOutputMinimum, out uint pdwOutputMaximum);
        [PreserveSig] int GetStreamCount(out uint pcInputStreams, out uint pcOutputStreams);
        [PreserveSig] int GetStreamIDs(uint dwInputIDArraySize, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] [Out] uint[] pdwInputIDs,
                                       uint dwOutputIDArraySize, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] [Out] uint[] pdwOutputIDs);
        [PreserveSig] int GetInputStreamInfo(uint dwInputStreamID, out MFT_INPUT_STREAM_INFO pStreamInfo);
        // MFT_OUTPUT_STREAM_INFO, NOT the input one — the native structs differ (the input one
        // leads with an 8-byte hnsMaxLatency), so the old shared declaration read cbAlignment
        // where dwFlags lives and never exposed cbSize at all.
        [PreserveSig] int GetOutputStreamInfo(uint dwOutputStreamID, out MFT_OUTPUT_STREAM_INFO pStreamInfo);
        [PreserveSig] int GetAttributes(out IMFAttributes pAttributes);
        [PreserveSig] int GetInputStreamAttributes(uint dwInputStreamID, out IMFAttributes pAttributes);
        [PreserveSig] int GetOutputStreamAttributes(uint dwOutputStreamID, out IMFAttributes pAttributes);
        [PreserveSig] int DeleteInputStream(uint dwStreamID);
        [PreserveSig] int AddInputStreams(uint cStreams, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] [In] uint[] adwStreamIDs);
        [PreserveSig] int GetInputAvailableType(uint dwInputStreamID, uint dwTypeIndex, out IMFMediaType ppType);
        [PreserveSig] int GetOutputAvailableType(uint dwOutputStreamID, uint dwTypeIndex, out IMFMediaType ppType);
        [PreserveSig] int SetInputType(uint dwInputStreamID, IMFMediaType pType, uint dwFlags);
        [PreserveSig] int SetOutputType(uint dwOutputStreamID, IMFMediaType pType, uint dwFlags);
        [PreserveSig] int GetInputCurrentType(uint dwInputStreamID, out IMFMediaType ppType);
        [PreserveSig] int GetOutputCurrentType(uint dwOutputStreamID, out IMFMediaType ppType);
        [PreserveSig] int GetInputStatus(uint dwInputStreamID, out uint pdwFlags);
        [PreserveSig] int GetOutputStatus(out uint pdwFlags);
        [PreserveSig] int SetOutputBounds(long hnsLowerBound, long hnsUpperBound);
        [PreserveSig] int ProcessEvent(uint dwInputStreamID, IntPtr pEvent);
        [PreserveSig] int ProcessMessage(int eMessage, IntPtr ulParam);
        [PreserveSig] int ProcessInput(uint dwInputStreamID, IMFSample pSample, uint dwFlags);
        // pOutputSamples is a pointer to a caller-allocated MFT_OUTPUT_DATA_BUFFER array. The codecs
        // always drain one sample at a time, so it is declared IntPtr and the caller marshals a
        // single element manually (struct-array and ref marshaling both misbehaved on this MFT).
        [PreserveSig] int ProcessOutput(uint dwFlags, uint cOutputBufferCount,
                                        IntPtr pOutputSamples, out uint pdwStatus);
    }

    [ComImport, Guid("2CD0BD52-BCD5-4B89-B62C-EADC0C031E7D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFMediaEventGenerator
    {
        [PreserveSig] int GetEvent(uint dwFlags, out IntPtr ppEvent);
        [PreserveSig] int BeginGetEvent(IntPtr pCallback, IntPtr punkState);
        [PreserveSig] int EndGetEvent(IntPtr pResult, out IntPtr ppEvent);
        [PreserveSig] int QueueEvent(int met, [MarshalAs(UnmanagedType.LPStruct)] Guid guidExtendedType,
                                     int hrStatus, IntPtr pvValue);
    }

    [ComImport, Guid("279A808D-AEC7-40C8-9C6B-A6B492C78A66"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFMediaSource
    {
        [PreserveSig] int GetEvent(uint dwFlags, out IntPtr ppEvent);
        [PreserveSig] int BeginGetEvent(IntPtr pCallback, IntPtr punkState);
        [PreserveSig] int EndGetEvent(IntPtr pResult, out IntPtr ppEvent);
        [PreserveSig] int QueueEvent(int met, [MarshalAs(UnmanagedType.LPStruct)] Guid guidExtendedType,
                                     int hrStatus, IntPtr pvValue);
        [PreserveSig] int GetCharacteristics(out uint pdwCharacteristics);
        [PreserveSig] int CreatePresentationDescriptor(out IntPtr ppPresentationDescriptor);
        [PreserveSig] int Start(IntPtr pPresentationDescriptor, [MarshalAs(UnmanagedType.LPStruct)] Guid guidTimeFormat,
                                IntPtr pvarStartPosition);
        [PreserveSig] int Stop();
        [PreserveSig] int Pause();
        [PreserveSig] int Shutdown();
    }

    [ComImport, Guid("70AE66F2-C809-4E4F-8915-BDCB406B7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFSourceReader
    {
        [PreserveSig] int GetStreamSelection(uint dwStreamIndex, out int pfSelected);
        [PreserveSig] int SetStreamSelection(uint dwStreamIndex, int fSelected);
        [PreserveSig] int GetNativeMediaType(uint dwStreamIndex, uint dwMediaTypeIndex, out IMFMediaType ppMediaType);
        [PreserveSig] int GetCurrentMediaType(uint dwStreamIndex, out IMFMediaType ppMediaType);
        [PreserveSig] int SetCurrentMediaType(uint dwStreamIndex, IntPtr pdwReserved, IMFMediaType pMediaType);
        [PreserveSig] int SetCurrentPosition([MarshalAs(UnmanagedType.LPStruct)] Guid guidTimeFormat, IntPtr pvarPosition);
        // ReadSample has SIX parameters (mfreadwrite.h): the THIRD is the ACTUAL stream index the
        // reader resolved (out DWORD*). The old declaration omitted it, so the native function
        // wrote its six outputs into five slots: the sample pointer landed in the timestamp slot
        // and the real sample went to a sixth (unallocated) slot — every ReadSample returned S_OK
        // with a garbage pointer that AV'd on first use. The out params are RAW POINTERS into a
        // caller-allocated buffer (the .NET out-param form misbehaves on this object; the H.264
        // codecs use the same raw-buffer pattern); see Mf.ReadSampleRaw.
        [PreserveSig] int ReadSample(uint dwStreamIndex, uint dwControlFlags, IntPtr pdwActualStreamIndex,
                                     IntPtr pdwStreamFlags, IntPtr pllTimestamp, IntPtr ppSample);
        [PreserveSig] int Flush(uint dwStreamIndex);
    }

    // ICodecAPI (DirectShow, strmif.h) — the MS H.264 encoder exposes it for codec properties.
    // Values are VARIANTs; the encoder only reads vt + the 32-bit lVal union member for the
    // integer properties we set, so a 16-byte layout suffices.
    [ComImport, Guid("901DB4C7-31CE-41A2-85DC-8FA0BF41B8DA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ICodecAPI
    {
        [PreserveSig] int IsSupported(Guid Api, out int pIsSupported);
        [PreserveSig] int IsModifiable(Guid Api, out int pIsModifiable);
        [PreserveSig] int GetParameterRange(Guid Api, ref PROPVARIANT ValueMin, ref PROPVARIANT ValueMax, ref PROPVARIANT SteppingDelta);
        [PreserveSig] int GetParameterValues(Guid Api, out IntPtr Values, out uint ValuesCount);
        [PreserveSig] int GetDefaultValue(Guid Api, ref PROPVARIANT Value);
        [PreserveSig] int GetValue(Guid Api, ref PROPVARIANT Value);
        [PreserveSig] int SetValue(Guid Api, ref PROPVARIANT Value);
        [PreserveSig] int RegisterForEvent(Guid Api, IntPtr userData);
        [PreserveSig] int UnregisterForEvent(Guid Api);
        [PreserveSig] int SetAllDefaults();
        [PreserveSig] int SetValueWithNotify(Guid Api, ref PROPVARIANT Value, out IntPtr ChangedParam, out uint ChangedParamCount);
        [PreserveSig] int SetAllDefaultsWithNotify(out IntPtr ChangedParam, out uint ChangedParamCount);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROPVARIANT
    {
        public ushort vt;        // 0
        public ushort wReserved1;// 2
        public ushort wReserved2;// 4
        public ushort wReserved3;// 6
        public long union;       // 8 (VT_I4 lives in the low 32 bits)
    }

    public const ushort VT_I4 = 3;

    // CODECAPI property GUIDs (strmif.h / wmcodecdsp.h).
    public static readonly Guid
        CodecApiRateControlMode = new("1C0608E9-370C-4710-8A58-CB6181C4242A"),
        CodecApiBpictureCount = new("9C2AC17C-31B7-44B2-9A71-1B0CD5E0A879"),
        CodecApiLowLatency = new("9C27891A-ED7A-4E5E-B9F2-9139E0AC9EBE"),
        // CODECAPI_AVEncVideoForceKeyFrame — the REAL GUID (codecapi.h). This was previously a
        // fabricated value, so "force keyframe" silently did nothing and the PLI path fell back
        // to tearing the encoder down. 95F31B26-95A4-41AA-9303-246A7FC6EEF1 is
        // CODECAPI_AVEncMPVGOPSize (periodic-IDR interval for live video).
        CodecApiForceKeyFrame = new("398C1B98-8353-475A-9EF2-8F265D260345"),
        CodecApiGopSize = new("95F31B26-95A4-41AA-9303-246A7FC6EEF1"),
        CodecApiCommonQuality = new("EC5C4FB7-1081-47F5-B9D0-8CA8A62E9BE6");

    public const int eAVEncCommonRateControlMode_CBR = 0;

    /// QI an MFT to ICodecAPI (returns null when unsupported) and set an integer property.
    public static bool CodecSetInt(object mft, Guid prop, int value)
    {
        try
        {
            if (!(mft is ICodecAPI capi)) return false;
            var pv = new PROPVARIANT { vt = VT_I4, union = value };
            return capi.SetValue(prop, ref pv) == S_OK;
        }
        catch { return false; }
    }

    [ComImport, Guid("3137F1CD-FE5E-4805-A5D8-FB477448CB3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFSinkWriter
    {
        [PreserveSig] int AddStream(IMFMediaType pTargetMediaType, out uint pdwStreamIndex);
        [PreserveSig] int SetInputMediaType(uint dwStreamIndex, IMFMediaType pInputMediaType, IMFAttributes pEncodingParameters);
        [PreserveSig] int BeginWriting();
        [PreserveSig] int WriteSample(uint dwStreamIndex, IMFSample pSample);
        [PreserveSig] int SendStreamTick(uint dwStreamIndex, long llTimestamp);
        [PreserveSig] int PlaceMarker(uint dwStreamIndex, IntPtr pContext);
        [PreserveSig] int NotifyEndOfSegment(uint dwStreamIndex);
        [PreserveSig] int Flush(uint dwStreamIndex);
        [PreserveSig] int Finalize_();
        [PreserveSig] int GetServiceForStream(uint dwStreamIndex, [MarshalAs(UnmanagedType.LPStruct)] Guid guidService,
                                               [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppvObject);
        [PreserveSig] int GetStatistics(uint dwStreamIndex, IntPtr pStats);
    }

    // ── P/Invoke ───────────────────────────────────────────────────────────────────────────────
    [DllImport("mfplat.dll")] public static extern int MFStartup(uint version, uint dwFlags);
    [DllImport("mfplat.dll")] public static extern int MFShutdown();
    [DllImport("mfplat.dll")] public static extern int MFCreateAttributes(out IMFAttributes attrs, uint cInitialSize);
    // MFEnumDeviceSources and MFCreateDeviceSource are exported by mf.dll (NOT mfplat.dll — the
    // plat DLL only carries the core MFStartup/media-type helpers). Declaring them against
    // mfplat.dll threw EntryPointNotFoundException the moment the camera button was pressed.
    [DllImport("mf.dll")] public static extern int MFEnumDeviceSources(IMFAttributes pAttributes,
                                                                       out IntPtr ppDevices, out uint pcCount);
    [DllImport("mf.dll")] public static extern int MFCreateDeviceSource(IMFAttributes pAttributes,
                                                                        out IMFMediaSource ppSource);
    // MFCreateSourceReaderFromMediaSource lives in mfreadwrite.dll (the Source Reader/Sink Writer
    // DLL), NOT mfplat.dll — declaring it against mfplat threw EntryPointNotFoundException the
    // moment the camera button was pressed (enumeration had been fixed but opening the source
    // reader still failed, so the camera "did nothing").
    [DllImport("mfreadwrite.dll")] public static extern int MFCreateSourceReaderFromMediaSource(
        IMFMediaSource pMediaSource, IMFAttributes pAttributes, out IMFSourceReader ppReader);

    /// Opens a media file (or URL) for decoding — what the inline video player reads frames from.
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] public static extern int MFCreateSourceReaderFromURL(
        string pwszURL, IMFAttributes pAttributes, out IMFSourceReader ppReader);
    [DllImport("mfplat.dll")] public static extern int MFCreateMediaType(out IMFMediaType ppMFType);
    [DllImport("mfplat.dll")] public static extern int MFCreateSample(out IMFSample ppIMFSample);
    [DllImport("mfplat.dll")] public static extern int MFCreateMemoryBuffer(uint cbMaxLength, out IMFMediaBuffer ppBuffer);
    [DllImport("mfplat.dll")] public static extern int MFTEnumEx([MarshalAs(UnmanagedType.LPStruct)] Guid guidCategory,
                                                                 uint Flags, IntPtr pInputType, IntPtr pOutputType,
                                                                 out IntPtr ppMFTs, out uint pcMFTs);
    [DllImport("mfreadwrite.dll")] public static extern int MFCreateSinkWriterFromURL(
        [MarshalAs(UnmanagedType.LPWStr)] string pwszOutputURL, IntPtr pByteStream, IntPtr pAttributes,
        out IMFSinkWriter ppWriter);

    /// Enumerate MFTs filtering on a raw (NV12 → H264) or (H264 → NV12) pair.
    public static int MFTEnumEx2(Guid category, uint flags, Guid? inMajor, Guid? inSub,
                                 Guid? outMajor, Guid? outSub, out IntPtr ppMFTs, out uint pcMFTs)
    {
        IntPtr pin = IntPtr.Zero, pout = IntPtr.Zero;
        MFT_REGISTER_TYPE_INFO ti;
        if (inMajor != null)
        {
            ti = new MFT_REGISTER_TYPE_INFO { guidMajorType = inMajor.Value, guidSubtype = inSub ?? Guid.Empty };
            pin = Marshal.AllocHGlobal(Marshal.SizeOf<MFT_REGISTER_TYPE_INFO>());
            Marshal.StructureToPtr(ti, pin, false);
        }
        if (outMajor != null)
        {
            ti = new MFT_REGISTER_TYPE_INFO { guidMajorType = outMajor.Value, guidSubtype = outSub ?? Guid.Empty };
            pout = Marshal.AllocHGlobal(Marshal.SizeOf<MFT_REGISTER_TYPE_INFO>());
            Marshal.StructureToPtr(ti, pout, false);
        }
        int hr = MFTEnumEx(category, flags, pin, pout, out ppMFTs, out pcMFTs);
        if (pin != IntPtr.Zero) Marshal.FreeHGlobal(pin);
        if (pout != IntPtr.Zero) Marshal.FreeHGlobal(pout);
        return hr;
    }
    // MFSetAttributeSize/Get/Ratio are INLINE functions in mfapi.h (not exported from mfplat.dll);
    // they pack a (hi, lo) uint32 pair into one UINT64 attribute. The interfaces were flattened
    // (no ComImport inheritance), so these accept the concrete interface via object and QI to
    // IMFAttributes (a runtime QI, which every MF object supports).
    public static void MFSetAttributeSize(object pAttr, Guid guidKey, uint unWidth, uint unHeight)
        => ((IMFAttributes)pAttr).SetUINT64(guidKey, ((ulong)unWidth << 32) | unHeight);

    public static int MFGetAttributeSize(object pAttr, Guid guidKey, out uint punWidth, out uint punHeight)
    {
        int hr = ((IMFAttributes)pAttr).GetUINT64(guidKey, out var v);
        punWidth = (uint)(v >> 32);
        punHeight = (uint)(v & 0xFFFFFFFF);
        return hr;
    }

    public static void MFSetAttributeRatio(object pAttr, Guid guidKey, uint unNumerator, uint unDenominator)
        => ((IMFAttributes)pAttr).SetUINT64(guidKey, ((ulong)unNumerator << 32) | unDenominator);

    public static int MFGetAttributeRatio(object pAttr, Guid guidKey, out uint punNumerator, out uint punDenominator)
    {
        int hr = ((IMFAttributes)pAttr).GetUINT64(guidKey, out var v);
        punNumerator = (uint)(v >> 32);
        punDenominator = (uint)(v & 0xFFFFFFFF);
        return hr;
    }
    [DllImport("ole32.dll")] public static extern int CoCreateInstance([MarshalAs(UnmanagedType.LPStruct)] Guid rclsid,
                                                                       IntPtr pUnkOuter, uint dwClsContext,
                                                                       [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
                                                                       out IntPtr ppv);
    [DllImport("ole32.dll")] public static extern void CoTaskMemFree(IntPtr pv);
    [DllImport("ole32.dll")] public static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    public const uint CLSCTX_INPROC_SERVER = 1;

    /// Drive one ProcessOutput with a manually-marshaled MFT_OUTPUT_DATA_BUFFER (the encoder MFT
    /// rejects every .NET-marshaled variant of this call). Returns the HRESULT and the resulting
    /// pSample / dwStatus. Caller owns (and must Release) pSample when non-zero.
    ///
    /// provideSample: pass an OUTPUT sample we allocated ourselves. MFTs that do not set
    /// MFT_OUTPUT_STREAM_PROVIDES_SAMPLES (0x20) in GetOutputStreamInfo REQUIRE the caller to
    /// supply the output sample in pSample; passing NULL makes them return E_INVALIDARG.
    /// `bufferSize` must be at least the MFT's MFT_OUTPUT_STREAM_INFO.cbSize for the current
    /// output type. A buffer that is too small makes ProcessOutput fail on EVERY frame — which is
    /// how a fixed 1MB buffer decoded our own 640x360 stream (0.34MB) and silently produced
    /// nothing at all for a 720p peer (1.32MB).
    public static int ProcessOutputOne(Mf.IMFTransform mft, out IntPtr pSample, out uint dwStatus,
                                       bool provideSample = false, uint bufferSize = 1024 * 1024)
    {
        IntPtr mem = Marshal.AllocHGlobal(32);
        IntPtr ownSample = IntPtr.Zero;
        try
        {
            if (provideSample)
            {
                MFCreateSample(out var s);
                MFCreateMemoryBuffer(Math.Max(bufferSize, 4096u), out var b);
                s.AddBuffer(b);
                ownSample = Marshal.GetIUnknownForObject(s);
            }
            // MFT_OUTPUT_DATA_BUFFER (native, x64): dwStreamID @0, dwStatus @4, pSample @8,
            // pEvents @16 — total 24 bytes. (The C# struct mirror is NOT used here because the
            // encoder rejects .NET-marshaled variants of this call.)
            Marshal.WriteInt32(mem, 0, 0);             // dwStreamID = 0
            Marshal.WriteInt32(mem, 4, 0);             // dwStatus = 0
            Marshal.WriteIntPtr(mem, 8, ownSample);    // pSample (NULL lets the MFT allocate)
            Marshal.WriteIntPtr(mem, 16, IntPtr.Zero); // pEvents = NULL
            int hr = mft.ProcessOutput(0, 1, mem, out _);
            pSample = Marshal.ReadIntPtr(mem, 8);
            dwStatus = (uint)Marshal.ReadInt32(mem, 4);
            // Refcount ownership: GetIUnknownForObject took a reference for the pointer we handed
            // the MFT. When the MFT RETURNS that sample (pSample != 0), ownership passes to the
            // caller — it must Release exactly once. When it does NOT (failure / nothing ready),
            // release OUR reference here; releasing it in both places double-frees the sample and
            // the encoder crashes a few frames later (observed: app died at frame 4).
            if (ownSample != IntPtr.Zero && pSample == IntPtr.Zero)
                Marshal.Release(ownSample);
            return hr;
        }
        finally
        {
            Marshal.FreeHGlobal(mem);
        }
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────
    /// Media Foundation is process-global and refcounted; the app holds one MF instance for the
    /// life of the process, so CameraCapture / H264Codec never call Start/Shutdown themselves.
    static int _mfUsers;
    public static void EnsureStarted()
    {
        if (_mfUsers++ == 0) MFStartup(MF_VERSION, 0);
    }

    // MFStartup is refcounted, and the MS H.264 encoder/decoder MFTs only produce output when the
    // DRIVING thread has called MFStartup itself: a thread whose EnsureStarted() call was skipped
    // by the refcount guard gets S_OK from ProcessInput but NEVER emits an access unit (verified:
    // the encoder probe emits only on threads that called MFStartup directly). Every thread that
    // drives an MFT (the encoder codec thread, the decoder's UDP thread, the camera capture
    // thread) calls this once — idempotent per thread, never shut down (process lifetime).
    [ThreadStatic] static bool _threadStarted;
    public static void EnsureThreadStarted()
    {
        if (_threadStarted) return;
        _threadStarted = true;
        int hr = MFStartup(MF_VERSION, 0);
        if (hr != 0) Console.WriteLine($"MFStartup on thread {Environment.CurrentManagedThreadId} -> 0x{hr:X8}");
    }

    public static void EnsureShutdown()
    {
        if (_mfUsers > 0 && --_mfUsers == 0) MFShutdown();
    }

    public static IMFMediaType MakeVideoType(Guid subtype, int width, int height, int fps)
    {
        MFCreateMediaType(out var t);
        t.SetGUID(MtMajorType, MediaTypeVideo);
        t.SetGUID(MtSubtype, subtype);
        MFSetAttributeSize(t, MtFrameSize, (uint)width, (uint)height);
        MFSetAttributeRatio(t, MtFrameRate, (uint)fps, 1);
        MFSetAttributeRatio(t, MtPixelAspectRatio, 1, 1);
        t.SetUINT32(MtInterlaceMode, MFVideoInterlace_Progressive);
        return t;
    }

    /// Copy bytes into a new memory buffer wrapped in a sample with the given 100ns timestamp.
    public static IMFSample MakeSample(byte[] data, long hnsTime)
    {
        MFCreateSample(out var sample);
        MFCreateMemoryBuffer((uint)Math.Max(1, data.Length), out var buffer);
        buffer.Lock(out var ptr, out _, out _);
        Marshal.Copy(data, 0, ptr, data.Length);
        buffer.Unlock();
        buffer.SetCurrentLength((uint)data.Length);
        sample.AddBuffer(buffer);
        if (hnsTime >= 0) sample.SetSampleTime(hnsTime);
        return sample;
    }

    /// Raw ReadSample: the reader writes its four out values through pointers into a caller-owned
    /// 24-byte buffer — DWORD actualStreamIndex @0, DWORD streamFlags @4, LONGLONG timestamp @8,
    /// IMFSample* @16 (the interface method takes raw IntPtrs; .NET out-param marshaling
    /// misbehaves on this object, see the interface comment). The returned `sample` pointer is
    /// owned by the caller: convert with GetObjectForIUnknown + Release (as the H.264 codecs do)
    /// or Release it directly.
    public static int ReadSampleRaw(IMFSourceReader reader, uint stream, uint control,
                                    out uint streamIndex, out uint flags, out long ts, out IntPtr sample)
    {
        var buf = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.WriteInt32(buf, 0, 0);
            Marshal.WriteInt32(buf, 4, 0);
            Marshal.WriteInt64(buf, 8, 0);
            Marshal.WriteIntPtr(buf, 16, IntPtr.Zero);
            int hr = reader.ReadSample(stream, control, buf, buf + 4, buf + 8, buf + 16);
            streamIndex = (uint)Marshal.ReadInt32(buf);
            flags = (uint)Marshal.ReadInt32(buf + 4);
            ts = Marshal.ReadInt64(buf + 8);
            sample = Marshal.ReadIntPtr(buf + 16);
            return hr;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// The payload bytes of a sample's contiguous buffer (decoded/copied into a fresh array).
    public static byte[]? SampleBytes(IMFSample sample)
    {
        try
        {
            if (sample.ConvertToContiguousBuffer(out var buffer) != S_OK) return null;
            buffer.Lock(out var ptr, out _, out var len);
            var outp = new byte[len];
            if (len > 0) Marshal.Copy(ptr, outp, 0, (int)len);
            buffer.Unlock();
            return outp;
        }
        catch { return null; }
    }

    /// Human-readable "subtype WxH@rate" for a video media type (diagnostics).
    public static string DescribeVideoType(IMFMediaType? t)
    {
        if (t == null) return "(null)";
        string sub = "?";
        try
        {
            if (t.GetGUID(MtSubtype, out var g) == S_OK)
            {
                if (g == VideoFormatNv12) sub = "NV12";
                else if (g == VideoFormatYuy2) sub = "YUY2";
                else if (g == VideoFormatIyuv) sub = "IYUV";
                else if (g == VideoFormatYv12) sub = "YV12";
                else if (g == VideoFormatH264) sub = "H264";
                else sub = g.ToString("N")[..8];
            }
        }
        catch { }
        uint w = 0, h = 0, fr = 0, fd = 1;
        try { MFGetAttributeSize(t, MtFrameSize, out w, out h); } catch { }
        try { MFGetAttributeRatio(t, MtFrameRate, out fr, out fd); } catch { }
        return $"{sub} {w}x{h}@{fr}/{fd}";
    }

    public static string? DeviceName(IMFActivate activate)
    {
        try
        {
            if (activate.GetAllocatedString(DevSourceFriendlyName, out var ptr, out _) == S_OK)
            {
                var name = Marshal.PtrToStringUni(ptr);
                CoTaskMemFree(ptr);
                return name;
            }
        }
        catch { }
        return null;
    }
}
