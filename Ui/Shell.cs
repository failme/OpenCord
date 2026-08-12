using System.Drawing;
using System.Runtime.InteropServices;

namespace ClaudeScord;

// The application window: custom chrome plus the four regions.
//
// Region order matters, and it is the opposite of what it looks like. WinForms resolves docking from
// the *highest* z-order index down to zero, and Controls.Add appends — so the control added first is
// positioned last and receives whatever space the others left. The Fill must therefore be added
// first. Add it second and it claims the member list's width as well, and every message in the chat
// wraps 264px too wide and then gets painted over.
//
// The rail and the sidebar deliberately share one colour with no seam between them — that is what
// the live client does, and drawing a divider there is the single most obvious tell that a clone was
// built from screenshots rather than measurement.
sealed class Shell : Form
{
    readonly TitleBar _title;

    public GuildRail Rail { get; } = new() { Dock = DockStyle.Left };
    public ChannelSidebar Sidebar { get; } = new() { Dock = DockStyle.Left };

    /// The account panel spans the rail *and* the channel list, so it cannot be docked inside
    /// either: it is positioned over the bottom-left of both. Rail and Sidebar each reserve
    /// AccountTray.TrayH at their bottom so nothing is drawn underneath it.
    public AccountTray Tray { get; } = new();
    public MemberList Members { get; } = new() { Dock = DockStyle.Right };
    public ChatView Chat { get; } = new() { Dock = DockStyle.Fill };

    /// Call overlay (incoming/active). Not docked (a second Fill would starve the chat); it is
    /// re-bounded over the whole client area whenever it is shown and on resize. Added last so it
    /// paints above everything.
    public CallBanner Call { get; } = new();

    /// The Friends page. Same "not docked" rule as the call overlay, but it covers only the chat
    /// pane's rectangle rather than the window.
    public FriendsView Friends { get; } = new();
    public DiscoverView Discover { get; } = new();

    /// The voice channel stage. Covers the chat pane while connected to a guild voice channel.
    public VoiceView Voice { get; } = new();

    /// A forum channel's post list. Same rule again: covers the chat pane's rectangle.
    public ForumView Forum { get; } = new();

    public Shell()
    {
        Text = "ClaudeScord";
        // ApplicationIcon only sets the icon on the .exe itself. A Form still shows the default
        // WinForms icon in the taskbar and Alt+Tab unless it is told, and FormBorderStyle.None hides
        // the caption where you would normally notice. Read it back out of our own executable so
        // there is one source of truth and nothing extra to ship.
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(Ui.S(940), Ui.S(560));
        // 1280x800 design px is 1920x1200 at 150%, i.e. larger than the screen it is meant to sit on.
        // Clamp to the work area so the default launch is a window, not an accidental fullscreen.
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        ClientSize = new Size(Math.Min(Ui.S(1280), wa.Width - Ui.S(40)),
                              Math.Min(Ui.S(800), wa.Height - Ui.S(40)));
        RestoreBounds_();
        BackColor = Theme.Chat;
        DoubleBuffered = true;
        KeyPreview = true;

        // Every region sizes itself from M in its own constructor.
        _title = new TitleBar(this) { Dock = DockStyle.Top, Height = Ui.S(M.TitleBar) };

        // Fill first. See the note above the class. The call banner goes on last so it covers the
        // whole window while active.
        Controls.Add(Chat);
        Controls.Add(Members);
        Controls.Add(Sidebar);
        Controls.Add(Rail);
        Controls.Add(Friends);
        Controls.Add(Discover);
        Controls.Add(Voice);
        Controls.Add(Forum);
        Controls.Add(Call);
        // Added before the title bar so it docks *under* it: WinForms lays docked children out
        // from the highest z-index down, and the later-added control takes the outer edge.
        Controls.Add(_conn);
        Controls.Add(_title);
        // Not docked — positioned over the bottom-left corner of the rail and the sidebar.
        Controls.Add(Tray);
        LayoutTray();
        SetUpTray();

        Resize += (_, _) =>
        {
            // Keep the call backdrop covering the app (minus the title bar strip it sits under).
            if (Call.Visible) Call.Bounds = ClientRectangle;
            if (Friends.Visible) Friends.Bounds = Chat.Bounds;
            if (Discover.Visible) Discover.Bounds = Chat.Bounds;
            if (Voice.Visible) Voice.Bounds = Chat.Bounds;
            if (Forum.Visible) Forum.Bounds = Chat.Bounds;
            LayoutTray();
        };
    }

