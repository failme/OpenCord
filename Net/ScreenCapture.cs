using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace OpenCord;

// The screen-share source: captures the primary screen with GDI (one StretchBlt per frame, which
// both grabs AND scales — no intermediate full-size bitmap), and hands RGB24 bytes to a callback
// on a background thread at the target frame rate. The caller encodes (H.264) and previews (JPEG).
// Pure managed interop, so the zero-native-footprint rule holds; there is no camera driver involved.
sealed class ScreenCapture : IDisposable
{
    [DllImport("user32.dll")]
    static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")]
    static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")]
    static extern int StretchBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
                                 IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, uint rop);
    const uint SrcCopyCapture = 0x00CC0020 | 0x40000000;   // SRCCOPY | CAPTUREBLT (get layered windows)

    readonly int _outW, _outH, _fps;
    readonly int _quality;
    readonly System.Threading.CancellationTokenSource _cts = new();
    Thread? _thread;
    bool _running;

    public event Action<byte[] /*rgb24*/, int /*width*/, int /*height*/>? Frame;
    public event Action? Stopped;

    public ScreenCapture(int width = 640, int height = 360, int fps = 15, int quality = 45)
    {
        _outW = width;
        _outH = height;
        _fps = fps;
        _quality = quality;
    }

    /// Start the capture loop. Returns false when the screen cannot be grabbed (no session).
    public bool Start()
    {
        if (_running) return true;
        var dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return false;
        ReleaseDC(IntPtr.Zero, dc);
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "screen-capture" };
        _thread.Start();
        return true;
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _cts.Cancel();
        _thread?.Join(2000);
        Stopped?.Invoke();
    }

    void Loop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long next = 0;
        var screen = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
        try
        {
            // Re-create the source + target bitmaps once; the loop reuses them per frame.
            using var source = new Bitmap(screen.Width, screen.Height, PixelFormat.Format24bppRgb);
            using var target = new Bitmap(_outW, _outH, PixelFormat.Format24bppRgb);

            while (!_cts.IsCancellationRequested)
            {
                next += 1000L / _fps;
                long delay = next - sw.ElapsedMilliseconds;
                if (delay > 0)
                {
                    try { Thread.Sleep((int)delay); } catch { }
                }
                else if (delay < -1000L / _fps) next = sw.ElapsedMilliseconds;  // fell behind: resync

                var srcDc = GetDC(IntPtr.Zero);
                if (srcDc == IntPtr.Zero) continue;
                try
                {
                    using var tg = Graphics.FromImage(target);
                    var tdc = tg.GetHdc();
                    try
                    {
                        StretchBlt(tdc, 0, 0, _outW, _outH, srcDc, 0, 0,
                                   screen.Width, screen.Height, SrcCopyCapture);
                    }
                    finally { tg.ReleaseHdc(tdc); }

                    // Hand the raw 24bpp bytes to the caller; stride can exceed width*3 on odd
                    // widths, so copy row by row.
                    var data = target.LockBits(new Rectangle(0, 0, _outW, _outH),
                                               ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                    try
                    {
                        var rgb = new byte[_outW * _outH * 3];
                        int rowBytes = Math.Min(data.Stride, _outW * 3);
                        for (int y = 0; y < _outH; y++)
                            Marshal.Copy(data.Scan0 + y * data.Stride, rgb, y * _outW * 3, rowBytes);
                        Frame?.Invoke(rgb, _outW, _outH);
                    }
                    finally { target.UnlockBits(data); }
                }
                finally { ReleaseDC(IntPtr.Zero, srcDc); }
            }
        }
        catch (Exception e)
        {
            Log.Voice("screen capture ended: " + e.Message);
        }
        _running = false;
    }

    public void Dispose() => Stop();
}
