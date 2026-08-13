using System.Drawing;
using System.Drawing.Drawing2D;

namespace OpenCord;

// The chat pane: channel header, the message list, and the composer.
sealed class ChatView : Panel
{
    public sealed record ChannelInfo(ulong Id, string Name, int Type, string? Topic = null,
                                     string? IconUrl = null, Presence Presence = Presence.Offline);

    public ChatHeader Header { get; } = new() { Dock = DockStyle.Top };
    public MessageList List { get; } = new() { Dock = DockStyle.Fill };
    public Composer Composer { get; } = new() { Dock = DockStyle.Bottom };

    /// (text, replyToId or 0). The view owns the reply state so callers never have to track it.
    public event Action<string, ulong>? Send;
    public event Action? NeedOlder;
    public event Action? Typing;

    public ChannelInfo? Channel { get; private set; }

    public ChatView()
    {
        BackColor = Theme.Chat;
        Controls.Add(List);         // fill first here: Top/Bottom siblings are added after, and
        Controls.Add(Composer);     // WinForms gives later-added docked controls the outer edge
        Controls.Add(Header);

        Composer.Send += t => { Send?.Invoke(t, Composer.ReplyTo); Composer.ClearReply(); ClearDraft(); };
        Composer.Typing += () => Typing?.Invoke();
        Composer.EditLast += () =>
        {
            // Up on an empty box edits your last message (Discord's behaviour).
            if (List.LastOwnMessage is { } m) Composer.BeginEdit(m);
        };
        List.NeedOlder += () => NeedOlder?.Invoke();
        List.FailedAction += (m, retry) => FailedAction?.Invoke(m, retry);
        List.ReplyRequested += m => { Composer.SetReply(m); Composer.FocusInput(); };
        List.EditRequested += m => Composer.BeginEdit(m);
        Header.MembersToggled += () => MembersToggled?.Invoke();
        Header.SearchRequested += q => SearchRequested?.Invoke(q);
        Header.PinsRequested += () => PinsRequested?.Invoke();
        Header.ThreadsRequested += () => ThreadsRequested?.Invoke();
        Header.CallRequested += v => CallRequested?.Invoke(v);
    }

    public event Action? MembersToggled;
    public event Action<string>? SearchRequested;
    public event Action? PinsRequested;
    public event Action? ThreadsRequested;
    public event Action<bool>? CallRequested;   // true = video, false = voice

    /// The header's members toggle pressed state — the 1:1 DM profile panel on or off. Guild rows
    /// pass a constant true; only the DM toggle flips.
    public void SetMembersActive(bool on) => Header.SetMembersActive(on);

    // What the composer held in each channel we have visited this session. Discord parks the draft
    // in the channel you typed it in; one shared box meant text followed you across a switch.
    readonly Dictionary<ulong, (string Text, UserMessage? Reply)> _drafts = new();

    public void SetChannel(ChannelInfo info)
    {
        // Park the outgoing channel's draft before anything clears the box. An in-progress *edit*
        // is not a draft — Discord drops it on a channel switch, so it is deliberately not saved.
        if (Channel is { } prev && prev.Id != info.Id)
        {
            if (!Composer.IsEditing && (Composer.Text.Length > 0 || Composer.Reply != null))
                _drafts[prev.Id] = (Composer.Text, Composer.Reply);
            else
                _drafts.Remove(prev.Id);
        }

        Channel = info;
        Header.Set(info);
        Composer.Placeholder = info.Type is 1 or 3 ? "Message @" + info.Name : "Message #" + info.Name;
        Composer.ClearReply();
        Composer.SetTyping(Array.Empty<string>());
        Composer.ResetChannel();   // slash command cache is per-channel

        var draft = _drafts.GetValueOrDefault(info.Id);
        Composer.Text = draft.Text ?? "";
        if (draft.Reply != null) Composer.SetReply(draft.Reply);

        Header.Invalidate();
        Composer.Invalidate();
    }

    /// Sending empties the draft, so re-entering the channel must not resurrect it.
    void ClearDraft() { if (Channel is { } c) _drafts.Remove(c.Id); }

    /// (message, retry) — the Retry / Delete links on a row whose send failed.
    public event Action<UserMessage, bool>? FailedAction;

    public void FailPending(string nonce, string? reason) => List.FailPending(nonce, reason);
    public UserMessage? TakeFailed(string nonce) => List.TakeFailed(nonce);

    public void SetMessages(IReadOnlyList<UserMessage> msgs, ulong lastRead = 0) => List.SetMessages(msgs, lastRead);
    public void PrependOlder(IReadOnlyList<UserMessage> msgs) => List.PrependOlder(msgs);
    public void OlderDone() => List.OlderDone();
    public void Append(UserMessage m) => List.Append(m);
    public void Update(UserMessage m) => List.Update(m);
    public void Remove(ulong id) => List.Remove(id);
    public void SetTyping(IReadOnlyList<string> names) => Composer.SetTyping(names);
    public void FocusComposer() => Composer.FocusInput();
    public bool AtBottom => List.Pinned;
    public ulong NewestId => List.NewestId;
    public void ScrollToBottom() => List.ScrollToBottom();
    public UserMessage? LastOwnMessage => List.LastOwnMessage;
    public string SelectedText => List.SelectedText;
    public bool HasSelection => List.HasSelection;
    public void ClearSelection() => List.ClearSelection();
    public bool ScrollKey(Keys key) => List.ScrollKey(key);
}

// ── header ──────────────────────────────────────────────────────────────────────────────────────

sealed class ChatHeader : Control
{
    ChatView.ChannelInfo? _info;
    readonly List<(Rectangle Box, string Icon, string Tip, bool Active, Action Click)> _buttons = new();
    int _hot = -1;

    // The 1:1 DM profile panel toggle. Separate from the button row because the row is cached —
    // the pressed state has to survive a rebuild, and flipping it must rebuild right away.
    bool _membersActive;

    public void SetMembersActive(bool on)
    {
        if (_membersActive == on) return;
        _membersActive = on;
        BuildButtons();
        Invalidate();
    }

    public event Action? MembersToggled;
    public event Action<string>? SearchRequested;
    public event Action? PinsRequested;
    public event Action? ThreadsRequested;
    public event Action<bool>? CallRequested;   // true = video, false = voice

