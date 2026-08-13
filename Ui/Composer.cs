using System.Drawing;
using System.Drawing.Drawing2D;

namespace OpenCord;

// The message box: reply bar, rounded input well with its action buttons, and the typing strip.
//
// A real TextBox sits inside the well so editing, selection, IME, undo and the caret all behave —
// painting a fake one is where hand-rolled composers usually go wrong. Everything around it is
// painted, because a WinForms Button cannot be made to match Discord's hover states.
sealed class Composer : Panel
{
    readonly HintBox _box;
    readonly List<(Rectangle Box, string Icon, string Tip, Action Click)> _buttons = new();
    int _hot = -1;
    string[] _typing = Array.Empty<string>();
    UserMessage? _editing;

    // Drives the typing dots: a 50ms tick that repaints only while someone is typing, so the
    // staggered bounce animates without burning CPU when nobody is.
    readonly System.Windows.Forms.Timer _dots = new() { Interval = 50 };

    // ── slash commands ──
    SlashMenu? _slash;
    List<UserAppCommand>? _commands;      // cached per channel, fetched once
    ulong? _cmdGuild;                     // which guild the cache belongs to
    bool _cmdLoading;
    // ── @mention / :emoji: autocomplete ──
    AutoMenu? _auto;
    bool _deactivateHooked;
    // ── slash options panel ──
    SlashOptionsForm? _options;
    UserAppCommand? _activeCmd;      // the picked command whose options the panel shows
    string? _activeSub;              // its chosen subcommand, once one is named
    int _missingIdx = -1;            // required option that blocked Enter, shown red

    public event Action<string>? Send;
    public event Action? Typing;
    public event Action? EditLast;        // Up on an empty box -> edit your last message
    public ulong ReplyTo { get; private set; }
    UserMessage? _reply;

    /// The @ toggle on the reply bar. Discord defaults a new reply to pinging the author and
    /// remembers nothing between replies, so this resets to true every time a reply is set.
    public bool PingReply { get; private set; } = true;

    public string Placeholder { get => _box.Hint; set { _box.Hint = value; _box.Invalidate(); } }

    // Draft state, saved and restored per channel by ChatView. Discord keeps whatever you had typed
    // (and who you were replying to) waiting in each channel; carrying one box across a switch put
    // half a message one Enter away from the wrong conversation.
    public UserMessage? Reply => _reply;

    // `new`: Panel.Text is the inherited caption nobody paints here — the draft store wants the
    // inner box's text, and hiding it deliberately is the whole point.
    public new string Text
    {
        get => _box.Text;
        set
        {
            _box.Text = value;
            _box.SelectionStart = value.Length;
            Remeasure();
        }
    }

    // Discord's Esc-to-mark-read only applies when the composer has nothing to cancel or send.
    public bool IsBusy => _editing != null || _reply != null || _box.Text.Length > 0;
    public bool IsEditing => _editing != null;
    public bool InputFocused => _box.Focused;

    /// Whether the input box itself holds a text selection. Ctrl+C has to prefer it over the
    /// message list's, or copying inside a half-written message would grab the wrong text.
    public bool HasInputSelection => _box.SelectionLength > 0;

    // Discord's / shortcut: focus the box and open the slash menu (empty filter shows all). The
    // menu needs the command cache, so a cold fetch first warms it and then opens — same flow as
    // typing / into a fresh channel.
    public void BeginSlash()
    {
        FocusInput();
        if (_box.Text.Length == 0) { _box.Text = "/"; _box.SelectionStart = 1; }
        // Discord types a literal slash into existing text — the caret sits right after it, so the
        // word under it starts with / and the menu opens with an empty filter.
        else { _box.Text += "/"; _box.SelectionStart = _box.Text.Length; }
        if (_commands != null) UpdateSlashMenu();
        else EnsureCommands();
    }

    public void OpenEmojiShortcut()
    {
        FocusInput();
        OpenEmoji();
    }

    public void OpenGifShortcut()
    {
        FocusInput();
        OpenGifs();
    }

    public Composer()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Chat;
        AllowDrop = true;

        // Build the input *before* setting Height: assigning Height raises OnSizeChanged, which lays
        // the box out, and doing it in the other order dereferences a field that does not exist yet.
        _box = new HintBox
        {
            BorderStyle = BorderStyle.None,
            Multiline = true,
            BackColor = Theme.Field,
            ForeColor = Theme.Text,
            Font = Theme.Body,
            AcceptsTab = false,
        };
        _box.KeyDown += OnKey;
        _box.TextChanged += OnTextChanged;
        _box.LostFocus += (_, _) => { HideSlashMenu(); HideAutoMenu(); HideOptionsPanel(); };
        // Only the typing strip animates; a full-width repaint of the input well every 50ms would
        // be pure waste, so the tick touches just the strip's band.
        _dots.Tick += (_, _) => Invalidate(new Rectangle(0, Height - Ui.S(24), Width, Ui.S(24)));
        Controls.Add(_box);

        Height = BaseHeight;

