namespace OpenCord;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Must run before anything touches Ui.Dpi: SystemAware fixes the process DPI for its whole
        // life, and Ui caches the value in a static initialiser that only runs once.
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.AddMessageFilter(new WheelRouter());
        InstallCrashLog(args.Contains("--log"));

        Prefs.Load();

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
