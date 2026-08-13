using System.Text;
using System.Text.RegularExpressions;
using Color = System.Drawing.Color;

namespace OpenCord;

[Flags]
enum Style
{
    None = 0, Bold = 1, Italic = 2, Underline = 4, Strike = 8, Code = 16, Spoiler = 32,
    H1 = 64, H2 = 128, H3 = 256, Subtext = 512,
}

// One styled inline fragment. Atoms (emoji/mention/link) set the extra fields.
sealed class Run
{
    public string Text = "";
    public Style Style;
    public Color? Color;
    public string? Url;      // link target, or emoji image url
    public ulong? UserId;    // user mention -> profile
    public bool Emoji;       // draw image (Url) inline
    public bool Mention;     // rounded pill background
    public bool Break;       // hard line break
    public bool Quote;       // inside a blockquote -> left bar + indent
    public int Indent;       // list nesting, in levels
    public int SpoilerId;    // runs sharing an id reveal together
    public bool BigEmoji;    // emoji-only message -> render large, like Discord
    public bool Block;       // ```fenced``` block -> its own full-width monospace box
    public string? Lang;     // the fence's language tag, for syntax colouring
}

static class Markdown
{
    // Atoms suppress emphasis parsing inside them. Ordered longest-first where they can overlap.
    static readonly Regex Atom = new(
        @"\[(?<ltxt>[^\]\n]+)\]\((?<lurl>(?:https?|discord)://[^\s)]+)\)" +   // masked link
        @"|<t:(?<ts>-?\d+)(?::(?<tsf>[tTdDfFR]))?>" +                        // <t:unix:R> timestamp
        @"|<a?:(?<ename>\w+):(?<eid>\d+)>" +                                 // custom emoji
        @"|<@!?(?<uid>\d+)>|<@&(?<rid>\d+)>|<#(?<cid>\d+)>" +                // mentions
        @"|</(?<cmd>[\w -]+):\d+>" +                                         // </name:id> command mention
        @"|<id:(?<nav>[a-z]+)>" +                                            // <id:customize> nav pills
        @"|<(?<bare>https?://[^\s>]+)>" +                                    // <url>: link, embed suppressed
        @"|https?://[^\s<>""]+|@everyone|@here",
        RegexOptions.Compiled);

    // A message that is nothing but (up to 27) emoji renders them large in Discord.
    static readonly Regex OnlyEmoji = new(@"^(?:\s|<a?:\w+:\d+>|\p{Cs}{2}|[☀-➿️←-⇿⬀-⯿])+$", RegexOptions.Compiled);
    static readonly Regex CustomEmoji = new(@"<a?:\w+:\d+>", RegexOptions.Compiled);

    static int _spoilerSeq;

    public static List<Run> Parse(string text)
    {
        var runs = new List<Run>();
        var lines = text.Replace("\r", "").Split('\n');
        bool big = IsEmojiOnly(text);

        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) runs.Add(new Run { Break = true });
            var line = lines[i];

            // ``` fences swallow everything up to the closing fence, markdown included. The opening
            // line's remainder is a language tag, which Discord uses for highlighting and drops from
            // the text — leaving it in renders "csharp" as the first line of every pasted snippet.
            if (line.StartsWith("```"))
            {
                var body = new StringBuilder();
                string tail = line[3..].Trim();
                bool tagIsLang = tail.Length > 0 && tail.All(c => char.IsLetterOrDigit(c) || c is '+' or '-' or '#');
                if (tail.Length > 0 && !tagIsLang) body.Append(tail);

                int j = i + 1;
                for (; j < lines.Length && !lines[j].TrimEnd().EndsWith("```"); j++)
                {
                    if (body.Length > 0) body.Append('\n');
                    body.Append(lines[j]);
                }
                if (j < lines.Length)
                {
                    var last = lines[j];
                    var before = last[..last.LastIndexOf("```", StringComparison.Ordinal)];
                    if (before.Length > 0) { if (body.Length > 0) body.Append('\n'); body.Append(before); }
                }
                runs.Add(new Run { Text = body.ToString(), Style = Style.Code, Block = true,
                                   Lang = tagIsLang && tail.Length > 0 ? tail : null });
                i = j;                                  // resume after the closing fence
                continue;
            }

            // ── Block prefixes, applied in Discord's own precedence ──
            bool quote = false;
            while (line.StartsWith("> ") || line == ">") { quote = true; line = line.Length > 1 ? line[2..] : ""; }