    /// The tray sits across the bottom of the rail and the sidebar. Positioned rather than docked,
    /// because a docked child can only take a full edge of its parent and this one needs the corner.
    void LayoutTray()
    {
        int h = Ui.S(AccountTray.TrayH);
        Tray.Bounds = new Rectangle(0, ClientSize.Height - h, Rail.Width + Sidebar.Width, h);
        Tray.BringToFront();
    }

    /// Which of the four mutually exclusive panes owns the chat rectangle right now.
    public enum Pane { Chat, Friends, Voice, Forum, Discover }

    Pane _pane = Pane.Chat;

    /// One switch for all of them. Toggling each pane against the others pairwise is how a fourth
    /// one ends up overlapping a third: with Chat/Friends/Voice/Forum there are too many pairs to
    /// keep straight, so visibility is derived from a single value instead.
    public void ShowPane(Pane p)
    {
        _pane = p;
        Chat.Visible = p == Pane.Chat;
        Friends.Visible = p == Pane.Friends;
        Discover.Visible = p == Pane.Discover;
        Voice.Visible = p == Pane.Voice;
        Forum.Visible = p == Pane.Forum;

        Control? front = p switch
        {
            Pane.Friends => Friends,
            Pane.Voice => Voice,
            Pane.Forum => Forum,
            Pane.Discover => Discover,
            _ => null,
        };
        if (front != null) { front.Bounds = Chat.Bounds; front.BringToFront(); }
        if (p == Pane.Friends) Friends.Reload();
    }

    // ── connection state ────────────────────────────────────────────────────────────────────────
    readonly ConnBar _conn = new() { Dock = DockStyle.Top, Visible = false };
    bool _online = true;
    string _connMsg = "";

    /// Show or clear the "Connecting…" strip across the top of the chat pane. Discord puts a
    /// coloured bar there rather than failing silently, which is what this client used to do.
    public void SetConnected(bool online, string message = "")
    {
        if (_online == online && _connMsg == message) return;
        _online = online;
        _connMsg = message;
        _conn.Message = message.Length > 0 ? message : "Connecting…";
        // Docked, so showing it pushes the regions down rather than covering them.
        _conn.Visible = !online;
        _conn.Invalidate();
    }

    public bool Online => _online;

