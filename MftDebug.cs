using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeScord;

// --mft: dump what the Media Foundation H.264 encoder/decoder MFTs actually offer, for debugging
// the codec bring-up. Prints CLSID creation results, the output/input types GetOutputAvailableType
// / GetInputAvailableType return (subtype + frame size + rate), and the Set*Type results.
static class MftDebug
{
    public static void Run()
    {
        Mf.EnsureStarted();
        // Camera device enumeration: MFEnumDeviceSources is exported by mf.dll (not mfplat.dll),
        // so this also verifies the DllImport fix that kept the camera button dead.
        try
        {
            var cams = CameraCapture.DeviceNames();
            Console.WriteLine($"== camera devices ({cams.Length}): {(cams.Length == 0 ? "(none)" : string.Join(", ", cams))} ==");
        }
        catch (Exception ex) { Console.WriteLine($"camera enumeration failed: {ex.GetType().Name}: {ex.Message}"); }
        CameraProbe();
        Console.WriteLine("== CameraCapture live (production class) =="); Console.Out.Flush();
        CameraCaptureLive();
        Console.WriteLine("== all video encoder MFTs ==");
        DumpCategory(Mf.CategoryVideoEncoder, "encoder");
        Console.WriteLine();
        Console.WriteLine("== all video decoder MFTs ==");
        DumpCategory(Mf.CategoryVideoDecoder, "decoder");
        Console.WriteLine();
        Console.WriteLine("== H264 encoder sweep ==");
        DumpEncoder();
        Console.WriteLine();
        Console.WriteLine("== H264 single-frame encode ==");
        SingleFrame();
        Console.WriteLine();
        Console.WriteLine("== screenshare production path (ScreenCapture -> RgbToJpeg/FromRgb -> H264Encoder -> PacketizeH264) ==");
        ScreenPath();
        Console.WriteLine();
        Console.WriteLine("== production thread shape (encoder created on a gateway-like bg thread, fed from a capture-like bg thread) ==");
        ProdShape();
        Console.WriteLine();
        Console.WriteLine("== H264 keyframe behavior ==");
        KeyframeTest();
        Mf.EnsureShutdown();
    }

    // Raw camera enumeration with every HRESULT logged: isolates whether MFStartup, attribute
    // creation, SetGUID, or the MFEnumDeviceSources call itself is the failure point. The
    // production path (CameraCapture.DeviceNames) swallows the error, so this prints it.
    static void CameraProbe()
    {
        Console.WriteLine("== camera HRESULT probe =="); Console.Out.Flush();
        try
        {
            Mf.EnsureThreadStarted();
            int hr0 = Mf.MFStartup(Mf.MF_VERSION, 0);
            Console.WriteLine($"  MFStartup 0x{hr0:X8}"); Console.Out.Flush();
            int hr1 = Mf.MFCreateAttributes(out var attrs, 2);
            Console.WriteLine($"  MFCreateAttributes 0x{hr1:X8}"); Console.Out.Flush();
            int hr2 = attrs.SetGUID(Mf.DevSourceType, Mf.DevSourceTypeVidCap);
            Console.WriteLine($"  SetGUID(DevSourceType, VidCap) 0x{hr2:X8}"); Console.Out.Flush();
            int hr3 = attrs.SetUINT32(Mf.DevSourceShareCapture, 1);
            Console.WriteLine($"  SetUINT32(ShareCapture, 1) 0x{hr3:X8}"); Console.Out.Flush();
            // Verify the attribute actually stored (round-trip), so a bad vtable slot is visible.
            int hr4 = attrs.GetGUID(Mf.DevSourceType, out var got);
            Console.WriteLine($"  GetGUID(DevSourceType) 0x{hr4:X8} match={got == Mf.DevSourceTypeVidCap}"); Console.Out.Flush();
            int hr5 = Mf.MFEnumDeviceSources(attrs, out var arr, out var count);
            Console.WriteLine($"  MFEnumDeviceSources 0x{hr5:X8} count={count}"); Console.Out.Flush();
            if (hr5 == Mf.S_OK)
            {
                for (uint i = 0; i < count; i++)
                {
                    var ptr = Marshal.ReadIntPtr(arr, (int)(i * IntPtr.Size));
                    var act = (Mf.IMFActivate)Marshal.GetObjectForIUnknown(ptr);
                    var name = Mf.DeviceName(act);
                    Console.WriteLine($"    [{i}] {name ?? "(no name)"}"); Console.Out.Flush();
                }
                Mf.CoTaskMemFree(arr);
            }
            // Open the FIRST device end-to-end exactly like CameraCapture.Start: activate the media
            // source, wrap it in a source reader, set the NV12 format, and read one sample. This
            // reproduces the production E_NOINTERFACE ("Unable to cast ... to interface type
            // 'IMFSourceReader'") so every step's HRESULT is visible.
            Console.WriteLine("  -- share-capture variant (as production) --"); Console.Out.Flush();
            OpenFirstDevice(attrs, shared: true);
            Console.WriteLine("  -- exclusive variant (no frame server) --"); Console.Out.Flush();
            Mf.MFCreateAttributes(out var attrs2, 2);
            attrs2.SetGUID(Mf.DevSourceType, Mf.DevSourceTypeVidCap);
            OpenFirstDevice(attrs2, shared: false);
        }
        catch (Exception e) { Console.WriteLine($"  CameraProbe EXC: {e.GetType().Name}: {e.Message}"); Console.Out.Flush(); }
    }