        // A non-activating menu would float over other apps if the whole window deactivates; tuck
        // it away when the shell loses focus rather than leaving a stray card on screen.
        HandleCreated += (_, _) =>
        {
            if (_deactivateHooked) return;
            _deactivateHooked = true;
            var form = FindForm();
            if (form != null) form.Deactivate += (_, _) => { HideSlashMenu(); HideAutoMenu(); HideOptionsPanel(); };
        };
    }

    // ── geometry ────────────────────────────────────────────────────────────────────────────────
    // ── pending attachments ─────────────────────────────────────────────────────────────────────
    // Files wait here until the message is actually sent, which is the whole point: picking a file
    // used to fire it off immediately as its own message, so there was no caption, no preview, no
    // way to change your mind, and ten files meant ten messages.
    sealed class Pending : IDisposable
    {
        public required string Path;
        public required string Name;
        public long Size;
        public Image? Thumb;          // decoded once, at card size
        public bool Spoiler;
        public void Dispose() { Thumb?.Dispose(); Thumb = null; }
    }

    readonly List<Pending> _files = new();
    int _fileHot = -1, _fileHotBtn = -1;    // hovered card, and which control on it (0 remove, 1 spoiler)
    bool _uploading;
    float _uploadPct;

    /// Discord caps one message at ten attachments.
    const int MaxFiles = 10;

    static int CardW => Ui.S(216);
    static int CardH => Ui.S(164);
    static int CardGap => Ui.S(8);
    int TrayH => _files.Count > 0 ? CardH + Ui.S(16) : 0;
    Rectangle Tray => new(Ui.S(16), Ui.S(4) + ReplyH, Math.Max(1, Width - Ui.S(32)), TrayH);

    Rectangle CardBox(int i) =>
        new(Tray.X + Ui.S(8) + i * (CardW + CardGap), Tray.Y + Ui.S(8), CardW, CardH);

    int ReplyH => _reply != null || _editing != null ? Ui.S(32) : 0;
    int TypingH => _typing.Length > 0 ? Ui.S(24) : Ui.S(8);
    int _lines = 1;
    int FieldH => Ui.S(M.ComposerField) + (_lines - 1) * Ui.S(22);
    int BaseHeight => Ui.S(12) + ReplyH + TrayH + FieldH + TypingH;

    Rectangle Field => new(Ui.S(16), Ui.S(4) + ReplyH + TrayH, Math.Max(1, Width - Ui.S(32)), FieldH);
    Rectangle ReplyBar => new(Ui.S(16), Ui.S(4), Math.Max(1, Width - Ui.S(32)), Ui.S(32));

    void Remeasure()
    {
        int want = Math.Clamp(_box.Lines.Length + WrapExtra(), 1, 10);
        int h = BaseHeight;
        if (want != _lines) { _lines = want; h = BaseHeight; }
        if (Height != h) Height = h;
        else Layout();
        Invalidate();
    }

    // TextBox reports logical lines, not visual ones; long single-line pastes still need room.
    int WrapExtra()
    {
        if (_box.Width <= 0 || _box.Text.Length == 0) return 0;
        int extra = 0;
        foreach (var l in _box.Lines)
            extra += Math.Max(0, Ui.Measure(l, Theme.Body).Width / Math.Max(1, _box.Width));
        return extra;
    }

    // Composer chrome, measured off the live well (888x56 at a 1280 viewport): every button is a
    // 32x32 hit box with a 20x20 icon centred in it, at a 40 pitch, 12 clear of the well's edge —
    // the same geometry as the chat header. The text starts at well+63, which is exactly where the
    // upload button ends plus 20.
    static int Btn => Ui.S(32);
    static int BtnIcon => Ui.S(M.HeaderIcon);
    static int BtnPitch => Ui.S(M.HeaderBtnPitch);
    static int BtnInset => Ui.S(12);

    void Layout()
    {
        var f = Field;
        int left = f.X + Ui.S(63);
        int right = f.Right - BtnInset - Btn - BtnPitch * (ButtonCount - 1);
        // 17px of padding above and below the text, per the live well — the box itself is exactly
        // as tall as its lines, so a second line grows the well rather than shifting the first.
        _box.SetBounds(left, f.Y + Ui.S(17), Math.Max(1, right - left), Math.Max(1, f.Height - Ui.S(34)));
        BuildButtons();
    }

    // Measured off the live composer: five 32px buttons on a 40 pitch — gift, GIF, sticker, emoji,
    // Apps. Neither the gift nor the Apps button is here: gift opens Discord's Nitro purchase flow,
    // and Apps duplicates the "/" command list this composer already has.
    int ButtonCount => 3;   // gif, sticker, emoji

    void BuildButtons()
    {
        _buttons.Clear();
        var f = Field;
        int b = Btn;
        int x = f.Right - BtnInset - b;
        int y = f.Bottom - BtnInset - b;

        void Add(string icon, string tip, Action click)
        {
            _buttons.Insert(0, (new Rectangle(x, y, b, b), icon, tip, click));
            x -= BtnPitch;
        }
        // Right to left, so emoji ends up rightmost.
        Add(Icons.SmileyLine, "Select emoji", OpenEmoji);
        Add(Icons.StickerLine, "Open sticker picker", OpenStickers);
        Add(Icons.GifBox, "Open GIF picker", OpenGifs);

        // The "+" upload button sits inside the well on the left, in its own circle.
        _buttons.Add((new Rectangle(f.X + Ui.S(11), y, b, b), Icons.PlusLine, "Upload a file", Upload));
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        if (_box is not null) Layout();
        if (_slash is { Visible: true }) _slash.ShowAbove(PointToScreen(new Point(Field.X + Ui.S(8), Field.Top)));
        if (_auto is { Visible: true }) _auto.ShowAbove(PointToScreen(new Point(Field.X + Ui.S(8), Field.Top)));
        base.OnSizeChanged(e);
    }

    // ── state ───────────────────────────────────────────────────────────────────────────────────
    public void SetReply(UserMessage m)
    {
        _reply = m; _editing = null; ReplyTo = m.Id;
        PingReply = true;
        Remeasure();
    }

    public void ClearReply()
    {
        if (_reply == null && _editing == null) return;
        _reply = null; _editing = null; ReplyTo = 0;
        Remeasure();
    }

    public void BeginEdit(UserMessage m)
    {
        _editing = m; _reply = null; ReplyTo = 0;
        _box.Text = m.Content;
        _box.SelectionStart = _box.Text.Length;
        FocusInput();
        Remeasure();
    }

    public void SetTyping(IReadOnlyList<string> names)
    {
        var next = names.Take(3).ToArray();
        if (next.SequenceEqual(_typing)) return;
        _typing = next;
        _dots.Enabled = _typing.Length > 0;
        Remeasure();
    }

    /// Called by ChatView when the channel changes: drop the command cache so it refetches (a
    /// guild's commands differ from a DM's), and warm it in the background so the first "/" is
    /// instant.
    public void ResetChannel()
    {
        _commands = null;
        HideSlashMenu();
        HideAutoMenu();
        _activeCmd = null; _activeSub = null; _missingIdx = -1;
        HideOptionsPanel();
        EnsureCommands();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _slash?.Dispose(); _auto?.Dispose(); _options?.Dispose(); _dots.Dispose(); }
        base.Dispose(disposing);
    }

    public void FocusInput() => _box.Focus();

    // ── input ───────────────────────────────────────────────────────────────────────────────────
    DateTime _lastTyping = DateTime.MinValue;

    void OnTextChanged(object? s, EventArgs e)
    {
        Remeasure();
        UpdateSlashMenu();
        UpdateAutoMenu();
        UpdateOptionsPanel();
        // Discord rate-limits the typing ping to once every 8s while you keep typing.
        if (_box.Text.Length > 0 && (DateTime.UtcNow - _lastTyping).TotalSeconds > 8)
        {
            _lastTyping = DateTime.UtcNow;
            Typing?.Invoke();
        }
    }

    void OnKey(object? s, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            if (_auto is { Visible: true }) { HideAutoMenu(); e.SuppressKeyPress = true; return; }
            if (_slash is { Visible: true }) { HideSlashMenu(); e.SuppressKeyPress = true; return; }
            if (_options is { Visible: true })
            {
                // Esc on the options panel cancels the command entirely, like Discord.
                _activeCmd = null; _activeSub = null; _missingIdx = -1;
                HideOptionsPanel();
                _box.Clear(); _lines = 1; Remeasure();
                e.SuppressKeyPress = true;
                return;
            }
            if (_reply != null || _editing != null)
            {
                if (_editing != null) _box.Clear();
                ClearReply();
                e.SuppressKeyPress = true;
            }
            return;
        }

        // While the mention/emoji menu is up, arrows navigate it and Enter/Tab picks the row.
        if (_auto is { Visible: true } am)
        {
            if (e.KeyCode == Keys.Up) { am.MoveSel(-1); e.SuppressKeyPress = true; return; }
            if (e.KeyCode == Keys.Down) { am.MoveSel(1); e.SuppressKeyPress = true; return; }
            if (e.KeyCode is Keys.Enter or Keys.Tab)
            {
                e.SuppressKeyPress = true;
                if (am.Selected >= 0) PickAuto(am.Current!);
                return;
            }
        }

        // While the slash menu is up, arrows navigate it and Enter picks the highlighted command
        // (which inserts the full name; a second Enter invokes it).
        if (_slash is { Visible: true } sm && sm.Selected >= 0)
        {
            if (e.KeyCode == Keys.Up) { sm.MoveSel(-1); e.SuppressKeyPress = true; return; }
            if (e.KeyCode == Keys.Down) { sm.MoveSel(1); e.SuppressKeyPress = true; return; }
            if (e.KeyCode == Keys.Tab) { e.SuppressKeyPress = true; if (sm.Selected >= 0) InsertCommandName(sm.Current!); return; }
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                if (sm.Selected >= 0) InsertCommandName(sm.Current!);
                return;
            }
        }

        // Up on an empty box edits your last message, like the real client.
        if (e.KeyCode == Keys.Up && _box.Text.Length == 0 && _reply == null && _editing == null)
        {
            e.SuppressKeyPress = true;
            EditLast?.Invoke();
            return;
        }

        // Ctrl+B / I / U wrap the selection in Discord's markers, exactly like the real composer.
        if (e.Control && !e.Alt && e.KeyCode is Keys.B or Keys.I or Keys.U)
        {
            var mark = e.KeyCode switch { Keys.B => "**", Keys.I => "*", _ => "__" };
            Wrap(mark);
            e.SuppressKeyPress = true;
            return;
        }

        // Ctrl+V with a bitmap on the clipboard attaches it instead of pasting nothing — a
        // screenshot straight into the composer is one of the most-used paths in the real client.
        if (e.Control && e.KeyCode == Keys.V && Clipboard.ContainsImage() && !Clipboard.ContainsText())
        {
            e.SuppressKeyPress = true;
            PasteImage();
            return;
        }

        if (e.KeyCode != Keys.Enter || e.Shift) return;    // Shift+Enter is a newline
        e.SuppressKeyPress = true;
        var t = _box.Text.Trim();

        // Attachments go up with whatever caption is in the box — including none, which is how you
        // send a bare image. This has to come before the empty-text guard for that to work.
        if (_files.Count > 0)
        {
            if (_uploading) return;                        // already in flight; ignore a second Enter
            _box.Clear();
            _lines = 1;
            var reply = ReplyTo;
            ClearReply();
            _ = SendWithFilesAsync(t, reply);
            return;
        }

        if (t.Length == 0) return;

        // Editing always wins: a message being edited can legitimately start with "/name".
        if (_editing is { } m)
        {
            var target = m;
            _editing = null;
            _box.Clear();
            _lines = 1;
            Remeasure();
            _ = SafeEdit(target, t);
            return;
        }

        // A line that names a known slash command invokes it instead of sending as text — but only
        // once every required option has a value, like the real client. Missing ones flash red on
        // the options panel and the box is left intact.
        if (t.StartsWith('/') && !t.Contains('\n'))
        {
            var first = t.Split(' ')[0];
            var cmd = _commands?.FirstOrDefault(c => string.Equals(c.Name, first[1..], StringComparison.OrdinalIgnoreCase));
            if (cmd != null)
            {
                if (cmd.Options.Count > 0)
                {
                    var so = ParseSlashOptions(_box.Text, cmd);
                    var opts = OptionsOf(cmd, so.Sub);
                    bool blocked = cmd.HasSubcommands && so.Sub == null;
                    if (!blocked)
                    {
                        var missing = FirstMissing(cmd, so);
                        if (missing == null)
                        {
                            _box.Clear(); _lines = 1;
                            HideSlashMenu(); HideOptionsPanel();
                            _activeCmd = null; _activeSub = null;
                            _ = InvokeSlash(cmd, so.Sub, so.Values);
                            return;
                        }
                        _missingIdx = opts.IndexOf(missing);
                        UpdateOptionsPanel();
                        Tip.Show(this, "This field is required", Field);
                        return;
                    }
                    _missingIdx = 0;
                    UpdateOptionsPanel();
                    Tip.Show(this, "Choose a subcommand", Field);
                    return;
                }
                _box.Clear(); _lines = 1;
                HideSlashMenu(); HideOptionsPanel();
                _activeCmd = null; _activeSub = null;
                _ = InvokeSlash(cmd, null, new List<(string, string)>());
                return;
            }
        }

        _box.Clear();
        _lines = 1;
        Send?.Invoke(t);
    }

    // ── slash command machinery ──
    async void EnsureCommands()
    {
        if (_cmdLoading) return;
        var guild = App.Guild?.Id;
        if (_commands != null && _cmdGuild == guild) return;
        _cmdLoading = true;
        var c = App.Client;
        if (c == null) { _commands = new(); _cmdGuild = guild; _cmdLoading = false; return; }
        try
        {
            var list = await c.Rest.GetCommandIndexAsync(guild);
            // A slow fetch for a previous guild must not clobber the cache after a channel switch.
            if (_commands == null || _cmdGuild == guild) { _commands = list; _cmdGuild = guild; }
        }
        catch (Exception e)
        {
            Log.Write("slash", e.Message);
            _commands ??= new();
            _cmdGuild ??= guild;
        }
        _cmdLoading = false;
        // A / shortcut may have asked while the fetch was in flight; open once the cache lands.
        // The caret-right-after-a-lone-slash test is the same one UpdateSlashMenu uses, so both the
        // empty-box path (text == "/") and the append path ("hello /") reopen after the fetch.
        bool trailingBareSlash = _box.SelectionStart == _box.Text.Length
                                 && _box.Text.EndsWith('/')
                                 && (_box.Text.Length == 1 || _box.Text[^2] == ' ');
        if (_slash is { Visible: true } && _commands != null && _cmdGuild == App.Guild?.Id)
        {
            _slash.SetCommands(_commands);
            _slash.ApplyFilter(CurrentSlashWord());
        }
        else if (trailingBareSlash && _commands != null && _cmdGuild == App.Guild?.Id)
            UpdateSlashMenu();
    }

    // The word under the caret ("beg" when typing "/beg"); empty when not in a slash word.
    string CurrentSlashWord()
    {
        string text = _box.Text;
        int end = Math.Min(_box.SelectionStart, text.Length);
        int start = text.LastIndexOf(' ', Math.Max(0, end - 1)) + 1;
        var word = text[start..end];
        return word.Length >= 2 && word[0] == '/' && !word.Contains('\n') ? word[1..] : "";
    }

    // Which autocomplete mode the word under the caret opens: / for slash, @ for mentions, : for
    // emoji. A completed :name: closes the emoji menu. Pure so SelfTest can pin the rules.
    public enum AutoMode { None, Slash, Mention, Emoji }

    public static AutoMode ModeOf(string word)
    {
        if (word.Length >= 2 && word[0] == '/') return AutoMode.Slash;
        if (word.Length >= 1 && word[0] == '@') return AutoMode.Mention;
        if (word.Length >= 1 && word[0] == ':') return word.Length >= 3 && word[^1] == ':' ? AutoMode.None : AutoMode.Emoji;
        return AutoMode.None;
    }

    // The markup for a custom emoji, the same form Discord sends in chat.
    public static string EmojiMarkup(string name, ulong id, bool animated) =>
        $"<{(animated ? "a" : "")}:{name}:{id}>";

    string CurrentWord()
    {
        string text = _box.Text;
        int end = Math.Min(_box.SelectionStart, text.Length);
        int start = text.LastIndexOf(' ', Math.Max(0, end - 1)) + 1;
        var word = text[start..end];
        return word.Contains('\n') ? "" : word;
    }

    void UpdateSlashMenu()
    {
        // The / shortcut lands the caret right after a lone "/" (alone, or typed into existing
        // text after a space); that opens with no filter. A word ending in "/" ("hello/") types
        // literally, while "//" filters the menu by "/" — same as Discord.
        string text = _box.Text;
        int end = Math.Min(_box.SelectionStart, text.Length);
        int start = text.LastIndexOf(' ', Math.Max(0, end - 1)) + 1;
        bool bare = end == start + 1 && start < text.Length && text[start] == '/';
        var word = bare ? "" : CurrentSlashWord();
        if (!bare && word.Length == 0) { HideSlashMenu(); return; }
        _slash ??= new SlashMenu(InsertCommandName);
        _activeCmd = null; _activeSub = null; _missingIdx = -1;   // picking anew drops any old panel
        if (_commands != null)
        {
            _slash.SetCommands(_commands);
            _slash.ApplyFilter(word);
            ShowSlashMenu();
        }
        else EnsureCommands();
    }

    // ── mention / emoji autocomplete ──

    // A compact, named slice of the full emoji table for :emoji: completion. Guild emoji (which
    // have real names) come first; this set covers the unicode ones people actually type.
    static readonly (string Name, string Seq)[] CommonEmoji =
    {
        ("joy", "😂"), ("smile", "😊"), ("grin", "😁"), ("laughing", "😆"), ("rofl", "🤣"),
        ("sob", "😭"), ("cry", "😢"), ("angry", "😡"), ("rage", "😠"), ("thinking", "🤔"),
        ("eyes", "👀"), ("thumbsup", "👍"), ("thumbsdown", "👎"), ("clap", "👏"), ("wave", "👋"),
        ("heart", "❤️"), ("broken_heart", "💔"), ("fire", "🔥"), ("star", "⭐"), ("sparkles", "✨"),
        ("100", "💯"), ("tada", "🎉"), ("partying_face", "🥳"), ("gift", "🎁"), ("birthday", "🎂"),
        ("pizza", "🍕"), ("hamburger", "🍔"), ("coffee", "☕"), ("beer", "🍺"), ("wine_glass", "🍷"),
        ("rocket", "🚀"), ("skull", "💀"), ("ghost", "👻"), ("alien", "👽"), ("robot", "🤖"),
        ("sleeping", "😴"), ("sunglasses", "😎"), ("nerd", "🤓"), ("ok_hand", "👌"), ("pray", "🙏"),
        ("muscle", "💪"), ("crown", "👑"), ("gem", "💎"), ("moneybag", "💰"), ("rainbow", "🌈"),
    };

    void UpdateAutoMenu()
    {
        if (OptionValueAutoMenu()) return;
        var w = CurrentWord();
        switch (ModeOf(w))
        {
            case AutoMode.Mention:
                ShowMentionMenu(w[1..]);
                break;
            case AutoMode.Emoji:
                ShowEmojiMenu(w[1..]);
                break;
            default:
                HideAutoMenu();
                break;
        }
    }

    void ShowMentionMenu(string filter)
    {
        var c = App.Client;
        var guild = App.Guild;
        var items = new List<AutoMenu.Item>();
        if (guild != null)
        {
            items.Add(new(AutoMenu.Kind.Everyone, null, "@everyone", "Mention everyone", Theme.Muted, "@everyone "));
            items.Add(new(AutoMenu.Kind.Everyone, null, "@here", "Mention online members", Theme.Muted, "@here "));
            foreach (var role in guild.Roles)
            {
                if (role.Id == guild.Id) continue;   // the implicit @everyone role
                items.Add(new(AutoMenu.Kind.Role, null, "@" + role.Name, "Role", role.Rgb ?? Theme.Muted, $"<@&{role.Id}> "));
            }
            foreach (var m in guild.Members)
            {
                if (m.User == null) continue;
                var dn = m.Nick ?? m.User.DisplayName;
                items.Add(new(AutoMenu.Kind.Member, m.User.GetAvatarUrl(64), dn,
                              "@" + m.User.Username, guild.NameColor(m.User.Id) ?? Theme.Muted, $"<@{m.User.Id}> "));
            }
        }
        else if (c != null)
        {
            foreach (var dm in c.DMChannels)
                foreach (var r in dm.Recipients)
                    items.Add(new(AutoMenu.Kind.Member, r.GetAvatarUrl(64), r.DisplayName,
                                  "@" + r.Username, Theme.Muted, $"<@{r.Id}> "));
        }
        ShowAuto(items, filter);
    }

    void ShowEmojiMenu(string filter)
    {
        var items = new List<AutoMenu.Item>();
        if (App.Guild is { } g)
            foreach (var e in g.Emojis)
            {
                if (!e.Available || !e.RequireColons) continue;
                items.Add(new(AutoMenu.Kind.Emoji, e.Url, ":" + e.Name + ":", "Server emoji", Theme.Muted,
                              EmojiMarkup(e.Name, e.Id, e.Animated)));
            }
        foreach (var (name, seq) in CommonEmoji)
            items.Add(new(AutoMenu.Kind.Emoji, Twemoji.Url(seq), ":" + name + ":", "", Theme.Muted, seq));
        ShowAuto(items, filter);
    }

    void ShowAuto(List<AutoMenu.Item> items, string filter)
    {
        _auto ??= new AutoMenu(PickAuto);
        _auto.SetItems(items);
        _auto.ApplyFilter(filter);
        if (_auto.Visible) _auto.BringToFront();
        else
        {
            // While the options panel is up, its value autocomplete floats above the panel instead
            // of stacking on top of it over the composer.
            var anchor = _options is { Visible: true }
                ? new Point(_options.Location.X + Ui.S(8), _options.Location.Y)
                : PointToScreen(new Point(Field.X + Ui.S(8), Field.Top));
            _auto.ShowAbove(anchor);
        }
    }

    void HideAutoMenu()
    {
        if (_auto is { Visible: true }) _auto.Hide();
    }

    // ── slash options: grammar, panel, sending ──

    /// Split a line on spaces, honouring double-quoted groups so a value like "two words" is one
    /// token. The same rule SearchView uses for its filters.
    public static List<string> SlashTokens(string line)
    {
        var toks = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool inQuote = false;
        foreach (var ch in line)
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

    public sealed record SlashOptions(string? Sub, List<(string Name, string Raw)> Values, Dictionary<int, int> Fill);

    /// The whole option grammar, pure: given the box text and the picked command, resolve which
    /// subcommand is named (if any), which options got values (positionally or as name:value), and
    /// which argument token filled which option (for caret highlighting and value autocomplete).
    /// Kept static and side-effect free so SelfTest can pin every rule.
    public static SlashOptions ParseSlashOptions(string line, UserAppCommand cmd)
    {
        var toks = SlashTokens(line);
        var sub = (string?)null;
        var opts = cmd.Options;
        int argBase = 0;
        bool subBlocked = false;

        // A command with subcommands takes its subcommand name first; values then belong to it.
        // A token that is not a subcommand name blocks filling — Discord shows "pick a subcommand".
        if (cmd.HasSubcommands && toks.Count >= 2)
        {
            var t1 = toks[1].TrimStart('/');
            var subCmd = cmd.Options.FirstOrDefault(o => o.Type is 1 or 2
                && o.Name.Equals(t1, StringComparison.OrdinalIgnoreCase));
            if (subCmd != null) { sub = subCmd.Name; opts = subCmd.Options; argBase = 1; }
            else if (t1.Length > 0) subBlocked = true;
        }

        var values = new List<(string Name, string Raw)>();
        var fill = new Dictionary<int, int>();
        var filled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 1 + argBase; i < toks.Count && !subBlocked; i++)
        {
            var t = toks[i];
            int c = t.IndexOf(':');
            // name:value explicitly targets an option (Discord's API form, handy for skipping).
            if (c > 0 && opts.Any(o => o.Name.Equals(t[..c], StringComparison.OrdinalIgnoreCase)))
            {
                var o = opts.First(o => o.Name.Equals(t[..c], StringComparison.OrdinalIgnoreCase));
                if (filled.Add(o.Name)) { values.Add((o.Name, t[(c + 1)..])); fill[i - argBase] = opts.IndexOf(o); }
                continue;
            }
            // Otherwise positional: the next option in declaration order that has no value yet.
            var target = opts.FirstOrDefault(o => !filled.Contains(o.Name));
            if (target == null) break;
            filled.Add(target.Name);
            values.Add((target.Name, Unquote(t)));
            fill[i - argBase] = opts.IndexOf(target);
        }
        // Discord sends options in declaration order regardless of the order they were typed.
        values.Sort((a, b) => opts.FindIndex(o => o.Name == a.Name).CompareTo(opts.FindIndex(o => o.Name == b.Name)));
        return new SlashOptions(sub, values, fill);
    }

    static string Unquote(string t) =>
        t.Length >= 2 && t[0] == '"' && t[^1] == '"' ? t[1..^1] : t;

    /// The option list a line is filling: the subcommand's options once one is named, else the
    /// command's own. One source of truth for the panel, the autocomplete, the gating and the send.
    static List<UserAppCommandOption> OptionsOf(UserAppCommand cmd, string? sub) =>
        sub is { } s
            ? cmd.Options.FirstOrDefault(o => o.Name == s)?.Options ?? new List<UserAppCommandOption>()
            : cmd.Options;

    /// The first required option that has no value yet, or null when the command is ready to fire.
    /// Pure so SelfTest can pin the gating rule that blocks Enter.
    public static UserAppCommandOption? FirstMissing(UserAppCommand cmd, SlashOptions so)
    {
        var opts = OptionsOf(cmd, so.Sub);
        return opts.FirstOrDefault(o => o.Required && !so.Values.Any(v => v.Name == o.Name));
    }

    /// Coerce a raw typed value to the JSON type Discord's interaction API expects for the option:
    /// numbers stay numbers, booleans stay booleans, and user/channel/role/mentionable values are
    /// snowflakes (bare or wrapped in <@id>/<#id>/<@&id> markup).
    public static object CoerceSlashValue(UserAppCommandOption o, string raw)
    {
        var v = raw.Trim();
        if (o.Type == 4 && long.TryParse(v, out var l)) return l;
        if (o.Type == 5 && bool.TryParse(v, out var b)) return b;
        if (o.Type == 10 && double.TryParse(v, out var d)) return d;
        if (o.Type is 6 or 7 or 8 or 9) return Snowflake(v) ?? v;
        return v;
    }

    // <@123>, <@!123>, <#123>, <@&123> → the id digits; null when it isn't markup.
    static string? Snowflake(string v)
    {
        if (v.Length < 4 || v[0] != '<' || v[^1] != '>') return null;
        var inner = v[1..^1].TrimStart('@', '#', '&', '!');
        return inner.Length > 0 && inner.All(char.IsDigit) ? inner : null;
    }

    // Which option row the caret's argument is editing. When the caret sits on an argument with no
    // value yet (right after picking a command), fall back to the first unfilled option so the row
    // highlights and its user/channel/role autocomplete pops immediately — like the real client.
    static int ActiveOption(SlashOptions so, string text, int caret, IReadOnlyList<UserAppCommandOption> opts)
    {
        if (opts.Count == 0) return -1;
        caret = Math.Clamp(caret, 0, text.Length);
        int toks = 1;
        bool inSpace = false;
        for (int i = 0; i < caret; i++)
        {
            if (text[i] == ' ') { if (!inSpace) toks++; inSpace = true; }
            else inSpace = false;
        }
        int arg = toks - 1 - (so.Sub != null ? 1 : 0);
        if (arg < 0) return -1;
        if (so.Fill.TryGetValue(arg, out var hit)) return hit;
        // Caret in whitespace past the typed values: the next option to fill.
        var filled = so.Values.Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < opts.Count; i++)
            if (!filled.Contains(opts[i].Name)) return i;
        return opts.Count - 1;
    }

    void UpdateOptionsPanel()
    {
        var cmd = _activeCmd;
        if (cmd == null || _box.Text.Length == 0) { HideOptionsPanel(); return; }
        // If the command name was edited away (a word boundary after it), the panel no longer
        // applies — typing /warning after picking /warn is a different command, not a value.
        var rest = _box.Text.TrimStart();
        bool sameCmd = rest.StartsWith("/" + cmd.Name, StringComparison.OrdinalIgnoreCase)
                       && (rest.Length == cmd.Name.Length + 1 || char.IsWhiteSpace(rest[cmd.Name.Length + 1]));
        if (!sameCmd)
        {
            _activeCmd = null; _activeSub = null; _missingIdx = -1;
            HideOptionsPanel();
            return;
        }
        var so = ParseSlashOptions(_box.Text, cmd);
        _activeSub = so.Sub;
        var opts = OptionsOf(cmd, so.Sub);
        // The panel lists the subcommands as rows until one is chosen; nothing else is fillable.
        bool blocked = cmd.HasSubcommands && so.Sub == null;
        // A field that was flagged missing clears once it has a value.
        if (_missingIdx >= 0 && _missingIdx < opts.Count
            && so.Values.Any(v => v.Name == opts[_missingIdx].Name)) _missingIdx = -1;
        int active = ActiveOption(so, _box.Text, _box.SelectionStart, opts);
        _options ??= new SlashOptionsForm(FocusOptionRow);
        _options.Set("/" + cmd.Name, cmd.Description, cmd.AppName, opts, so.Values, active, _missingIdx, blocked);
        if (!_options.Visible) _options.ShowAbove(PointToScreen(new Point(Field.X + Ui.S(8), Field.Top)));
        else _options.BringToFront();
    }

    void HideOptionsPanel()
    {
        if (_options is { Visible: true }) _options.Hide();
    }

    // Clicking a subcommand row picks it (types its name); any other row just refocuses the box
    // so the value keeps typing — a non-activating form can't take the caret itself.
    void FocusOptionRow(int row)
    {
        var cmd = _activeCmd;
        if (cmd == null) return;
        var so = ParseSlashOptions(_box.Text, cmd);
        var opts = OptionsOf(cmd, so.Sub);
        if (row < 0 || row >= opts.Count) { FocusInput(); return; }
        var o = opts[row];
        if (o.Type is 1 or 2)   // subcommand row: insert its name
        {
            string text = _box.Text;
            int caret = Math.Min(_box.SelectionStart, text.Length);
            int sp = text.LastIndexOf(' ', Math.Max(0, caret - 1));
            _box.Text = (sp >= 0 ? text[..(sp + 1)] : "") + "/" + cmd.Name + " " + o.Name + " ";
            _box.SelectionStart = _box.Text.Length;
            Remeasure();
        }
        FocusInput();
    }

    // While filling a user/channel/role option of the active command, the autocomplete lists the
    // guild roster / channels / roles instead of the ordinary @/: menus.
    bool OptionValueAutoMenu()
    {
        var cmd = _activeCmd;
        if (cmd == null || _box.Text.Length == 0) return false;
        var so = ParseSlashOptions(_box.Text, cmd);
        var opts = OptionsOf(cmd, so.Sub);
        int idx = ActiveOption(so, _box.Text, _box.SelectionStart, opts);
        if (idx < 0 || idx >= opts.Count) return false;
        var o = opts[idx];
        if (o.Type is not (6 or 7 or 8)) return false;

        string w = CurrentWord();
        int c = w.IndexOf(':');
        string filter = c > 0 ? w[(c + 1)..] : w;
        var items = new List<AutoMenu.Item>();
        var guild = App.Guild;
        if (o.Type == 6)
        {
            if (guild != null)
                foreach (var m in guild.Members)
                {
                    if (m.User == null) continue;
                    var dn = m.Nick ?? m.User.DisplayName;
                    items.Add(new(AutoMenu.Kind.Member, m.User.GetAvatarUrl(64), dn,
                                  "@" + m.User.Username, guild.NameColor(m.User.Id) ?? Theme.Muted, $"<@{m.User.Id}> "));
                }
            else if (App.Client != null)
                foreach (var dm in App.Client.DMChannels)
                    foreach (var r in dm.Recipients)
                        items.Add(new(AutoMenu.Kind.Member, r.GetAvatarUrl(64), r.DisplayName,
                                      "@" + r.Username, Theme.Muted, $"<@{r.Id}> "));
        }
        else if (o.Type == 7 && guild != null)
            foreach (var ch in guild.Channels.Where(ch => ch.IsText))
                items.Add(new(AutoMenu.Kind.Channel, null, "#" + ch.Name, "Text channel", Theme.Muted, $"<#{ch.Id}> "));
        else if (o.Type == 8 && guild != null)
            foreach (var role in guild.Roles)
            {
                if (role.Id == guild.Id) continue;
                items.Add(new(AutoMenu.Kind.Role, null, role.Name, "Role", role.Rgb ?? Theme.Muted, $"<@&{role.Id}> "));
            }
        if (items.Count == 0) return false;
        ShowAuto(items, filter);
        return true;
    }

    // Replace the @word / :word: under the caret with the picked mention or emoji, however far the
    // caret sits into it. Mentions keep their trailing space (so the next word just types); emoji
    // leave the caret adjacent to insert.
    void PickAuto(AutoMenu.Item item)
    {
        HideAutoMenu();
        string text = _box.Text;
        int caret = Math.Min(_box.SelectionStart, text.Length);
        int start = text.LastIndexOf(' ', Math.Max(0, caret - 1)) + 1;
        int end = text.IndexOf(' ', caret);
        if (end < 0) end = text.Length;
        string insert = item.Insert;
        // A slash option typed as name:value keeps the "name:" and replaces only the value part.
        if (_activeCmd != null)
        {
            var tok = text[start..caret];
            int c = tok.IndexOf(':');
            if (c > 0)
            {
                _box.Text = text[..(start + c + 1)] + insert + text[end..];
                _box.SelectionStart = start + c + 1 + insert.Length;
                Remeasure();
                FocusInput();
                return;
            }
        }
        _box.Text = text[..start] + insert + text[end..];
        _box.SelectionStart = start + insert.Length;
        Remeasure();
        FocusInput();
    }

    void ShowSlashMenu()
    {
        if (_slash is not { } sm) return;
        if (!sm.Visible) sm.ShowAbove(PointToScreen(new Point(Field.X + Ui.S(8), Field.Top)));
        else sm.BringToFront();
    }

    void HideSlashMenu()
    {
        if (_slash is { Visible: true }) _slash.Hide();
    }

    void InsertCommandName(UserAppCommand cmd)
    {
        HideSlashMenu();
        string text = _box.Text;
        int end = Math.Min(_box.SelectionStart, text.Length);
        int start = text.LastIndexOf(' ', Math.Max(0, end - 1)) + 1;
        _box.Text = text[..start] + "/" + cmd.Name + " ";
        _box.SelectionStart = _box.Text.Length;
        // The panel only matters for commands that take options; it drives the rest of the flow.
        _activeCmd = cmd.Options.Count > 0 ? cmd : null;
        _activeSub = null; _missingIdx = -1;
        if (_activeCmd != null) UpdateOptionsPanel();   // TextChanged already fired for the text set
        Remeasure();
        FocusInput();
    }

    async Task InvokeSlash(UserAppCommand cmd, string? sub, List<(string Name, string Raw)> values)
    {
        var c = App.Client;
        ulong ch = (FindForm() as Shell)?.Chat.Channel?.Id ?? 0;
        if (c == null || ch == 0) return;
        var opts = OptionsOf(cmd, sub);
        var payload = new List<object>();
        foreach (var (name, raw) in values)
        {
            var o = opts.FirstOrDefault(x => x.Name == name);
            if (o == null) continue;
            payload.Add(new { name, value = CoerceSlashValue(o, raw) });
        }
        object options = payload;
        if (sub != null)
            options = new List<object> { new { name = sub, type = 1, options = payload } };
        var err = await c.Rest.InvokeCommandAsync(cmd, ch, App.Guild?.Id, options);
        if (err != null)
        {
            Log.Write("slash", err);
            Tip.Show(this, err, Field);
        }
    }

    static async Task SafeEdit(UserMessage m, string text)
    {
        try { await m.ModifyAsync(text); }
        catch (Exception e) { Log.Write("chat", "edit failed: " + e.Message); }
    }

    /// The screen rect of the composer button carrying `icon`, so a picker can hang off the button
    /// that opened it. Looked up by icon rather than passed in, because the same picker is opened
    /// both by clicking the button and by its keyboard shortcut.
    Rectangle ButtonRect(string icon)
    {
        int i = _buttons.FindIndex(b => b.Icon == icon);
        return RectangleToScreen(i >= 0 ? _buttons[i].Box : Field);
    }

    /// The picker whose button is currently showing a popup, so a click on the *same* button reads
    /// as "close", and a click on a different one as "swap".
    string? _openPicker;

    /// A click dismissed the open picker. The dropdown swallowed it, so if it landed on another
    /// picker button, run that button here — otherwise switching pickers takes two clicks.
    public void PickerDismissedAt(Point screen)
    {
        var local = PointToClient(screen);
        int i = _buttons.FindIndex(b => b.Box.Contains(local));
        var was = _openPicker;
        _openPicker = null;
        if (i < 0 || _buttons[i].Icon == was) return;   // same button = the user closed it
        _buttons[i].Click();
    }

    /// The three pickers share one panel with a tab row (see PickerChrome); switching tabs reopens
    /// the sibling at the same anchor. Registered here because the composer is the only thing that
    /// knows how to build all three.
    void HookPickerTabs() => PickerChrome.Open = t =>
    {
        switch (t)
        {
            case PickerChrome.Tab.Gifs: OpenGifs(); break;
            case PickerChrome.Tab.Stickers: OpenStickers(); break;
            case PickerChrome.Tab.Emoji: OpenEmoji(); break;
        }
    };

    void OpenEmoji()
    {
        HookPickerTabs();
        _openPicker = Icons.SmileyLine;
        EmojiPicker.Pick(this, ButtonRect(Icons.SmileyLine), Insert);
    }

    void OpenGifs()
    {
        HookPickerTabs();
        _openPicker = Icons.GifBox;
        GifPicker.Pick(this, ButtonRect(Icons.GifBox), Insert);
    }

    void OpenStickers()
    {
        ulong ch = (FindForm() as Shell)?.Chat.Channel?.Id ?? 0;
        if (ch == 0) return;
        HookPickerTabs();
        _openPicker = Icons.StickerLine;
        StickerPicker.Pick(this, ButtonRect(Icons.StickerLine), ch);
    }

    /// Wrap the selection (or, with nothing selected, drop the pair and put the caret inside).
    /// Wrapping an already-wrapped selection unwraps it, which is what every editor does.
    void Wrap(string mark)
    {
        int start = _box.SelectionStart, len = _box.SelectionLength;
        var text = _box.Text;
        var sel = text.Substring(start, len);

        if (len > 0 && sel.Length >= mark.Length * 2
            && sel.StartsWith(mark, StringComparison.Ordinal) && sel.EndsWith(mark, StringComparison.Ordinal))
        {
            var inner = sel[mark.Length..^mark.Length];
            _box.Text = text.Remove(start, len).Insert(start, inner);
            _box.SelectionStart = start;
            _box.SelectionLength = inner.Length;
            return;
        }

        _box.Text = text.Remove(start, len).Insert(start, mark + sel + mark);
        if (len > 0) { _box.SelectionStart = start; _box.SelectionLength = sel.Length + mark.Length * 2; }
        else _box.SelectionStart = start + mark.Length;   // caret between the markers
    }

    public void Insert(string text)
    {
        int at = _box.SelectionStart;
        _box.Text = _box.Text.Insert(at, text);
        _box.SelectionStart = at + text.Length;
        FocusInput();
    }

    void Upload()
    {
        using var dlg = new OpenFileDialog { Multiselect = true, Title = "Upload files" };
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        AddFiles(dlg.FileNames);
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        base.OnDragEnter(e);
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) AddFiles(files);
        base.OnDragDrop(e);
    }

    /// Queue files onto the tray. Never sends: that is Enter's job, so the message and its
    /// attachments go up together the way the real client does.
    public void AddFiles(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (_files.Count >= MaxFiles) { Tip.Show(this, $"Up to {MaxFiles} files per message", Field); break; }
            if (!File.Exists(p)) continue;
            var fi = new FileInfo(p);
            var pend = new Pending { Path = p, Name = fi.Name, Size = fi.Length };
            pend.Thumb = LoadThumb(p);
            _files.Add(pend);
        }
        Remeasure();
        FocusInput();
    }

    /// A pasted image becomes an attachment, like Ctrl+V in the real client. Written to a temp file
    /// because the upload path streams from disk rather than holding the bitmap in memory.
    public bool PasteImage()
    {
        try
        {
            if (!Clipboard.ContainsImage()) return false;
            using var img = Clipboard.GetImage();
            if (img == null) return false;
            var path = Path.Combine(Path.GetTempPath(), $"opencord-paste-{DateTime.Now:HHmmssfff}.png");
            img.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            AddFiles(new[] { path });
            return true;
        }
        catch (Exception e) { Log.Write("chat", "paste image: " + e.Message); return false; }
    }

    static Image? LoadThumb(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp")) return null;
        try
        {
            // Decode straight to card size: holding a 12MP camera JPEG per card is how a tray of
            // ten photos turns into a few hundred MB of bitmaps.
            using var src = Image.FromFile(path);
            int w = CardW - Ui.S(16), h = CardH - Ui.S(48);
            float scale = Math.Min(w / (float)src.Width, h / (float)src.Height);
            int tw = Math.Max(1, (int)(src.Width * scale)), th = Math.Max(1, (int)(src.Height * scale));
            var bmp = new Bitmap(tw, th);
            using var g = Graphics.FromImage(bmp);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, 0, 0, tw, th);
            return bmp;
        }
        catch { return null; }
    }

    void RemoveFile(int i)
    {
        if (i < 0 || i >= _files.Count) return;
        _files[i].Dispose();
        _files.RemoveAt(i);
        _fileHot = _fileHotBtn = -1;
        Remeasure();
    }

    void ClearFiles()
    {
        foreach (var f in _files) f.Dispose();
        _files.Clear();
        _fileHot = _fileHotBtn = -1;
    }

    // ── tray paint ──────────────────────────────────────────────────────────────────────────────
    void PaintTray(Graphics g)
    {
        var tray = Tray;
        Ui.FillRound(g, tray, Ui.S(8), Theme.Surface);

        for (int i = 0; i < _files.Count; i++)
        {
            var box = CardBox(i);
            if (box.Right > tray.Right) break;            // more than fits: the rest stay queued
            var f = _files[i];
            Ui.FillRound(g, box, Ui.S(8), Theme.Field);

            var preview = new Rectangle(box.X + Ui.S(8), box.Y + Ui.S(8), box.Width - Ui.S(16), box.Height - Ui.S(48));
            if (f.Thumb != null)
            {
                var dst = new Rectangle(preview.X + (preview.Width - f.Thumb.Width) / 2,
                                        preview.Y + (preview.Height - f.Thumb.Height) / 2,
                                        f.Thumb.Width, f.Thumb.Height);
                g.DrawImage(f.Thumb, dst);
                // A spoilered attachment is blurred in the real client; a flat scrim is the honest
                // cheap equivalent and still reads as "hidden until clicked".
                if (f.Spoiler)
                {
                    using var scrim = new SolidBrush(Color.FromArgb(200, 24, 25, 28));
                    g.FillRectangle(scrim, dst);
                    Ui.Text(g, "SPOILER", Theme.SmallMedium, dst, Theme.Text,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            }
            else
            {
                Ui.Text(g, "📄", Theme.Emoji, preview, Theme.Muted,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            Ui.Text(g, f.Name, Theme.SmallMedium,
                    new Rectangle(box.X + Ui.S(8), box.Bottom - Ui.S(38), box.Width - Ui.S(16), Ui.S(18)),
                    Theme.Text, TextFormatFlags.EndEllipsis);
            Ui.Text(g, Size(f.Size) + (f.Spoiler ? "  ·  spoiler" : ""), Theme.Small,
                    new Rectangle(box.X + Ui.S(8), box.Bottom - Ui.S(21), box.Width - Ui.S(16), Ui.S(16)),
                    Theme.Faint, TextFormatFlags.EndEllipsis);

            // Controls appear on hover, like the real card's action row.
            if (_fileHot == i)
            {
                PaintCardBtn(g, RemoveBox(i), Icons.CloseLine, _fileHotBtn == 0, danger: true);
                // A labelled pill rather than an icon: there is no eye glyph in the extracted set
                // and inventing one would be the only hand-drawn shape in the whole client.
                var sb = SpoilerBox(i);
                Ui.FillRound(g, sb, Ui.S(6), f.Spoiler ? Theme.Blurple
                                           : _fileHotBtn == 1 ? Theme.SurfaceHigh : Theme.Floating);
                Ui.Text(g, "Spoiler", Theme.Small, sb, f.Spoiler ? Color.White : Theme.Muted,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        if (_uploading)
        {
            var bar = new Rectangle(tray.X + Ui.S(8), tray.Bottom - Ui.S(5), tray.Width - Ui.S(16), Ui.S(3));
            Ui.FillRound(g, bar, Ui.S(2), Theme.Border);
            Ui.FillRound(g, new Rectangle(bar.X, bar.Y, (int)(bar.Width * _uploadPct), bar.Height), Ui.S(2), Theme.Blurple);
        }
    }

    void PaintCardBtn(Graphics g, Rectangle b, string icon, bool hot, bool danger)
    {
        Ui.FillRound(g, b, Ui.S(6), hot ? (danger ? Theme.Danger : Theme.SurfaceHigh) : Theme.Floating);
        Svg.SvgFill(g, icon, Rectangle.Inflate(b, -Ui.S(6), -Ui.S(6)), hot ? Color.White : Theme.Muted);
    }

    Rectangle RemoveBox(int i) { var b = CardBox(i); return new Rectangle(b.Right - Ui.S(32), b.Y + Ui.S(8), Ui.S(24), Ui.S(24)); }
    Rectangle SpoilerBox(int i) { var b = CardBox(i); return new Rectangle(b.Right - Ui.S(90), b.Y + Ui.S(8), Ui.S(52), Ui.S(24)); }

    static string Size(long n) =>
        n >= 1024L * 1024 ? $"{n / 1024.0 / 1024:0.0} MB" : n >= 1024 ? $"{n / 1024.0:0.0} KB" : $"{n} B";

    async Task SendWithFilesAsync(string text, ulong replyTo)
    {
        var ch = (FindForm() as Shell)?.Chat.Channel;
        if (ch == null || App.Client == null) { ClearFiles(); Remeasure(); return; }

        // Discord marks a spoilered attachment by the filename alone, so the flag travels as a
        // SPOILER_ prefix rather than a field.
        var send = _files.Select(f => (f.Path, Name: f.Spoiler ? "SPOILER_" + f.Name : f.Name)).ToList();
        _uploading = true;
        _uploadPct = 0f;
        Invalidate();
        try
        {
            await App.Client.Rest.SendFilesAsync(ch.Id, send, text, replyTo,
                                                 p => { _uploadPct = p; Invalidate(); });
        }
        catch (Exception e) { Log.Write("chat", "upload failed: " + e.Message); }
        _uploading = false;
        ClearFiles();
        Remeasure();
    }

    // ── mouse ───────────────────────────────────────────────────────────────────────────────────
    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = _buttons.FindIndex(b => b.Box.Contains(e.Location));
        if (h != _hot)
        {
            _hot = h;
            Tip.Show(this, h >= 0 ? _buttons[h].Tip : null, h >= 0 ? _buttons[h].Box : Rectangle.Empty);
            Invalidate();
        }
        int card = -1, cardBtn = -1;
        for (int i = 0; i < _files.Count; i++)
            if (CardBox(i).Contains(e.Location))
            {
                card = i;
                cardBtn = RemoveBox(i).Contains(e.Location) ? 0
                        : SpoilerBox(i).Contains(e.Location) ? 1 : -1;
                break;
            }
        if (card != _fileHot || cardBtn != _fileHotBtn) { _fileHot = card; _fileHotBtn = cardBtn; Invalidate(); }

        Cursor = h >= 0 || cardBtn >= 0 || CloseBox.Contains(e.Location) || PingBox.Contains(e.Location)
               ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hot != -1) { _hot = -1; Tip.Hide(); Invalidate(); }
        base.OnMouseLeave(e);
    }

    Rectangle CloseBox => ReplyH == 0 ? Rectangle.Empty
        : new Rectangle(ReplyBar.Right - Ui.S(28), ReplyBar.Y + Ui.S(8), Ui.S(16), Ui.S(16));

    /// The @ pill. Only a reply has one — editing a message cannot ping anyone.
    Rectangle PingBox => _reply == null ? Rectangle.Empty
        : new Rectangle(ReplyBar.Right - Ui.S(104), ReplyBar.Y + Ui.S(5), Ui.S(66), Ui.S(22));

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) { base.OnMouseDown(e); return; }
        if (CloseBox.Contains(e.Location)) { if (_editing != null) _box.Clear(); ClearReply(); return; }
        if (PingBox.Contains(e.Location)) { PingReply = !PingReply; Invalidate(); return; }
        if (_fileHot >= 0 && _fileHotBtn == 0) { RemoveFile(_fileHot); return; }
        if (_fileHot >= 0 && _fileHotBtn == 1)
        {
            _files[_fileHot].Spoiler = !_files[_fileHot].Spoiler;
            Invalidate();
            return;
        }
        if (_hot >= 0) { _buttons[_hot].Click(); return; }
        if (Field.Contains(e.Location)) FocusInput();
        base.OnMouseDown(e);
    }

    // ── paint ───────────────────────────────────────────────────────────────────────────────────
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.Fill(g, ClientRectangle, Theme.Chat);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (_buttons.Count == 0) BuildButtons();

        if (ReplyH > 0) PaintReplyBar(g);
        if (TrayH > 0) PaintTray(g);
        Ui.FillRound(g, Field, Ui.S(M.ComposerRadius), Theme.Field);

        for (int i = 0; i < _buttons.Count; i++)
        {
            var (box, icon, _, _) = _buttons[i];
            var col = _hot == i ? Theme.Text : Theme.ChannelIcon;
            // The hit box is 32; the glyph inside it is 20. Drawing the icon at the box's size is
            // what made the composer chrome read a size larger than the real client's.
            var ib = new Rectangle(box.X + (box.Width - BtnIcon) / 2, box.Y + (box.Height - BtnIcon) / 2,
                                   BtnIcon, BtnIcon);
            if (icon == Icons.PlusLine)
            {
                // Upload is a filled circle with a knocked-out plus, not a bare glyph.
                using var b = new SolidBrush(_hot == i ? Theme.Text : Theme.ChannelIcon);
                g.FillEllipse(b, ib);
                Svg.SvgFill(g, Icons.PlusLine, Rectangle.Inflate(ib, -Ui.S(4), -Ui.S(4)), Theme.Field);
            }
            else if (icon == Icons.GifBox)
            {
                Icons.Draw(g, icon, ib, col);
                Ui.Text(g, "GIF", Theme.GifBadge, ib, col,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            else Icons.Draw(g, icon, ib, col);
        }

        if (_typing.Length > 0) PaintTyping(g);
    }

    void PaintReplyBar(Graphics g)
    {
        var r = ReplyBar;
        Ui.FillRound(g, r, Ui.S(8), Theme.Surface);
        // Only the top corners are round: the bar sits flush on the input well below it.
        Ui.Fill(g, new Rectangle(r.X, r.Bottom - Ui.S(10), r.Width, Ui.S(10)), Theme.Surface);

        string text = _editing != null
            ? "Editing message  —  escape to cancel"
            : "Replying to " + (_reply?.Member?.Nick ?? _reply?.Author?.DisplayName ?? "message");
        Ui.Text(g, text, Theme.Small,
                new Rectangle(r.X + Ui.S(12), r.Y, r.Width - Ui.S(_reply != null ? 130 : 60), Ui.S(30)),
                Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        if (_reply != null)
        {
            var pb = PingBox;
            Ui.FillRound(g, pb, Ui.S(4), PingReply ? Theme.Blurple : Theme.Field);
            Ui.Text(g, PingReply ? "@ ON" : "@ OFF", Theme.SmallMedium, pb,
                    PingReply ? Color.White : Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        Svg.SvgFill(g, Icons.CloseLine, CloseBox, Theme.Muted);
    }

    void PaintTyping(Graphics g)
    {
        var r = new Rectangle(Ui.S(20), Height - Ui.S(22), Width - Ui.S(40), Ui.S(20));
        // Measured: 7px dots on a 9px pitch, then the text 24 further along — the name in 12px/600
        // --text-default, the rest in 12px/500 --text-subtle. It was one flat 12px/400 string in
        // --text-muted, which read a shade dim and lost the emphasis on who is typing.
        double t = Environment.TickCount64 / 300.0;
        for (int i = 0; i < 3; i++)
        {
            int d = Ui.S(7);
            int lift = (int)(Math.Max(0, Math.Sin(t - i * 0.5)) * Ui.S(3));
            using var b = new SolidBrush(Theme.Subtle);
            g.FillEllipse(b, r.X + i * Ui.S(9), r.Y + Ui.S(7) - lift, d, d);
        }

        string names = string.Join(", ", _typing);
        string tail = _typing.Length == 1 ? " is typing..." : " are typing...";
        int tx = r.X + Ui.S(28);
        int nameW = Math.Min(Ui.Measure(names, Theme.SmallSemibold).Width, Math.Max(0, r.Right - tx - Ui.S(80)));
        Ui.Text(g, names, Theme.SmallSemibold, new Rectangle(tx, r.Y, nameW, r.Height), Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        Ui.Text(g, tail, Theme.SmallMedium, new Rectangle(tx + nameW, r.Y, r.Right - tx - nameW, r.Height),
                Theme.Subtle, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

// A TextBox that keeps its hint visible while focused.
//
// WinForms' own PlaceholderText hides the moment the box takes focus, but Discord leaves
// "Message #general" in place until you actually type a character — with the caret sitting in front
// of it. That difference is very visible, because clicking the composer is the first thing anyone
// does. Painting after WM_PAINT keeps the real TextBox (caret, selection, IME, undo) and only adds
// the one thing it gets wrong.
sealed class HintBox : TextBox
{
    const int WM_PAINT = 0x000F;

    public string Hint { get; set; } = "";

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg != WM_PAINT || TextLength > 0 || Hint.Length == 0) return;
        using var g = Graphics.FromHwnd(Handle);
        // GetPositionFromCharIndex(0) is where the box would put the first character, so the hint
        // lands exactly where typing will — no guessing at the control's internal margin.
        var at = GetPositionFromCharIndex(0);
        Ui.Text(g, Hint, Font, at, Theme.Placeholder);
    }
}
