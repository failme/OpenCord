using System.Drawing;
using System.Drawing.Drawing2D;

namespace OpenCord;

// Discord's search overlay for the current server/channel. One round trip to the search endpoint
// per query (debounced), results listed with author + preview, click to jump to the message.
//
// The box understands Discord's filter syntax — `from:user`, `mentions:@user`, `has:image`,
// `before:date`, `in:channel`, `pinned` — parsed into chips with an × to drop a filter, plus a
// member autocomplete while typing from:/mentions:. Filters become real search parameters
// (author_id, mentions, has, …); only the plain words go into content=.
sealed class SearchPopup : Control
{
    static readonly string[] HasKinds = { "link", "embed", "file", "video", "image", "sound", "sticker", "poll" };

    readonly Session _session;
    readonly TextBox _box;
    readonly List<Result> _results = new();
    readonly List<Filter> _filters = new();
    readonly List<(ulong Id, string Name, string? Avatar, Color Color)> _sug = new();
    static readonly string[] Tips = { "from:", "has:image", "mentions:", "before:", "in:", "pinned" };
    readonly System.Windows.Forms.Timer _debounce = new() { Interval = 320 };
    readonly Scroller _scroll;
    int _hover = -1, _sugSel = -1, _chipHover = -1;
    bool _busy;
    string _content = "";
    ulong? _channelOverride;
    bool _serverWide;                        // Ctrl+Shift+F: search the whole server, no channel
    bool _suggesting;
    Point _mouse;

    static ToolStripDropDown? _host;

    sealed record Result(ulong Guild, ulong Channel, ulong MsgId, string Author, Color AuthorColor,
                         string Preview, string When);

    // A parsed filter. Token is the raw text the chip came from (restored when the × is clicked),
    // ParamKey/ParamValue the REST query parameter it maps to. Internal: SelfTest pins the grammar.
    internal sealed record Filter(string Token, string ParamKey, string ParamValue, string Label);