            var style = Style.None;
            Color? color = null;
            int indent = 0;

            if (line.StartsWith("### ")) { style = Style.H3; line = line[4..]; }
            else if (line.StartsWith("## ")) { style = Style.H2; line = line[3..]; }
            else if (line.StartsWith("# ")) { style = Style.H1; line = line[2..]; }
            else if (line.StartsWith("-# ")) { style = Style.Subtext; color = Theme.Muted; line = line[3..]; }
            else
            {
                // Leading spaces before a bullet are Discord's nesting.
                int lead = 0;
                while (lead < line.Length && line[lead] == ' ') lead++;
                var body = line[lead..];
                var bullet = BulletPrefix(body);
                if (bullet != null)
                {
                    indent = 1 + lead / 2;
                    runs.Add(new Run { Text = bullet, Color = Theme.Text, Quote = quote, Indent = indent });
                    line = body[BulletLen(body)..];
                }
            }

            if (quote) color ??= Theme.Text;
            var start = runs.Count;
            ParseInline(line, style, color, runs, big);
            for (int k = start; k < runs.Count; k++) { runs[k].Quote = quote; runs[k].Indent = indent; }
        }
        return runs;
    }

    static bool IsEmojiOnly(string t)
    {
        var s = t.Trim();
        if (s.Length == 0 || s.Length > 200) return false;
        if (!OnlyEmoji.IsMatch(s)) return false;
        // Cap it the way Discord does — a wall of emoji goes back to normal size.
        int count = CustomEmoji.Matches(s).Count + s.Length / 2;
        return count <= 27;
    }

    static string? BulletPrefix(string s)
    {
        if (s.StartsWith("- ") || s.StartsWith("* ") || s.StartsWith("+ ")) return "•  ";
        var m = Regex.Match(s, @"^(\d{1,3})\. ");
        return m.Success ? m.Groups[1].Value + ".  " : null;
    }

    static int BulletLen(string s)
    {
        if (s.StartsWith("- ") || s.StartsWith("* ") || s.StartsWith("+ ")) return 2;
        var m = Regex.Match(s, @"^\d{1,3}\. ");
        return m.Success ? m.Length : 0;
    }

    static void ParseInline(string s, Style baseStyle, Color? baseColor, List<Run> outp, bool big = false)
    {
        int i = 0;
        var buf = new StringBuilder();
        void Flush()
        {
            if (buf.Length == 0) return;
            var text = buf.ToString();
            buf.Clear();

            // Unicode emoji become image atoms so they render as Twemoji, the same sprites the real
            // client uses, instead of whatever Segoe UI Emoji draws. Inline code keeps its literal
            // text — a sprite inside a `code span` would be wrong.
            if (baseStyle.HasFlag(Style.Code) || !Twemoji.Any(text))
            {
                outp.Add(new Run { Text = text, Style = baseStyle, Color = baseColor, BigEmoji = big });
                return;
            }

            foreach (var (seg, isEmoji) in Twemoji.Split(text))
            {
                if (isEmoji)
                    outp.Add(new Run
                    {
                        Emoji = true, Text = seg, Url = Twemoji.Url(seg),
                        Style = baseStyle & (Style.Spoiler | Style.H1 | Style.H2 | Style.H3 | Style.Subtext),
                        SpoilerId = 0, BigEmoji = big,
                    });
                else if (seg.Length > 0)
                    outp.Add(new Run { Text = seg, Style = baseStyle, Color = baseColor, BigEmoji = big });
            }
        }

        while (i < s.Length)
        {
            // Backslash escape — Discord lets you type a literal * or _ this way.
            if (s[i] == '\\' && i + 1 < s.Length && !char.IsLetterOrDigit(s[i + 1]))
            {
                buf.Append(s[i + 1]);
                i += 2;
                continue;
            }

            var m = Atom.Match(s, i);
            if (m.Success && m.Index == i)
            {
                Flush();
                var run = AtomRun(m);
                run.Style |= baseStyle & (Style.H1 | Style.H2 | Style.H3 | Style.Subtext);
                if (big && run.Emoji) run.BigEmoji = true;
                outp.Add(run);
                i += m.Length;
                continue;
            }

            var (marker, style) = MarkerAt(s, i);
            if (marker != null)
            {
                int close = FindClose(s, marker, i + marker.Length);
                if (close > i + marker.Length)
                {
                    Flush();
                    var inner = s.Substring(i + marker.Length, close - (i + marker.Length));
                    if (style == Style.Code)
                        outp.Add(new Run { Text = inner, Style = baseStyle | Style.Code });
                    else if (style == Style.Spoiler)
                    {
                        int id = ++_spoilerSeq;
                        int from = outp.Count;
                        ParseInline(inner, baseStyle | Style.Spoiler, baseColor, outp, big);
                        for (int k = from; k < outp.Count; k++) outp[k].SpoilerId = id;
                    }
                    else
                        ParseInline(inner, baseStyle | style, baseColor, outp, big);
                    i = close + marker.Length;
                    continue;
                }
            }

            buf.Append(s[i]);
            i++;
        }
        Flush();
    }

    // Skip over escaped copies of the marker when looking for the closing one.
    static int FindClose(string s, string marker, int from)
    {
        for (int i = from; i <= s.Length - marker.Length; i++)
        {
            if (i > 0 && s[i - 1] == '\\') continue;
            if (string.CompareOrdinal(s, i, marker, 0, marker.Length) == 0) return i;
        }
        return -1;
    }

    static (string?, Style) MarkerAt(string s, int i)
    {
        bool Is(string t) => i + t.Length <= s.Length && string.CompareOrdinal(s, i, t, 0, t.Length) == 0;
        if (Is("***")) return ("***", Style.Bold | Style.Italic);
        if (Is("**")) return ("**", Style.Bold);
        if (Is("__")) return ("__", Style.Underline);
        if (Is("~~")) return ("~~", Style.Strike);
        if (Is("||")) return ("||", Style.Spoiler);
        if (Is("`")) return ("`", Style.Code);
        if (Is("*")) return ("*", Style.Italic);
        if (Is("_")) return ("_", Style.Italic);
        return (null, Style.None);
    }

    // Every mention resolves through an App.Resolve* delegate. The predecessor also kept a
    // Discord.Net fallback here, which is the only reason the bot library was still referenced;
    // the user gateway is the sole source of guild data now, so the fallback is gone.
    static Run AtomRun(Match m)
    {
        if (m.Groups["ltxt"].Success)
        {
            var url = m.Groups["lurl"].Value;
            return new Run { Text = m.Groups["ltxt"].Value, Url = url, Color = Theme.Link };
        }
        if (m.Groups["ts"].Success)
        {
            var when = DateTimeOffset.FromUnixTimeSeconds(long.Parse(m.Groups["ts"].Value)).LocalDateTime;
            var fmt = m.Groups["tsf"].Success ? m.Groups["tsf"].Value[0] : 'f';
            return new Run { Mention = true, Text = FormatStamp(when, fmt), Color = Theme.Text };
        }
        if (m.Groups["eid"].Success)
        {
            bool anim = m.Value.StartsWith("<a:");
            var id = m.Groups["eid"].Value;
            return new Run
            {
                Emoji = true,
                Text = ":" + m.Groups["ename"].Value + ":",
                Url = $"https://cdn.discordapp.com/emojis/{id}.{(anim ? "gif" : "png")}?size=48",
            };
        }
        if (m.Groups["uid"].Success)
        {
            ulong id = ulong.Parse(m.Groups["uid"].Value);
            var (name, col) = App.ResolveUserMention?.Invoke(id) ?? ("unknown-user", null);
            return new Run { Mention = true, Text = "@" + name, Color = col ?? Theme.MentionText, UserId = id };
        }
        if (m.Groups["rid"].Success)
        {
            ulong id = ulong.Parse(m.Groups["rid"].Value);
            var (name, col) = App.ResolveRoleMention?.Invoke(id) ?? ("deleted-role", null);
            return new Run { Mention = true, Text = At(name), Color = col ?? Theme.MentionText };
        }
        if (m.Groups["cid"].Success)
        {
            ulong id = ulong.Parse(m.Groups["cid"].Value);
            var name = App.ResolveChannelName?.Invoke(id);
            return new Run { Mention = true, Text = "#" + (name ?? "unknown"), Color = Theme.MentionText };
        }
        // </name:id> — a command mention. Bots use these constantly to point at their own commands,
        // and unparsed they leaked the raw id into the message text.
        if (m.Groups["cmd"].Success)
            return new Run { Mention = true, Text = "/" + m.Groups["cmd"].Value, Color = Theme.MentionText };
        if (m.Groups["nav"].Success)
            return new Run { Mention = true, Text = "#" + m.Groups["nav"].Value, Color = Theme.MentionText };
        // <https://…> — Discord's "don't unfurl this" form. The brackets are syntax, not text.
        if (m.Groups["bare"].Success)
        {
            var bare = m.Groups["bare"].Value;
            return new Run { Text = bare, Url = bare, Color = Theme.Link };
        }
        if (m.Value.StartsWith("http"))
        {
            var url = m.Value.TrimEnd('.', ',', ')', ']', '!', '?');
            return new Run { Text = url, Url = url, Color = Theme.Link };
        }
        return new Run { Mention = true, Text = m.Value, Color = Theme.MentionText }; // @everyone / @here
    }

    // Discord names the default role literally "@everyone", so prefixing it again gave "@@everyone".
    static string At(string name) => name.StartsWith('@') ? name : "@" + name;

    // Mirrors Discord's <t:…:X> styles.
    static string FormatStamp(DateTime t, char fmt) => fmt switch
    {
        't' => t.ToString("h:mm tt"),
        'T' => t.ToString("h:mm:ss tt"),
        'd' => t.ToString("MM/dd/yyyy"),
        'D' => t.ToString("MMMM d, yyyy"),
        'F' => t.ToString("dddd, MMMM d, yyyy h:mm tt"),
        'R' => Relative(t),
        _ => t.ToString("MMMM d, yyyy h:mm tt"),
    };

    // Discord's markup, flattened for the one-line places that can't render it: the DM list
    // preview, search results, notification toasts. Without this a preview reads
    // "You: <:be_robloxepicface:947417021>" instead of ":be_robloxepicface:".
    static readonly Regex PreviewAtom = new(
        @"<a?:(?<ename>\w+):\d+>|<@!?(?<uid>\d+)>|<@&(?<rid>\d+)>|<#(?<cid>\d+)>|<t:(?<ts>-?\d+)(?::[tTdDfFR])?>" +
        @"|</(?<cmd>[\w -]+):\d+>",
        RegexOptions.Compiled);

    public static string Flatten(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var flat = PreviewAtom.Replace(text, m =>
        {
            if (m.Groups["ename"].Success) return ":" + m.Groups["ename"].Value + ":";
            if (m.Groups["uid"].Success && ulong.TryParse(m.Groups["uid"].Value, out var uid))
                return "@" + (App.ResolveUserMention?.Invoke(uid)?.Item1 ?? "user");
            if (m.Groups["rid"].Success) return "@role";
            if (m.Groups["cmd"].Success) return "/" + m.Groups["cmd"].Value;
            if (m.Groups["cid"].Success && ulong.TryParse(m.Groups["cid"].Value, out var cid))
                return "#" + (App.ResolveChannelName?.Invoke(cid) ?? "channel");
            if (m.Groups["ts"].Success && long.TryParse(m.Groups["ts"].Value, out var ts))
                return DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime.ToString("g");
            return m.Value;
        }).Replace("\n", "  ").Trim();
        return Marker.Replace(flat, "").Trim();
    }

    // Reply previews, search hits and notifications are drawn in one font, so the emphasis markers
    // have nothing to turn into and were showing up literally ("**bold**").
    static readonly Regex Marker = new(@"\*{1,3}|_{1,3}|~~|\|\||`{1,3}|^\s*(#{1,3}|>|-#)\s+", RegexOptions.Compiled | RegexOptions.Multiline);

    public static string Relative(DateTime t)
    {
        var d = DateTime.Now - t;
        bool past = d.Ticks >= 0;
        var a = past ? d : -d;
        string unit =
            a.TotalSeconds < 60 ? $"{(int)a.TotalSeconds} second" :
            a.TotalMinutes < 60 ? $"{(int)a.TotalMinutes} minute" :
            a.TotalHours < 24 ? $"{(int)a.TotalHours} hour" :
            a.TotalDays < 30 ? $"{(int)a.TotalDays} day" :
            a.TotalDays < 365 ? $"{(int)(a.TotalDays / 30)} month" :
                                $"{(int)(a.TotalDays / 365)} year";
        int n = int.Parse(unit.Split(' ')[0]);
        if (n != 1) unit += "s";
        return past ? unit + " ago" : "in " + unit;
    }
}