    public ChatHeader()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = Ui.S(M.HeaderHeight);
        BackColor = Theme.Chat;
    }

    // The button row differs per channel kind (Threads + Members in a guild, call buttons in a DM),
    // and it is cached — so it has to be dropped here or the header keeps the *previous* channel's
    // buttons until something resizes the window. That is what left a Members toggle sitting on a
    // DM header, still wired to the closure that toggles a guild roster.
    public void Set(ChatView.ChannelInfo info) { _info = info; _buttons.Clear(); _hot = -1; Invalidate(); }

    // The search *box* — the refresh replaced the magnifier icon with a real 244x32 field pinned to
    // the right of the header, and moved Inbox and Help out to the top bar.
    Rectangle _searchBox;
    bool _searchHot;

    void BuildButtons()
    {
        _buttons.Clear();
        if (_info == null) return;

        int box = Ui.S(M.HeaderBtn), icon = Ui.S(M.HeaderIcon), pitch = Ui.S(M.HeaderBtnPitch);
        int sw = Ui.S(M.HeaderSearchW), sh = Ui.S(M.HeaderSearchH);
        _searchBox = new Rectangle(Width - Ui.S(M.HeaderPadRight) - sw, (Height - sh) / 2, sw, sh);

        // Buttons run right-to-left from the search box, 12px clear of it.
        int x = _searchBox.X - Ui.S(12) - box;

        void Add(string ic, string tip, Action click, bool active = false)
        {
            _buttons.Insert(0, (new Rectangle(x, (Height - box) / 2, box, box), ic, tip, active, click));
            x -= pitch;
        }

        // The members toggle. A 1:1 DM gets it too — it opens the right-hand profile panel (the
        // live client's "Show Member List"), and its pressed state mirrors the panel's visibility.
        // Only a group DM has nothing to toggle: its roster is always on.
        if (_info.Type != 3)
            Add(Icons.People, _info.Type == 1 ? "Profile" : "Members",
                () => MembersToggled?.Invoke(), active: _info.Type == 1 && _membersActive);
        Add(Icons.PinLine, "Pinned Messages", () => PinsRequested?.Invoke());
        Add(Icons.BellLine, "Notification Settings", () =>
        {
            if (_info != null)
                NotifSettings.Show(this, PointToScreen(new Point(Width - Ui.S(310), Height)),
                                   _info.Id, _info.Type is 1 or 3);
        });
        // A DM gets call buttons where a guild channel gets Threads; both sit leftmost.
        if (_info.Type is not (1 or 3)) Add(Icons.ThreadLine, "Threads", () => ThreadsRequested?.Invoke());
        else { Add(Icons.VideoLine, "Start Video Call", () => CallRequested?.Invoke(true)); Add(Icons.PhoneLine, "Start Voice Call", () => CallRequested?.Invoke(false)); }
    }

    /// Placeholder text matches the live client: the guild's name in a server, the person's in a DM.
    string SearchHint => _info == null ? "Search"
                       : _info.Type is 1 or 3 ? "Search" : "Search " + (App.Guild?.Name ?? "server");

    protected override void OnSizeChanged(EventArgs e) { BuildButtons(); base.OnSizeChanged(e); }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = _buttons.FindIndex(b => b.Box.Contains(e.Location));
        bool sh = _searchBox.Contains(e.Location);
        if (h != _hot || sh != _searchHot)
        {
            _hot = h;
            _searchHot = sh;
            Tip.Show(this, h >= 0 ? _buttons[h].Tip : null, h >= 0 ? _buttons[h].Box : Rectangle.Empty);
            Invalidate();
        }
        Cursor = h >= 0 ? Cursors.Hand : sh ? Cursors.IBeam : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hot != -1 || _searchHot) { _hot = -1; _searchHot = false; Tip.Hide(); Invalidate(); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && _hot >= 0) _buttons[_hot].Click();
        else if (e.Button == MouseButtons.Left && _searchBox.Contains(e.Location)) SearchRequested?.Invoke("");
        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.Fill(g, ClientRectangle, Theme.Chat);
        if (_buttons.Count == 0) BuildButtons();

        int icon = Ui.S(M.HeaderIcon);
        int x = Ui.S(M.HeaderPadLeft);
        var info = _info;
        if (info != null)
        {
            // A DM shows a 20px avatar, the same size as a channel's glyph — measured off the live
            // header, where both sit at 16 from the pane's left with an 8px gap to the name. It was
            // 24 here, which made a DM's header sit a few px wider than a channel's.
            if (info.Type is 1 or 3)
            {
                int av = Ui.S(20);
                Ui.Avatar(g, Media.Get(info.IconUrl, this), new Rectangle(x, (Height - av) / 2, av, av), Theme.Surface, this);
                Ui.PresenceDot(g, new Rectangle(x, (Height - av) / 2, av, av), info.Presence, Theme.Chat, Ui.S(9));
                x += av + Ui.S(8);
            }
            else
            {
                Svg.SvgFill(g, info.Type is 11 or 12 ? Icons.ThreadLine
                            : info.Type == 2 ? Icons.Speaker
                            : info.Type == 5 ? Icons.Megaphone
                            : Icons.Hash,
                            new RectangleF(x, (Height - icon) / 2f, icon, icon), Theme.ChannelIcon);
                x += icon + Ui.S(8);
            }
            int nameW = Ui.Measure(info.Name, Theme.BodyMedium).Width;
            int limit = (_buttons.Count > 0 ? _buttons[0].Box.X : Width) - x - Ui.S(16);
            Ui.Text(g, info.Name, Theme.BodyMedium, new Rectangle(x, 0, Math.Min(nameW, limit), Height),
                    Theme.Strong, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            if (!string.IsNullOrWhiteSpace(info.Topic) && limit > nameW + Ui.S(40))
            {
                int tx = x + nameW + Ui.S(16);
                Ui.Fill(g, new Rectangle(tx - Ui.S(8), (Height - Ui.S(20)) / 2, 1, Ui.S(20)), Theme.Border);
                Ui.Text(g, Markdown.Flatten(info.Topic), Theme.Small,
                        new Rectangle(tx, 0, x + limit - tx, Height), Theme.Faint,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }

        // Icons are 20 inside a 32 hit box, not 32 — see M.HeaderIcon. A pressed toggle gets the
        // same quiet fill the live client gives its toggled header icons.
        int ib = Ui.S(M.HeaderIcon);
        for (int i = 0; i < _buttons.Count; i++)
        {
            var (box, ic, _, active, _) = _buttons[i];
            if (active) Ui.FillRound(g, box, Ui.S(8), Theme.RowHover);
            var col = _hot == i || active ? Theme.Text : Theme.Muted;
            Icons.Draw(g, ic, new Rectangle(box.X + (box.Width - ib) / 2, box.Y + (box.Height - ib) / 2, ib, ib), col);
        }

        if (!_searchBox.IsEmpty) PaintSearch(g);

        Ui.Fill(g, new Rectangle(0, Height - 1, Width, 1), Theme.BorderSubtle);
    }

    void PaintSearch(Graphics g)
    {
        Ui.FillRound(g, _searchBox, Ui.S(8), _searchHot ? Theme.Field : Theme.SearchBg);
        int mag = Ui.S(16);
        var mr = new Rectangle(_searchBox.Right - Ui.S(8) - mag, _searchBox.Y + (_searchBox.Height - mag) / 2, mag, mag);
        Icons.Draw(g, Icons.SearchLine, mr, Theme.Muted);
        // The field's type is 14px/500, not the 12px this used — the placeholder keeps its own
        // dimmer colour, which is the one thing here that is not the input's own.
        Ui.Text(g, SearchHint, Theme.Category,
                new Rectangle(_searchBox.X + Ui.S(8), _searchBox.Y, mr.X - _searchBox.X - Ui.S(14), _searchBox.Height),
                Theme.Placeholder, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

// ── message list ────────────────────────────────────────────────────────────────────────────────

// Cozy-mode message list. Rows are laid out once per width change and painted from the cached
// result — re-parsing markdown inside OnPaint is what makes a chat view stutter while scrolling.
sealed class MessageList : Control
{
    readonly List<MessageRow> _rows = new();
    readonly HashSet<int> _revealed = new();     // spoiler groups the user has clicked
    readonly HashSet<ulong> _shownSpoilers = new();
    readonly Frames _glide;                      // vblank-paced; see Frames for why not a Timer
    // Poll countdowns tick down in whole units ("2h left" -> "1h left"); a quiet repaint every
    // half minute keeps them honest without a per-frame cost. Only poll rows are re-laid out.
    readonly System.Windows.Forms.Timer _pollTick = new() { Interval = 30000 };
    // New-arrival entrance: the newest row glides down 6px and fades in over ~180ms, matching
    // Discord's message slide. Driven off the same glide timer as scrolling.
    float _entrance;

    int _contentH, _hover = -1, _laidOutFor = -1;
    // The scroll offset and the glide chasing it. The glide is a fixed-duration retarget, not an
    // eased chase of an accumulated target: each wheel event re-aims from where the list currently
    // is, so it keeps up with the wheel and stops ~90ms after the last notch (see Scroller).
    const float ScrollGlide = 0.09f;
    float _scroll, _from, _to, _animT;
    bool _animating;
    bool _pinned = true;                          // stay stuck to the newest message unless scrolled away
    bool _loadingOlder;
    ulong _lastRead;
    int _toolbarHot = -1;

    public event Action? NeedOlder;
    public event Action<UserMessage>? ReplyRequested;
    public event Action<UserMessage>? EditRequested;

    public bool Pinned => _pinned;

    // Acking a locally-invented id would tell the server we had read a message that does not exist,
    // so an optimistic or failed row is never the newest *confirmed* one.
    public ulong NewestId
    {
        get
        {
            for (int i = _rows.Count - 1; i >= 0; i--)
                if (_rows[i].Msg.SendState == 0) return _rows[i].Msg.Id;
            return 0;
        }
    }

    // The newest message in view written by the current user — what Up-arrow-on-empty-box edits.
    public UserMessage? LastOwnMessage
    {
        get
        {
            var me = App.Client?.CurrentUser?.Id ?? 0;
            for (int i = _rows.Count - 1; i >= 0; i--)
            {
                var m = _rows[i].Msg;
                if (m.Author?.Id == me && !m.IsSystem) return m;
            }
            return null;
        }
    }

    public MessageList()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Chat;
        // Discord's scroll is eased, not stepped. A short timer (see Frames) that only runs while
        // there is distance left costs nothing at rest and is the single biggest "feels native"
        // difference. Everything here eases on *elapsed time* rather than per frame — see Ui.Ease —
        // so a tick that arrives late still lands in the right place.
        _glide = new Frames(dt =>
        {
            if (IsDisposed) { _glide.Stop(); return; }
            // A fixed-duration ease to wherever the last wheel event aimed. Retargeting from the
            // current offset is what keeps the list in step with the wheel: the old exponential
            // chase of an accumulated target fell behind during a spin and slid for ~300ms after
            // the wheel stopped — the rubber-band feel.
            bool gliding = _animating;
            if (gliding)
            {
                _animT += dt;
                if (_animT >= ScrollGlide) { _scroll = _to; _animating = false; gliding = false; }
                else _scroll = _from + (_to - _from) * Ui.EaseOut(_animT / ScrollGlide);
            }
            bool entrancing = _entrance > 0.01f;
            if (entrancing) _entrance = Math.Max(0f, _entrance - dt * 6f);
            else _entrance = 0f;
            // Scrollbar reveal: visible while the mouse is over the list or just scrolled, fading
            // back out ~1s after the last activity — the glide keeps running until it's settled.
            float sbWant = (DateTime.UtcNow - _lastScrollActivity).TotalSeconds < 1.2 ? 1f : 0f;
            bool sbMoving = Math.Abs(sbWant - _sbAlpha) > 0.02f;
            if (sbMoving) _sbAlpha = Ui.Ease(_sbAlpha, sbWant, dt, 19f);
            else _sbAlpha = sbWant;
            if (!gliding && !entrancing && !sbMoving) _glide.Stop();
            // Repainting the whole list is the most expensive surface in the app; when the content
            // is static and only the scrollbar's alpha is moving, touch just its 8px strip.
            if (!gliding && !entrancing) Invalidate(new Rectangle(Width - Ui.S(12), 0, Ui.S(12), Height));
            else Invalidate();
        });
        _pollTick.Tick += (_, _) =>
        {
            bool any = false;
            foreach (var r in _rows)
                if (r.Msg.Poll is { } p && !p.Closed) { r.Invalidate(); any = true; }
            // force: rows whose width was cleared re-lay; the rest short-circuit inside Layout.
            if (any) { Relayout(force: true); Invalidate(); }
        };
        _pollTick.Start();

        // While a clip plays, the card's progress and clock have to move. One 10fps tick is plenty
        // for a seek bar and it only runs while something is actually playing.
        _audioTick.Tick += (_, _) =>
        {
            if (Audio.Current == null && Video.Current == null) { _audioTick.Stop(); return; }
            if (Audio.IsPlaying || Video.IsPlaying) Invalidate();
        };
        Audio.Changed += OnAudioChanged;
        // A decoded frame arrives on the decode thread; the same marshal-and-repaint the audio
        // card uses gets it onto the screen.
        Video.Changed += OnAudioChanged;
    }

    readonly System.Windows.Forms.Timer _audioTick = new() { Interval = 100 };

    void OnAudioChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(() =>
            {
                if (IsDisposed) return;
                if ((Audio.Current != null || Video.Current != null) && !_audioTick.Enabled) _audioTick.Start();
                Invalidate();
            });
        }
        catch { }
    }

    protected override void Dispose(bool disposing)
    {
        // Audio.Changed is a static event: without unsubscribing, every message list this session
        // ever created stays reachable from it.
        if (disposing) { Audio.Changed -= OnAudioChanged; Video.Changed -= OnAudioChanged; _glide.Stop(); _pollTick.Dispose(); _audioTick.Dispose(); }
        base.Dispose(disposing);
    }

    // ── content ─────────────────────────────────────────────────────────────────────────────────
    public void SetMessages(IReadOnlyList<UserMessage> msgs, ulong lastRead)
    {
        _rows.Clear();
        _lastRead = lastRead;
        foreach (var m in msgs) _rows.Add(new MessageRow { Msg = m });
        _laidOutFor = -1;
        _pinned = true;
        _jumpCount = 0;    // a fresh channel must not inherit the last one's unread badge
        _loadingOlder = false;
        _hover = -1;
        Relayout(force: true);
        SitAt(MaxScroll);
        Invalidate();
    }

    /// The fetch finished with nothing to add — it failed, the channel changed under it, or there
    /// is no more history. Either way the in-flight latch has to drop or the channel would never
    /// load older messages again.
    public void OlderDone() => _loadingOlder = false;

    public void PrependOlder(IReadOnlyList<UserMessage> msgs)
    {
        if (msgs.Count == 0) { _loadingOlder = false; return; }
        int before = _contentH;
        var known = _rows.Select(r => r.Msg.Id).ToHashSet();
        var add = msgs.Where(m => !known.Contains(m.Id)).Select(m => new MessageRow { Msg = m }).ToList();
        _rows.InsertRange(0, add);
        Relayout(force: true);
        // Keep the viewport over the same message rather than letting it jump to the new top. The
        // glide endpoints move with it so a scroll in flight lands where it was heading.
        _scroll += _contentH - before;
        _from += _contentH - before;
        _to += _contentH - before;
        float max = MaxScroll;
        _scroll = Math.Clamp(_scroll, 0, max);
        _from = Math.Clamp(_from, 0, max);
        _to = Math.Clamp(_to, 0, max);
        _loadingOlder = false;
        Invalidate();
    }

    public void Append(UserMessage m)
    {
        if (_rows.Any(r => r.Msg.Id == m.Id)) return;

        // The server's copy of a row we already drew optimistically. Swap it in place: appending it
        // would show the message twice, and re-adding at the bottom would make it jump. Both the
        // REST reply and the gateway echo carry the nonce, so whichever arrives first wins and the
        // other is dropped by the id check above.
        if (m.Nonce is { Length: > 0 } nonce
            && _rows.FirstOrDefault(r => r.Msg.SendState != 0 && r.Msg.Nonce == nonce) is { } pending)
        {
            pending.Msg = m;
            pending.Invalidate();
            Relayout(force: true);
            Invalidate();
            return;
        }

        _rows.Add(new MessageRow { Msg = m });
        Relayout(force: true);
        if (_pinned)
        {
            _entrance = 1f;   // slide the new row in only when it's actually in view
            Retarget(MaxScroll);
            _glide.Start();   // the entrance needs frames even when the scroll target did not move
        }
        else _jumpCount++;   // Discord's down-arrow badge: new arrivals while you're scrolled up
        Invalidate();
    }

    public void Update(UserMessage m)
    {
        var row = _rows.FirstOrDefault(r => r.Msg.Id == m.Id);
        if (row == null) return;
        row.Msg = m;
        row.Invalidate();
        Relayout(force: true);
        Invalidate();
    }

    public void Remove(ulong id)
    {
        int i = _rows.FindIndex(r => r.Msg.Id == id);
        if (i < 0) return;
        _rows.RemoveAt(i);
        Relayout(force: true);
        Invalidate();
    }

    public UserMessage? MessageById(ulong id) => _rows.FirstOrDefault(r => r.Msg.Id == id)?.Msg;

    /// Throw away every cached row measurement and lay out again — what a density change needs,
    /// since a row only re-measures when the width changed.
    public void Rebuild()
    {
        foreach (var r in _rows) r.Invalidate();
        Relayout(force: true);
        Invalidate();
    }

    /// The post failed: turn the optimistic row red and offer Retry / Delete, like the real client.
    public void FailPending(string nonce, string? reason)
    {
        var row = _rows.FirstOrDefault(r => r.Msg.SendState == 1 && r.Msg.Nonce == nonce);
        if (row == null) return;
        row.Msg.SendState = 2;
        row.FailReason = reason;
        row.Invalidate();
        Relayout(force: true);
        Invalidate();
    }

    /// Drop a failed row — the user chose Delete, or Retry is about to re-post it.
    public UserMessage? TakeFailed(string nonce)
    {
        int i = _rows.FindIndex(r => r.Msg.SendState == 2 && r.Msg.Nonce == nonce);
        if (i < 0) return null;
        var m = _rows[i].Msg;
        _rows.RemoveAt(i);
        Relayout(force: true);
        Invalidate();
        return m;
    }

    /// Raised by the Retry / Delete links on a failed row.
    public event Action<UserMessage, bool>? FailedAction;   // (message, retry)

    // ── layout ──────────────────────────────────────────────────────────────────────────────────
    int Viewport => Math.Max(0, Height);
    int MaxScroll => Math.Max(0, _contentH - Viewport);

    void Relayout(bool force = false)
    {
        if (Width <= 0) return;

        // Any row removal leaves _hover pointing past the end, and PaintToolbar indexes _rows with
        // it on the very next paint — an ArgumentOutOfRange inside OnPaint, which WinForms renders
        // as a white box with a red cross where the message list should be. Every mutation comes
        // through here, so clamping once covers deleting a message, dropping a failed row, and
        // switching channels alike.
        if (_hover >= _rows.Count) { _hover = -1; _toolbarHot = -1; }

        if (!force && _laidOutFor == Width) return;
        _laidOutFor = Width;

        int y = Ui.S(16);
        MessageRow? prev = null;
        var lastDate = DateTime.MinValue;
        ulong selfId = App.Client?.CurrentUser?.Id ?? 0;
        var myRoles = App.Guild is { } g && selfId != 0 ? g.GetMember(selfId)?.RoleIds : null;
        bool unreadDrawn = false;

        foreach (var r in _rows)
        {
            var day = r.Msg.Timestamp.ToLocalTime().Date;
            bool newDay = day != lastDate;
            lastDate = day;

            // Discord starts a new group on a different author, a 7-minute gap, a reply, or a day change.
            bool groupStart = prev == null || newDay
                        || prev.Msg.Author?.Id != r.Msg.Author?.Id
                        || r.Msg.ReferencedMessage != null || r.Msg.Interaction != null
                        || r.Msg.IsSystem || prev.Msg.IsSystem
                        || (r.Msg.Timestamp - prev.Msg.Timestamp).TotalMinutes > 7;
            var dateLabel = newDay ? DayLabel(day) : null;
            bool unreadStart = !unreadDrawn && _lastRead != 0 && r.Msg.Id > _lastRead && prev != null;

            // All three change the row's *height* and whether it draws an avatar and name, but
            // MessageRow.Layout short-circuits on an unchanged width — so a row whose grouping
            // changed underneath it would keep the geometry from when it was the other kind. That
            // is what left a header-sized gap above a message after deleting the one above it, and
            // an avatar-less row with a phantom name line after loading older history.
            if (r.GroupStart != groupStart || r.DateLabel != dateLabel || r.UnreadStart != unreadStart)
                r.Invalidate();

            r.GroupStart = groupStart;
            r.DateLabel = dateLabel;
            r.UnreadStart = unreadStart;
            r.Mentioned = selfId != 0 && r.Msg.Author?.Id != selfId && r.Msg.MentionsMe(selfId, myRoles);
            if (r.UnreadStart) unreadDrawn = true;

            r.Layout(Width, prev);
            r.Y = y;
            y += r.Height;
            prev = r;
        }
        _contentH = y + Ui.S(24);
        if (_pinned) { SitAt(MaxScroll); }
        else
        {
            float max = MaxScroll;
            _scroll = Math.Clamp(_scroll, 0, max);
            _from = Math.Clamp(_from, 0, max);
            _to = Math.Clamp(_to, 0, max);
        }
    }

    static string DayLabel(DateTime d) =>
        d == DateTime.Today ? "Today"
        : d == DateTime.Today.AddDays(-1) ? "Yesterday"
        : d.ToString("MMMM d, yyyy");

    protected override void OnSizeChanged(EventArgs e)
    {
        foreach (var r in _rows) r.Invalidate();
        Relayout(force: true);
        base.OnSizeChanged(e);
    }

    // ── scrolling ───────────────────────────────────────────────────────────────────────────────
    void StartGlide() => _glide.Start();

    /// Begin a glide toward `to` (clamped). True if the list will move. Re-aims from wherever the
    /// list currently is, so a stream of wheel events composes instead of stacking up behind it.
    bool Retarget(float to)
    {
        to = Math.Clamp(to, 0, MaxScroll);
        _from = _scroll;
        if (to == _from && !_animating) { _to = to; return false; }
        _to = to;
        _animT = 0;
        _animating = true;
        _glide.Start();
        return to != _from;
    }

    /// Sit exactly at `v` (clamped) — a channel switch, the scrollbar thumb, a pinned relayout.
    void SitAt(float v)
    {
        _scroll = _to = Math.Clamp(v, 0, MaxScroll);
        _from = _scroll;
        _animating = false;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (Ui.Precise(e.Delta))
        {
            // Trackpad reports, and the multi-notch bursts Windows coalesces out of them when the
            // UI thread is busy: track the finger exactly, do not glide. A glided burst is what
            // felt like the list flinging itself on a quick scroll.
            float nv = Math.Clamp(_scroll - Ui.Wheel(e.Delta), 0, MaxScroll);
            if (Math.Abs(nv - _scroll) >= 0.5f) Invalidate();
            _scroll = nv;
            _from = _to = nv;
            _animating = false;
        }
        else Retarget(_scroll - Ui.Wheel(e.Delta));
        _pinned = _to >= MaxScroll - Ui.S(8);
        BumpScrollbar();
        if (_to < Ui.S(600) && !_loadingOlder && _rows.Count > 0)
        {
            _loadingOlder = true;
            NeedOlder?.Invoke();
        }
    }

    public void ScrollToBottom() { _pinned = true; _jumpCount = 0; _entrance = 0f; Retarget(MaxScroll); _glide.Start(); }

    /// Page/Home/End scrolling. Returns false when the key was not one this list handles, so the
    /// shell can pass it on. A page is one viewport less a two-line overlap, which is what every
    /// scrolling surface does so you keep your place across the jump.
    public bool ScrollKey(Keys key)
    {
        int page = Math.Max(Ui.S(40), Height - Ui.S(44));
        switch (key)
        {
            case Keys.PageUp: Retarget(_scroll - page); break;
            case Keys.PageDown: Retarget(_scroll + page); break;
            case Keys.Home:
                Retarget(0);
                // Jumping to the top is also a request for the rest of the history.
                if (!_loadingOlder && _rows.Count > 0) { _loadingOlder = true; NeedOlder?.Invoke(); }
                break;
            case Keys.End: Retarget(MaxScroll); break;
            default: return false;
        }
        _pinned = _to >= MaxScroll - Ui.S(8);
        BumpScrollbar();
        return true;
    }

    public void ScrollTo(ulong messageId)
    {
        var r = _rows.FirstOrDefault(x => x.Msg.Id == messageId);
        if (r == null) return;
        Retarget(Math.Clamp(r.Y - Viewport / 3f, 0, MaxScroll));
        _pinned = false;
    }

    // ── hit testing ─────────────────────────────────────────────────────────────────────────────
    int RowAt(Point p)
    {
        int y = p.Y + (int)_scroll;
        for (int i = 0; i < _rows.Count; i++)
            if (y >= _rows[i].Y && y < _rows[i].Y + _rows[i].Height) return i;
        return -1;
    }

    Point Local(int i, Point p) => new(p.X, p.Y + (int)_scroll - _rows[i].Y);

    // The hover toolbar: Discord floats it over the top-right of the message being pointed at.
    //
    // Measured off the live client: a 33px bar at 8 radius on --background-surface-high, holding
    // 28x28 cells at 6 radius with a 3px inset. The three most recently used reaction emoji lead,
    // then a 1px separator, then the actions.
    const int TbCell = 28, TbPad = 3, TbH = 33, TbSep = 9;

    /// The toolbar's cells, left to right. An entry carries either an emoji suggestion or an action
    /// glyph, so paint, hit-testing and click all walk the same list and cannot disagree.
    List<(Rectangle Box, string? Emoji, string? Icon)> ToolbarCells(int i)
    {
        var cells = new List<(Rectangle, string?, string?)>();
        var box = ToolbarBox(i);
        bool mine = _rows[i].Msg.Author?.Id == App.Client?.CurrentUser?.Id;
        int x = box.X + Ui.S(TbPad), y = box.Y + (box.Height - Ui.S(TbCell)) / 2;

        foreach (var e in Prefs.Current.RecentReactions.Take(Prefs.RecentReactionCount))
        {
            cells.Add((new Rectangle(x, y, Ui.S(TbCell), Ui.S(TbCell)), e, null));
            x += Ui.S(TbCell);
        }
        if (cells.Count > 0) x += Ui.S(TbSep);
        foreach (var ic in mine
            ? new[] { Icons.SmileyLine, Icons.PencilLine, Icons.ReplyLine, Icons.DotsHorizontal }
            : new[] { Icons.SmileyLine, Icons.ReplyLine, Icons.DotsHorizontal })
        {
            cells.Add((new Rectangle(x, y, Ui.S(TbCell), Ui.S(TbCell)), null, ic));
            x += Ui.S(TbCell);
        }
        return cells;
    }

    int ToolbarWidth(int i)
    {
        bool mine = _rows[i].Msg.Author?.Id == App.Client?.CurrentUser?.Id;
        int emoji = Math.Min(Prefs.Current.RecentReactions.Count, Prefs.RecentReactionCount);
        int n = emoji + (mine ? 4 : 3);
        return Ui.S(TbPad * 2) + n * Ui.S(TbCell) + (emoji > 0 ? Ui.S(TbSep) : 0);
    }

    Rectangle ToolbarBox(int i)
    {
        var r = _rows[i];
        int w = ToolbarWidth(i), h = Ui.S(TbH);
        int top = r.Y - (int)_scroll + (r.DateLabel != null ? Ui.S(40) : 0) - Ui.S(16);
        return new Rectangle(Width - Ui.S(48) - w, Math.Max(top, r.Y - (int)_scroll), w, h);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        BumpScrollbar();

        if (_sbDrag) { ScrollByThumb(e.Y); return; }
        if (_scrub != null) { ScrubTo(e.X); return; }

        // An armed press becomes a selection once it travels past the drag threshold — below that
        // it is still a click, so tapping a message does not wipe an existing selection early.
        if (_anchor is { } start && (e.Button & MouseButtons.Left) != 0)
        {
            if (!_dragging &&
                (Math.Abs(e.X - _downPt.X) > Ui.S(3) || Math.Abs(e.Y - _downPt.Y) > Ui.S(3)))
            {
                _dragging = true;
                _selA = start;
            }
            if (_dragging)
            {
                _selB = HitAnchor(e.Location);
                // Dragging past either edge scrolls, the way a text area does; without it you can
                // only ever select what is already on screen.
                if (e.Y < 0) Retarget(_scroll - Ui.S(24));
                else if (e.Y > Height) Retarget(_scroll + Ui.S(24));
                Cursor = Cursors.IBeam;
                Invalidate();
                return;
            }
        }

        int h = RowAt(e.Location);
        int tb = -1;
        int hb = -1, hp = -1;
        bool hotChanged = false;
        if (h >= 0)
        {
            var lp = Local(h, e.Location);
            var row = _rows[h];
            if (ToolbarBox(h).Contains(e.Location))
                tb = ToolbarCells(h).FindIndex(c => c.Box.Contains(e.Location));
            // The per-element hover indices change *within* a row, so a repaint needs triggering on
            // them too — not just when the row or toolbar slot changes.
            hb = row.ButtonAt(lp);
            hp = row.PollAt(lp);
            int hpill = row.PillIndexAt(lp);
            bool hadd = row.OverAddReaction(lp);
            hotChanged = hb != row.HotButton || hp != row.HotPoll
                      || hpill != row.HotPill || hadd != row.HotAddReaction;
            row.HotButton = hb;
            row.HotPoll = hp;
            row.HotPill = hpill;
            row.HotAddReaction = hadd;
            ShowReactorTip(row, hpill);
        }
        if (h != _hover || tb != _toolbarHot || hotChanged) { _hover = h; _toolbarHot = tb; Invalidate(); }

        var cur = Cursors.Default;
        if (h >= 0)
        {
            var lp = Local(h, e.Location);
            var row = _rows[h];
            if (tb >= 0 || row.LinkAt(lp) != null || row.ShotAt(lp) != null || row.PillAt(lp) != null
                || row.FileAt(lp) != null || row.OverAvatar(lp) || row.OverName(lp) || row.OverReply(lp)
                || hb >= 0 || hp >= 0
                || (row.Msg.IsFailed && (row.RetryBox.Contains(lp) || row.DeleteBox.Contains(lp)))
                || row.OverAddReaction(lp))
                cur = Cursors.Hand;
            else if (OverBodyText(h, e.Location)) cur = Cursors.IBeam;
        }
        Cursor = cur;
        base.OnMouseMove(e);
    }

    // ── who reacted ─────────────────────────────────────────────────────────────────────────────
    // Discord names the reactors in a tooltip on hover. The list is not in the message payload, so
    // it is fetched once per (message, emoji) and cached — hovering along a row of pills must not
    // fire a request per frame.
    readonly Dictionary<(ulong, string), string> _reactors = new();
    (ulong, string) _reactorPending;
    (ulong Msg, int Pill) _tipFor = (0, -1);

    void ShowReactorTip(MessageRow row, int pill)
    {
        if (pill < 0)
        {
            if (_tipFor.Pill >= 0) { Tip.Hide(); _tipFor = (0, -1); }
            return;
        }
        if (_tipFor == (row.Msg.Id, pill)) return;
        _tipFor = (row.Msg.Id, pill);

        var r = row.Reactions[pill].R;
        var key = (row.Msg.Id, r.Emoji.Key);
        var box = new Rectangle(row.Reactions[pill].Box.X,
                                row.Y - (int)_scroll + row.Reactions[pill].Box.Y,
                                row.Reactions[pill].Box.Width, row.Reactions[pill].Box.Height);

        if (_reactors.TryGetValue(key, out var names)) { Tip.Show(this, names, box); return; }

        Tip.Show(this, $"{r.Count} reacted with {r.Emoji.Display}", box);
        if (_reactorPending == key) return;
        _reactorPending = key;
        _ = LoadReactors(row.Msg, r, key, box);
    }

    async Task LoadReactors(UserMessage m, UserReaction r, (ulong, string) key, Rectangle box)
    {
        try
        {
            var users = await m.Client.Rest.GetReactionUsersAsync(m.ChannelId, m.Id, r.Emoji.Key);
            var me = App.Client?.CurrentUser?.Id ?? 0;
            var names = users.Select(u => u.Id == me ? "You" : u.DisplayName).ToList();
            // Discord's phrasing: up to three names, then "and N others".
            string text = names.Count == 0 ? $"{r.Count} reacted"
                : names.Count <= 3 ? string.Join(", ", names)
                : string.Join(", ", names.Take(3)) + $" and {names.Count - 3} other" + (names.Count - 3 == 1 ? "" : "s");
            text += "  reacted with " + r.Emoji.Display;
            _reactors[key] = text;
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke(() => { if (_tipFor == (m.Id, _tipFor.Pill)) Tip.Show(this, text, box); });
        }
        catch (Exception e) { Log.Write("chat", "reactors: " + e.Message); }
        finally { if (_reactorPending == key) _reactorPending = default; }
    }

    /// True when the pointer is actually over a word, so the caret only appears over selectable
    /// text rather than everywhere in the row's bounding box.
    bool OverBodyText(int rowIndex, Point p)
    {
        var row = _rows[rowIndex];
        var lp = Local(rowIndex, p);
        for (int i = 0; i < row.Body.Count; i++)
            if (!row.Body[i].Bg && row.Body[i].Text.Length > 0 && row.BodyPieceBox(i).Contains(lp))
                return true;
        return false;
    }

    // ── video scrubbing ─────────────────────────────────────────────────────────────────────────
    (int Row, string Clip)? _scrub;
    float _scrubbedTo = -1;

    /// Seek the clip being scrubbed from an x on its transport bar. Every seek flushes the decoder
    /// and re-seeks the audio, which is far too much work to do on all ~125 mouse moves a second a
    /// drag produces, so a move that lands on the same part of the clip is ignored.
    void ScrubTo(int x)
    {
        if (_scrub is not { } s || s.Row >= _rows.Count) return;
        var bar = _rows[s.Row].VideoBar;
        if (bar.IsEmpty) return;
        float f = Math.Clamp((x - bar.X) / (float)bar.Width, 0f, 1f);
        if (Math.Abs(f - _scrubbedTo) < 0.002f) return;
        _scrubbedTo = f;
        Video.Seek(s.Clip, f);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_scrub != null) { _scrub = null; Capture = false; }
        if (_sbDrag) { _sbDrag = false; Capture = false; Invalidate(); }
        // A press that never became a drag was a click on empty space: drop the old selection.
        else if (e.Button == MouseButtons.Left && !_dragging) ClearSelection();
        _anchor = null;
        _dragging = false;
        base.OnMouseUp(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hover != -1)
        {
            _hover = -1; _toolbarHot = -1;
            foreach (var r in _rows) { r.HotButton = -1; r.HotPoll = -1; }
            Invalidate();
        }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        // The scrollbar is a real control, not decoration: grab the thumb, or jump the page when
        // the track is clicked either side of it.
        if (e.Button == MouseButtons.Left && MaxScroll > 0 && e.X >= Width - Ui.S(12))
        {
            var t = ThumbBox;
            if (t.Contains(e.Location)) { _sbDrag = true; _sbGrabOffset = e.Y - t.Y; Capture = true; }
            else
            {
                Retarget(_scroll + (e.Y < t.Y ? -Height : Height));
                _pinned = _to >= MaxScroll - Ui.S(8);
            }
            BumpScrollbar();
            return;
        }

        int i = RowAt(e.Location);
        if (i < 0) return;
        var row = _rows[i];
        var lp = Local(i, e.Location);

        if (e.Button == MouseButtons.Right) { ShowMenu(row, PointToScreen(e.Location)); return; }
        if (e.Button != MouseButtons.Left) return;

        // Retry / Delete on a failed row, before anything else: the links sit inside the row's
        // normal body area and would otherwise be swallowed by the link or avatar hit tests.
        if (row.Msg.IsFailed)
        {
            if (row.RetryBox.Contains(lp)) { FailedAction?.Invoke(row.Msg, true); return; }
            if (row.DeleteBox.Contains(lp)) { FailedAction?.Invoke(row.Msg, false); return; }
        }

        if (_toolbarHot >= 0) { ToolbarClick(row, _toolbarHot); return; }

        int sp = row.SpoilerAt(lp);
        if (sp != 0) { _revealed.Add(sp); Invalidate(); return; }

        if (row.ShotAt(lp) is { } shot)
        {
            if (shot.Spoiler && !_shownSpoilers.Contains(row.Msg.Id)) { _shownSpoilers.Add(row.Msg.Id); Invalidate(); return; }
            var clip = shot.OpenUrl ?? shot.Url;
            if (shot.Play)
            {
                // A playing clip's transport wins over "start playing"; the bar scrubs, the button
                // and anywhere else on the frame toggle — the same shape as the audio card.
                if (Video.Current == clip)
                {
                    if (!row.VideoBar.IsEmpty && row.VideoBar.Contains(lp))
                    {
                        // Held, not just clicked: scrubbing a clip means dragging along the bar,
                        // and the row is repainted under the pointer as the frames change, so the
                        // drag has to be owned here rather than re-hit-tested every move.
                        _scrub = (i, clip);
                        _scrubbedTo = -1;
                        Capture = true;
                        ScrubTo(lp.X);
                    }
                    else Video.Toggle(clip);
                }
                else Video.Toggle(clip);
                return;
            }
            Lightbox.Show(shot.Url, clip);
            return;
        }
        if (row.FileAt(lp) is { } file)
        {
            // An audio card plays inline: the bar seeks, anywhere else on the card toggles. Only a
            // non-audio attachment still opens in the browser.
            if (file.A.IsAudio)
            {
                var clip = file.A.ProxyUrl ?? file.A.Url;
                if (!file.Bar.IsEmpty && file.Bar.Contains(lp) && Audio.Current == clip)
                    Audio.Seek(clip, (lp.X - file.Bar.X) / (float)file.Bar.Width);
                else
                    Audio.Toggle(clip);
                return;
            }
            Ui.OpenUrl(file.A.Url);
            return;
        }
        if (row.OverAddReaction(lp)) { AddReaction(row); return; }
        if (row.PillAt(lp) is { } pill) { _ = Toggle(row.Msg, pill.R); return; }
        int bi = row.ButtonAt(lp);
        if (bi >= 0 && row.ButtonC(bi) is { } bc)
        {
            if (bc.IsLink && bc.Url != null) Ui.OpenUrl(bc.Url);
            else if (bc.Clickable) _ = FireComponent(row.Msg, bc);
            return;
        }
        int pi = row.PollAt(lp);
        if (pi >= 0) { _ = TogglePoll(row.Msg, row.PollAnswers[pi]); return; }
        if (row.LinkAt(lp) is { } url) { Ui.OpenUrl(url); return; }
        if (row.OverReply(lp) && row.Msg.ReferencedMessage is { } rm) { ScrollTo(rm.Id); return; }
        if ((row.OverAvatar(lp) || row.OverName(lp)) && row.Msg.Author != null)
        {
            ProfileCard.Show(this, row.Msg.Author, PointToScreen(new Point(lp.X, e.Location.Y)));
            return;
        }

        // Nothing interactive under the pointer: this press is the start of a text selection. It is
        // only *armed* here — the drag threshold in OnMouseMove decides whether it becomes one, so a
        // plain click still behaves like a click.
        _anchor = HitAnchor(e.Location);
        _downPt = e.Location;
        _dragging = false;
    }

    // ── text selection ──────────────────────────────────────────────────────────────────────────
    // Anchored on (row, piece, character). RichText lays each word out as its own Piece with its
    // own box, so a word is the coarse unit and the character offset is measured inside it — which
    // is what lets a selection start and end mid-word the way every other text surface does.
    readonly record struct Anchor(int Row, int Piece, int Ch) : IComparable<Anchor>
    {
        public int CompareTo(Anchor o) =>
            Row != o.Row ? Row.CompareTo(o.Row)
            : Piece != o.Piece ? Piece.CompareTo(o.Piece)
            : Ch.CompareTo(o.Ch);
    }

    Anchor? _anchor, _selA, _selB;
    Point _downPt;
    bool _dragging;

    /// The selected text, in document order, or "" when there is no selection. Newlines are
    /// reinserted wherever the layout wrapped, so a copied paragraph pastes back the way it looked.
    public string SelectedText
    {
        get
        {
            if (_selA is not { } a || _selB is not { } b) return "";
            var (lo, hi) = a.CompareTo(b) <= 0 ? (a, b) : (b, a);
            var sb = new System.Text.StringBuilder();
            int lastY = int.MinValue, lastRow = -1;
            for (int r = lo.Row; r <= hi.Row && r < _rows.Count; r++)
            {
                var body = _rows[r].Body;
                int from = r == lo.Row ? lo.Piece : 0;
                int to = r == hi.Row ? hi.Piece : body.Count - 1;
                for (int p = from; p <= to && p < body.Count; p++)
                {
                    var pc = body[p];
                    if (pc.Bg) continue;                      // the code block's backing panel
                    // An emoji is laid out as an image atom with no text of its own; the run still
                    // knows what it was, so it copies as ":name:" / the character like Discord's.
                    var text = pc.Text.Length == 0 && pc.Run.Emoji ? pc.Run.Text : pc.Text;
                    if (text.Length == 0) continue;
                    // Words() keeps each word's trailing space, so the pieces already carry their
                    // own spacing — joining them with another space is what doubled every gap.
                    if (pc.Text.Length > 0)
                    {
                        if (p == hi.Piece && r == hi.Row) text = text[..Math.Min(hi.Ch, text.Length)];
                        if (p == lo.Piece && r == lo.Row) text = text[Math.Min(lo.Ch, text.Length)..];
                        if (text.Length == 0) continue;
                    }
                    int y = pc.Box.Y;
                    if (lastRow >= 0 && (r != lastRow || y != lastY)) sb.Append('\n');
                    sb.Append(text);
                    lastY = y; lastRow = r;
                }
            }
            // Wrapped lines end on the space that caused the wrap; trailing blanks are noise.
            return string.Join('\n', sb.ToString().Split('\n').Select(l => l.TrimEnd()));
        }
    }

    public bool HasSelection => _selA is { } a && _selB is { } b && a.CompareTo(b) != 0;

    public void ClearSelection()
    {
        if (_selA == null && _selB == null) return;
        _selA = _selB = _anchor = null;
        Invalidate();
    }

    /// Nearest selectable character to a point in list coordinates. Clamps rather than failing, so
    /// dragging past the end of a line extends to it instead of dropping the selection.
    Anchor? HitAnchor(Point p)
    {
        int y = p.Y + (int)_scroll;
        int row = -1;
        for (int i = 0; i < _rows.Count; i++)
            if (y >= _rows[i].Y && y < _rows[i].Y + _rows[i].Height) { row = i; break; }
        if (row < 0)   // above the first row or below the last: clamp to the nearer end
            row = _rows.Count == 0 ? -1 : y < _rows[0].Y ? 0 : _rows.Count - 1;
        if (row < 0) return null;

        var body = _rows[row].Body;
        if (body.Count == 0) return new Anchor(row, 0, 0);
        var lp = new Point(p.X, y - _rows[row].Y);

        int best = -1; long bestScore = long.MaxValue;
        for (int i = 0; i < body.Count; i++)
        {
            if (body[i].Bg) continue;
            var b = _rows[row].BodyPieceBox(i);
            // Prefer the piece on the same line; among those, the nearest horizontally.
            long dy = lp.Y < b.Top ? b.Top - lp.Y : lp.Y > b.Bottom ? lp.Y - b.Bottom : 0;
            long dx = lp.X < b.Left ? b.Left - lp.X : lp.X > b.Right ? lp.X - b.Right : 0;
            long score = dy * 10000 + dx;
            if (score < bestScore) { bestScore = score; best = i; }
        }
        if (best < 0) return new Anchor(row, 0, 0);
        return new Anchor(row, best, CharAt(_rows[row], best, lp.X));
    }

    /// Index of the character boundary nearest an x position inside a piece.
    static int CharAt(MessageRow row, int piece, int x)
    {
        var pc = row.Body[piece];
        var box = row.BodyPieceBox(piece);
        if (x <= box.Left) return 0;
        if (x >= box.Right) return pc.Text.Length;
        int rel = x - box.Left, bestI = 0, bestD = int.MaxValue;
        for (int i = 0; i <= pc.Text.Length; i++)
        {
            int w = i == 0 ? 0 : Ui.Measure(pc.Text[..i], pc.Font).Width;
            int d = Math.Abs(w - rel);
            if (d < bestD) { bestD = d; bestI = i; }
        }
        return bestI;
    }

    void PaintSelection(Graphics g, int rowIndex, int top)
    {
        if (_selA is not { } a || _selB is not { } b) return;
        var (lo, hi) = a.CompareTo(b) <= 0 ? (a, b) : (b, a);
        if (rowIndex < lo.Row || rowIndex > hi.Row) return;

        var row = _rows[rowIndex];
        int from = rowIndex == lo.Row ? lo.Piece : 0;
        int to = rowIndex == hi.Row ? hi.Piece : row.Body.Count - 1;
        using var brush = new SolidBrush(Color.FromArgb(110, Theme.Selection));

        for (int i = from; i <= to && i < row.Body.Count; i++)
        {
            var pc = row.Body[i];
            if (pc.Bg || pc.Text.Length == 0) continue;
            var box = row.BodyPieceBox(i);
            int x0 = box.Left, x1 = box.Right;
            if (rowIndex == lo.Row && i == lo.Piece && lo.Ch > 0)
                x0 = box.Left + Ui.Measure(pc.Text[..Math.Min(lo.Ch, pc.Text.Length)], pc.Font).Width;
            if (rowIndex == hi.Row && i == hi.Piece && hi.Ch < pc.Text.Length)
                x1 = box.Left + Ui.Measure(pc.Text[..Math.Max(0, hi.Ch)], pc.Font).Width;
            if (x1 <= x0) continue;
            g.FillRectangle(brush, new Rectangle(x0, top + box.Y, x1 - x0, box.Height));
        }
    }

    static async Task Toggle(UserMessage m, UserReaction r)
    {
        try
        {
            if (r.Me) await m.RemoveReactionAsync(r.Emoji.Key);
            // Clicking an existing pill counts as using that emoji, same as picking it.
            else { await m.AddReactionAsync(r.Emoji.Key); Prefs.NoteReaction(r.Emoji.Markup); }
        }
        catch (Exception e) { Log.Write("chat", "reaction failed: " + e.Message); }
    }

    // A button click is an interaction, not a REST call with a visible result: Discord answers 204
    // and the bot's edit arrives over the gateway as MESSAGE_UPDATE.
    static async Task FireComponent(UserMessage m, UserComponent c)
    {
        try { await m.Client.Rest.SendComponentInteractionAsync(m, c); }
        catch (Exception e) { Log.Write("chat", "button failed: " + e.Message); }
    }

    // First vote casts; a multiselect toggle removes the vote from an answer the user already chose.
    static async Task TogglePoll(UserMessage m, MessageRow.PollAns ans)
    {
        var poll = m.Poll;
        if (poll == null) return;
        try
        {
            if (poll.AllowMultiselect && poll.IVoted)
            {
                var current = poll.Results?.AnswerCounts.Where(a => a.MeVoted).Select(a => a.Id).ToList() ?? new();
                if (!current.Remove(ans.A.AnswerId)) current.Add(ans.A.AnswerId);
                await m.VoteAsync(current);
            }
            else await m.VoteAsync(new[] { ans.A.AnswerId });
        }
        catch (Exception e) { Log.Write("chat", "vote failed: " + e.Message); }
    }

    void ToolbarClick(MessageRow row, int idx)
    {
        int i = _rows.IndexOf(row);
        if (i < 0 || idx < 0) return;
        var cells = ToolbarCells(i);
        if (idx >= cells.Count) return;

        // A suggestion reacts straight away — that is the whole point of it being there.
        if (cells[idx].Emoji is { } markup) { _ = SafeReact(row.Msg, markup); return; }

        bool mine = row.Msg.Author?.Id == App.Client?.CurrentUser?.Id;
        var actions = mine
            ? new Action[] { () => AddReaction(row), () => EditRequested?.Invoke(row.Msg), () => ReplyRequested?.Invoke(row.Msg), () => ShowMenu(row, Cursor.Position) }
            : new Action[] { () => AddReaction(row), () => ReplyRequested?.Invoke(row.Msg), () => ShowMenu(row, Cursor.Position) };
        int a = idx - cells.Count(c => c.Emoji != null);
        if (a >= 0 && a < actions.Length) actions[a]();
    }

    void AddReaction(MessageRow row)
    {
        // No button to hang off here — the reaction picker opens from the hover toolbar, so anchor
        // it to the pointer itself.
        EmojiPicker.Pick(this, new Rectangle(Cursor.Position, Size.Empty), key => _ = SafeReact(row.Msg, key));
    }

    /// `markup` is the stored form ("a:name:id" / "name:id" / a glyph); the REST endpoints want it
    /// without the animated prefix. Recorded only once the server has accepted it, so a reaction
    /// that failed does not become a suggestion.
    static async Task SafeReact(UserMessage m, string markup)
    {
        var key = markup.StartsWith("a:", StringComparison.Ordinal) ? markup[2..] : markup;
        try { await m.AddReactionAsync(key); Prefs.NoteReaction(markup); }
        catch (Exception e) { Log.Write("chat", "reaction failed: " + e.Message); }
    }

    void ShowMenu(MessageRow row, Point screen)
    {
        var m = row.Msg;
        bool mine = m.Author?.Id == App.Client?.CurrentUser?.Id;
        var items = new List<ToolStripItem>
        {
            Menu.Item("Add Reaction", () => AddReaction(row)),
            Menu.Item("Reply", () => ReplyRequested?.Invoke(m)),
            Menu.Item("Forward", () => ForwardPicker.Pick(this, Cursor.Position, m)),
        };
        if (mine) items.Add(Menu.Item("Edit Message", () => EditRequested?.Invoke(m)));
        items.Add(Menu.Item(m.Pinned ? "Unpin Message" : "Pin Message", () => _ = Safe(m.PinAsync(!m.Pinned))));

        // Threads only exist in guild text channels.
        if (App.Guild != null) items.Add(Menu.Item("Create Thread", () => CreateThread(m)));

        items.Add(Menu.Item("Mark Unread", () => MarkUnread(m)));
        items.Add(Menu.Sep());
        // With text highlighted the menu offers that, the way any text surface does; "Copy Text"
        // (the whole message, raw markdown) stays available underneath it.
        if (HasSelection) items.Add(Menu.Item("Copy Selection", () => TrySetClipboard(SelectedText)));
        items.Add(Menu.Item("Copy Text", () => TrySetClipboard(m.Content)));
        items.Add(Menu.Item("Copy Message Link", () => TrySetClipboard(m.JumpLink)));
        items.Add(Menu.Item("Copy Message ID", () => TrySetClipboard(m.Id.ToString())));

        // Deleting someone else's message is Manage Messages, not just "mine" — that gate meant a
        // moderator could not delete anything from inside the client.
        bool canDelete = mine || (App.Guild is { } g
            && (g.PermissionsFor(App.Client?.CurrentUser?.Id ?? 0, null) & (Perm.ManageMessages | Perm.Administrator)) != 0);
        if (canDelete)
        {
            items.Add(Menu.Sep());
            items.Add(Menu.Item("Delete Message", () => _ = Safe(m.DeleteAsync()), danger: true));
        }
        Menu.Show(this, screen, items.ToArray());
    }

    // Discord acks the message *before* the one you picked, so the divider lands above it.
    void MarkUnread(UserMessage m)
    {
        if (App.Client is not { } c) return;
        _ = Safe(c.Rest.MarkUnreadAsync(m.ChannelId, m.Id));
        _lastRead = m.Id - 1;
        foreach (var r in _rows) r.Invalidate();
        Relayout(force: true);
        Invalidate();
    }

    void CreateThread(UserMessage m)
    {
        var name = Prompt.Ask(FindForm()!, "Create Thread",
                              "Threads keep a side conversation out of the main channel.",
                              Trim(Markdown.Flatten(m.Content)), "Create");
        if (string.IsNullOrWhiteSpace(name)) return;
        _ = Safe(App.Client!.Rest.CreateThreadAsync(m.ChannelId, name.Trim(), m.Id));

        static string Trim(string s)
        {
            s = s.Replace("\n", " ").Trim();
            return s.Length <= 40 ? s : s[..40];
        }
    }

    static void TrySetClipboard(string s)
    {
        try { if (!string.IsNullOrEmpty(s)) Clipboard.SetText(s); } catch { }
    }

    static async Task Safe(Task t)
    {
        try { await t; } catch (Exception e) { Log.Write("chat", e.Message); }
    }

    // ── paint ───────────────────────────────────────────────────────────────────────────────────
    // A throw out of a paint handler is not a dropped frame: WinForms latches it and paints a white
    // box with a red X in place of the control for the rest of the session, so one bad image costs
    // the whole conversation. Losing a frame is the better trade — and the log line says which.
    protected override void OnPaint(PaintEventArgs e)
    {
        try { PaintList(e); }
        catch (Exception ex) { Log.Write("chat", "paint: " + ex); }
    }

    void PaintList(PaintEventArgs e)
    {
        using var _perf = Log.Frame("chat-list");
        Relayout();
        var g = e.Graphics;
        Ui.Fill(g, ClientRectangle, Theme.Chat);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        int scroll = (int)_scroll;
        for (int i = 0; i < _rows.Count; i++)
        {
            var r = _rows[i];
            int top = r.Y - scroll;
            // The newest arrival slides down from slightly above its resting spot. Only the last
            // row animates; the glide drives _entrance toward 0 so the offset decays to none.
            if (_entrance > 0.01f && i == _rows.Count - 1)
                top += (int)(Ui.S(6) * _entrance);
            if (top + r.Height < 0 || top > Height) continue;      // offscreen
            r.Paint(g, top, Width, this, _revealed, _shownSpoilers, _hover == i);
            // Over the text rather than behind it: the row paints its own mention and code-block
            // backgrounds, so anything drawn first would be covered. A translucent wash reads as a
            // selection and leaves the glyphs legible.
            PaintSelection(g, i, top);
        }

        if (_hover >= 0) PaintToolbar(g, _hover);
        DrawScrollbar(g);
        if (!_pinned && MaxScroll > 0) PaintJumpToPresent(g);
    }

    void PaintToolbar(Graphics g, int i)
    {
        var box = ToolbarBox(i);
        if (box.Bottom < 0 || box.Top > Height) return;

        Ui.FillRound(g, box, Ui.S(8), Theme.Field);
        using (var pen = new Pen(Theme.Border))
        using (var path = Ui.RoundRect(new Rectangle(box.X, box.Y, box.Width - 1, box.Height - 1), Ui.S(8)))
            g.DrawPath(pen, path);

        var cells = ToolbarCells(i);
        for (int k = 0; k < cells.Count; k++)
        {
            var (cell, emoji, icon) = cells[k];
            bool hot = _toolbarHot == k;
            if (hot) Ui.FillRound(g, cell, Ui.S(6), Theme.SurfaceHigh);
            if (emoji != null) PaintEmoji(g, emoji, Rectangle.Inflate(cell, -Ui.S(4), -Ui.S(4)));
            else Icons.Draw(g, icon!, Rectangle.Inflate(cell, -Ui.S(4), -Ui.S(4)),
                            hot ? Theme.Text : Theme.Muted, 1.9f);
        }

        // The 1px rule between the suggestions and the actions.
        if (cells.Count > 0 && cells[0].Emoji != null)
        {
            int sepX = cells.First(c => c.Icon != null).Box.X - Ui.S(TbSep) / 2;
            Ui.Fill(g, new Rectangle(sepX, box.Y + Ui.S(4), Math.Max(1, Ui.S(1)), box.Height - Ui.S(8)),
                    Theme.Tint(Theme.Field, Color.FromArgb(151, 151, 159), 0.12f));
        }
    }

    /// One reaction suggestion: the custom emoji's image, or the Twemoji for a unicode one, falling
    /// back to the glyph in the emoji font while the download is in flight.
    void PaintEmoji(Graphics g, string markup, Rectangle box)
    {
        var e = UserEmoji.Parse(markup);
        var img = Media.Get(e.ImageUrl ?? (e.Name is { } n ? Twemoji.Url(n) : null), this);
        if (img != null)
        {
            if (Media.IsAnimated(img)) Media.Animate(img, this);
            g.DrawImage(img, box);
        }
        else Ui.Text(g, e.Glyph, Theme.Emoji, box, Theme.Text, TextFormatFlags.HorizontalCenter);
    }

    // Discord's scrolled-up state has two elements: a "You're viewing older messages" banner near
    // the top of the viewport, and a circular down-arrow button bottom-right whose red badge counts
    // the messages that arrived while you were away. Both jump to present on click.
    //
    // They do not appear together. The down-arrow shows as soon as you leave the bottom, but the
    // banner waits until you are genuinely reading history — nudging the wheel a couple of notches
    // to re-read the last message should not make a bar drop over the conversation. One and a half
    // viewports is about where "I scrolled a bit" becomes "I am browsing the past".
    const float BannerAfterViewports = 1.5f;
    int _jumpCount;

    bool FarFromPresent => MaxScroll - _scroll > Height * BannerAfterViewports;

    void PaintJumpToPresent(Graphics g)
    {
        // Top banner, centred like the web client's — only once you are well clear of the newest
        // message. _jumpBanner is cleared otherwise so the hit test cannot fire on a hidden bar.
        _jumpBanner = Rectangle.Empty;
        if (FarFromPresent)
        {
            const string label = "You're viewing older messages";
            const string jump = "Jump to present";
            var sz = Ui.Measure(label, Theme.SmallMedium);
            var js = Ui.Measure(jump, Theme.SmallMedium);
            int pw = sz.Width + js.Width + Ui.S(52);
            var box = new Rectangle((Width - pw) / 2, Ui.S(10), pw, Ui.S(30));
            Ui.FillRound(g, box, Ui.S(8), Theme.Surface);
            using (var pen = new Pen(Theme.Border))
            using (var path = Ui.RoundRect(new Rectangle(box.X, box.Y, box.Width - 1, box.Height - 1), Ui.S(8)))
                g.DrawPath(pen, path);
            Ui.Text(g, label, Theme.SmallMedium,
                    new Rectangle(box.X + Ui.S(12), box.Y, sz.Width, box.Height), Theme.Muted,
                    TextFormatFlags.VerticalCenter);
            Ui.Text(g, jump, Theme.SmallMedium,
                    new Rectangle(box.Right - js.Width - Ui.S(12), box.Y, js.Width, box.Height), Theme.Link,
                    TextFormatFlags.VerticalCenter);
            _jumpBanner = box;
        }

        // Circular down-arrow button, bottom-right above the composer, with the unread badge.
        int d = Ui.S(34);
        var btn = new Rectangle(Width - d - Ui.S(20), Height - d - Ui.S(20), d, d);
        Ui.FillRound(g, btn, d, Theme.Surface);
        using (var pen = new Pen(Theme.Border))
        using (var path = Ui.RoundRect(new Rectangle(btn.X, btn.Y, btn.Width - 1, btn.Height - 1), d))
            g.DrawPath(pen, path);
        Svg.SvgFill(g, Icons.ArrowDownLine,
                      Rectangle.Inflate(btn, -Ui.S(9), -Ui.S(9)), Theme.Text);
        if (_jumpCount > 0)
        {
            var label2 = _jumpCount > 99 ? "99+" : _jumpCount.ToString();
            var sz2 = Ui.Measure(label2, Theme.SmallMedium);
            int bw = Math.Max(Ui.S(16), sz2.Width + Ui.S(8)), bh = Ui.S(16);
            var badge = new Rectangle(btn.Right - bw + Ui.S(2), btn.Top - bh / 2 + Ui.S(1), bw, bh);
            Ui.FillRound(g, badge, bh / 2, Theme.Danger);
            Ui.Text(g, label2, Theme.SmallMedium, badge, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        _jumpBox = btn;
    }

    Rectangle _jumpBox, _jumpBanner;

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (!_pinned && (_jumpBox.Contains(e.Location) || _jumpBanner.Contains(e.Location))) ScrollToBottom();
        base.OnMouseClick(e);
    }

    void DrawScrollbar(Graphics g)
    {
        if (MaxScroll <= 0) return;
        // Discord's thin scrollbar only appears while the list is hovered or scrolling; it fades
        // back out after a second of rest. The alpha rides the same glide tick as the scroll.
        int a = _sbDrag ? 255 : (int)(_sbAlpha * 255);
        if (a <= 4) return;
        var t = ThumbBox;
        Ui.FillRound(g, t, t.Width / 2, Color.FromArgb(a, Theme.ScrollThumb));
    }

    /// The thumb, in list coordinates. Shared by the painter and the hit test so a drag can never
    /// grab somewhere the thumb is not actually drawn.
    Rectangle ThumbBox
    {
        get
        {
            int w = Ui.S(8), track = Height;
            int h = Math.Max(Ui.S(30), (int)(track * (track / (float)Math.Max(1, _contentH))));
            int y = MaxScroll <= 0 ? 0 : (int)((track - h) * (_scroll / MaxScroll));
            return new Rectangle(Width - w - Ui.S(2), y, w, h);
        }
    }

    bool _sbDrag;
    int _sbGrabOffset;

    /// Move the thumb's top to `y`, converting back to a scroll offset.
    void ScrollByThumb(int y)
    {
        int track = Height, h = ThumbBox.Height;
        int span = Math.Max(1, track - h);
        SitAt((y - _sbGrabOffset) / (float)span * MaxScroll);
        _pinned = _to >= MaxScroll - Ui.S(8);
        Invalidate();
    }

    // ── scrollbar reveal state ──
    float _sbAlpha;                     // 0..1, glided by the scroll timer
    DateTime _lastScrollActivity = DateTime.MinValue;

    void BumpScrollbar()
    {
        _lastScrollActivity = DateTime.UtcNow;
        if (_sbAlpha < 1f) StartGlide();
    }
}