    // ── window bounds ───────────────────────────────────────────────────────────────────────────
    /// Put the window back where it was. Validated against the *current* monitor layout, so a
    /// window last closed on a monitor that is no longer attached still opens somewhere visible.
    void RestoreBounds_()
    {
        var p = Prefs.Current;
        if (p.WindowW <= 0 || p.WindowH <= 0) return;
        var want = new Rectangle(p.WindowX, p.WindowY, p.WindowW, p.WindowH);
        if (p.WindowX == int.MinValue || !Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(want)))
        {
            ClientSize = new Size(p.WindowW, p.WindowH);
            return;
        }
        StartPosition = FormStartPosition.Manual;
        Bounds = want;
        if (p.WindowMaximized) WindowState = FormWindowState.Maximized;
    }

    void SaveBounds()
    {
        var p = Prefs.Current;
        p.WindowMaximized = WindowState == FormWindowState.Maximized;
        // RestoreBounds is the *normal* rectangle even while maximised, which is what should be
        // remembered — restoring a maximised window to its maximised size makes un-maximising
        // do nothing.
        var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        if (b.Width > 0 && b.Height > 0)
        {
            p.WindowX = b.X; p.WindowY = b.Y; p.WindowW = b.Width; p.WindowH = b.Height;
        }
        Prefs.Save();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SaveBounds();
        // Closing hides to the tray, like the real client — the app keeps its gateway session so
        // notifications and calls still arrive. Quit is on the tray menu.
        if (e.CloseReason == CloseReason.UserClosing && Prefs.Current.MinimizeToTray && !_reallyQuit)
        {
            e.Cancel = true;
            Hide();
            _tray.Visible = true;
            return;
        }
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosing(e);
    }

    // ── tray ────────────────────────────────────────────────────────────────────────────────────
    bool _reallyQuit;
    readonly NotifyIcon _tray = new();

    void SetUpTray()
    {
        _tray.Text = "ClaudeScord";
        try { _tray.Icon = Icon; } catch { }
        var menu = Menu.New();
        menu.Items.Add(Menu.Item("Open ClaudeScord", RestoreFromTray));
        menu.Items.Add(Menu.Sep());
        menu.Items.Add(Menu.Item("Quit", () => { _reallyQuit = true; Close(); }, danger: true));
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => RestoreFromTray();
        // Kept visible only while hidden, so there is not both a taskbar button and a tray icon.
        _tray.Visible = false;
    }

    void RestoreFromTray()
    {
        Show();
        WindowState = Prefs.Current.WindowMaximized ? FormWindowState.Maximized : FormWindowState.Normal;
        Activate();
        _tray.Visible = false;
    }

    /// Bring the window back for a toast click even when it is sitting in the tray.
    public void SurfaceWindow()
    {
        if (!Visible) RestoreFromTray();
        else { if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal; Activate(); }
    }

    /// Swap the chat pane for the Friends page, or back. The member list stays hidden either way
    /// while in home mode, so only the chat/friends pair is toggled here.
    public void ShowFriends(bool on) { if (on) ShowPane(Pane.Friends); else if (_pane == Pane.Friends) ShowPane(Pane.Chat); }
    /// Discovery is a full-width page in the live client, so the member column comes down with it.
    /// The session puts the column back the next time a channel is opened.
    public void ShowDiscover()
    {
        Members.Visible = false;
        PerformLayout();              // so Chat.Bounds is the full width before Discover takes it
        ShowPane(Pane.Discover);
        Discover.Load();
    }

    public void ShowMembers(bool on) => Members.Visible = on;

    /// Swap the chat pane for the voice stage, or back.
    public void ShowVoice(bool on) { if (on) ShowPane(Pane.Voice); else if (_pane == Pane.Voice) ShowPane(Pane.Chat); }

    /// Swap the chat pane for a forum channel's post list, or back.
    public void ShowForum(bool on) { if (on) ShowPane(Pane.Forum); else if (_pane == Pane.Forum) ShowPane(Pane.Chat); }

    /// Name the window: the guild on screen, or "Direct Messages" at home. The bar shows it centred
    /// with the guild's icon, the way the refresh does.
    public void SetContext(string name, string? iconUrl, bool home = false) =>
        _title.SetContext(name, iconUrl, home);

    /// Raised by the title bar's Inbox button.
    public event Action? InboxRequested { add => _title.InboxRequested += value; remove => _title.InboxRequested -= value; }

    // Discord's global shortcuts, raised from the shell because it owns KeyPreview.
    // Only the ones that must be intercepted here (they'd otherwise type into a focused control)
    // are handled by the shell; the rest ride the same event the header buttons fire.
    public event Action? QuickSwitcherShortcut;    // Ctrl+K
    public event Action? SettingsShortcut;         // Ctrl+,
    public event Action? SearchShortcut;           // Ctrl+F (same as Discord's in-client search)
    public event Action? SearchAllShortcut;        // Ctrl+Shift+F — search the whole server
    public event Action? EmojiShortcut;            // Ctrl+E
    public event Action? GifShortcut;              // Ctrl+G
    public event Action? MembersShortcut;          // Ctrl+U
    public event Action? PinsShortcut;             // Ctrl+P
    public event Action? JoinServerShortcut;       // Ctrl+Shift+N
    public event Action? MarkReadShortcut;         // Esc on an idle composer marks the channel read
    public event Action? MarkServerReadShortcut;   // Shift+Esc
    public event Action? MuteShortcut;             // Ctrl+Shift+M
    public event Action? DeafenShortcut;           // Ctrl+Shift+D
    public event Action? SlashShortcut;            // / focuses the composer and opens slash commands
    public event Action<int>? NavChannel;          // Alt+Up / Alt+Down: -1 previous, +1 next
    public event Action<int>? NavGuild;            // Ctrl+Alt+Up / Down
    public event Action<int>? GuildByIndex;        // Ctrl+1..9

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Copy a message-list selection. Handled here rather than on the list so the composer keeps
        // focus while you drag-select, exactly like the real client. The composer's own selection
        // wins when it has one — Ctrl+C must never steal a copy out of the box you are typing in.
        if (e.Control && e.KeyCode == Keys.C && Chat.Visible
            && Chat.HasSelection && !Chat.Composer.HasInputSelection)
        {
            try { Clipboard.SetText(Chat.SelectedText); } catch { }
            e.Handled = true;
            return;
        }
        // PageUp/PageDown always belong to the message list — a single-line caret has no use for
        // them. Home/End only when the composer is empty, since there they move the caret.
        if (Chat.Visible && e.KeyCode is Keys.PageUp or Keys.PageDown
            or Keys.Home or Keys.End)
        {
            bool caretKey = e.KeyCode is Keys.Home or Keys.End;
            if ((!caretKey || Chat.Composer.Text.Length == 0) && Chat.ScrollKey(e.KeyCode))
            {
                e.Handled = true;
                return;
            }
        }
        if (e.Control && e.Shift && e.KeyCode == Keys.M) { MuteShortcut?.Invoke(); e.Handled = true; }
        else if (e.Control && e.Shift && e.KeyCode == Keys.D) { DeafenShortcut?.Invoke(); e.Handled = true; }
        else if (e.Control && e.Shift && e.KeyCode == Keys.F) { SearchAllShortcut?.Invoke(); e.Handled = true; }
        else if (e.Control && e.Shift && e.KeyCode == Keys.N) { JoinServerShortcut?.Invoke(); e.Handled = true; }
        // Navigation, all of it missing before: Alt+↑/↓ walks channels, Ctrl+Alt+↑/↓ walks servers,
        // Ctrl+1..9 jumps to the Nth server. Ctrl+Alt is tested first — plain Alt would swallow it.
        else if (e.Control && e.Alt && e.KeyCode is Keys.Up or Keys.Down)
        { NavGuild?.Invoke(e.KeyCode == Keys.Down ? 1 : -1); e.Handled = e.SuppressKeyPress = true; }
        else if (e.Alt && e.KeyCode is Keys.Up or Keys.Down)
        { NavChannel?.Invoke(e.KeyCode == Keys.Down ? 1 : -1); e.Handled = e.SuppressKeyPress = true; }
        else if (e.Control && e.KeyCode is >= Keys.D1 and <= Keys.D9)
        { GuildByIndex?.Invoke(e.KeyCode - Keys.D1); e.Handled = true; }
        else if (e.Control && e.KeyCode == Keys.K) { QuickSwitcherShortcut?.Invoke(); e.Handled = true; }
        else if (e.Control && e.KeyCode == Keys.Oemcomma) { SettingsShortcut?.Invoke(); e.Handled = true; }
        else if (e.Control && e.KeyCode == Keys.F) { SearchShortcut?.Invoke(); e.Handled = true; }
        else if (e.Control && e.KeyCode == Keys.E) { EmojiShortcut?.Invoke(); e.Handled = true; }
        else if (e.Control && e.KeyCode == Keys.G) { GifShortcut?.Invoke(); e.Handled = true; }
        else if (e.Control && e.KeyCode == Keys.U) { MembersShortcut?.Invoke(); e.Handled = true; }
        else if (e.Control && e.KeyCode == Keys.P) { PinsShortcut?.Invoke(); e.Handled = true; }
        else if (e.Shift && e.KeyCode == Keys.Escape) { MarkServerReadShortcut?.Invoke(); e.Handled = true; }
        // Esc drops a selection before it means "mark read" — the nearer thing to dismiss wins.
        else if (e.KeyCode == Keys.Escape && Chat.Visible && Chat.HasSelection) { Chat.ClearSelection(); e.Handled = true; }
        else if (e.KeyCode == Keys.Escape && !Chat.Composer.IsBusy) { MarkReadShortcut?.Invoke(); e.Handled = true; }
        else if (e.KeyCode == Keys.OemQuestion && !e.Shift && !Chat.Composer.InputFocused)
        {
            // Discord: / focuses the message box and opens slash autocomplete — but only when you
            // are not already typing somewhere. A bare / while the composer is focused types it.
            SlashShortcut?.Invoke(); e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    // ── Native chrome ────────────────────────────────────────────────────────────────────────────
    // FormBorderStyle.None removes the resize grips along with the border, so hit-testing has to be
    // supplied by hand. Everything below is that.
    const int HTCLIENT = 1, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
              HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    const int WM_NCHITTEST = 0x0084, WM_GETMINMAXINFO = 0x0024;
    const int GWL_STYLE = -16, GWL_EXSTYLE = -20;

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool AdjustWindowRectEx(ref RECT r, int style, bool menu, int exStyle);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);

    protected override void WndProc(ref Message m)
    {
        // A borderless window maximises to the whole *monitor*, not the work area, so it covers the
        // taskbar. Windows only asks once, through WM_GETMINMAXINFO, and it wants the values in
        // coordinates relative to the monitor's own origin — on a secondary monitor, or one whose
        // taskbar is not at the bottom, using the desktop origin puts the window in the wrong place.
        //
        // WM_GETMINMAXINFO is about the *window* rect, and WS_THICKFRAME (below, for the shadow and
        // snap layouts) means the client sits a resize border inside that — 10px at 144 DPI, all
        // four edges. Handing it the work area verbatim therefore stopped the app 10px short of the
        // taskbar, which is the gap you could see along the bottom of a maximised window; the sides
        // and top had it too, against the screen edge where it reads as a dark seam rather than a
        // gap. AdjustWindowRectEx grows the box by exactly that border, for this window's real
        // style, so the *client* lands on the work area.
        if (m.Msg == WM_GETMINMAXINFO)
        {
            var screen = Screen.FromHandle(m.HWnd);
            var wa = screen.WorkingArea;
            var mb = screen.Bounds;
            var frame = new RECT { Right = wa.Width, Bottom = wa.Height };
            AdjustWindowRectEx(ref frame, GetWindowLong(m.HWnd, GWL_STYLE), false, GetWindowLong(m.HWnd, GWL_EXSTYLE));
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(m.LParam);
            mmi.ptMaxPosition = new Point(wa.Left - mb.Left + frame.Left, wa.Top - mb.Top + frame.Top);
            mmi.ptMaxSize = new Point(frame.Right - frame.Left, frame.Bottom - frame.Top);
            mmi.ptMinTrackSize = new Point(MinimumSize.Width, MinimumSize.Height);
            // The default max track size is the monitor plus a smaller allowance than this, and
            // Windows clamps ptMaxSize to it — which would put a few pixels of the gap back.
            mmi.ptMaxTrackSize = new Point(Math.Max(mmi.ptMaxTrackSize.X, mmi.ptMaxSize.X),
                                           Math.Max(mmi.ptMaxTrackSize.Y, mmi.ptMaxSize.Y));
            Marshal.StructureToPtr(mmi, m.LParam, false);
            m.Result = IntPtr.Zero;
            return;
        }

        base.WndProc(ref m);
        if (m.Msg != WM_NCHITTEST || (int)m.Result != HTCLIENT) return;

        int grip = Ui.S(6);
        var p = PointToClient(new Point(m.LParam.ToInt32() & 0xFFFF, m.LParam.ToInt32() >> 16));
        bool l = p.X <= grip, r = p.X >= ClientSize.Width - grip;
        bool t = p.Y <= grip, b = p.Y >= ClientSize.Height - grip;

        m.Result = (t && l) ? HTTOPLEFT : (t && r) ? HTTOPRIGHT
                 : (b && l) ? HTBOTTOMLEFT : (b && r) ? HTBOTTOMRIGHT
                 : l ? HTLEFT : r ? HTRIGHT : t ? HTTOP : b ? HTBOTTOM
                 : HTCLIENT;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // WS_THICKFRAME buys the drop shadow and snap layouts, but Windows 11 also paints a 1px
        // system border, near-white by default; match it to the rail.
        Native.FrameColor(Handle, Theme.Rail);
    }

    // A borderless window still gets the OS drop shadow and the snap-layouts affordance if it keeps
    // a thick frame style; without this the window looks pasted onto the desktop.
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= 0x00040000;   // WS_THICKFRAME - resizable, and restores the shadow
            cp.Style |= 0x00020000;   // WS_MINIMIZEBOX - taskbar click minimises
            return cp;
        }
    }
}