    static void OpenFirstDevice(Mf.IMFAttributes attrs, bool shared)
    {
        try
        {
            if (shared) attrs.SetUINT32(Mf.DevSourceShareCapture, 1);
            int hrE = Mf.MFEnumDeviceSources(attrs, out var arr, out var count);
            Console.WriteLine($"  MFEnumDeviceSources(shared={shared}) 0x{hrE:X8} count={count}"); Console.Out.Flush();
            if (hrE != Mf.S_OK || count == 0) return;
            var ptr = Marshal.ReadIntPtr(arr, 0);
            Mf.CoTaskMemFree(arr);
            Mf.IMFMediaSource src = null!;
            // Probe the source object itself BEFORE wrapping it in a reader: GetCharacteristics and
            // the presentation descriptor reveal whether the activated source is actually alive
            // (a dead/broken source makes every source-reader call return MF_E_INVALIDMEDIATYPE).
            {
                var act = (Mf.IMFActivate)Marshal.GetObjectForIUnknown(ptr);
                int hrA = act.ActivateObject(typeof(Mf.IMFMediaSource).GUID, out var srcPtr);
                Console.WriteLine($"  ActivateObject(IMFMediaSource) 0x{hrA:X8}"); Console.Out.Flush();
                if (hrA == Mf.S_OK)
                {
                    try { src = (Mf.IMFMediaSource)Marshal.GetObjectForIUnknown(srcPtr); Console.WriteLine($"  cast to IMFMediaSource OK"); }
                    catch (Exception e) { Console.WriteLine($"  cast to IMFMediaSource FAILED: {e.Message}"); }
                }
                if (src != null)
                {
                    int hrCh = src.GetCharacteristics(out var chars);
                    Console.WriteLine($"  src.GetCharacteristics 0x{hrCh:X8} chars=0x{chars:X8}"); Console.Out.Flush();
                    int hrPd = src.CreatePresentationDescriptor(out var pdPtr);
                    Console.WriteLine($"  src.CreatePresentationDescriptor 0x{hrPd:X8}"); Console.Out.Flush();
                    if (hrPd == Mf.S_OK && pdPtr != IntPtr.Zero)
                    {
                        try
                        {
                            var pd = (Mf.IMFPresentationDescriptor)Marshal.GetObjectForIUnknown(pdPtr);
                            int hrC2 = pd.GetStreamDescriptorCount(out var nStreams);
                            Console.WriteLine($"  pd.GetStreamDescriptorCount 0x{hrC2:X8} n={nStreams}"); Console.Out.Flush();
                            for (uint s = 0; s < nStreams; s++)
                            {
                                int hrS = pd.GetStreamDescriptorByIndex(s, out var selected, out var sdPtr);
                                Console.WriteLine($"  pd stream[{s}] selected={selected} 0x{hrS:X8}"); Console.Out.Flush();
                                if (hrS == Mf.S_OK && sdPtr != IntPtr.Zero)
                                {
                                    try
                                    {
                                        var sd = (Mf.IMFStreamDescriptor)Marshal.GetObjectForIUnknown(sdPtr);
                                        int hrH = sd.GetMediaTypeHandler(out var hPtr);
                                        Console.WriteLine($"    sd.GetMediaTypeHandler 0x{hrH:X8}"); Console.Out.Flush();
                                        if (hrH == Mf.S_OK && hPtr != IntPtr.Zero)
                                        {
                                            var h = (Mf.IMFMediaTypeHandler)Marshal.GetObjectForIUnknown(hPtr);
                                            int hrN = h.GetMediaTypeCount(out var nTypes);
                                            Console.WriteLine($"    handler.GetMediaTypeCount 0x{hrN:X8} n={nTypes}"); Console.Out.Flush();
                                            for (uint t = 0; t < nTypes && t < 4; t++)
                                            {
                                                int hrM = h.GetMediaTypeByIndex(t, out var mt);
                                                Console.WriteLine($"    type[{t}] 0x{hrM:X8} " + (hrM == Mf.S_OK ? Mf.DescribeVideoType(mt) : "")); Console.Out.Flush();
                                            }
                                        }
                                    }
                                    catch (Exception e) { Console.WriteLine($"    sd EXC: {e.Message}"); }
                                }
                            }
                        }
                        catch (Exception e) { Console.WriteLine($"  pd EXC: {e.GetType().Name}: {e.Message}"); }
                    }
                }
            }
            // Also try the one-call activation MFCreateDeviceSource with FRESH attributes (the
            // enumeration call above consumed the attribute store, which is why passing the same
            // attrs returns MF_E_ATTRIBUTENOTFOUND) — if THAT produces a working source, the
            // ActivateObject path is the issue.
            Mf.IMFMediaSource altSrc = null!;
            {
                Mf.MFCreateAttributes(out var attrs2, 2);
                attrs2.SetGUID(Mf.DevSourceType, Mf.DevSourceTypeVidCap);
                if (shared) attrs2.SetUINT32(Mf.DevSourceShareCapture, 1);
                int hrD2 = Mf.MFCreateDeviceSource(attrs2, out var alt);
                Console.WriteLine($"  MFCreateDeviceSource(fresh attrs) 0x{hrD2:X8} src={alt}"); Console.Out.Flush();
                if (hrD2 == Mf.S_OK)
                {
                    try { altSrc = alt; Console.WriteLine($"  alt cast OK"); }
                    catch (Exception e) { Console.WriteLine($"  cast alt FAILED: {e.Message}"); }
                }
            }
            if (src == null && altSrc != null) src = altSrc;
            // Rebuild the reader against a FRESH source if the first activation produced a bad one:
            // try MFCreateSourceReaderFromMediaSource with the alt source (a plain source, no
            // activate wrapper) to see whether the reader works on a non-activate-created source.
            if (altSrc != null)
            {
                int hrB2 = Mf.MFCreateSourceReaderFromMediaSource(altSrc, null!, out var reader2);
                Console.WriteLine($"  reader2(alt src) 0x{hrB2:X8}"); Console.Out.Flush();
                if (hrB2 == Mf.S_OK)
                {
                    int hrn2 = reader2.GetNativeMediaType(Mf.MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, out var nt2);
                    Console.WriteLine($"  reader2 GetNativeMediaType[0] 0x{hrn2:X8} " + (hrn2 == Mf.S_OK ? Mf.DescribeVideoType(nt2) : "")); Console.Out.Flush();
                    try { reader2.Flush(Mf.MF_SOURCE_READER_FIRST_VIDEO_STREAM); } catch { }
                    try { altSrc.Stop(); } catch { }
                    try { altSrc.Shutdown(); } catch { }
                }
            }
            int hrB = Mf.MFCreateSourceReaderFromMediaSource(src, null!, out var reader);
            Console.WriteLine($"  MFCreateSourceReaderFromMediaSource 0x{hrB:X8} reader={reader}"); Console.Out.Flush();
            if (hrB == Mf.S_OK)
            {
                try
                {
                    // Try BOTH the FIRST_VIDEO_STREAM sentinel AND an explicit index 0 — if the
                    // sentinel resolves wrong, index 0 still finds the stream.
                    foreach (var sidx in new uint[] { Mf.MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0 })
                    {
                        int hrSel = reader.GetStreamSelection(sidx, out var sel);
                        Console.WriteLine($"  GetStreamSelection(0x{sidx:X}) 0x{hrSel:X8} sel={sel}"); Console.Out.Flush();
                        int hrn = reader.GetNativeMediaType(sidx, 0, out var nt);
                        Console.WriteLine($"  GetNativeMediaType(0x{sidx:X},0) 0x{hrn:X8} " + (hrn == Mf.S_OK ? Mf.DescribeVideoType(nt) : "")); Console.Out.Flush();
                        int hrc = reader.GetCurrentMediaType(sidx, out var cur);
                        Console.WriteLine($"  GetCurrentMediaType(0x{sidx:X}) 0x{hrc:X8} " + (hrc == Mf.S_OK ? Mf.DescribeVideoType(cur) : "")); Console.Out.Flush();
                    }
                    var want = Mf.MakeVideoType(Mf.VideoFormatNv12, 640, 360, 15);
                    int hrC = reader.SetCurrentMediaType(0, IntPtr.Zero, want);
                    Console.WriteLine($"  SetCurrentMediaType(NV12 640x360, stream 0) 0x{hrC:X8}"); Console.Out.Flush();
                    int hrD = Mf.ReadSampleRaw(reader, 0, 0, out var actIdx, out var flags, out var ts, out var samplePtr);
                    Console.WriteLine($"  ReadSample(stream 0) 0x{hrD:X8} actualIdx={actIdx} flags=0x{flags:X} ts={ts} sample=0x{samplePtr:X}"); Console.Out.Flush();
                    reader.GetCurrentMediaType(0, out var cur2);
                    Console.WriteLine($"  after read current: " + Mf.DescribeVideoType(cur2)); Console.Out.Flush();
                    if (hrD == Mf.S_OK && samplePtr != IntPtr.Zero)
                    {
                        var samp = (Mf.IMFSample)Marshal.GetObjectForIUnknown(samplePtr);
                        Marshal.Release(samplePtr);
                        var bytes = Mf.SampleBytes(samp);
                        Console.WriteLine($"  SampleBytes len={bytes?.Length ?? -1}"); Console.Out.Flush();
                        try { Marshal.ReleaseComObject(samp); } catch { }
                    }
                }
                catch (Exception e) { Console.WriteLine($"  reader use EXC: {e.GetType().Name}: {e.Message}"); Console.Out.Flush(); }
                try { reader.Flush(Mf.MF_SOURCE_READER_FIRST_VIDEO_STREAM); } catch { }
                try { src.Stop(); } catch { }
                try { src.Shutdown(); } catch { }
            }
        }
        catch (Exception e) { Console.WriteLine($"  OpenFirstDevice EXC: {e.GetType().Name}: {e.Message}"); Console.Out.Flush(); }
    }

