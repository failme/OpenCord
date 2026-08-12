using System.Text.Json;

namespace ClaudeScord;

// Where the account token comes from, and the little bit of state worth remembering between runs.
//
// A user token is unscoped full account access — every DM, every guild, read and write — so it is
// never stored in plain text. The login form calls SetToken, which DPAPI-encrypts it (see [[Crypto]])
// into TokenProtected. Two escape hatches remain for power users:
//
//   setx CLAUDESCORD_TOKEN "..."        environment, wins over everything, never touches this folder
//   prefs.json -> { "Token": "..." }    legacy plaintext, read but never written
static class Prefs
{
    public sealed class Data
    {
        public string? Token { get; set; }            // legacy plaintext, read-only
        public string? TokenProtected { get; set; }   // DPAPI blob written by the login form
        public ulong LastGuild { get; set; }
        public ulong LastChannel { get; set; }
        public bool NotifyEnabled { get; set; } = true;
        public bool NotifyMentionsOnly { get; set; }
        // The message ping and the ring tones. Separate from NotifyEnabled because Discord splits
        // them: you can keep the desktop toast and silence the sound, or the reverse.
        public bool SoundsEnabled { get; set; } = true;
        // NAudio device indices. -1 means "whatever Windows calls the default", which is what a
        // fresh install should use — the list is rebuilt every launch and indices shift when a
        // headset is plugged in, so a saved index is a hint, always re-validated against the
        // current device count before use.
        public int InputDevice { get; set; } = -1;
        public int OutputDevice { get; set; } = -1;

        // ── voice ──
        /// 0 = voice activity, 1 = push to talk.
        public int InputMode { get; set; }
        /// Extra RMS above the measured noise floor before the mic opens. 0 = automatic.
        ///
        /// Shipped briefly as an absolute 0.02, which is louder than a normal microphone ever
        /// reaches — anyone who ran that build has it saved and would keep transmitting silence,
        /// so a stored value from that range is reset rather than honoured.
        public float Sensitivity { get; set; }
        public float InputVolume { get; set; } = 1f;
        public float OutputVolume { get; set; } = 1f;
        /// A plain squelch below this RMS. 0 disables it.
        public float NoiseGate { get; set; }
        /// Virtual-key code held to transmit in push-to-talk. Default is left Ctrl.
        public int PttKey { get; set; } = 0xA2;
        /// Per-user playback gain, keyed by user id. 1 is unity; absent means unity.
        public Dictionary<string, float> UserVolume { get; set; } = new();
        /// The voice UI sounds (join, leave, mute, deafen, disconnect).
        public bool VoiceSounds { get; set; } = true;

        // ── window / display ──
        public int WindowX { get; set; } = int.MinValue;
        public int WindowY { get; set; } = int.MinValue;
        public int WindowW { get; set; }
        public int WindowH { get; set; }
        public bool WindowMaximized { get; set; }
        /// Discord's cozy (false) / compact (true) message display.
        public bool CompactMode { get; set; }
        /// Whole-UI scale, 0.8–1.4, on top of the DPI scale.
        public float Zoom { get; set; } = 1f;
        /// Keep running in the tray when the window is closed, like the real client.
        public bool MinimizeToTray { get; set; } = true;

        /// The reaction emoji last used, most recent first — the message hover bar offers them as
        /// one-click suggestions. Stored in Discord's own markup form ("a:name:id" / "name:id" /
        /// the raw glyph) so the animated flag survives the round trip; the REST key drops the
        /// leading "a:".
        public List<string> RecentReactions { get; set; } = new();
    }

    /// How many suggestions the hover bar shows, which is also all that is worth keeping.
    public const int RecentReactionCount = 3;

    /// Record a reaction the user just added. Re-using one moves it to the front rather than
    /// duplicating it, so the three slots are three *distinct* emoji.
    public static void NoteReaction(string markup)
    {
        if (string.IsNullOrWhiteSpace(markup)) return;
        var list = Current.RecentReactions;
        list.RemoveAll(e => e == markup);
        list.Insert(0, markup);
        if (list.Count > RecentReactionCount) list.RemoveRange(RecentReactionCount, list.Count - RecentReactionCount);
        Save();
    }

    static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "prefs.json");
    public static Data Current { get; private set; } = new();

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath)) ?? new();
            // A threshold this high is not a choice anyone could have made deliberately — the
            // slider tops out far below it. It can only be the old absolute default, which muted
            // the microphone outright, so drop it back to automatic.
            if (Current.Sensitivity >= 0.015f) { Current.Sensitivity = 0f; Save(); }
        }
        catch { Current = new(); }
    }

    // Only ever writes back what was already in the file. A token supplied through the environment
    // stays in the environment — Save must not be the thing that puts it on disk.
    public static void Save()
    {
        try
        {
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public static string? Token =>
        Environment.GetEnvironmentVariable("CLAUDESCORD_TOKEN") is { Length: > 0 } env ? env
        : Current.TokenProtected is { Length: > 0 } prot ? Crypto.TryUnprotect(prot)
        : Current.Token;

    // Called by the login form. Encrypts, drops any legacy plaintext, and persists. If DPAPI throws
    // the token is simply not saved — the session for this run still has it, and next launch asks
    // again. Storing plaintext as a fallback would defeat the point.
    public static void SetToken(string token)
    {
        try { Current.TokenProtected = Crypto.Protect(token); Current.Token = null; Save(); }
        catch { }
    }

    /// Playback gain for one speaker. Stored as a string key because JSON object keys are strings.
    public static float UserVolume(ulong userId) =>
        Current.UserVolume.TryGetValue(userId.ToString(), out var v) ? v : 1f;

    public static void SetUserVolume(ulong userId, float gain)
    {
        var key = userId.ToString();
        if (Math.Abs(gain - 1f) < 0.001f) Current.UserVolume.Remove(key);
        else Current.UserVolume[key] = gain;
        Save();
    }

    public static void ClearToken()
    {
        Current.TokenProtected = null;
        Current.Token = null;
        Save();
    }
}
