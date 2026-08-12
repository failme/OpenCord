namespace ClaudeScord;

// Where the protocol layer sends its diagnostics.
//
// The predecessor called straight into the voice and activity *windows* from UserClient
// (`Voice.Note(...)`, `ActivityWindow.LogLine(...)`), which meant the network code could not be
// compiled — let alone run — without those Forms existing. Inverting it costs one delegate.
//
// Nothing is attached by default, so logging on an unwired app is a single null check.
static class Log
{
    public static Action<string, string>? Sink;   // (category, line)

    public static void Write(string category, string line) => Sink?.Invoke(category, line);
    public static void Voice(string line) => Write("voice", line);
    public static void Activity(string line) => Write("activity", line);

    // ── frame times ─────────────────────────────────────────────────────────────────────────────
    /// `using var _ = Log.Frame("chat");` at the top of an OnPaint. Off by default: with no sink
    /// this is a null check and a `using` on null, which the JIT removes entirely — so instrumented
    /// paints cost nothing in a normal run, and `--log` turns them on.
    ///
    /// Percentiles, not an average: a scroll that is smooth apart from one 90ms hitch every second
    /// feels broken and averages fine. The p95 and the max are the numbers that describe it.
    public static IDisposable? Frame(string name) => Sink == null ? null : new Span(name);

    /// A measurement that isn't a `using` block — the *gap* between frames, above all. Judder is a
    /// pacing defect: paints of 3ms arriving 26ms apart look bad and every per-paint number is fine.
    public static void Sample(string name, double ms) { if (Sink != null) Span.Add(name, ms); }

    sealed class Span : IDisposable
    {
        static readonly Dictionary<string, List<double>> Samples = new();
        static long _lastReport = Environment.TickCount64;

        readonly string _name;
        readonly long _start = System.Diagnostics.Stopwatch.GetTimestamp();

        public Span(string name) => _name = name;

        public void Dispose()
        {
            Add(_name, (System.Diagnostics.Stopwatch.GetTimestamp() - _start) * 1000.0
                       / System.Diagnostics.Stopwatch.Frequency);
        }

        public static void Add(string _name, double ms)
        {
            lock (Samples)
            {
                if (!Samples.TryGetValue(_name, out var list)) Samples[_name] = list = new List<double>();
                list.Add(ms);
                long window = Environment.TickCount64 - _lastReport;
                if (window < 5000) return;
                _lastReport = Environment.TickCount64;
                foreach (var (k, v) in Samples)
                {
                    if (v.Count == 0) continue;
                    v.Sort();
                    // fps matters as much as the paint cost: paints of 3ms arriving 21ms apart are a
                    // pacing problem, and only the frame *rate* shows that.
                    Write("perf", $"{k}: {v.Count} frames in {window}ms = {v.Count * 1000 / window}fps  "
                                + $"p50 {P(v, .50):F1}ms  p95 {P(v, .95):F1}ms  max {v[^1]:F1}ms  "
                                + $"over-16ms {v.Count(x => x > 16.0) * 100 / v.Count}%");
                    v.Clear();
                }
            }
        }

        static double P(List<double> sorted, double q) => sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * q))];
    }
}