// Title bar: drag to move, double-click to maximise, and the three window buttons. Painted rather
// than composed from Buttons so the hover colours match the rest of the client exactly.
sealed class TitleBar : Control
{
    readonly Form _owner;
    int _hot = -1;      // hovered window button (0..2), -1 for none
    int _hotIcon = -1;  // hovered tray icon (0 = inbox, 1 = help)
    string _name = "";
    string? _iconUrl;
    bool _home;

    public event Action? InboxRequested;

    /// What the bar names: the guild on screen, or "Direct Messages" at home. Home is passed rather
    /// than inferred from a null icon — a guild with no icon is also iconless, and it must still get
    /// its initials tile instead of the Discord mark.
    public void SetContext(string name, string? iconUrl, bool home = false)
    {
        if (_name == name && _iconUrl == iconUrl && _home == home) return;
        _name = name;
        _iconUrl = iconUrl;
        _home = home;
        Invalidate();
    }

    // A dialog's bar carries Close and nothing else. Inbox and Help belong to the app window, not
    // to a modal opened on top of it, and minimise/maximise on a fixed-size dialog either do
    // nothing useful or strand it behind the window it was opened from.
    readonly bool _dialog;

    public TitleBar(Form owner, bool dialog = false)
    {
        _owner = owner;
        _dialog = dialog;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Rail;
    }