    SearchPopup(Session session)
    {
        _session = session;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Size = new Size(Ui.S(480), Ui.S(430));
        BackColor = Theme.Floating;
        _scroll = new Scroller(this);

        _box = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Theme.InputBg,
            ForeColor = Theme.Text,
            Font = Theme.Body,
            PlaceholderText = "Search",
        };
        _box.SetBounds(Ui.S(20), Ui.S(44), Width - Ui.S(40), Ui.S(26));
        _box.TextChanged += (_, _) =>
        {
            Parse(_box.Text);
            UpdateSuggestions();
            _chipHover = -1;
            _debounce.Stop();
            _debounce.Start();
            Invalidate();
        };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = Run(); };
        _box.KeyDown += OnKey;
        Controls.Add(_box);
    }

    public static void Show(Shell shell, Session session, bool serverWide = false)
    {
        Pop.Close(_host);
        var p = new SearchPopup(session) { _serverWide = serverWide };
        if (serverWide) p._box.PlaceholderText = "Search this server";
        var wa = Screen.FromControl(shell).WorkingArea;
        var pt = shell.PointToScreen(new Point((shell.ClientSize.Width - p.Width) / 2, Ui.S(60)));
        pt.X = Math.Clamp(pt.X, wa.Left + Ui.S(8), wa.Right - p.Width - Ui.S(8));
        pt.Y = Math.Clamp(pt.Y, wa.Top + Ui.S(8), wa.Bottom - p.Height - Ui.S(8));
        _host = Pop.Host(p, pt);
        p._box.Focus();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _debounce.Dispose();   // a closed popup must not keep debouncing
        base.Dispose(disposing);
    }

    // ── parsing ──

    static List<string> Tokenize(string q)
    {
        var toks = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool inQuote = false;
        foreach (var ch in q)
        {
            if (ch == '"') { inQuote = !inQuote; cur.Append(ch); continue; }
            if (char.IsWhiteSpace(ch) && !inQuote)
            {
                if (cur.Length > 0) { toks.Add(cur.ToString()); cur.Clear(); }
            }
            else cur.Append(ch);
        }
        if (cur.Length > 0) toks.Add(cur.ToString());
        return toks;
    }

    void Parse(string raw)
    {
        var c = App.Client;
        var guild = App.Guild;
        var (filters, content, channelOverride) = ParseQuery(raw,
            n => ResolveUser(n, c, guild), n => ResolveChannel(n, guild));
        _filters.Clear();
        _filters.AddRange(filters);
        _content = content;
        _channelOverride = channelOverride;
    }

    // The whole filter grammar, pure: given a raw query and two name resolvers (user -> id,
    // channel name -> id), split it into content text plus structured filters. Kept static and
    // side-effect free so SelfTest can pin every rule.
    internal static (List<Filter> Filters, string Content, ulong? ChannelOverride)
        ParseQuery(string raw, Func<string, ulong?> resolveUser, Func<string, ulong?> resolveChannel)
    {
        var filters = new List<Filter>();
        ulong? channelOverride = null;
        var text = new List<string>();
        foreach (var t in Tokenize(raw))
        {
            var bare = t.Length >= 2 && t[0] == '"' && t[^1] == '"' ? t[1..^1] : t;
            var idx = t.IndexOf(':');
            if (idx > 0)
            {
                var key = t[..idx].ToLowerInvariant();
                var val = t[(idx + 1)..].Trim().TrimStart('@');
                switch (key)
                {
                    case "from" when resolveUser(val) is { } uid:
                        filters.Add(new Filter(t, "author_id", uid.ToString(), "from: " + val));
                        continue;
                    case "mentions" when resolveUser(val) is { } uid:
                        filters.Add(new Filter(t, "mentions", uid.ToString(), "mentions: " + val));
                        continue;
                    case "has" when HasKinds.Contains(val.ToLowerInvariant()):
                        filters.Add(new Filter(t, "has", val.ToLowerInvariant(), "has: " + val));
                        continue;
                    case "before" when DateTime.TryParse(val, out var d):
                        filters.Add(new Filter(t, "before", d.ToString("yyyy-MM-dd"), "before: " + d.ToString("MMM d, yyyy")));
                        continue;
                    case "after" when DateTime.TryParse(val, out var d):
                        filters.Add(new Filter(t, "after", d.ToString("yyyy-MM-dd"), "after: " + d.ToString("MMM d, yyyy")));
                        continue;
                    case "in" when resolveChannel(val) is { } cid:
                        filters.Add(new Filter(t, "channel_id", cid.ToString(), "in: #" + val));
                        channelOverride = cid;
                        continue;
                    case "pinned" when val is "" or "true" or "false":
                        filters.Add(new Filter(t, "pinned", val is "false" ? "false" : "true", "pinned"));
                        continue;
                }
            }
            else if (t.Equals("pinned", StringComparison.OrdinalIgnoreCase))
            {
                // Discord accepts a bare `pinned` keyword as well as the pinned:true form.
                filters.Add(new Filter("pinned", "pinned", "true", "pinned"));
                continue;
            }
            text.Add(bare);
        }
        return (filters, string.Join(" ", text), channelOverride);
    }

    // Resolve a from:/mentions: value to a user id: exact display-name or username match against
    // the guild roster, then DM recipients, then the "me" shortcut. Unmatched names stay plain text.
    static ulong? ResolveUser(string name, UserClient? c, UserGuild? guild)
    {
        if (c == null || name.Length == 0) return null;
        if (name.Equals("me", StringComparison.OrdinalIgnoreCase)) return c.CurrentUser?.Id;
        var n = name.ToLowerInvariant();
        if (guild != null)
            foreach (var m in guild.Members)
                if (m.User != null
                    && ((m.DisplayName ?? "").ToLowerInvariant() == n || (m.User.Username ?? "").ToLowerInvariant() == n))
                    return m.User.Id;
        foreach (var dm in c.DMChannels)
            foreach (var r in dm.Recipients)
                if ((r.DisplayName ?? "").ToLowerInvariant() == n || (r.Username ?? "").ToLowerInvariant() == n)
                    return r.Id;
        return null;
    }

    static ulong? ResolveChannel(string name, UserGuild? guild)
    {
        if (guild == null) return null;
        var n = name.TrimStart('#').ToLowerInvariant();
        foreach (var ch in guild.Channels)
            if ((ch.Name ?? "").ToLowerInvariant() == n) return ch.Id;
        return null;
    }

    // ── autocomplete ──

    // While the token under the caret begins with from:/mentions:, list matching members (guild
    // roster first, then DM recipients, then "me") so Enter/Tab can complete the name.
    void UpdateSuggestions()
    {
        _sug.Clear();
        _sugSel = -1;
        _suggesting = false;
        var c = App.Client;
        if (c == null) return;
        var guild = App.Guild;
        var tok = _box.Text.Length > 0 && char.IsWhiteSpace(_box.Text[^1]) ? "" : _box.Text.Split(' ').Last();
        var idx = tok.IndexOf(':');
        if (idx <= 0) return;
        var key = tok[..idx].ToLowerInvariant();
        var val = tok[(idx + 1)..].TrimStart('@').ToLowerInvariant();
        if (key is not ("from" or "mentions") || val.Length == 0) return;
        // A complete, resolvable name needs no picker — picking one (or typing it exactly) must
        // close the dropdown, not keep it open under the finished filter chip.
        if (ResolveUser(val, c, guild) != null) return;

        var seen = new HashSet<ulong>();
        void Add(ulong id, string name, string? avatar, Color color)
        {
            if (!seen.Add(id) || _sug.Count >= 8) return;
            _sug.Add((id, name, avatar, color));
        }
        if (guild != null)
            foreach (var m in guild.Members)
            {
                if (m.User == null) continue;
                var dn = m.DisplayName ?? "";
                if (dn.ToLowerInvariant().StartsWith(val) || (m.User.Username ?? "").ToLowerInvariant().StartsWith(val))
                    Add(m.User.Id, dn.Length > 0 ? dn : m.User.Username, m.User.GetAvatarUrl(48), guild.NameColor(m.User.Id) ?? Theme.Muted);
            }
        foreach (var dm in c.DMChannels)
            foreach (var r in dm.Recipients)
                if ((r.DisplayName ?? "").ToLowerInvariant().StartsWith(val) || (r.Username ?? "").ToLowerInvariant().StartsWith(val))
                    Add(r.Id, r.DisplayName, r.GetAvatarUrl(48), Theme.Muted);
        if ("me".StartsWith(val)) Add(c.CurrentUser?.Id ?? 0, "me", null, Theme.Muted);

        _suggesting = _sug.Count > 0;
    }

    // Complete the from:/mentions: token with the selected member and let TextChanged re-run.
    void PickSuggestion()
    {
        if (_sugSel < 0 || _sugSel >= _sug.Count) return;
        var (id, name, _, _) = _sug[_sugSel];
        var raw = _box.Text;
        int sp = raw.LastIndexOf(' ');
        var head = sp >= 0 ? raw[..(sp + 1)] : "";
        var tok = sp >= 0 ? raw[(sp + 1)..] : raw;
        var idx = tok.IndexOf(':');
        if (idx <= 0) return;
        var key = tok[..idx];
        _box.Text = head + key + ":" + name;
        _box.SelectionStart = _box.Text.Length;
    }

    // ── querying ──

    async Task Run()
    {
        var client = App.Client;
        var guild = App.Guild;
        if (client == null) return;
        var channel = _channelOverride ?? _session.CurrentChannelId;
        // Server-wide search has no channel at all; a channel-restricted one needs one.
        if (!_serverWide && channel == 0) return;

        if (_content.Length < 2 && _filters.Count == 0)
        {
            _results.Clear();
            _hover = -1;
            _busy = false;
            Invalidate();
            return;
        }

        _busy = true;
        Invalidate();
        var extra = new Dictionary<string, string>();
        foreach (var f in _filters)
            if (f.ParamKey.Length > 0 && f.ParamKey != "channel_id")
                extra[f.ParamKey] = f.ParamValue;
        // channel_id (from an in: filter) is already the searched channel, passed as the arg above;
        // adding it again would duplicate the query parameter.

        // An in: filter overrides the search scope to that channel, so a Ctrl+Shift+F search
        // degrades to channel scope (channel_id is passed as the arg; serverWide would drop it).
        var hits = await client.Rest.SearchAsync(guild?.Id, channel, _content, extra: extra,
                                                  serverWide: _serverWide && _channelOverride == null);
        _results.Clear();
        foreach (var m in hits)
        {
            var author = m.Member?.DisplayName ?? m.Author?.DisplayName ?? "Unknown";
            var color = guild?.NameColor(m.Author?.Id ?? 0) ?? Theme.Muted;
            _results.Add(new Result(m.GuildId ?? guild?.Id ?? 0, m.ChannelId, m.Id, author, color,
                                    Markdown.Flatten(m.Content).Replace("\n", " "), MessageRow.Stamp(m.Timestamp)));
        }
        if (_results.Count > 60) _results.RemoveRange(60, _results.Count - 60);
        _hover = _results.Count > 0 ? 0 : -1;
        _busy = false;
        Invalidate();
    }

    void Pick()
    {
        if (_hover < 0 || _hover >= _results.Count) return;
        var r = _results[_hover];
        Pop.Close(_host);
        _host = null;
        _session.GoToMessage(r.Guild, r.Channel, r.MsgId);
    }

    // Removing a chip restores the raw query without its token, which re-parses and re-runs.
    void RemoveChip(Filter f)
    {
        var toks = Tokenize(_box.Text).Where(t => t != f.Token).ToList();
        _box.Text = string.Join(" ", toks);
    }

    // ── keyboard ──

    void OnKey(object? s, KeyEventArgs e)
    {
        if (_suggesting)
        {
            if (e.KeyCode == Keys.Down) { _sugSel = (_sugSel + 1) % _sug.Count; Invalidate(); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Up) { _sugSel = _sugSel <= 0 ? _sug.Count - 1 : _sugSel - 1; Invalidate(); e.SuppressKeyPress = true; }
            else if (e.KeyCode is Keys.Enter or Keys.Tab) { e.SuppressKeyPress = true; PickSuggestion(); }
            else if (e.KeyCode == Keys.Escape) { _suggesting = false; _sug.Clear(); Invalidate(); e.SuppressKeyPress = true; }
            return;
        }
        if (e.KeyCode == Keys.Down && _results.Count > 0) { _hover = (_hover + 1) % _results.Count; Invalidate(); e.SuppressKeyPress = true; }
        else if (e.KeyCode == Keys.Up && _results.Count > 0) { _hover = _hover <= 0 ? _results.Count - 1 : _hover - 1; Invalidate(); e.SuppressKeyPress = true; }
        else if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Pick(); }
    }

    // ── layout ──

    int ResultsTop => _filters.Count > 0 ? Ui.S(116) : Ui.S(88);
    int SuggestionTop => Ui.S(80);
    int SuggestionRow => Ui.S(36);

    int RowAt(Point p)
    {
        if (p.Y < ResultsTop) return -1;
        int i = (p.Y - ResultsTop + _scroll.Value) / Ui.S(54);
        return i >= 0 && i < _results.Count ? i : -1;
    }

    // Chips lay out left to right, wrapping. Shared by hit-testing and painting so a duplicate
    // filter ("pinned pinned") can't collapse two chips onto one index — both walk by position.
    static Rectangle ChipBox(List<Filter> filters, int width, int i)
    {
        int x = Ui.S(16), y = Ui.S(80);
        for (int k = 0; k <= i; k++)
        {
            var sz = Ui.Measure(filters[k].Label, Theme.Small);
            int w = sz.Width + Ui.S(30);
            if (x + w > width - Ui.S(16) && x > Ui.S(16)) { x = Ui.S(16); y += Ui.S(26); }
            if (k == i) return new Rectangle(x, y, w, Ui.S(22));
            x += w + Ui.S(6);
        }
        return Rectangle.Empty;
    }

    int ChipAt(Point p)
    {
        if (p.Y < Ui.S(80) || p.Y >= ResultsTop - Ui.S(6)) return -1;
        for (int i = 0; i < _filters.Count; i++)
            if (ChipBox(_filters, Width, i).Contains(p)) return i;
        return -1;
    }

    Rectangle ChipXBox(int i) =>
        new(ChipBox(_filters, Width, i).Right - Ui.S(20), ChipBox(_filters, Width, i).Y + Ui.S(3), Ui.S(16), Ui.S(16));

    int SuggestionAt(Point p)
    {
        if (!_suggesting || p.Y < SuggestionTop) return -1;
        int i = (p.Y - SuggestionTop) / SuggestionRow;
        return i >= 0 && i < _sug.Count ? i : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _mouse = e.Location;
        int h = RowAt(e.Location);
        int ch = ChipAt(e.Location);
        int s = SuggestionAt(e.Location);
        if (_suggesting)
        {
            if (s != _sugSel) { _sugSel = s; Invalidate(); }
            Cursor = s >= 0 ? Cursors.Hand : Cursors.Default;
        }
        else
        {
            if (h != _hover || ch != _chipHover) { _hover = h; _chipHover = ch; Invalidate(); }
            Cursor = (h >= 0 || ch >= 0) ? Cursors.Hand : Cursors.Default;
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (_suggesting)
        {
            if (SuggestionAt(e.Location) >= 0) PickSuggestion();
        }
        else
        {
            int ci = ChipAt(e.Location);
            if (ci >= 0 && ChipXBox(ci).Contains(e.Location)) RemoveChip(_filters[ci]);
            else if (RowAt(e.Location) >= 0) Pick();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _chipHover = -1;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        _scroll.Wheel(e.Delta, Math.Max(0, _results.Count * Ui.S(54) - (Height - ResultsTop)));
        base.OnMouseWheel(e);
    }

    // ── paint ──

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.Fill(g, ClientRectangle, Theme.Floating);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Ui.Text(g, "Search", Theme.SmallMedium, new Rectangle(Ui.S(20), Ui.S(12), Width - Ui.S(40), Ui.S(18)),
                Theme.Faint, TextFormatFlags.NoPadding);
        Ui.FillRound(g, new Rectangle(Ui.S(16), Ui.S(36), Width - Ui.S(32), Ui.S(40)), Ui.S(6), Theme.InputBg);
        Ui.Text(g, "⌕", Theme.Body, new Rectangle(Ui.S(20), Ui.S(44), Ui.S(22), Ui.S(24)), Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        // Filter chips under the box.
        for (int i = 0; i < _filters.Count; i++)
        {
            var chip = ChipBox(_filters, Width, i);
            var xb = new Rectangle(chip.Right - Ui.S(20), chip.Y + Ui.S(3), Ui.S(16), Ui.S(16));
            bool hot = _chipHover == i && xb.Contains(_mouse);
            Ui.FillRound(g, chip, Ui.S(11), _chipHover == i ? Theme.SurfaceHigh : Theme.Surface);
            using (var pen = new Pen(Theme.Border))
            using (var path = Ui.RoundRect(chip, Ui.S(11)))
                g.DrawPath(pen, path);
            Ui.Text(g, _filters[i].Label, Theme.Small, new Rectangle(chip.X + Ui.S(9), chip.Y, chip.Width - Ui.S(30), chip.Height),
                    Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            Ui.Text(g, "×", Theme.Small, xb, hot ? Theme.Strong : Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        // from:/mentions: autocomplete panel.
        if (_suggesting)
        {
            int n = Math.Min(_sug.Count, 6);
            var panel = new Rectangle(Ui.S(8), SuggestionTop, Width - Ui.S(16), n * SuggestionRow + Ui.S(8));
            Ui.FillRound(g, panel, Ui.S(8), Theme.Chat);
            using (var pen = new Pen(Theme.Border))
            using (var path = Ui.RoundRect(panel, Ui.S(8)))
                g.DrawPath(pen, path);
            for (int i = 0; i < n; i++)
            {
                var (id, name, avatar, color) = _sug[i];
                var row = new Rectangle(panel.X + Ui.S(4), panel.Y + Ui.S(4) + i * SuggestionRow, panel.Width - Ui.S(8), SuggestionRow);
                if (i == _sugSel) Ui.FillRound(g, row, Ui.S(6), Theme.SidebarSelected);
                var ab = new Rectangle(row.X + Ui.S(8), row.Y + Ui.S(4), Ui.S(28), Ui.S(28));
                Ui.Avatar(g, avatar == null ? null : Media.Get(avatar, this), ab, Theme.Surface);
                Ui.Text(g, name, Theme.Body, new Rectangle(ab.Right + Ui.S(10), row.Y, row.Width - ab.Right - Ui.S(16), row.Height),
                        color, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
            return;
        }

        if (_busy)
        {
            Ui.Text(g, "Searching…", Theme.Body, new Rectangle(Ui.S(16), ResultsTop + Ui.S(28), Width - Ui.S(32), Ui.S(24)),
                    Theme.Muted, TextFormatFlags.HorizontalCenter);
            return;
        }

        var clip = g.Save();
        g.SetClip(new Rectangle(0, ResultsTop, Width, Height - ResultsTop));
        for (int i = 0; i < _results.Count; i++)
        {
            int ry = ResultsTop + i * Ui.S(54) - _scroll.Value;
            if (ry + Ui.S(54) < ResultsTop || ry > Height) continue;
            DrawRow(g, ry, i);
        }
        g.Restore(clip);

        if (_filters.Count == 0 && _content.Length == 0 && _results.Count == 0 && !_busy)
        {
            Ui.Text(g, "Search for messages in this channel", Theme.Body,
                    new Rectangle(Ui.S(16), ResultsTop + Ui.S(20), Width - Ui.S(32), Ui.S(24)),
                    Theme.Muted, TextFormatFlags.HorizontalCenter);
            // Filter tip pills, like Discord's empty state.
            int px = Ui.S(16);
            int py = ResultsTop + Ui.S(52);
            foreach (var label in Tips)
            {
                var sz = Ui.Measure(label, Theme.Small);
                int w = sz.Width + Ui.S(18);
                if (px + w > Width - Ui.S(16) && px > Ui.S(16)) { px = Ui.S(16); py += Ui.S(26); }
                var pill = new Rectangle(px, py, w, Ui.S(20));
                if (_mouse.Y >= py && _mouse.Y < py + Ui.S(20) && _mouse.X >= px && _mouse.X < px + w)
                    Ui.FillRound(g, pill, Ui.S(10), Theme.SurfaceHigh);
                else Ui.FillRound(g, pill, Ui.S(10), Theme.Surface);
                Ui.Text(g, label, Theme.Small, pill, Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                px += w + Ui.S(6);
            }
        }
        else if (_results.Count == 0 && !_busy)
            Ui.Text(g, "No results", Theme.Body, new Rectangle(Ui.S(16), ResultsTop + Ui.S(28), Width - Ui.S(32), Ui.S(24)),
                    Theme.Muted, TextFormatFlags.HorizontalCenter);
    }

    void DrawRow(Graphics g, int y, int i)
    {
        var r = _results[i];
        bool sel = _hover == i;
        var row = new Rectangle(Ui.S(8), y, Width - Ui.S(16), Ui.S(50));
        if (sel) Ui.FillRound(g, row, Ui.S(6), Theme.SidebarSelected);

        int tx = row.X + Ui.S(12);
        Ui.Text(g, r.Author, Theme.BodyMedium, new Rectangle(tx, row.Y + Ui.S(4), Ui.S(160), Ui.S(20)),
                r.AuthorColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        Ui.Text(g, r.When, Theme.Small, new Rectangle(row.Right - Ui.S(110), row.Y + Ui.S(4), Ui.S(96), Ui.S(20)),
                Theme.Faint, TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
        Ui.Text(g, r.Preview, Theme.Body, new Rectangle(tx, row.Y + Ui.S(26), row.Width - Ui.S(24), Ui.S(20)),
                sel ? Theme.Text : Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

// The pinned-messages panel for the current channel: one row per pin, click to jump.
sealed class PinsPopup : Control
{
    readonly Session _session;
    readonly List<(ulong Guild, ulong Channel, ulong MsgId, string Author, Color Color, string Preview, string When)> _pins = new();
    readonly Scroller _scroll;
    int _hover = -1;
    bool _busy;

    static ToolStripDropDown? _host;

    PinsPopup(Session session)
    {
        _session = session;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Size = new Size(Ui.S(440), Ui.S(360));
        BackColor = Theme.Floating;
        _scroll = new Scroller(this);
    }

    public static async Task ShowAsync(Shell shell, Session session)
    {
        Pop.Close(_host);
        var p = new PinsPopup(session);
        _host = Pop.Host(p, shell.PointToScreen(new Point((shell.ClientSize.Width - p.Width) / 2, Ui.S(60))));
        await p.Load();
    }

    async Task Load()
    {
        var client = App.Client;
        var guild = App.Guild;
        var channel = _session.CurrentChannelId;
        if (client == null || channel == 0) return;
        _busy = true;
        Invalidate();
        var msgs = await client.Rest.GetPinnedMessagesAsync(channel);
        _pins.Clear();
        foreach (var m in msgs)
        {
            var author = m.Member?.DisplayName ?? m.Author?.DisplayName ?? "Unknown";
            var color = guild?.NameColor(m.Author?.Id ?? 0) ?? Theme.Muted;
            _pins.Add((m.GuildId ?? guild?.Id ?? 0, m.ChannelId, m.Id, author, color,
                       Markdown.Flatten(m.Content).Replace("\n", " "), MessageRow.Stamp(m.Timestamp)));
        }
        _busy = false;
        Invalidate();
    }

    void Pick()
    {
        if (_hover < 0 || _hover >= _pins.Count) return;
        var (g, c, id, _, _, _, _) = _pins[_hover];
        Pop.Close(_host);
        _host = null;
        _session.GoToMessage(g, c, id);
    }

    int RowAt(Point p)
    {
        if (p.Y < Ui.S(56)) return -1;
        int i = (p.Y - Ui.S(56) + _scroll.Value) / Ui.S(54);
        return i >= 0 && i < _pins.Count ? i : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = RowAt(e.Location);
        if (h != _hover) { _hover = h; Invalidate(); }
        Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (RowAt(e.Location) >= 0) Pick();
        base.OnMouseDown(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        _scroll.Wheel(e.Delta, Math.Max(0, _pins.Count * Ui.S(54) - (Height - Ui.S(56))));
        base.OnMouseWheel(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.Fill(g, ClientRectangle, Theme.Floating);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Ui.Text(g, "Pinned Messages", Theme.SmallMedium, new Rectangle(Ui.S(20), Ui.S(14), Width - Ui.S(40), Ui.S(20)),
                Theme.Faint, TextFormatFlags.NoPadding);

        if (_busy)
        {
            Ui.Text(g, "Loading…", Theme.Body, new Rectangle(Ui.S(16), Ui.S(80), Width - Ui.S(32), Ui.S(24)),
                    Theme.Muted, TextFormatFlags.HorizontalCenter);
            return;
        }

        var clip = g.Save();
        g.SetClip(new Rectangle(0, Ui.S(56), Width, Height - Ui.S(56)));
        for (int i = 0; i < _pins.Count; i++)
        {
            int y = Ui.S(56) + i * Ui.S(54) - _scroll.Value;
            if (y + Ui.S(54) < Ui.S(56) || y > Height) continue;
            var (_, _, _, author, color, preview, when) = _pins[i];
            bool sel = _hover == i;
            var row = new Rectangle(Ui.S(8), y, Width - Ui.S(16), Ui.S(50));
            if (sel) Ui.FillRound(g, row, Ui.S(6), Theme.SidebarSelected);
            int tx = row.X + Ui.S(12);
            Ui.Text(g, author, Theme.BodyMedium, new Rectangle(tx, row.Y + Ui.S(4), Ui.S(150), Ui.S(20)),
                    color, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            Ui.Text(g, when, Theme.Small, new Rectangle(row.Right - Ui.S(110), row.Y + Ui.S(4), Ui.S(96), Ui.S(20)),
                    Theme.Faint, TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
            Ui.Text(g, preview, Theme.Body, new Rectangle(tx, row.Y + Ui.S(26), row.Width - Ui.S(24), Ui.S(20)),
                    sel ? Theme.Text : Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        g.Restore(clip);

        if (_pins.Count == 0 && !_busy)
            Ui.Text(g, "No pinned messages in this channel", Theme.Body,
                    new Rectangle(Ui.S(16), Ui.S(80), Width - Ui.S(32), Ui.S(24)),
                    Theme.Muted, TextFormatFlags.HorizontalCenter);
    }
}

// The threads panel for the current channel: one row per active thread, click to jump into it.
// Unlike the pins panel this is fully local — the thread list rides the gateway's THREAD_LIST_SYNC.
sealed class ThreadsPopup : Control
{
    readonly Session _session;
    readonly List<(ulong Id, string Name, int Messages, int Members, string When)> _threads = new();
    readonly Scroller _scroll;
    int _hover = -1;

    static ToolStripDropDown? _host;

    ThreadsPopup(Session session)
    {
        _session = session;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Size = new Size(Ui.S(440), Ui.S(320));
        BackColor = Theme.Floating;
        _scroll = new Scroller(this);

        var channel = _session.CurrentChannelId;
        var guild = App.Guild;
        if (guild != null)
            foreach (var t in guild.Threads.Where(t => t.ParentId == channel && t.Metadata?.Archived != true)
                                           .OrderByDescending(t => t.LastMessageId ?? 0))
                _threads.Add((t.Id, t.Name, t.TotalMessageSent, t.MemberCount,
                              t.LastMessageId is { } l ? MessageRow.Stamp(SnowflakeTime(l)) : ""));
        if (_threads.Count > 60) _threads.RemoveRange(60, _threads.Count - 60);
    }

    // A snowflake's top 41 bits are milliseconds since the Discord epoch — the only timestamp a
    // thread row carries without an extra fetch.
    static DateTimeOffset SnowflakeTime(ulong id) =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)((id >> 22) + 1420070400000UL));

    public static void Show(Shell shell, Session session)
    {
        Pop.Close(_host);
        var p = new ThreadsPopup(session);
        _host = Pop.Host(p, shell.PointToScreen(new Point((shell.ClientSize.Width - p.Width) / 2, Ui.S(60))));
    }

    void Pick()
    {
        if (_hover < 0 || _hover >= _threads.Count) return;
        var (id, _, _, _, _) = _threads[_hover];
        Pop.Close(_host);
        _host = null;
        _session.GoToMessage(App.Guild?.Id ?? 0, id, 0);
    }

    int RowAt(Point p)
    {
        if (p.Y < Ui.S(56)) return -1;
        int i = (p.Y - Ui.S(56) + _scroll.Value) / Ui.S(52);
        return i >= 0 && i < _threads.Count ? i : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = RowAt(e.Location);
        if (h != _hover) { _hover = h; Invalidate(); }
        Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (RowAt(e.Location) >= 0) Pick();
        base.OnMouseDown(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        _scroll.Wheel(e.Delta, Math.Max(0, _threads.Count * Ui.S(52) - (Height - Ui.S(56))));
        base.OnMouseWheel(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.Fill(g, ClientRectangle, Theme.Floating);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Ui.Text(g, "Threads", Theme.SmallMedium, new Rectangle(Ui.S(20), Ui.S(14), Width - Ui.S(40), Ui.S(20)),
                Theme.Faint, TextFormatFlags.NoPadding);

        var clip = g.Save();
        g.SetClip(new Rectangle(0, Ui.S(56), Width, Height - Ui.S(56)));
        for (int i = 0; i < _threads.Count; i++)
        {
            int y = Ui.S(56) + i * Ui.S(52) - _scroll.Value;
            if (y + Ui.S(52) < Ui.S(56) || y > Height) continue;
            var (_, name, messages, members, when) = _threads[i];
            bool sel = _hover == i;
            var row = new Rectangle(Ui.S(8), y, Width - Ui.S(16), Ui.S(48));
            if (sel) Ui.FillRound(g, row, Ui.S(6), Theme.SidebarSelected);

            int icon = Ui.S(20);
            var ib = new RectangleF(row.X + Ui.S(12), row.Y + Ui.S(8), icon, icon);
            Svg.SvgFill(g, Icons.ThreadLine, ib, Theme.ChannelIcon);

            int tx = row.X + Ui.S(44);
            Ui.Text(g, name, Theme.BodyMedium, new Rectangle(tx, row.Y + Ui.S(5), row.Width - tx - Ui.S(120), Ui.S(20)),
                    sel ? Theme.Strong : Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            Ui.Text(g, $"{messages} messages · {members} members", Theme.Small,
                    new Rectangle(tx, row.Y + Ui.S(25), row.Width - tx - Ui.S(120), Ui.S(16)),
                    Theme.Faint, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            Ui.Text(g, when, Theme.Small,
                    new Rectangle(row.Right - Ui.S(110), row.Y + Ui.S(5), Ui.S(96), Ui.S(20)),
                    Theme.Faint, TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
        }
        g.Restore(clip);

        if (_threads.Count == 0)
            Ui.Text(g, "No active threads in this channel", Theme.Body,
                    new Rectangle(Ui.S(16), Ui.S(80), Width - Ui.S(32), Ui.S(24)),
                    Theme.Muted, TextFormatFlags.HorizontalCenter);
    }
}
