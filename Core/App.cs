using System.Drawing;

namespace OpenCord;

// Process-wide state, plus the handful of lookups the pure-logic layers need without reaching into
// the client themselves. Markdown resolves mentions through these delegates, which is what lets the
// parser stay free of any dependency on UserClient or on a guild being loaded.
static class App
{
    public static UserClient? Client;
    public static UserGuild? Guild;          // the guild currently on screen; null while in DMs

    /// Open (or create) a DM with a user. Set by Session; the profile popout calls it without
    /// needing a reference to the shell it was opened from.
    public static Action<ulong>? OpenDm;

    /// Force the message list to re-measure every row. Set by Session; the settings page calls it
    /// when the display density changes, since row heights are cached per width.
    public static Action? Relayout;

    public static Func<ulong, (string, Color?)?>? ResolveUserMention;
    public static Func<ulong, (string, Color?)?>? ResolveRoleMention;
    public static Func<ulong, string?>? ResolveChannelName;
}
