using System.Drawing;
using System.Drawing.Imaging;
using System.Net.Http;

namespace ClaudeScord;

// `--memtest [token]`: what an image actually costs this process, measured rather than assumed.
//
// The cache's budget is only as good as its cost model, and the two plausible models for an animated
// GIF differ by the frame count — a 60x error either way. This downloads real GIFs through the same
// path the picker uses and prints the measured private-bytes delta next to both models, then checks
// that dropping them gives the memory back.
static class MemTest
{
    static long Priv() => Environment.WorkingSet;   // replaced below by the real counter
    static readonly System.Diagnostics.Process Self = System.Diagnostics.Process.GetCurrentProcess();

    static long PrivMb100()
    {
        Self.Refresh();
        return Self.PrivateMemorySize64;
    }

    static string Mb(long b) => (b / 1024.0 / 1024.0).ToString("0.0") + "MB";

    public static int Run(string? token)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/124.0.0.0");

        var urls = new List<string>();
        if (!string.IsNullOrEmpty(token))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    "https://discord.com/api/v9/gifs/trending-gifs?media_format=gif&limit=20&locale=en-US");
                req.Headers.TryAddWithoutValidation("Authorization", token);
                req.Headers.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36 discord/1.0.9013 Electron/31.3.1");
                var resp = http.Send(req);
                var json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                Console.WriteLine($"trending-gifs: {(int)resp.StatusCode}");
                Console.WriteLine("raw[0..1600]: " + json[..Math.Min(1600, json.Length)]);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                var arr = root.ValueKind == System.Text.Json.JsonValueKind.Array ? root
                        : root.TryGetProperty("gifs", out var gg) ? gg
                        : root.TryGetProperty("results", out var rr) ? rr : default;
                if (arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var g in arr.EnumerateArray())
                        if (g.TryGetProperty("src", out var s) && s.GetString() is { } u) urls.Add(u);
            }
            catch (Exception e) { Console.WriteLine("gif fetch failed: " + e.Message); }
        }
        if (urls.Count == 0) { Console.WriteLine("no urls"); return 1; }

        Console.WriteLine($"\n{urls.Count} gifs\n");
        Console.WriteLine($"{"px",12} {"frames",7} {"encoded",9} {"1 frame",9} {"all frames",11} {"MEASURED",9}");

        var held = new List<Image>();
        long totalMeasured = 0, totalOne = 0, totalAll = 0, totalEnc = 0;
        foreach (var u in urls)
        {
            byte[] bytes;
            try { bytes = http.GetByteArrayAsync(u).GetAwaiter().GetResult(); } catch { continue; }
            Settle();
            long before = PrivMb100();
            var img = Image.FromStream(new MemoryStream(bytes));
            int frames = 1;
            try { frames = Math.Max(1, img.GetFrameCount(FrameDimension.Time)); } catch { }
            // Touch every frame: a decoder that decodes lazily only shows its true cost once asked.
            for (int f = 0; f < frames; f++)
                try { img.SelectActiveFrame(FrameDimension.Time, f); } catch { }
            Settle();
            long after = PrivMb100();
            held.Add(img);

            long one = (long)img.Width * img.Height * 4;
            Console.WriteLine($"{img.Width + "x" + img.Height,12} {frames,7} {Mb(bytes.Length),9} {Mb(one),9} {Mb(one * frames),11} {Mb(after - before),9}");
            totalMeasured += after - before; totalOne += one; totalAll += one * frames; totalEnc += bytes.Length;
        }

        Settle();
        long peak = PrivMb100();
        Console.WriteLine($"\ntotals: encoded {Mb(totalEnc)}  1-frame {Mb(totalOne)}  all-frames {Mb(totalAll)}  measured {Mb(totalMeasured)}");
        Console.WriteLine($"private now: {Mb(peak)}");

        // Now give them back the way the cache does today: drop the reference, no dispose.
        held.Clear();
        Settle();
        Console.WriteLine($"after drop+GC (no dispose): {Mb(PrivMb100())}");

        // And the way it would if eviction disposed.
        var again = new List<Image>();
        foreach (var u in urls)
        {
            try { again.Add(Image.FromStream(new MemoryStream(http.GetByteArrayAsync(u).GetAwaiter().GetResult()))); } catch { }
        }
        foreach (var i in again) { int f = 1; try { f = i.GetFrameCount(FrameDimension.Time); } catch { } for (int k = 0; k < f; k++) try { i.SelectActiveFrame(FrameDimension.Time, k); } catch { } }
        Settle();
        Console.WriteLine($"reloaded: {Mb(PrivMb100())}");
        foreach (var i in again) i.Dispose();
        again.Clear();
        Settle();
        Console.WriteLine($"after dispose+GC: {Mb(PrivMb100())}");
        return 0;
    }

    static void Settle()
    {
        for (int i = 0; i < 3; i++) { GC.Collect(2, GCCollectionMode.Aggressive, true, true); GC.WaitForPendingFinalizers(); }
        Thread.Sleep(150);
    }
}