    // Instantiates the production CameraCapture class (the same code the camera button uses):
    // open on the capture thread, read a few frames, verify they arrive at 640x360 NV12.
    static void CameraCaptureLive()
    {
        try
        {
            var cam = new CameraCapture(640, 360, 15);
            int got = 0;
            var stop = new System.Diagnostics.Stopwatch();
            cam.Frame += (nv12, w, h) =>
            {
                if (got < 3) Console.WriteLine($"  frame[{got}] {w}x{h} nv12={nv12.Length}B");
                got++;
            };
            bool ok = cam.Start();
            Console.WriteLine($"  Start()={ok} device={cam.DeviceName}"); Console.Out.Flush();
            if (ok)
            {
                stop.Start();
                while (got < 3 && stop.ElapsedMilliseconds < 8000) Thread.Sleep(50);
                Console.WriteLine($"  frames received in {stop.ElapsedMilliseconds}ms"); Console.Out.Flush();
            }
            cam.Dispose();
        }
        catch (Exception e) { Console.WriteLine($"  CameraCaptureLive EXC: {e.GetType().Name}: {e.Message}"); Console.Out.Flush(); }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int AddRefDel(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int ReleaseDel(IntPtr self);

    // Drives the EXACT production screenshare pipeline from Net/VoiceClient.OnRgbFrame, to reproduce
    // the "unexpected parameters" crash: a real ScreenCapture feed through RgbToJpeg (Bitmap from
    // an unpinned scan0!), FromRgb, the production H264Encoder class, and the H.264 packetizer.
    static void ScreenPath()
    {
        try
        {
            using var enc = new H264Encoder(640, 360, 15, 900_000);
            Console.WriteLine($"  encoder Ready={enc.Ready} err={enc.Error}");

            // (a) Same-thread control: the identical OnRgbFrame pipeline on THIS thread with a
            // synthetic RGB frame. If this works, the pipeline math is fine and the AV is
            // about the cross-thread MFT call.
            // (a2) Same data, but encoded from a plain Task.Run background thread — is the AV
            // any-background-thread, or specific to the GDI+/screen-capture thread?
            try
            {
                var synth = new byte[640 * 360 * 3];
                for (int i = 0; i < synth.Length; i++) synth[i] = (byte)(i * 7);
                var j0 = Nv12.RgbToJpeg(synth, 640, 360, 45);
                var n0 = Nv12.FromRgb(synth, 640, 360, 640 * 3);
                int a0 = 0, p0 = 0;
                foreach (var au in enc.Encode(n0)) { a0++; p0 += VideoRtp.PacketizeH264(au).Count; }
                Console.WriteLine($"  same-thread: jpeg={j0?.Length ?? -1} aus={a0} packets={p0}");
                Console.Out.Flush();
            }
            catch (Exception e)
            {
                Console.WriteLine($"  same-thread: EXCEPTION {e.GetType().Name}: {e.Message}");
                Console.Out.Flush();
            }
            try
            {
                var synth2 = new byte[640 * 360 * 3];
                for (int i = 0; i < synth2.Length; i++) synth2[i] = (byte)(255 - i * 3);
                var n1 = Nv12.FromRgb(synth2, 640, 360, 640 * 3);
                int a1 = 0, p1 = 0;
                var t = Task.Run(() =>
                {
                    foreach (var au in enc.Encode(n1)) { a1++; p1 += VideoRtp.PacketizeH264(au).Count; }
                });
                t.Wait(10000);
                Console.WriteLine($"  task-thread: aus={a1} packets={p1} faulted={t.IsFaulted} {t.Exception?.GetBaseException()?.Message}");
                Console.Out.Flush();
            }
            catch (Exception e)
            {
                Console.WriteLine($"  task-thread: EXCEPTION {e.GetType().Name}: {e.Message}");
                Console.Out.Flush();
            }
            if (!enc.Ready)
            {
                Console.WriteLine("  (skipping capture feed: encoder probe failed)");
            }
            else
            {
                using var cap = new ScreenCapture(640, 360, 15, 45);
                int got = 0, aus = 0, pkts = 0;
                long firstJpeg = 0, firstEncode = 0;
                var sw = Stopwatch.StartNew();
                cap.Frame += (rgb, w, h) =>
                {
                    try
                    {
                        got++;
                        var jpeg = Nv12.RgbToJpeg(rgb, w, h, 45);
                        if (jpeg == null) { Console.WriteLine($"  frame {got}: RgbToJpeg -> null!"); return; }
                        if (firstJpeg == 0) firstJpeg = sw.ElapsedMilliseconds;
                        var nv12 = Nv12.FromRgb(rgb, w, h, w * 3);
                        foreach (var au in enc.Encode(nv12))
                        {
                            aus++;
                            pkts += VideoRtp.PacketizeH264(au).Count;
                        }
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"  frame {got}: EXCEPTION {e.GetType().Name}: {e.Message}");
                    }
                };
                if (!cap.Start()) { Console.WriteLine("  ScreenCapture.Start() failed"); return; }
                Thread.Sleep(3000);
                cap.Stop();
                Console.WriteLine($"  frames={got} aus={aus} packets={pkts} firstJpegMs={firstJpeg} firstEncodeMs={firstEncode}");
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"  ScreenPath crashed: {e.GetType().Name}: {e.Message}");
            Console.WriteLine(e.StackTrace);
        }
    }

    // Mirrors the live call exactly: VoiceClient creates the encoder on the gateway thread (a
    // background Task.Run here), then the screen capture thread (another Task.Run) feeds frames.
    // The encoder pins all MFT work to its own codec thread, so creation/caller threads should be
    // irrelevant — this test proves it and would have caught the "previews but sends nothing" bug.
    static void ProdShape()
    {
        try
        {
            H264Encoder? enc = null;
            Exception? createErr = null;
            var create = Task.Run(() =>
            {
                try { enc = new H264Encoder(640, 360, 15, 900_000); }
                catch (Exception e) { createErr = e; }
            });
            create.Wait(15000);
            if (createErr != null) { Console.WriteLine($"  create on bg thread FAILED: {createErr.Message}"); return; }
            if (enc == null) { Console.WriteLine("  create on bg thread TIMED OUT"); return; }
            Console.WriteLine($"  encoder Ready={enc.Ready} err={enc.Error}");
            if (!enc.Ready) return;
            int aus = 0, pkts = 0;
            var feed = Task.Run(() =>
            {
                var nv = new byte[640 * 360 * 3 / 2];
                for (int f = 0; f < 45; f++)
                {
                    for (int i = 0; i < nv.Length; i++) nv[i] = (byte)((i + f * 13) & 0xFF);
                    foreach (var au in enc!.Encode(nv)) { aus++; pkts += VideoRtp.PacketizeH264(au).Count; }
                }
            });
            feed.Wait(20000);
            Console.WriteLine($"  bg-created encoder fed from bg thread: aus={aus} packets={pkts} faulted={feed.IsFaulted}");
            enc.Dispose();
        }
        catch (Exception e) { Console.WriteLine($"  ProdShape crashed: {e.GetType().Name}: {e.Message}"); }
    }

    // Direct RCW calls — the paths that AV'd / mis-dispatched when the interfaces inherited
    // IMFAttributes. With the flattened declarations these must all return S_OK.
    static void ProbeMakeSample()
    {
        try
        {
            Mf.CoInitializeEx(IntPtr.Zero, 0);
            Mf.MFCreateSample(out var s); Console.WriteLine("  create sample ok"); Console.Out.Flush();
            uint flags = 999; int h1 = s.GetSampleFlags(out flags);
            Console.WriteLine($"  RCW GetSampleFlags = 0x{h1:X8} flags={flags}"); Console.Out.Flush();
            int h2 = s.SetSampleTime(12345);
            Console.WriteLine($"  RCW SetSampleTime = 0x{h2:X8}"); Console.Out.Flush();
            Mf.MFCreateMemoryBuffer(4096, out var b);
            int h3 = b.Lock(out var ptr, out _, out _);
            b.Unlock();
            int h4 = b.SetCurrentLength(16);
            Console.WriteLine($"  RCW buffer lock=0x{h3:X8} setlen=0x{h4:X8}"); Console.Out.Flush();
            int h5 = s.AddBuffer(b);
            Console.WriteLine($"  RCW AddBuffer = 0x{h5:X8}"); Console.Out.Flush();
            uint cnt = 0; int h6 = s.GetBufferCount(out cnt);
            Console.WriteLine($"  RCW GetBufferCount = 0x{h6:X8} = {cnt}"); Console.Out.Flush();
            Mf.MFCreateMediaType(out var mt);
            int h7 = mt.GetMajorType(out var maj);
            Console.WriteLine($"  RCW mediaType GetMajorType = 0x{h7:X8} {maj}"); Console.Out.Flush();
            int h8 = mt.SetGUID(Mf.MtSubtype, Mf.VideoFormatH264);
            int h9 = mt.GetGUID(Mf.MtSubtype, out var sub);
            Console.WriteLine($"  RCW mediaType set/get subtype = 0x{h8:X8}/0x{h9:X8} ok={sub == Mf.VideoFormatH264}");
            Console.Out.Flush();
        }
        catch (Exception e) { Console.WriteLine($"  EXC {e}"); Console.Out.Flush(); }
    }

    // Drive the encoder MFT directly with every HRESULT logged — isolates whether ProcessInput
    // or ProcessOutput is the silent failure in H264Encoder. Each recipe gets a FRESH encoder
    // instance (MFT state after a failed attempt is not trustworthy).
    static void DriveRaw()
    {
        Console.WriteLine($"  OUT_BUFFER size={Marshal.SizeOf<Mf.MFT_OUTPUT_DATA_BUFFER>()}"); Console.Out.Flush();
        var recipes = new (string name, bool profile, bool level, bool inputFirst, int inSub)[]
        {
            ("NV12 (current)", true, false, false, 0),
            ("IYUV", true, false, false, 1),
            ("YUY2", true, false, false, 2),
            ("YV12", true, false, false, 3),
            ("IYUV+profile77+level30", true, true, false, 1),
        };
        // Also try NOT providing the output sample (pSample = NULL) for the NV12 recipe — if the
        // MFT actually wants to allocate its own output, our provided sample would be the blocker.
        foreach (var (name, profile, level, inputFirst, inSub) in recipes.Take(1))
        {
            try
            {
                Mf.CoCreateInstance(Mf.ClsidH264Encoder, IntPtr.Zero, Mf.CLSCTX_INPROC_SERVER,
                                    typeof(Mf.IMFTransform).GUID, out var obj2);
                var mft2 = (Mf.IMFTransform)Marshal.GetObjectForIUnknown(obj2);
                mft2.GetOutputAvailableType(0, 0, out var ot);
                ot.SetGUID(Mf.MtMajorType, Mf.MediaTypeVideo);
                ot.SetGUID(Mf.MtSubtype, Mf.VideoFormatH264);
                Mf.MFSetAttributeSize(ot, Mf.MtFrameSize, 640, 360);
                Mf.MFSetAttributeRatio(ot, Mf.MtFrameRate, 15, 1);
                ot.SetUINT32(Mf.MtAvgBitrate, 900000);
                ot.SetUINT32(Mf.MtMpeg2Profile, 77);
                mft2.SetOutputType(0, ot, 0);
                mft2.SetInputType(0, Mf.MakeVideoType(Mf.VideoFormatNv12, 640, 360, 15), 0);
                mft2.ProcessMessage(Mf.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero);
                mft2.ProcessMessage(Mf.MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero);
                Console.WriteLine("==== NV12 (self-allocated output, pSample=NULL) ===="); Console.Out.Flush();
                var fr = new byte[640 * 360 * 3 / 2];
                long t2 = 10_000_000;
                for (int f = 0; f < 20; f++)
                {
                    var s = Mf.MakeSample(fr, t2); s.SetSampleDuration(666667L); t2 += 666667;
                    Console.WriteLine($"  frame {f} ProcessInput: 0x{mft2.ProcessInput(0, s, 0):X8}"); Console.Out.Flush();
                    for (int p = 0; p < 8; p++)
                    {
                        int oh = Mf.ProcessOutputOne(mft2, out var ps, out _, provideSample: false);
                        string extra = oh == Mf.S_OK ? $" sample={ps}" : "";
                        Console.WriteLine($"    ProcessOutput[{p}]: 0x{oh:X8}{extra}"); Console.Out.Flush();
                        if (oh == Mf.MF_E_TRANSFORM_NEED_MORE_INPUT) break;
                        if (oh != Mf.S_OK) break;
                        if (ps != IntPtr.Zero) Marshal.Release(ps);
                    }
                }
                Marshal.Release(obj2);
            }
            catch (Exception ex) { Console.WriteLine($"  self-alloc EXC: {ex.Message}"); Console.Out.Flush(); }
        }
        foreach (var (name, profile, level, inputFirst, inSub) in recipes)
        {
            var inSubGuid = inSub switch { 1 => Mf.VideoFormatIyuv, 2 => Mf.VideoFormatYuy2, 3 => Mf.VideoFormatYv12, _ => Mf.VideoFormatNv12 };
            try
            {
                Mf.CoCreateInstance(Mf.ClsidH264Encoder, IntPtr.Zero, Mf.CLSCTX_INPROC_SERVER,
                                    typeof(Mf.IMFTransform).GUID, out var obj);
                var mft = (Mf.IMFTransform)Marshal.GetObjectForIUnknown(obj);
                Console.WriteLine($"==== {name} ===="); Console.Out.Flush();
                // Async MFT? An async encoder never answers a sync ProcessOutput poll with data —
                // it signals ME_MFT_OUTPUT_DATA_AVAILABLE via its media event generator instead.
                try
                {
                    if (mft.GetAttributes(out var attrs) == 0)
                    {
                        int asyncOk = attrs.GetUINT32(Mf.MfTransformAsync, out var asyncFlag);
                        Console.WriteLine($"  async attr: hr=0x{asyncOk:X8} flag={asyncFlag}");
                        Console.Out.Flush();
                    }
                    else Console.WriteLine("  async attr: GetAttributes failed");
                }
                catch (Exception ae) { Console.WriteLine($"  async attr EXC: {ae.Message}"); Console.Out.Flush(); }
                try
                {
                    bool evtGen = mft is Mf.IMFMediaEventGenerator;
                    Console.WriteLine($"  is IMFMediaEventGenerator: {evtGen}"); Console.Out.Flush();
                }
                catch { }
                mft.GetOutputStreamInfo(0, out var osi);
                Console.WriteLine($"  out stream info flags=0x{osi.dwFlags:X}"); Console.Out.Flush();

                // Log what the encoder's own templates look like.
                for (int idx = 0; idx < 2; idx++)
                {
                    int h1 = mft.GetOutputAvailableType(0, (uint)idx, out var at);
                    string info = h1 != 0 ? "" : $" subtype={Subtype(at)} br?={TryGetBr(at)}";
                    Console.WriteLine($"  avail out[{idx}]: 0x{h1:X8}{info}"); Console.Out.Flush();
                }

                Mf.IMFMediaType outType()
                {
                    mft.GetOutputAvailableType(0, 0, out var ot);
                    ot.SetGUID(Mf.MtMajorType, Mf.MediaTypeVideo);
                    ot.SetGUID(Mf.MtSubtype, Mf.VideoFormatH264);
                    Mf.MFSetAttributeSize(ot, Mf.MtFrameSize, 640, 360);
                    Mf.MFSetAttributeRatio(ot, Mf.MtFrameRate, 15, 1);
                    Mf.MFSetAttributeRatio(ot, Mf.MtPixelAspectRatio, 1, 1);
                    ot.SetUINT32(Mf.MtInterlaceMode, 2);
                    ot.SetUINT32(Mf.MtAvgBitrate, 900000);
                    if (profile) ot.SetUINT32(Mf.MtMpeg2Profile, profile ? 77u : 66u);
                    if (level) ot.SetUINT32(Mf.MtMpeg2Level, 30u);
                    return ot;
                }

                mft.GetStreamCount(out var nIn, out var nOut);
                Console.WriteLine($"  streams: in={nIn} out={nOut}"); Console.Out.Flush();
                if (nIn <= 3 && nOut <= 3)
                {
                    var ids = new uint[nIn + nOut];
                    mft.GetStreamIDs(nIn, ids, nOut, ids.AsSpan((int)nIn).ToArray());
                    Console.WriteLine($"  stream ids: in=[{string.Join(",", ids.Take((int)nIn))}] out=[{string.Join(",", ids.Skip((int)nIn))}]"); Console.Out.Flush();
                }
                int so = mft.SetOutputType(0, outType(), 0);
                Console.WriteLine($"  set output: 0x{so:X8}"); Console.Out.Flush();

                // NOW (after output set) the encoder reveals its input templates — dump them.
                Mf.IMFMediaType? nativeIn = null;
                for (uint idx = 0; idx < 4; idx++)
                {
                    int inAvail = mft.GetInputAvailableType(0, idx, out var at);
                    string info = inAvail != 0 ? "" : $" subtype={Subtype(at)} {TryGetSize(at)}";
                    Console.WriteLine($"  avail in[{idx}] after out-type: 0x{inAvail:X8}{info}"); Console.Out.Flush();
                    // Use the NV12 template (index 2) when the recipe wants NV12, else the first.
                    int wantIdx = inSub == 0 ? 2 : (inSub == 1 ? 0 : inSub == 2 ? 3 : 1);
                    if (inAvail == 0 && nativeIn == null && idx == wantIdx) { nativeIn = at; DumpAttrs(at, "  native in template"); }
                    if (inAvail != 0) break;
                }
                // Complete the encoder's OWN input template instead of a hand-made one.
                if (nativeIn != null)
                {
                    nativeIn.SetGUID(Mf.MtMajorType, Mf.MediaTypeVideo);
                    nativeIn.SetGUID(Mf.MtSubtype, inSubGuid);
                    Mf.MFSetAttributeSize(nativeIn, Mf.MtFrameSize, 640, 360);
                    Mf.MFSetAttributeRatio(nativeIn, Mf.MtFrameRate, 15, 1);
                    Mf.MFSetAttributeRatio(nativeIn, Mf.MtPixelAspectRatio, 1, 1);
                    nativeIn.SetUINT32(Mf.MtInterlaceMode, 2);
                }

                Mf.IMFMediaType inType = nativeIn ?? Mf.MakeVideoType(inSubGuid, 640, 360, 15);
                int si2 = mft.SetInputType(0, inType, 0);
                Console.WriteLine($"  set input: 0x{si2:X8}"); Console.Out.Flush();
                if (so != 0 || si2 != 0) { Console.WriteLine("  SKIP (types rejected)"); Console.Out.Flush(); Marshal.Release(obj); continue; }
                // Stream info: the minimum input buffer size the encoder will actually process.
                // A sample buffer smaller than cbSize is silently dropped even though ProcessInput
                // returns S_OK — the classic "accepted but never emits" trap.
                if (mft.GetInputStreamInfo(0, out var isi) == 0)
                    Console.WriteLine($"  in stream info: cbSize={isi.cbSize} align={isi.cbAlignment} lookahead={isi.cbMaxLookahead} flags=0x{isi.dwFlags:X}");
                else Console.WriteLine("  in stream info: GetInputStreamInfo failed");
                Console.Out.Flush();
                mft.ProcessMessage(Mf.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero);
                mft.ProcessMessage(Mf.MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero);
                // If the MFT does not set MFT_OUTPUT_STREAM_PROVIDES_SAMPLES (0x20), the caller
                // must hand it an output sample — NULL pSample returns E_INVALIDARG.
                bool provide = (osi.dwFlags & 0x20) == 0;
                Console.WriteLine($"  provideSample={provide}"); Console.Out.Flush();

                // Some encoders refuse ProcessInput until ProcessOutput has been exercised once.
                Console.WriteLine("  pre-drain with provided sample..."); Console.Out.Flush();
                TryDrain(mft, "pre-drain", provide);

                // Build a sample sized for the claimed subtype (YUY2 is 2Bpp packed; the rest are
                // 4:2:0 planar at 1.5Bpp). The encoder validates the buffer length against the type.
                int bufLen = inSub == 2 ? 640 * 360 * 2 : 640 * 360 * 3 / 2;
                var frame = new byte[bufLen];
                // Feed a CONTINUOUS stream (up to 60 frames) with output polled after each one —
                // a frame-based encoder only emits after its internal look-ahead fills, so a
                // one-frame test says nothing about whether the drive pattern works. Start the
                // timestamp at 1s (some encoders treat a 0 timestamp oddly).
                bool accepted = false, gotOut = false;
                long ts = 10_000_000;
                for (int f = 0; f < 60; f++)
                {
                    var sample = Mf.MakeSample(frame, ts);
                    sample.SetSampleDuration(666667L);
                    ts += 666667;
                    int ih = mft.ProcessInput(0, sample, 0);
                    Console.WriteLine($"  frame {f} ProcessInput: 0x{ih:X8}"); Console.Out.Flush();
                    if (ih != Mf.S_OK)
                    {
                        if (f == 0) { TryDrain(mft, "post-fail", provide); break; }
                        continue;                       // a rejected frame must not stop the stream
                    }
                    accepted = true;
                    // Drain everything the encoder has produced so far.
                    for (int p = 0; p < 8; p++)
                    {
                        int oh = Mf.ProcessOutputOne(mft, out var ps, out _, provide);
                        string extra = oh == Mf.S_OK ? $" sample={ps}" : "";
                        Console.WriteLine($"    ProcessOutput[{p}]: 0x{oh:X8}{extra}"); Console.Out.Flush();
                        if (oh == Mf.MF_E_TRANSFORM_NEED_MORE_INPUT) break;
                        if (oh == Mf.MF_E_TRANSFORM_STREAM_CHANGE)
                        {
                            mft.GetOutputCurrentType(0, out var mt);
                            mft.SetOutputType(0, mt, 0);
                            Console.WriteLine($"    re-applied output type after STREAM_CHANGE"); Console.Out.Flush();
                        }
                        if (oh != Mf.S_OK) break;
                        gotOut = true;
                        if (ps != IntPtr.Zero) Marshal.Release(ps);
                    }
                }
                if (accepted)
                {
                    Console.WriteLine("  drain..."); Console.Out.Flush();
                    mft.ProcessMessage(Mf.MFT_MESSAGE_COMMAND_DRAIN, IntPtr.Zero);
                    TryDrain(mft, "drain", provide);
                }
                Console.WriteLine($"  RESULT: accepted={accepted} output={gotOut}"); Console.Out.Flush();
                Marshal.Release(obj);
            }
            catch (Exception e) { Console.WriteLine($"  EXC: {e.Message}"); Console.Out.Flush(); }
        }
    }

    static void DumpAttrs(Mf.IMFMediaType t, string label)
    {
        try
        {
            t.GetCount(out var n);
            for (uint i = 0; i < n; i++)
            {
                t.GetItemByIndex(i, out var key, IntPtr.Zero);
                t.GetItemType(key, out var type);
                string val = type switch
                {
                    19 => "UINT32",   // MF_ATTRIBUTE_UINT32
                    20 => "UINT64",   // MF_ATTRIBUTE_UINT64
                    21 => "DOUBLE",
                    22 => "GUID",
                    23 => "STRING",
                    24 => "BLOB",
                    _ => type.ToString(),
                };
                Console.WriteLine($"{label} [{i}] {Short(key)}");
            }
            Console.Out.Flush();
        }
        catch { }
    }

    static string Short(Guid g) => g.ToString()[..8];

    static string TryGetBr(Mf.IMFMediaType t)
    {
        if (t.GetUINT32(Mf.MtAvgBitrate, out var br) == 0) return $"br={br}";
        return "no-br";
    }

    static string TryGetSize(Mf.IMFMediaType t)
    {
        if (Mf.MFGetAttributeSize(t, Mf.MtFrameSize, out var w, out var h) == 0) return $"{w}x{h}";
        return "no-size";
    }

    static void TryDrain(Mf.IMFTransform mft, string who, bool provide = false)
    {
        for (int p = 0; p < 8; p++)
        {
            int oh = Mf.ProcessOutputOne(mft, out var ps, out _, provide);
            Console.WriteLine($"    {who} ProcessOutput[{p}]: 0x{oh:X8}"); Console.Out.Flush();
            if (oh != Mf.S_OK) break;
            if (ps != IntPtr.Zero) Marshal.Release(ps);
        }
    }

    // Decisive environmental test: encode via the SinkWriter (it drives the encoder MFT itself).
    // If this works, the MS H.264 encoder is healthy and the raw MFT drive pattern has the bug.
    static void SinkTest()
    {
        try
        {
            string path = "C:\\tmp\\swtest.mp4";
            int hr = Mf.MFCreateSinkWriterFromURL(path, IntPtr.Zero, IntPtr.Zero, out var sw);
            Console.WriteLine($"  sinkwriter create: 0x{hr:X8}"); Console.Out.Flush();
            if (hr != 0) return;

            var outType = Mf.MakeVideoType(Mf.VideoFormatH264, 640, 360, 15);
            outType.SetUINT32(Mf.MtAvgBitrate, 900000);
            outType.SetUINT32(Mf.MtMpeg2Profile, 77);
            uint idx;
            hr = sw.AddStream(outType, out idx);
            Console.WriteLine($"  addstream: 0x{hr:X8} idx={idx}"); Console.Out.Flush();
            if (hr != 0) return;

            var inType = Mf.MakeVideoType(Mf.VideoFormatNv12, 640, 360, 15);
            hr = sw.SetInputMediaType(idx, inType, null!);
            Console.WriteLine($"  setinputmediatype: 0x{hr:X8}"); Console.Out.Flush();
            if (hr != 0) return;

            hr = sw.BeginWriting();
            Console.WriteLine($"  beginwriting: 0x{hr:X8}"); Console.Out.Flush();
            var nv12 = MakeGradientNv12(640, 360, 0);
            for (int f = 0; f < 3; f++)
            {
                var s = Mf.MakeSample(nv12, f * 666667L);
                s.SetSampleDuration(666667L);
                hr = sw.WriteSample(idx, s);
                Console.WriteLine($"  writesample[{f}]: 0x{hr:X8}"); Console.Out.Flush();
                if (hr != 0) break;
            }
            hr = sw.Finalize_();
            Console.WriteLine($"  finalize: 0x{hr:X8}"); Console.Out.Flush();
        }
        catch (Exception e) { Console.WriteLine($"  SinkTest EXC: {e.Message}"); Console.Out.Flush(); }
    }

    // Pull raw Annex-B access units out of an MP4 (mdat is AVCC: 4-byte BE length + NAL; each
    // sample becomes one AU). Handles 64-bit box sizes (size field == 1 -> 8-byte size follows).
    // Returns null on parse failure.
    static List<byte[]>? ExtractAus(string path)
    {
        var file = File.ReadAllBytes(path);
        long pos = 0;
        long mdatStart = -1, mdatLen = 0;
        while (pos + 8 <= file.Length)
        {
            long size = ((long)file[(int)pos] << 24) | ((long)file[(int)pos + 1] << 16) | ((long)file[(int)pos + 2] << 8) | file[(int)pos + 3];
            string type = new string(new[] { (char)file[(int)pos + 4], (char)file[(int)pos + 5], (char)file[(int)pos + 6], (char)file[(int)pos + 7] });
            int hdr = 8;
            if (size == 1) { size = (long)(((ulong)file[(int)pos + 8] << 56) | ((ulong)file[(int)pos + 9] << 48) | ((ulong)file[(int)pos + 10] << 40) | ((ulong)file[(int)pos + 11] << 32) | ((ulong)file[(int)pos + 12] << 24) | ((ulong)file[(int)pos + 13] << 16) | ((ulong)file[(int)pos + 14] << 8) | file[(int)pos + 15]); hdr = 16; }
            if (type == "mdat") { mdatStart = pos + hdr; mdatLen = (size == 0 || size == 1) ? file.Length - mdatStart : size - hdr; break; }
            pos += size == 0 ? file.Length - pos : size;
        }
        if (mdatStart < 0) return null;
        var aus = new List<byte[]>();
        long end = mdatStart + mdatLen;
        while (mdatStart + 4 <= end)
        {
            long len = ((long)file[(int)mdatStart] << 24) | ((long)file[(int)mdatStart + 1] << 16) | ((long)file[(int)mdatStart + 2] << 8) | file[(int)mdatStart + 3];
            mdatStart += 4;
            if (len == 0 || mdatStart + len > end) break;
            var au = new List<byte>();
            long npos = mdatStart, nend = mdatStart + len;
            while (npos + 4 <= nend)
            {
                long nlen = ((long)file[(int)npos] << 24) | ((long)file[(int)npos + 1] << 16) | ((long)file[(int)npos + 2] << 8) | file[(int)npos + 3];
                npos += 4;
                au.Add(0); au.Add(0); au.Add(0); au.Add(1);
                for (long i = 0; i < nlen && npos + i < nend; i++) au.Add(file[(int)(npos + i)]);
                npos += nlen;
            }
            aus.Add(au.ToArray());
            mdatStart += len;
        }
        return aus;
    }

    // Does the encoder emit SPS/PPS/IDR (decodable keyframes)? The stream above only produced
    // AUD + P-slices — no parameter sets, so a remote decoder can never start. Sweep the config
    // axes that differ between the working SinkWriter drive (profile 77, timestamps from 0) and
    // the broken direct drive (profile 66 + level 31 + 1s clock) with a MOVING gradient (a static
    // frame gets dropped, which earlier made every config look broken).
    static void KeyframeTest()
    {
        byte[] Moving(int phase)
        {
            var f = new byte[640 * 360 * 3 / 2];
            int stride = 640;
            for (int y = 0; y < 360; y++)
                for (int x = 0; x < 640; x++)
                    f[y * stride + x] = (byte)((x * 3 + y * 5 + phase * 7) & 0xFF);
            for (int y = 0; y < 180; y++)
                for (int x = 0; x < 320; x++) { int o = stride * 360 + y * stride + x * 2; f[o] = 128; f[o + 1] = 128; }
            return f;
        }
        string Run(string name, uint profile, bool level, long ts0, int frames)
        {
            var sb = new StringBuilder(name + ": ");
            Mf.CoCreateInstance(Mf.ClsidH264Encoder, IntPtr.Zero, Mf.CLSCTX_INPROC_SERVER,
                                typeof(Mf.IMFTransform).GUID, out var obj);
            var mft = (Mf.IMFTransform)Marshal.GetObjectForIUnknown(obj);
            mft.GetOutputAvailableType(0, 0, out var ot);
            ot.SetGUID(Mf.MtMajorType, Mf.MediaTypeVideo);
            ot.SetGUID(Mf.MtSubtype, Mf.VideoFormatH264);
            Mf.MFSetAttributeSize(ot, Mf.MtFrameSize, 640, 360);
            Mf.MFSetAttributeRatio(ot, Mf.MtFrameRate, 15, 1);
            Mf.MFSetAttributeRatio(ot, Mf.MtPixelAspectRatio, 1, 1);
            ot.SetUINT32(Mf.MtInterlaceMode, 2);
            ot.SetUINT32(Mf.MtAvgBitrate, 900000);
            ot.SetUINT32(Mf.MtMpeg2Profile, profile);
            if (level) ot.SetUINT32(Mf.MtMpeg2Level, 31);
            int so = mft.SetOutputType(0, ot, 0);
            int si = mft.SetInputType(0, Mf.MakeVideoType(Mf.VideoFormatNv12, 640, 360, 15), 0);
            mft.ProcessMessage(Mf.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero);
            mft.ProcessMessage(Mf.MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero);
            mft.GetOutputStreamInfo(0, out var osi);
            bool provide = (osi.dwFlags & 0x20) == 0;
            Mf.ProcessOutputOne(mft, out var prime, out _, provide);
            if (prime != IntPtr.Zero) Marshal.Release(prime);
            sb.Append($"set=0x{so:X8}/{si:X8} ts0={ts0} ");
            long ts = ts0;
            for (int i = 0; i < frames; i++)
            {
                var s = Mf.MakeSample(Moving(i), ts);
                s.SetSampleDuration(666_667L);
                ts += 666_667;
                if (mft.ProcessInput(0, s, 0) != Mf.S_OK) { sb.Append("INFAIL "); continue; }
                for (int p = 0; p < 200; p++)   // mirror production Drain
                {
                    int hr = Mf.ProcessOutputOne(mft, out var ps, out _, provide);
                    if (hr != Mf.S_OK) break;
                    if (ps != IntPtr.Zero)
                    {
                        var samp = (Mf.IMFSample)Marshal.GetObjectForIUnknown(ps);
                        var b = Mf.SampleBytes(samp);
                        if (b != null && b.Length > 0)
                            sb.Append($"[{string.Join("-", VideoRtp.SplitNals(b).Select(n => n[0] & 0x1F))}]".Replace("-9", ""));
                        Marshal.Release(ps);
                    }
                }
            }
            Marshal.Release(obj);
            return sb.ToString();
        }
        Console.WriteLine($"  {Run("main77 ts0", 77, false, 0, 5)}"); Console.Out.Flush();
        Console.WriteLine($"  {Run("main77 ts1s", 77, false, 10_000_000, 5)}"); Console.Out.Flush();
        Console.WriteLine($"  {Run("base66+31 ts0", 66, true, 0, 5)}"); Console.Out.Flush();
        Console.WriteLine($"  {Run("base66+31 ts1s", 66, true, 10_000_000, 5)}"); Console.Out.Flush();
        Console.WriteLine($"  {Run("base66 ts0", 66, false, 0, 5)}"); Console.Out.Flush();
        Console.WriteLine("  force-keyframe test:"); Console.Out.Flush();
        try
        {
            Mf.CoCreateInstance(Mf.ClsidH264Encoder, IntPtr.Zero, Mf.CLSCTX_INPROC_SERVER,
                                typeof(Mf.IMFTransform).GUID, out var fkObj);
            var fk = (Mf.IMFTransform)Marshal.GetObjectForIUnknown(fkObj);
            fk.GetOutputAvailableType(0, 0, out var fkot);
            fkot.SetGUID(Mf.MtMajorType, Mf.MediaTypeVideo);
            fkot.SetGUID(Mf.MtSubtype, Mf.VideoFormatH264);
            Mf.MFSetAttributeSize(fkot, Mf.MtFrameSize, 640, 360);
            Mf.MFSetAttributeRatio(fkot, Mf.MtFrameRate, 15, 1);
            Mf.MFSetAttributeRatio(fkot, Mf.MtPixelAspectRatio, 1, 1);
            fkot.SetUINT32(Mf.MtInterlaceMode, 2);
            fkot.SetUINT32(Mf.MtAvgBitrate, 900000);
            fkot.SetUINT32(Mf.MtMpeg2Profile, 66);
            fkot.SetUINT32(Mf.MtMpeg2Level, 31);
            fk.SetOutputType(0, fkot, 0);
            fk.SetInputType(0, Mf.MakeVideoType(Mf.VideoFormatNv12, 640, 360, 15), 0);
            fk.ProcessMessage(Mf.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero);
            fk.ProcessMessage(Mf.MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero);
            fk.GetOutputStreamInfo(0, out var fkosi);
            bool fkProvide = (fkosi.dwFlags & 0x20) == 0;
            Mf.ProcessOutputOne(fk, out var fkPrime, out _, fkProvide);
            if (fkPrime != IntPtr.Zero) Marshal.Release(fkPrime);
            bool capi = fk is Mf.ICodecAPI;
            var fkPv = new Mf.PROPVARIANT { vt = Mf.VT_I4, union = 1 };
            int fkSet = fk is Mf.ICodecAPI c2 ? c2.SetValue(Mf.CodecApiForceKeyFrame, ref fkPv) : -1;
            var gopPv = new Mf.PROPVARIANT { vt = Mf.VT_I4, union = 15 };
            int gopSet = fk is Mf.ICodecAPI c3 ? c3.SetValue(Mf.CodecApiGopSize, ref gopPv) : -1;
            Console.WriteLine($"  capi={capi} forceKeyFrameSet=0x{fkSet:X8} gopSet=0x{gopSet:X8}"); Console.Out.Flush();
            long ts = 10_000_000;
            var lastTypes = "";
            for (int i = 0; i < 40; i++)
            {
                var s = Mf.MakeSample(Moving(i), ts);
                s.SetSampleDuration(666_667L);
                ts += 666_667;
                if (fk.ProcessInput(0, s, 0) != Mf.S_OK) { lastTypes += "I"; continue; }
                var nals = new List<int>();
                for (int p = 0; p < 200; p++)
                {
                    int hr = Mf.ProcessOutputOne(fk, out var ps, out _, fkProvide);
                    if (hr != Mf.S_OK) break;
                    if (ps != IntPtr.Zero)
                    {
                        var samp = (Mf.IMFSample)Marshal.GetObjectForIUnknown(ps);
                        var b = Mf.SampleBytes(samp);
                        if (b != null && b.Length > 0)
                            nals.AddRange(VideoRtp.SplitNals(b).Select(n => n[0] & 0x1F));
                        Marshal.Release(ps);
                    }
                }
                if (nals.Count > 0) lastTypes += "[" + string.Join("-", nals.Distinct()) + "]";
                // At frame 20 (post warm-up) force a keyframe and check the very next output.
                if (i == 20 && fk is Mf.ICodecAPI c4)
                {
                    var fkpv2 = new Mf.PROPVARIANT { vt = Mf.VT_I4, union = 1 };
                    int hrf = c4.SetValue(Mf.CodecApiForceKeyFrame, ref fkpv2);
                    Console.WriteLine($"  force@20 hr=0x{hrf:X8}"); Console.Out.Flush();
                }
            }
            Console.WriteLine($"  types={lastTypes}"); Console.Out.Flush();
            Marshal.Release(fkObj);
        }
        catch (Exception e) { Console.WriteLine($"  force-keyframe EXC: {e.GetType().Name}: {e.Message}"); Console.Out.Flush(); }
    }

    static void SingleFrame()
    {
        // Production-class round trip: the same H264Encoder + PacketizeH264 + H264Decoder used by
        // the live call path, fed synthetic moving NV12 frames. If this is green, the encoder
        // emits IDR-then-P AUs that packetize into RFC 6184 RTP payloads and the decoder turns
        // them back into frames — the whole video plane, minus the wire.
        try
        {
            int aus = 0, packets = 0, firstIdr = -1;
            var all = new List<byte[]>();
            using (var enc = new H264Encoder(640, 360, 15, 900_000))
            {
                Console.WriteLine($"  encoder Ready={enc.Ready} err={enc.Error}"); Console.Out.Flush();
                if (!enc.Ready) return;
                // Encode ALL AUs first, THEN decode: interleaving Encode()/Decode() on one thread
                // stalls the MS H.264 decoder MFT (0 frames — verified), and production never
                // interleaves either (encoder on capture thread, decoder on UDP thread).
                for (int f = 0; f < 120; f++)
                {
                    var nv = MakeGradientNv12(640, 360, f);     // moving: encoder emits real P-frames
                    foreach (var au in enc.Encode(nv))
                    {
                        // Skip AUD (type 9) and report the first payload NAL: the encoder puts an
                        // AUD before the SPS, so the first meaningful NAL of the first AU is 7 (SPS).
                        var nals = VideoRtp.SplitNals(au);
                        var payload = nals.FirstOrDefault(n => (n[0] & 0x1F) != 9);
                        if (firstIdr < 0 && payload != null) firstIdr = payload[0] & 0x1F;
                        packets += VideoRtp.PacketizeH264(au).Count;
                        all.Add(au);
                        aus++;
                    }
                }
            }
            using var dec = new H264Decoder();
            Console.WriteLine($"  decoder Ready={dec.Ready} err={dec.Error}"); Console.Out.Flush();
            if (!dec.Ready) return;
            int decoded = 0;
            foreach (var au in all) decoded += dec.Decode(au).Count;

            Console.WriteLine($"  aus={aus} rtp-packets={packets} decoded-frames={decoded} firstNalType={firstIdr}");
            Console.WriteLine($"  {(firstIdr == 5 ? "first AU is an IDR (5)" : firstIdr == 7 ? "first AU is SPS (7), check PPS/IDR follow" : "first AU is NOT an IDR!")}");
            Console.Out.Flush();
        }
        catch (Exception e) { Console.WriteLine($"  single-frame round trip EXC: {e.GetType().Name}: {e.Message}"); Console.Out.Flush(); }
    }


    // Moving gradient so consecutive frames differ (the encoder drops identical frames otherwise).
    static byte[] MakeGradientNv12(int w, int h, int phase)
    {
        // Tight stride (w), exactly like the production camera frames: the MS encoder MFT is
        // configured for 640x360 and misinterprets a padded-stride buffer (skewed rows + shifted
        // chroma plane -> garbage bitstream that decodes to nothing).
        var nv = new byte[w * h * 3 / 2];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                nv[y * w + x] = (byte)((x * 3 + y * 5 + phase * 7) & 0xFF);
        for (int y = 0; y < h / 2; y++)
            for (int x = 0; x < w / 2; x++)
            {
                int o = w * h + y * w + x * 2;
                nv[o] = 128; nv[o + 1] = 128;
            }
        return nv;
    }

    static void DumpCategory(Guid cat, string who)
    {
        int hr = Mf.MFTEnumEx(cat, Mf.MFT_ENUM_FLAG_ALL, IntPtr.Zero, IntPtr.Zero, out var arr, out var count);
        Console.WriteLine($"  MFTEnumEx(cat={who}) hr=0x{hr:X8} count={count}");
        if (hr != 0) return;
        for (uint i = 0; i < count && i < 12; i++)
        {
            var p = Marshal.ReadIntPtr(arr, (int)(i * IntPtr.Size));
            var a = (Mf.IMFActivate)Marshal.GetObjectForIUnknown(p);
            int hr2 = a.ActivateObject(typeof(Mf.IMFTransform).GUID, out var tObj);
            string info;
            if (hr2 == 0 && tObj != IntPtr.Zero)
            {
                var mft = (Mf.IMFTransform)Marshal.GetObjectForIUnknown(tObj);
                mft.GetStreamCount(out var ni, out var no);
                info = $"in={ni} out={no}";
                for (uint s = 0; s < no && s < 2; s++)
                {
                    if (mft.GetOutputAvailableType(s, 0, out var t) == 0)
                        info += $" out[{s}]={Subtype(t)}";
                }
                Marshal.Release(tObj);
            }
            else info = $"activate=0x{hr2:X8}";
            Console.WriteLine($"  [{i}] {info}");
            Marshal.Release(p);
        }
        Mf.CoTaskMemFree(arr);
    }

    static void DumpEncoder()
    {
        IntPtr obj;
        int hr = Mf.CoCreateInstance(Mf.ClsidH264Encoder, IntPtr.Zero, Mf.CLSCTX_INPROC_SERVER,
                                     typeof(Mf.IMFTransform).GUID, out obj);
        Console.WriteLine($"CoCreateInstance(CLSID_CMSH264EncoderMFT) = 0x{hr:X8} obj={obj}");
        if (hr != 0) return;
        var mft = (Mf.IMFTransform)Marshal.GetObjectForIUnknown(obj);
        // Verify the IMFAttributes binding: set + read back an attribute.
        Mf.MFCreateMediaType(out var probe);
        int ph1 = probe.SetGUID(Mf.MtSubtype, Mf.VideoFormatH264);
        int ph2 = probe.GetGUID(Mf.MtSubtype, out var probeSub);
        int ph3 = probe.SetUINT32(Mf.MtAvgBitrate, 900000);
        int ph4 = probe.GetUINT32(Mf.MtAvgBitrate, out var probeBr);
        Mf.MFSetAttributeSize(probe, Mf.MtFrameSize, 640, 360);
        int ph6 = Mf.MFGetAttributeSize(probe, Mf.MtFrameSize, out var pw, out var phh);
        Console.WriteLine($"  attr binding: setSub=0x{ph1:X8} getSub=0x{ph2:X8} ok={probeSub == Mf.VideoFormatH264} " +
                          $"setBr=0x{ph3:X8} getBr=0x{ph4:X8} val={probeBr} getSize=0x{ph6:X8} {pw}x{phh}");
        DumpTypes(mft, "encoder");
        Console.WriteLine("  -- attempts --");
        // Sweep recipes to isolate the required attribute set. Each one builds a FRESH type from
        // the encoder's own template (which carries the profile/level defaults it accepts).
        var recipes = new (string name, bool profile, bool level, bool br, bool par, bool interl)[]
        {
            ("template+sub+size+rate+br+par+interl(no profile/level)", false, false, true, true, true),
            ("+profile66", true, false, true, true, true),
            ("+profile77", true, false, true, true, true),
            ("+profile77+level30", true, true, true, true, true),
            ("+profile66+level30", true, true, true, true, true),
            ("profile only, no br", true, false, false, true, true),
            ("no par", true, false, true, false, true),
            ("no interlace", true, false, true, true, false),
            ("30fps", true, false, true, true, true),
            ("640x360@30 bitrate 1200000", true, false, true, true, true),
        };
        foreach (var (name, profile, level, br, par, interl) in recipes)
        {
            TrySet(mft, name, () =>
            {
                mft.GetOutputAvailableType(0, 0, out var t);
                t.SetGUID(Mf.MtMajorType, Mf.MediaTypeVideo);
                t.SetGUID(Mf.MtSubtype, Mf.VideoFormatH264);
                Mf.MFSetAttributeSize(t, Mf.MtFrameSize, 640, 360);
                Mf.MFSetAttributeRatio(t, Mf.MtFrameRate, name.Contains("30fps") || name.StartsWith("640x360@30") ? 30u : 15u, 1);
                if (par) Mf.MFSetAttributeRatio(t, Mf.MtPixelAspectRatio, 1, 1);
                if (interl) t.SetUINT32(Mf.MtInterlaceMode, 2);
                if (br) t.SetUINT32(Mf.MtAvgBitrate, name.StartsWith("640x360@30") ? 1200000u : 900000u);
                if (profile) t.SetUINT32(Mf.MtMpeg2Profile, name.Contains("profile77") ? 77u : 66u);
                if (level) t.SetUINT32(Mf.MtMpeg2Level, 30u);
                return t;
            });
        }
        Marshal.Release(obj);
    }

    static void TrySet(Mf.IMFTransform mft, string what, Func<Mf.IMFMediaType> build)
    {
        try
        {
            var t = build();
            int hr = mft.SetOutputType(0, t, 0);
            Console.WriteLine($"  {what}: 0x{hr:X8}");
            // If it took, try the input too.
            if (hr == 0)
            {
                var it = Mf.MakeVideoType(Mf.VideoFormatNv12, 640, 360, 15);
                int ih = mft.SetInputType(0, it, 0);
                Console.WriteLine($"  input NV12: 0x{ih:X8}");
            }
        }
        catch (Exception e) { Console.WriteLine($"  {what}: EXCEPTION {e.Message}"); }
    }

    // Sweep every H.264 encoder MFT on the machine with a few type recipes and report which stick.
    public static void SweepEncoders()
    {
        // For a video ENCODER MFT, the input type filter is the RAW type it consumes (NV12);
        // the output type filter is H.264. MFT_REGISTER_TYPE_INFO is a struct, not a media type.
        foreach (uint flags in new[] { Mf.MFT_ENUM_FLAG_ALL, Mf.MFT_ENUM_FLAG_HARDWARE | Mf.MFT_ENUM_FLAG_SYNCMFT })
        {
            int hr = Mf.MFTEnumEx2(Mf.CategoryVideoEncoder, flags, Mf.MediaTypeVideo, Mf.VideoFormatNv12,
                                   Mf.MediaTypeVideo, Mf.VideoFormatH264, out var arr, out var count);
            Console.WriteLine($"  MFTEnumEx NV12->H264 flags=0x{flags:X} hr=0x{hr:X8} count={count}");
            if (hr != 0 || count == 0) continue;
            for (uint i = 0; i < count && i < 8; i++)
            {
                var p = Marshal.ReadIntPtr(arr, (int)(i * IntPtr.Size));
                var act = (Mf.IMFActivate)Marshal.GetObjectForIUnknown(p);
                int ah = act.ActivateObject(typeof(Mf.IMFTransform).GUID, out var obj);
                if (ah != 0 || obj == IntPtr.Zero)
                {
                    Console.WriteLine($"  ---- encoder [{i}] activate=0x{ah:X8} ----");
                    continue;
                }
                var mft = (Mf.IMFTransform)Marshal.GetObjectForIUnknown(obj);
                Console.WriteLine($"  ---- encoder [{i}] activated ----");
                TrySet(mft, "from-scratch (all attrs, no level)", () =>
                {
                    Mf.MFCreateMediaType(out var t);
                    t.SetGUID(Mf.MtMajorType, Mf.MediaTypeVideo);
                    t.SetGUID(Mf.MtSubtype, Mf.VideoFormatH264);
                    t.SetUINT32(Mf.MtAvgBitrate, 900000);
                    Mf.MFSetAttributeSize(t, Mf.MtFrameSize, 640, 360);
                    Mf.MFSetAttributeRatio(t, Mf.MtFrameRate, 15, 1);
                    Mf.MFSetAttributeRatio(t, Mf.MtPixelAspectRatio, 1, 1);
                    t.SetUINT32(Mf.MtInterlaceMode, 2);
                    t.SetUINT32(Mf.MtMpeg2Profile, 77);
                    return t;
                });
                TrySet(mft, "from-scratch (all attrs, level30)", () =>
                {
                    Mf.MFCreateMediaType(out var t);
                    t.SetGUID(Mf.MtMajorType, Mf.MediaTypeVideo);
                    t.SetGUID(Mf.MtSubtype, Mf.VideoFormatH264);
                    t.SetUINT32(Mf.MtAvgBitrate, 900000);
                    Mf.MFSetAttributeSize(t, Mf.MtFrameSize, 640, 360);
                    Mf.MFSetAttributeRatio(t, Mf.MtFrameRate, 15, 1);
                    Mf.MFSetAttributeRatio(t, Mf.MtPixelAspectRatio, 1, 1);
                    t.SetUINT32(Mf.MtInterlaceMode, 2);
                    t.SetUINT32(Mf.MtMpeg2Profile, 77);
                    t.SetUINT32(Mf.MtMpeg2Level, 30);
                    return t;
                });
                TrySet(mft, "template + profile66", () =>
                {
                    mft.GetOutputAvailableType(0, 0, out var t);
                    t.SetGUID(Mf.MtMajorType, Mf.MediaTypeVideo);
                    t.SetGUID(Mf.MtSubtype, Mf.VideoFormatH264);
                    t.SetUINT32(Mf.MtAvgBitrate, 900000);
                    Mf.MFSetAttributeSize(t, Mf.MtFrameSize, 640, 360);
                    Mf.MFSetAttributeRatio(t, Mf.MtFrameRate, 15, 1);
                    Mf.MFSetAttributeRatio(t, Mf.MtPixelAspectRatio, 1, 1);
                    t.SetUINT32(Mf.MtInterlaceMode, 2);
                    t.SetUINT32(Mf.MtMpeg2Profile, 66);
                    return t;
                });
            }
            Mf.CoTaskMemFree(arr);
        }
    }

    static void DumpDecoder()
    {
        IntPtr obj;
        int hr = Mf.CoCreateInstance(Mf.ClsidH264Decoder, IntPtr.Zero, Mf.CLSCTX_INPROC_SERVER,
                                     typeof(Mf.IMFTransform).GUID, out obj);
        Console.WriteLine($"CoCreateInstance(CLSID_CMSH264DecoderMFT) = 0x{hr:X8} obj={obj}");
        if (hr != 0) return;
        var mft = (Mf.IMFTransform)Marshal.GetObjectForIUnknown(obj);
        DumpTypes(mft, "decoder");
        Marshal.Release(obj);
    }

    static string Subtype(Mf.IMFMediaType t)
    {
        t.GetGUID(Mf.MtSubtype, out var g);
        string s = new string(new[] { (char)(g.ToString().Length > 0 ? '?' : '?') });
        // The subtype GUID's first 4 bytes spell the FOURCC for video formats.
        byte[] b = g.ToByteArray();
        return $"{(char)b[0]}{(char)b[1]}{(char)b[2]}{(char)b[3]}";
    }

    static void DumpTypes(Mf.IMFTransform mft, string who)
    {
        mft.GetStreamCount(out var ni, out var no);
        Console.WriteLine($"  streams: in={ni} out={no}");
        // Output types first (encoders), up to 6.
        Console.WriteLine("  output available types:");
        for (uint i = 0; i < 6; i++)
        {
            int hr = mft.GetOutputAvailableType(0, i, out var t);
            if (hr != 0) { Console.WriteLine($"    [{i}] 0x{hr:X8}"); break; }
            t.GetGUID(Mf.MtMajorType, out var maj);
            bool isVideo = maj == Mf.MediaTypeVideo;
            uint w = 0, h = 0, fr = 0, fd = 0, br = 0;
            if (isVideo)
            {
                Mf.MFGetAttributeSize(t, Mf.MtFrameSize, out w, out h);
                t.GetUINT64(Mf.MtFrameRate, out var frv);
                fr = (uint)(frv >> 32); fd = (uint)(frv & 0xFFFFFFFF);
                t.GetUINT32(Mf.MtAvgBitrate, out br);
            }
            Console.WriteLine($"    [{i}] subtype={Subtype(t)} video={isVideo} {w}x{h}@{fr}/{fd} bitrate={br}");
        }
        Console.WriteLine("  input available types:");
        for (uint i = 0; i < 6; i++)
        {
            int hr = mft.GetInputAvailableType(0, i, out var t);
            if (hr != 0) { Console.WriteLine($"    [{i}] 0x{hr:X8}"); break; }
            uint w = 0, h = 0;
            Mf.MFGetAttributeSize(t, Mf.MtFrameSize, out w, out h);
            Console.WriteLine($"    [{i}] subtype={Subtype(t)} {w}x{h}");
        }
    }
}
