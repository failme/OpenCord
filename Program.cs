namespace ClaudeScord;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Must run before anything touches Ui.Dpi: SystemAware fixes the process DPI for its whole
        // life, and Ui caches the value in a static initialiser that only runs once.
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        if (args.Contains("--selftest")) { Environment.ExitCode = SelfTest.Run(); return; }

        // Dump the Media Foundation H.264 encoder/decoder MFT behaviour: which CLSIDs exist, what
        // output/input types they offer, and what setting them returns. For debugging the codec
        // bring-up on a new machine.
        if (args.Contains("--mft")) { MftDebug.Run(); return; }

        if (args.Contains("--memtest"))
        {
            Prefs.Load();
            var t = args.SkipWhile(a => a != "--memtest").Skip(1).FirstOrDefault();
            Environment.ExitCode = MemTest.Run(string.IsNullOrEmpty(t) ? Prefs.Token : t);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.AddMessageFilter(new WheelRouter());
        InstallCrashLog(args.Contains("--log"));

        Prefs.Load();

        // Headless REST smoke test: --apitest [token]. Writes apitest.log next to the exe.
        if (args.Contains("--apitest"))
        {
            var tok = args.SkipWhile(a => a != "--apitest").Skip(1).FirstOrDefault();
            Environment.ExitCode = ApiTest.Run(string.IsNullOrEmpty(tok) ? Prefs.Token : tok);
            return;
        }

        // Headless live voice probe: --voiceprobe [token] [channelId]. Joins a real voice channel
        // and dumps the transport handshake + DAVE opcodes. Console.WriteLine goes to stdout here.
        if (args.Contains("--voiceprobe"))
        {
            var rest = args.SkipWhile(a => a != "--voiceprobe").Skip(1).ToArray();
            var tok = rest.FirstOrDefault();
            if (string.IsNullOrEmpty(tok) && !string.IsNullOrEmpty(Prefs.Token)) tok = Prefs.Token;
            if (string.IsNullOrEmpty(tok)) { Console.WriteLine("no token: --voiceprobe [token] [channelId]"); return; }
            var ch = rest.Length > 1 ? rest[1] : null;
            VoiceProbe.Run(tok, ch).GetAwaiter().GetResult();
            return;
        }

        var shell = new Shell();

        if (args.Contains("--demo"))
        {
            Demo.Populate(shell);
        }
        else
        {
            var token = Prefs.Token;
            if (string.IsNullOrEmpty(token))
            {
                // No saved token: show the login screen. It has already validated the token against
                // /users/@me by the time it returns OK, so persist it and carry on.
                using var login = new LoginForm();
                if (login.ShowDialog() != DialogResult.OK) return;   // closed without logging in
                token = login.Token!;
                Prefs.SetToken(token);
            }

            var session = new Session(shell, token);
            // Connect after the window exists, so the first dispatch has somewhere to marshal to.
            shell.Shown += async (_, _) =>
            {
                try { await session.StartAsync(); }
                catch (Exception e) { Log.Write("gateway", "connect failed: " + e.Message); }
            };
        }

        Application.Run(shell);
    }

    // An unhandled exception on the UI thread otherwise takes the window down with nothing to look
    // at afterwards. Append it to crash.log next to the exe, and keep the app alive: a picker that
    // throws should not cost you the session.
    static void InstallCrashLog(bool debug)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "crash.log");

        // Opt-in diagnostics: --log sends Log.Write to debug.log next to the exe. Nothing is
        // attached otherwise, so logging on a normal run stays a single null check.
        if (debug)
        {
            var dbg = Path.Combine(AppContext.BaseDirectory, "debug.log");
            try { File.Delete(dbg); } catch { }
            Log.Sink = (cat, line) =>
            {
                try { File.AppendAllText(dbg, $"[{cat}] {line}{Environment.NewLine}"); } catch { }
            };
        }

        void Write(string where, Exception? ex)
        {
            try
            {
                File.AppendAllText(path,
                    $"---- {DateTime.Now:yyyy-MM-dd HH:mm:ss} {where}{Environment.NewLine}{ex}{Environment.NewLine}");
            }
            catch { }
        }

        Application.ThreadException += (_, e) => Write("ui", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write("background", e.ExceptionObject as Exception);
        // Without this WinForms only routes to ThreadException when a debugger is not attached; being
        // explicit means the handler runs the same way under both.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
    }
}