    int BtnW => Ui.S(46);
    int Btns => _dialog ? 1 : 3;
    Rectangle BtnRect(int i) => new(Width - BtnW * (Btns - i), 0, BtnW, Height);
    /// Which of minimise(0)/maximise(1)/close(2) the i-th drawn button is — the dialog's one
    /// button is the last of the three.
    int Which(int i) => i + 3 - Btns;

    // Inbox then Help, right-to-left. In the browser these sit 12 from the window edge; in a desktop
    // window the caption buttons are outboard of them, so they start inboard of those instead.
    Rectangle IconRect(int i)
    {
        if (_dialog) return Rectangle.Empty;
        int d = Ui.S(M.TitleIcon), pitch = Ui.S(M.TitleIconPitch);
        int right = Width - BtnW * 3 - Ui.S(M.HeaderPadRight);
        return new Rectangle(right - d - (1 - i) * pitch, (Height - d) / 2, d, d);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int was = _hot, wasIcon = _hotIcon;
        _hot = -1;
        _hotIcon = -1;
        for (int i = 0; i < Btns; i++) if (BtnRect(i).Contains(e.Location)) { _hot = i; break; }
        if (_hot < 0 && !_dialog)
            for (int i = 0; i < 2; i++) if (IconRect(i).Contains(e.Location)) { _hotIcon = i; break; }
        if (_hot != was || _hotIcon != wasIcon)
        {
            Tip.Show(this, _hotIcon >= 0 ? (_hotIcon == 0 ? "Inbox" : "Help") : null,
                     _hotIcon >= 0 ? IconRect(_hotIcon) : Rectangle.Empty);
            Invalidate();
        }
        Cursor = _hotIcon >= 0 ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hot != -1 || _hotIcon != -1) { _hot = -1; _hotIcon = -1; Tip.Hide(); Invalidate(); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        switch (_hot < 0 ? -1 : Which(_hot))
        {
            case 0: _owner.WindowState = FormWindowState.Minimized; return;
            case 1: ToggleMax(); return;
            case 2: _owner.Close(); return;
        }
        if (_hotIcon == 0) { InboxRequested?.Invoke(); return; }
        if (_hotIcon == 1) { Ui.OpenUrl("https://support.discord.com"); return; }

        // Anywhere else on the bar drags the window. Releasing capture first and forwarding a
        // caption click is what lets Windows' own snap/aero-drag take over, rather than
        // reimplementing drag with mouse deltas.
        Native.ReleaseCapture();
        Native.SendMessage(_owner.Handle, 0x00A1 /*WM_NCLBUTTONDOWN*/, 2 /*HTCAPTION*/, 0);
    }

    // Only the empty caption area toggles; double-clicking Inbox must not maximise the window.
    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        if (_hot < 0 && _hotIcon < 0 && !_dialog) ToggleMax();
    }

    void ToggleMax() => _owner.WindowState =
        _owner.WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.Fill(g, ClientRectangle, Theme.Rail);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        PaintContext(g);

        int d = Ui.S(M.HeaderIcon);
        if (!_dialog)
            for (int i = 0; i < 2; i++)
            {
                var ib = IconRect(i);
                Icons.Draw(g, i == 0 ? Icons.InboxLine : Icons.HelpLine,
                           new Rectangle(ib.X + (ib.Width - d) / 2, ib.Y + (ib.Height - d) / 2, d, d),
                           _hotIcon == i ? Theme.Text : Theme.Muted);
            }

        // Segoe MDL2 / Fluent glyphs: chrome minimise, maximise, restore, close.
        bool max = _owner.WindowState == FormWindowState.Maximized;
        string[] glyph = { "", max ? "" : "", "" };
        for (int i = 0; i < Btns; i++)
        {
            var r = BtnRect(i);
            if (_hot == i) Ui.Fill(g, r, Which(i) == 2 ? Theme.Danger : Theme.SidebarHover);
            Ui.Text(g, glyph[Which(i)], Theme.IconSmall, r, _hot == i ? Color.White : Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    // Guild tile + name, centred on the whole bar the way the live client does. The limit is
    // computed symmetrically so the text stays centred as it shrinks rather than drifting left:
    // whatever the chrome takes on the right is reserved on the left too.
    void PaintContext(Graphics g)
    {
        if (_name.Length == 0) return;
        // Home carries the Clyde mark at 18; a guild carries its own 16px tile. The label beside
        // either is 14px/500 — it was 12px medium, which at this weight read as bold-and-small.
        int tile = Ui.S(_home ? M.TitleLogo : M.TitleGuildIcon), gap = Ui.S(8);
        var sz = Ui.Measure(_name, Theme.Category);

        int chrome = BtnW * Btns + (_dialog ? 0 : Ui.S(M.HeaderPadRight) + Ui.S(M.TitleIconPitch) * 2);
        int limit = Width - chrome * 2 - tile - gap;
        int nameW = Math.Min(sz.Width, limit);
        if (nameW <= 0) return;

        int x = (Width - (tile + gap + nameW)) / 2;
        var box = new Rectangle(x, (Height - tile) / 2, tile, tile);

        var icon = _iconUrl == null ? null : Media.Get(_iconUrl, this);
        if (_home)
            Svg.SvgFill(g, Icons.Clyde, new RectangleF(box.X, box.Y, box.Width, box.Height), Theme.Text);
        else if (icon != null) Ui.Avatar(g, icon, box, Theme.Surface);
        else
        {
            Ui.FillRound(g, box, tile / 2, Theme.Surface);
            Ui.Text(g, GuildRail.Initials(_name), Theme.IconSmall, box, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        Ui.Text(g, _name, Theme.Category, new Rectangle(x + tile + gap, 0, nameW, Height),
                Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
struct MINMAXINFO
{
    public Point ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
}

static class Native
{
    [DllImport("user32.dll")] public static extern bool ReleaseCapture();
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);

    // Paint the window's 1px system border and caption strip to match the app, instead of the
    // near-white Windows 11 default that reads as a bright seam against a dark client. COLORREF is
    // 0x00BBGGRR, not RGB.
    public static void FrameColor(IntPtr handle, Color c)
    {
        int v = c.B << 16 | c.G << 8 | c.R;
        DwmSetWindowAttribute(handle, 34 /*BORDER_COLOR*/, ref v, sizeof(int));
        DwmSetWindowAttribute(handle, 35 /*CAPTION_COLOR*/, ref v, sizeof(int));
    }
}

// The "Connecting…" strip. A docked control rather than something painted over the shell: the four
// regions fill the client area, so anything drawn by the form itself would sit behind them.
sealed class ConnBar : Control
{
    public string Message = "Connecting…";

    public ConnBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = Ui.S(24);
        BackColor = Theme.Danger;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.Fill(g, ClientRectangle, Theme.Danger);
        Ui.Text(g, Message, Theme.SmallMedium, ClientRectangle, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}
