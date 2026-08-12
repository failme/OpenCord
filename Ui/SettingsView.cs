using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ClaudeScord;

// User Settings, Discord-style: a left nav of sections and a content pane, opened over the shell.
//
// Sections are kept to what actually works against the user gateway and REST endpoints: My Account
// (avatar, name, bio, status, custom status), User Profile (bio + pronouns), Notifications (real
// toggles persisted in Prefs and honoured by the session's toast logic), and Appearance (the two
// fixed choices this client ships). Every edit path is the same one the web client uses.
sealed class SettingsView : Form
{
    readonly UserClient _client;
    readonly NavList _nav = new();
    readonly Panel _content = new() { BackColor = Theme.Chat, Dock = DockStyle.Fill };
    readonly TitleBar _title;
    SectionPage? _page;

    static readonly string[] Sections =
    {
        "My Account", "User Profile", "Notifications", "Voice & Video", "Appearance",
    };

    SettingsView(UserClient client)
    {
        _client = client;
        Text = "User Settings";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(Ui.S(880), Ui.S(640));
        DoubleBuffered = true;
        KeyPreview = true;
        BackColor = Theme.Chat;

        _title = new TitleBar(this, dialog: true) { Dock = DockStyle.Top, Height = Ui.S(M.TitleBar) };
        _nav.Dock = DockStyle.Left;
        _nav.Width = Ui.S(218);
        _nav.SetSections(Sections, "User Settings");
        _nav.Selected += i => ShowSection(i);
        _nav.LogOut += LogOut;

        Controls.Add(_content);
        Controls.Add(_nav);
        Controls.Add(_title);
        ShowSection(0);
    }

    void LogOut()
    {
        try { _client.DisconnectAsync().GetAwaiter().GetResult(); } catch { }
        Prefs.ClearToken();
        Application.Restart();
    }

    public static void Show(Shell shell, UserClient client)
    {
        // Settings is a window sitting on top of the app painted in the app's own colours, so
        // without something between them the two surfaces run together and there is no edge to
        // read. Dimming what is behind it is what the real client's overlay does, and it is the
        // difference between "a dialog" and "the sidebar changed".
        using var scrim = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Bounds = shell.Bounds,
            BackColor = Color.Black,
            Opacity = 0.6,
            ShowInTaskbar = false,
        };
        scrim.Show(shell);
        using var v = new SettingsView(client);
        v.ShowDialog(scrim);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // While rebinding, the next key pressed becomes the push-to-talk key — including Escape,
        // which is why this runs before the close handler.
        if (_bindingPtt)
        {
            _bindingPtt = false;
            Prefs.Current.PttKey = (int)e.KeyCode;
            Prefs.Save();
            Apply();
            e.Handled = e.SuppressKeyPress = true;
            ShowSection(3);
            return;
        }
        if (e.KeyCode == Keys.Escape) Close();
        base.OnKeyDown(e);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.FrameColor(Handle, Theme.Rail);
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.Style |= 0x00020000 /*WS_MINIMIZEBOX*/; return cp; }
    }

    void ShowSection(int i)
    {
        _nav.Select(i);
        if (_page != null) { _content.Controls.Remove(_page); _page.Dispose(); }
        _page = new SectionPage { Dock = DockStyle.Fill };
        _content.Controls.Add(_page);
        switch (i)
        {
            case 0: BuildAccount(_page); break;
            case 1: BuildProfile(_page); break;
            case 2: BuildNotifications(_page); break;
            case 3: BuildVoice(_page); break;
            default: BuildAppearance(_page); break;
        }
        _page.Done();
    }

    // ── section builders ────────────────────────────────────────────────────────────────────────

    void Header(SectionPage p, string title, string sub)
    {
        p.Y += Ui.S(24);
        p.Text(title, Theme.H1, new Rectangle(Ui.S(28), p.Y, p.Width - Ui.S(56), Ui.S(34)), Theme.Strong);
        p.Y += Ui.S(34);
        p.Text(sub, Theme.Body, new Rectangle(Ui.S(28), p.Y, p.Width - Ui.S(56), Ui.S(22)), Theme.Muted);
        p.Y += Ui.S(30);
    }

    void BuildAccount(SectionPage p)
    {
        Header(p, "My Account", "Manage your account information.");
        var me = _client.CurrentUser;

        // ── avatar ──
        int av = Ui.S(92);
        var ab = new Rectangle(Ui.S(28), p.Y, av, av);
        p.Avatar(me?.GetDisplayAvatarUrl(160), ab);
        int bx = ab.Right + Ui.S(20);
        // 44 not 36: a button *is* 36 tall, so stacking at the button height leaves the two flush
        // against each other with no seam.
        p.Button(bx, p.Y, "Change Avatar", async () => await PickAvatar(p, false), Theme.Blurple);
        p.Button(bx, p.Y + Ui.S(44), "Remove Avatar", async () => await PickAvatar(p, true), Theme.Field);
        p.Y += av + Ui.S(26);

        // ── name fields ──
        var userField = p.Field("USERNAME", me?.Username ?? "", p.Y);
        p.Y += Ui.S(66);
        var nameField = p.Field("DISPLAY NAME", me?.DisplayName ?? "", p.Y);
        p.Y += Ui.S(66);
        p.Button(Ui.S(28), p.Y, "Save Changes", async () =>
        {
            var (ok, err) = await _client.Rest.UpdateSelfAsync(
                userField.Box.Text.Trim(), nameField.Box.Text.Trim());
            if (!ok) p.Flash(err ?? "Couldn't save.", Theme.Danger);
            else p.Flash("Saved!", Theme.Positive);
        }, Theme.Blurple);
        p.Y += Ui.S(64);

        // ── status ──
        p.Text("STATUS", Theme.SmallMedium, new Rectangle(Ui.S(28), p.Y, p.Width, Ui.S(16)), Theme.Muted);
        p.Y += Ui.S(20);
        foreach (var (label, status, pres) in new[]
        {
            ("Online", "online", Presence.Online), ("Idle", "idle", Presence.Idle),
            ("Do Not Disturb", "dnd", Presence.Dnd), ("Invisible", "invisible", Presence.Offline),
        })
        {
            bool cur = (me?.Status ?? "online") == status;
            var row = new Rectangle(Ui.S(28), p.Y, Ui.S(240), Ui.S(30));
            p.Hot(row, cur ? 1 : 0, () => _ = _client.SetPresenceAsync(status));
            if (cur) p.FillRound(row, Ui.S(6), Theme.SidebarSelected);
            var dot = new Rectangle(row.X + Ui.S(8), row.Y + Ui.S(7), Ui.S(16), Ui.S(16));
            p.Dot(dot, Theme.Dot(pres));
            p.Text(label, Theme.Body, new Rectangle(dot.Right + Ui.S(12), row.Y, Ui.S(180), row.Height),
                    cur ? Theme.Strong : Theme.Muted, TextFormatFlags.VerticalCenter);
            p.Y += Ui.S(34);
        }
        p.Y += Ui.S(10);

        // ── custom status ──
        var statusField = p.Field("CUSTOM STATUS", me?.CustomStatus ?? "", p.Y);
        p.Y += Ui.S(66);
        p.Button(Ui.S(28), p.Y, "Set Custom Status", async () =>
        {
            var ok = await _client.Rest.SetCustomStatusAsync(statusField.Box.Text);
            if (ok && _client.CurrentUser != null)
            {
                _client.CurrentUser.CustomStatus =
                    string.IsNullOrWhiteSpace(statusField.Box.Text) ? null : statusField.Box.Text.Trim();
                _client.NotifySelfChanged();
                p.Flash("Status set!", Theme.Positive);
            }
            else p.Flash("Couldn't set status.", Theme.Danger);
        }, Theme.Blurple);
        p.Y += Ui.S(76);

        p.Button(Ui.S(28), p.Y, "Log Out", LogOut, Theme.Danger);
    }

    async Task PickAvatar(SectionPage p, bool remove)
    {
        var me = _client.CurrentUser;
        if (me == null) return;
        if (remove)
        {
            var (ok, err) = await _client.Rest.UpdateSelfAsync(me.Username, me.DisplayName, null);
            if (ok) { me.Avatar = null; _client.NotifySelfChanged(); p.Flash("Avatar removed.", Theme.Positive); }
            else p.Flash(err ?? "Couldn't remove avatar.", Theme.Danger);
            return;
        }
        using var dlg = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.gif;*.webp" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        string uri;
        try
        {
            using var img = Image.FromFile(dlg.FileName);
            int side = Math.Min(img.Width, img.Height);
            using var crop = new Bitmap(side, side);
            using (var g = Graphics.FromImage(crop))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(img, new Rectangle(0, 0, side, side),
                    new Rectangle((img.Width - side) / 2, (img.Height - side) / 2, side, side),
                    GraphicsUnit.Pixel);
            }
            using var small = new Bitmap(128, 128);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(crop, new Rectangle(0, 0, 128, 128));
            }
            using var ms = new MemoryStream();
            small.Save(ms, ImageFormat.Png);
            uri = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
        }
        catch (Exception ex) { p.Flash("Couldn't read that image: " + ex.Message, Theme.Danger); return; }

        var (ok2, err2) = await _client.Rest.UpdateSelfAsync(me.Username, me.DisplayName, uri);
        if (ok2) { p.Flash("Avatar updated!", Theme.Positive); _client.NotifySelfChanged(); }
        else p.Flash(err2 ?? "Couldn't update avatar.", Theme.Danger);
    }

    void BuildProfile(SectionPage p)
    {
        Header(p, "User Profile", "Your bio and pronouns, shown on your profile.");
        var bio = p.Field("ABOUT ME", _client.CurrentUser?.Bio ?? "", p.Y, 3);
        p.Y += Ui.S(66) * 2;
        var pronouns = p.Field("PRONOUNS", "", p.Y);
        p.Y += Ui.S(66);
        p.Button(Ui.S(28), p.Y, "Save Profile", async () =>
        {
            var err = await _client.Rest.SetProfileAsync(bio.Box.Text, pronouns.Box.Text);
            if (err == null)
            {
                if (_client.CurrentUser != null) _client.CurrentUser.Bio = bio.Box.Text.Trim();
                p.Flash("Profile saved!", Theme.Positive);
            }
            else p.Flash(err, Theme.Danger);
        }, Theme.Blurple);
    }

    void BuildNotifications(SectionPage p)
    {
        Header(p, "Notifications", "Choose what shows up as a notification.");
        p.Y += Ui.S(6);
        p.Toggle("Desktop notifications",
            "Show a notification when a message arrives while ClaudeScord isn't focused.",
            Prefs.Current.NotifyEnabled, on => { Prefs.Current.NotifyEnabled = on; Prefs.Save(); });
        p.Toggle("Only mentions and direct messages",
            "Don't notify for every message in a channel — only when you're mentioned.",
            Prefs.Current.NotifyMentionsOnly, on => { Prefs.Current.NotifyMentionsOnly = on; Prefs.Save(); });
        p.Text("Notifications only fire while this window doesn't have focus — the same rule the\n"
             + "desktop client uses.", Theme.Small,
            new Rectangle(Ui.S(28), p.Y + Ui.S(8), p.Width - Ui.S(56), Ui.S(36)), Theme.Faint);
    }

    // Device indices come from NAudio's legacy MME enumeration, which is also what VoiceAudio opens
    // with, so the numbers here mean the same thing there. -1 is "system default".
    static List<(int Index, string Name)> Inputs()
    {
        var list = new List<(int, string)> { (-1, "Default") };
        try
        {
            for (int i = 0; i < NAudio.Wave.WaveIn.DeviceCount; i++)
                list.Add((i, NAudio.Wave.WaveIn.GetCapabilities(i).ProductName));
        }
        catch { }
        return list;
    }

    static List<(int Index, string Name)> Outputs()
    {
        var list = new List<(int, string)> { (-1, "Default") };
        try
        {
            for (int i = 0; i < NAudio.Wave.WaveOut.DeviceCount; i++)
                list.Add((i, NAudio.Wave.WaveOut.GetCapabilities(i).ProductName));
        }
        catch { }
        return list;
    }

    void BuildVoice(SectionPage p)
    {
        Header(p, "Voice & Video", "Choose which devices this client records from and plays through.");
        DeviceList(p, "INPUT DEVICE", Inputs(), Prefs.Current.InputDevice,
                   i => { Prefs.Current.InputDevice = i; Prefs.Save(); ShowSection(4); });
        p.Y += Ui.S(16);
        DeviceList(p, "OUTPUT DEVICE", Outputs(), Prefs.Current.OutputDevice,
                   i => { Prefs.Current.OutputDevice = i; Prefs.Save(); ShowSection(3); });
        p.Y += Ui.S(20);

        // ── volumes ──
        p.SliderRow("Input Volume", "", Prefs.Current.InputVolume, 0f, 2f,
                    v => $"{(int)(v * 100)}%",
                    v => { Prefs.Current.InputVolume = v; Prefs.Save(); Apply(); });
        p.SliderRow("Output Volume", "", Prefs.Current.OutputVolume, 0f, 2f,
                    v => $"{(int)(v * 100)}%",
                    v => { Prefs.Current.OutputVolume = v; Prefs.Save(); Apply(); });
        p.Y += Ui.S(10);

        // ── input mode ──
        p.Text("INPUT MODE", Theme.SmallMedium,
               new Rectangle(Ui.S(28), p.Y, p.Width - Ui.S(56), Ui.S(16)), Theme.Muted);
        p.Y += Ui.S(22);
        foreach (var (label, mode) in new[] { ("Voice Activity", 0), ("Push to Talk", 1) })
        {
            bool cur = Prefs.Current.InputMode == mode;
            var row = new Rectangle(Ui.S(28), p.Y, Ui.S(240), Ui.S(30));
            p.Hot(row, cur ? 1 : 0, () => { Prefs.Current.InputMode = mode; Prefs.Save(); Apply(); ShowSection(3); });
            if (cur) p.FillRound(row, Ui.S(6), Theme.SidebarSelected);
            var dot = new Rectangle(row.X + Ui.S(8), row.Y + Ui.S(7), Ui.S(16), Ui.S(16));
            p.Dot(dot, cur ? Theme.Blurple : Theme.Border);
            p.Text(label, Theme.Body, new Rectangle(dot.Right + Ui.S(12), row.Y, Ui.S(200), row.Height),
                   cur ? Theme.Strong : Theme.Muted, TextFormatFlags.VerticalCenter);
            p.Y += Ui.S(34);
        }
        p.Y += Ui.S(8);

        if (Prefs.Current.InputMode == 1)
        {
            p.Text("SHORTCUT", Theme.SmallMedium,
                   new Rectangle(Ui.S(28), p.Y, p.Width - Ui.S(56), Ui.S(16)), Theme.Muted);
            p.Y += Ui.S(22);
            var keyBtn = new Rectangle(Ui.S(28), p.Y, Ui.S(200), Ui.S(32));
            bool binding = _bindingPtt;
            p.FillRound(keyBtn, Ui.S(6), binding ? Theme.Blurple : Theme.Field);
            p.Text(binding ? "Press any key…" : PushToTalk.KeyName(Prefs.Current.PttKey), Theme.BodyMedium,
                   keyBtn, Theme.Strong, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            p.Hot(keyBtn, 0, () => { _bindingPtt = true; ShowSection(3); });
            p.Y += Ui.S(44);
        }
        else
        {
            // The meter only moves during a call, so say so rather than leaving a dead bar.
            p.SliderRow("Input Sensitivity", VoiceClient.Current == null
                            ? "Automatic — the gate follows your room. Join a call to see the meter."
                            : "Automatic at the far left; drag right only if background noise gets through.",
                        Prefs.Current.Sensitivity, 0f, 0.03f,
                        v => v <= 0.0005f ? "Auto" : $"{(int)(v * 1000)}",
                        v => { Prefs.Current.Sensitivity = v; Prefs.Save(); Apply(); });
            _meter = new LevelMeter
            {
                Location = new Point(Ui.S(28), p.Y),
                Size = new Size(p.Width - Ui.S(230), Ui.S(14)),
            };
            p.Controls.Add(_meter);
            p.Y += Ui.S(30);
        }

        p.SliderRow("Noise Gate", "Squelches anything quieter than this. 0 turns it off.",
                    Prefs.Current.NoiseGate, 0f, 0.1f,
                    v => v <= 0.0005f ? "Off" : $"{(int)(v * 1000)}",
                    v => { Prefs.Current.NoiseGate = v; Prefs.Save(); Apply(); });

        p.Toggle("Voice Sounds", "Join, leave, mute and disconnect chimes.",
                 Prefs.Current.VoiceSounds,
                 v => { Prefs.Current.VoiceSounds = v; Prefs.Save(); });
        p.Toggle("Message Sounds", "The ping when a message arrives, and call ringtones.",
                 Prefs.Current.SoundsEnabled,
                 v => { Prefs.Current.SoundsEnabled = v; Prefs.Save(); if (!v) Sfx.StopLoop(); });

        p.Text("Devices are picked up the next time you join a voice channel; everything else\n"
             + "applies to the call you are already in.", Theme.Small,
            new Rectangle(Ui.S(28), p.Y + Ui.S(8), p.Width - Ui.S(56), Ui.S(40)), Theme.Faint);
    }

    LevelMeter? _meter;
    bool _bindingPtt;

    /// Push the sliders into the running call, so a change is audible immediately.
    static void Apply() => VoiceClient.Current?.ApplyVoicePrefs();

    void DeviceList(SectionPage p, string label, List<(int Index, string Name)> devices, int selected,
                    Action<int> pick)
    {
        p.Text(label, Theme.SmallMedium,
               new Rectangle(Ui.S(28), p.Y, p.Width - Ui.S(56), Ui.S(18)), Theme.Muted);
        p.Y += Ui.S(24);

        // A device that has since been unplugged leaves a saved index pointing at nothing; show the
        // default as active rather than no selection at all.
        bool known = devices.Any(d => d.Index == selected);
        foreach (var (index, name) in devices)
        {
            bool on = index == selected || (!known && index == -1);
            var row = new Rectangle(Ui.S(28), p.Y, p.Width - Ui.S(56), Ui.S(40));
            p.FillRound(row, Ui.S(8), on ? Theme.SidebarSelected : Theme.Field);
            int d = Ui.S(14);
            var dot = new Rectangle(row.X + Ui.S(14), row.Y + (row.Height - d) / 2, d, d);
            p.Dot(dot, on ? Theme.Blurple : Theme.Surface);
            if (on) p.Dot(Rectangle.Inflate(dot, -Ui.S(4), -Ui.S(4)), Color.White);
            p.Text(name, Theme.Body,
                   new Rectangle(dot.Right + Ui.S(12), row.Y, row.Width - Ui.S(60), row.Height),
                   on ? Theme.Strong : Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            int captured = index;
            p.Hot(row, 0, () => pick(captured));
            p.Y += Ui.S(46);
        }
    }

    void BuildAppearance(SectionPage p)
    {
        Header(p, "Appearance", "How messages are laid out, and how big everything is drawn.");

        p.Text("MESSAGE DISPLAY", Theme.SmallMedium,
               new Rectangle(Ui.S(28), p.Y, p.Width - Ui.S(56), Ui.S(16)), Theme.Muted);
        p.Y += Ui.S(22);
        foreach (var (label, note, compact) in new[]
        {
            ("Cozy", "Avatars beside every group, roomy spacing.", false),
            ("Compact", "One line per message, sender inline.", true),
        })
        {
            bool cur = Prefs.Current.CompactMode == compact;
            var row = new Rectangle(Ui.S(28), p.Y, p.Width - Ui.S(56), Ui.S(46));
            p.Hot(row, cur ? 1 : 0, () =>
            {
                Prefs.Current.CompactMode = compact;
                Prefs.Save();
                App.Relayout?.Invoke();
                ShowSection(4);
            });
            if (cur) p.FillRound(row, Ui.S(8), Theme.SidebarSelected);
            var dot = new Rectangle(row.X + Ui.S(12), row.Y + Ui.S(15), Ui.S(16), Ui.S(16));
            p.Dot(dot, cur ? Theme.Blurple : Theme.Border);
            p.Text(label, Theme.BodyMedium,
                   new Rectangle(dot.Right + Ui.S(12), row.Y + Ui.S(5), Ui.S(300), Ui.S(20)),
                   cur ? Theme.Strong : Theme.Text, TextFormatFlags.VerticalCenter);
            p.Text(note, Theme.Small,
                   new Rectangle(dot.Right + Ui.S(12), row.Y + Ui.S(24), row.Width - Ui.S(60), Ui.S(18)),
                   Theme.Faint);
            p.Y += Ui.S(52);
        }

        p.Y += Ui.S(10);
        p.SliderRow("Zoom", "Scales the whole interface on top of your system DPI. Restart to apply.",
                    Prefs.Current.Zoom, 0.8f, 1.4f,
                    v => $"{(int)Math.Round(v * 100)}%",
                    v => { Prefs.Current.Zoom = (float)Math.Round(v * 20) / 20f; Prefs.Save(); });

        p.Toggle("Close to Tray", "Keep running in the notification area when the window is closed.",
                 Prefs.Current.MinimizeToTray,
                 v => { Prefs.Current.MinimizeToTray = v; Prefs.Save(); });

        p.Text("Theme is dark only — the palette is measured off Discord's dark theme throughout,\n"
             + "and a light one would need every colour re-measured rather than inverted.",
            Theme.Small, new Rectangle(Ui.S(28), p.Y + Ui.S(8), p.Width - Ui.S(56), Ui.S(40)), Theme.Faint);
    }
}

// ── left nav ────────────────────────────────────────────────────────────────────────────────────

sealed class NavList : Control
{
    string[] _sections = Array.Empty<string>();
    string _header = "";
    int _sel = -1, _hot = -1;

    public event Action<int>? Selected;
    public event Action? LogOut;

    public NavList()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Rail;
    }

    public void SetSections(string[] sections, string header)
    {
        _sections = sections;
        _header = header;
        Invalidate();
    }

    public void Select(int i) { _sel = i; Invalidate(); }

    int RowTop(int i) => Ui.S(24) + Ui.S(40) + i * Ui.S(36);

    int RowAt(Point p)
    {
        for (int i = 0; i < _sections.Length; i++)
            if (p.Y >= RowTop(i) && p.Y < RowTop(i) + Ui.S(34)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = RowAt(e.Location);
        if (h != _hot) { _hot = h; Invalidate(); }
        Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hot != -1) { _hot = -1; Invalidate(); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        int h = RowAt(e.Location);
        if (h >= 0) Selected?.Invoke(h);
        else if (e.Y > Height - Ui.S(70)) LogOut?.Invoke();
        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.Fill(g, ClientRectangle, Theme.Rail);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Ui.Text(g, _header, Theme.H1, new Rectangle(Ui.S(16), Ui.S(20), Width - Ui.S(32), Ui.S(34)),
                Theme.Strong, TextFormatFlags.EndEllipsis);

        for (int i = 0; i < _sections.Length; i++)
        {
            bool sel = _sel == i, hot = _hot == i;
            var row = new Rectangle(Ui.S(8), RowTop(i), Width - Ui.S(16), Ui.S(34));
            if (sel) Ui.FillRound(g, row, Ui.S(6), Theme.SidebarSelected);
            else if (hot) Ui.FillRound(g, row, Ui.S(6), Theme.SidebarHover);
            Ui.Text(g, _sections[i], Theme.Body, new Rectangle(row.X + Ui.S(12), row.Y, row.Width - Ui.S(24), row.Height),
                    sel ? Theme.Strong : hot ? Theme.Text : Theme.Muted,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        // Log Out, pinned to the bottom like the real client's settings nav.
        var lo = new Rectangle(Ui.S(8), Height - Ui.S(64), Width - Ui.S(16), Ui.S(34));
        if (lo.Contains(PointToClient(Cursor.Position))) Ui.FillRound(g, lo, Ui.S(6), Theme.SidebarHover);
        Ui.Text(g, "Log Out", Theme.Body, new Rectangle(lo.X + Ui.S(12), lo.Y, lo.Width - Ui.S(24), lo.Height),
                Theme.Danger, TextFormatFlags.VerticalCenter);
    }
}

// ── content host ────────────────────────────────────────────────────────────────────────────────

// One settings section. Builders position child controls and record paint ops (Text/FillRound/…)
// into a list that OnPaint replays — painting outside OnPaint is not legal in WinForms. p.Y is the
// vertical cursor. Hot registers a clickable painted rect; Flash shows a transient status line.
sealed class SectionPage : Panel
{
    public int Y = Ui.S(16);
    readonly List<Action<Graphics>> _paint = new();
    readonly List<(Rectangle Box, Action Click)> _hots = new();
    // Drives the AutoScrollPosition with the same physics as every other list in the app (see
    // Scroller). AutoScroll still owns the child-control movement; the Scroller just aims it.
    readonly Scroller _scroll;
    string? _flash;
    Color _flashColor = Theme.Positive;
    System.Windows.Forms.Timer? _flashTimer;

    public SectionPage()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Chat;
        AutoScroll = true;
        _scroll = new Scroller(this, y => AutoScrollPosition = new Point(0, y));
    }

    protected override void OnPaintBackground(PaintEventArgs e) => Ui.Fill(e.Graphics, ClientRectangle, Theme.Chat);

    /// Called once the section has finished laying itself out. AutoScroll only measures child
    /// *controls*, and most of a page is painted rather than composed of them — so without telling
    /// it the real content height, anything below the fold is simply unreachable.
    public void Done()
    {
        AutoScrollMinSize = new Size(0, Y + Ui.S(24));
        Y = Ui.S(16);
    }

    // ── paint ops ──
    // Every op offsets its own rect by the scroll position at draw time rather than relying on a
    // Graphics transform: Ui.Text goes through GDI's TextRenderer, which ignores the transform
    // entirely — so a translated page moved its shapes and left every label behind.
    Rectangle Off(Rectangle r) => new(r.X, r.Y + AutoScrollPosition.Y, r.Width, r.Height);

    public void Text(string s, Font f, Rectangle r, Color c,
                     TextFormatFlags flags = TextFormatFlags.Default) =>
        _paint.Add(g => Ui.Text(g, s, f, Off(r), c, flags));
    public void FillRound(Rectangle r, int radius, Color c) =>
        _paint.Add(g => Ui.FillRound(g, Off(r), radius, c));
    public void Dot(Rectangle r, Color c) =>
        _paint.Add(g => { g.SmoothingMode = SmoothingMode.AntiAlias; using var b = new SolidBrush(c); g.FillEllipse(b, Off(r)); });
    /// Takes the *url*, not the Image, and resolves it inside the paint op.
    ///
    /// Media.Get returns null on a cold cache and invalidates the control once the download lands.
    /// A page built ahead of time would capture that first null in its closure and keep painting it
    /// forever, so the settings avatar stayed an empty circle no matter how many repaints followed.
    public void Avatar(string? url, Rectangle r) =>
        _paint.Add(g => Ui.Avatar(g, Media.Get(url, this), Off(r), Theme.Surface));

    // ── interactive children ──
    public void Hot(Rectangle box, int kind, Action click) => _hots.Add((box, click));

    public TextField Field(string label, string value, int y, int lines = 1)
    {
        Text(label, Theme.SmallMedium, new Rectangle(Ui.S(28), y, Width - Ui.S(56), Ui.S(16)), Theme.Muted);
        var f = new TextField
        {
            Location = new Point(Ui.S(28), y + Ui.S(20)),
            Width = Math.Min(Ui.S(460), Width - Ui.S(56)),
            Height = Ui.S(44) + (lines - 1) * Ui.S(24),
        };
        f.Box.Text = value;
        f.Box.Multiline = lines > 1;
        Controls.Add(f);
        return f;
    }

    public void Button(int x, int y, string text, Action click, Color color)
    {
        var b = new FlatButton { Text = text, BackColor = color, Location = new Point(x, y), Width = Ui.S(170), Height = Ui.S(36) };
        b.Click += (_, _) => click();
        Controls.Add(b);
    }

    public void Toggle(string title, string note, bool on, Action<bool> changed)
    {
        Text(title, Theme.BodyMedium, new Rectangle(Ui.S(28), Y, Width - Ui.S(120), Ui.S(22)),
             Theme.Text, TextFormatFlags.VerticalCenter);
        Text(note, Theme.Small, new Rectangle(Ui.S(28), Y + Ui.S(24), Width - Ui.S(120), Ui.S(18)), Theme.Faint);
        var t = new Toggle { On = on, Location = new Point(Width - Ui.S(120), Y + Ui.S(2)), Size = new Size(Ui.S(84), Ui.S(28)) };
        t.Changed += v => changed(v);
        Controls.Add(t);
        Y += Ui.S(56);
    }

    /// A labelled slider. Live-updates while dragging so a volume or a threshold can be heard
    /// being set, which is the whole point of the meter next to it.
    public void SliderRow(string title, string note, float value, float min, float max,
                          Func<float, string> format, Action<float> changed)
    {
        Text(title, Theme.BodyMedium, new Rectangle(Ui.S(28), Y, Width - Ui.S(200), Ui.S(22)),
             Theme.Text, TextFormatFlags.VerticalCenter);
        if (note.Length > 0)
            Text(note, Theme.Small, new Rectangle(Ui.S(28), Y + Ui.S(24), Width - Ui.S(200), Ui.S(18)), Theme.Faint);
        var s = new Slider
        {
            Min = min, Max = max, Value = value, Format = format,
            Location = new Point(Width - Ui.S(190), Y + Ui.S(2)),
            Size = new Size(Ui.S(160), Ui.S(30)),
        };
        s.Changed += v => changed(v);
        Controls.Add(s);
        Y += note.Length > 0 ? Ui.S(56) : Ui.S(40);
    }

    public void Flash(string text, Color color)
    {
        _flash = text;
        _flashColor = color;
        Invalidate();
        _flashTimer?.Dispose();
        _flashTimer = new System.Windows.Forms.Timer { Interval = 2600 };
        _flashTimer.Tick += (_, _) => { _flash = null; _flashTimer?.Dispose(); _flashTimer = null; Invalidate(); };
        _flashTimer.Start();
    }

    /// Painted hot zones live in content space; the pointer arrives in view space.
    Point ToContent(Point p) => new(p.X, p.Y - AutoScrollPosition.Y);

    // ── scrollbar ──
    // AutoScroll is what moves the child controls, and that is worth keeping — but its scrollbar is
    // a native Windows one: a wide light-grey slab with arrow buttons down the side of a dark page,
    // and the only thing in the client that does not look like Discord. Hiding it before Windows
    // works out the client area (WM_NCCALCSIZE is where that happens) keeps every bit of the
    // scrolling and drops the chrome; the thumb below is drawn the same way the message list's is.
    const int WM_NCCALCSIZE = 0x0083, SB_BOTH = 3;
    [DllImport("user32.dll")] static extern bool ShowScrollBar(IntPtr hWnd, int bar, bool show);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCCALCSIZE && IsHandleCreated) ShowScrollBar(Handle, SB_BOTH, false);
        base.WndProc(ref m);
    }

    int MaxScroll => Math.Max(0, AutoScrollMinSize.Height - ClientSize.Height);

    Rectangle ThumbBox
    {
        get
        {
            int w = Ui.S(8), track = ClientSize.Height, max = MaxScroll;
            int h = Math.Max(Ui.S(30), (int)(track * (track / (float)Math.Max(1, AutoScrollMinSize.Height))));
            int y = max <= 0 ? 0 : (int)((track - h) * (-AutoScrollPosition.Y / (float)max));
            return new Rectangle(ClientSize.Width - w - Ui.S(2), y, w, h);
        }
    }

    bool _sbDrag;
    int _sbGrab;

    void ScrollTo(int offset) => _scroll.JumpTo(offset, MaxScroll);

    // Driven explicitly rather than left to ScrollableControl: the app's global wheel router
    // re-sends WM_MOUSEWHEEL to whatever is under the pointer, and the panel's own handling did
    // not pick it up — which left the bottom of a long section unreachable.
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        int max = Math.Max(0, AutoScrollMinSize.Height - ClientSize.Height);
        if (max <= 0) return;
        _scroll.Wheel(e.Delta, max);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (MaxScroll > 0 && e.Button == MouseButtons.Left && e.X >= ClientSize.Width - Ui.S(14))
        {
            var t = ThumbBox;
            if (t.Contains(e.Location)) { _sbDrag = true; _sbGrab = e.Y - t.Y; Capture = true; }
            else ScrollTo(-AutoScrollPosition.Y + (e.Y < t.Y ? -ClientSize.Height : ClientSize.Height));
            return;
        }

        var c = ToContent(e.Location);
        for (int i = _hots.Count - 1; i >= 0; i--)
            if (_hots[i].Box.Contains(c) && e.Button == MouseButtons.Left)
            {
                _hots[i].Click();
                Invalidate();
                return;
            }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_sbDrag)
        {
            int track = ClientSize.Height, span = Math.Max(1, track - ThumbBox.Height);
            ScrollTo((int)((e.Y - _sbGrab) / (float)span * MaxScroll));
            return;
        }
        var c = ToContent(e.Location);
        Cursor = _hots.Any(h => h.Box.Contains(c)) ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_sbDrag) { _sbDrag = false; Capture = false; }
        base.OnMouseUp(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        foreach (var op in _paint) op(g);
        base.OnPaint(e);

        if (_flash != null)
            Ui.Text(g, _flash, Theme.SmallMedium,
                    new Rectangle(Ui.S(28), Height - Ui.S(44), Width - Ui.S(56), Ui.S(20)),
                    _flashColor, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

        if (MaxScroll > 0)
        {
            var t = ThumbBox;
            Ui.FillRound(g, t, t.Width / 2, Theme.ScrollThumb);
        }
    }
}

// A Discord-style labelled field: rounded dark well with a real TextBox inside.
sealed class TextField : Panel
{
    public readonly TextBox Box;

    public TextField()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Box = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Theme.InputBg,
            ForeColor = Theme.Text,
            Font = Theme.Body,
        };
        Controls.Add(Box);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        if (Box is not null)
            Box.SetBounds(Ui.S(12), (Height - Box.PreferredHeight) / 2,
                          Math.Max(1, Width - Ui.S(24)), Box.PreferredHeight);
        base.OnSizeChanged(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Ui.FillRound(g, new Rectangle(0, 0, Width - 1, Height - 1), Ui.S(4), Theme.InputBg);
    }
}

// A flat painted button for settings actions.
sealed class FlatButton : Control
{
    bool _hover, _down;

    public FlatButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        ForeColor = Color.White;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var c = _down ? ControlPaint.Dark(BackColor, 0.15f)
              : _hover ? ControlPaint.Light(BackColor, 0.12f)
              : BackColor;
        Ui.FillRound(g, new Rectangle(0, 0, Width - 1, Height - 1), Ui.S(4), c);
        Ui.Text(g, Text, Theme.BodyMedium, ClientRectangle, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

// Discord's switch: a 40x24 track with a sliding knob, animated on a 15ms glide.
sealed class Toggle : Control
{
    bool _on;
    float _knob;
    readonly System.Windows.Forms.Timer _glide = new() { Interval = 15 };

    public event Action<bool>? Changed;

    public bool On
    {
        get => _on;
        set { _on = value; _knob = value ? 1 : 0; Invalidate(); }
    }

    public Toggle()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        _glide.Tick += (_, _) =>
        {
            float d = (_on ? 1 : 0) - _knob;
            if (Math.Abs(d) < 0.04f) { _knob = _on ? 1 : 0; _glide.Stop(); }
            else _knob += d * 0.35f;
            Invalidate();
        };
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _on = !_on;
        Changed?.Invoke(_on);
        _glide.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int w = Ui.S(40), h = Ui.S(24);
        var track = new Rectangle(0, (Height - h) / 2, w, h);
        Ui.FillRound(g, track, h / 2, _on ? Theme.Positive : Theme.Field);
        using (var pen = new Pen(Theme.Border))
        using (var path = Ui.RoundRect(new Rectangle(track.X, track.Y, track.Width - 1, track.Height - 1), h / 2))
            g.DrawPath(pen, path);
        int k = Ui.S(20);
        int kx = track.X + Ui.S(2) + (int)((track.Width - k - Ui.S(4)) * _knob);
        using (var b = new SolidBrush(_on ? Color.White : Theme.Muted))
            g.FillEllipse(b, kx, track.Y + (track.Height - k) / 2, k, k);
    }
}

// A horizontal slider with its value printed to the right. Discord's voice page is mostly these.
sealed class Slider : Control
{
    public float Min = 0f, Max = 1f;
    public Func<float, string>? Format;
    bool _drag;
    float _value;

    public event Action<float>? Changed;

    public float Value
    {
        get => _value;
        set { _value = Math.Clamp(value, Min, Max); Invalidate(); }
    }

    public Slider()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    int LabelW => Ui.S(44);
    Rectangle Track => new(0, Height / 2 - Ui.S(2), Math.Max(1, Width - LabelW), Ui.S(4));

    void SetFromX(int x)
    {
        var t = Track;
        float f = Math.Clamp((x - t.X) / (float)Math.Max(1, t.Width), 0f, 1f);
        Value = Min + f * (Max - Min);
        Changed?.Invoke(Value);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _drag = true;
        Capture = true;
        SetFromX(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        Cursor = Cursors.Hand;
        if (_drag) SetFromX(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!_drag) return;
        _drag = false;
        Capture = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var t = Track;
        float f = Max > Min ? (_value - Min) / (Max - Min) : 0f;
        int fill = (int)(t.Width * f);

        Ui.FillRound(g, t, t.Height / 2, Theme.Border);
        if (fill > 0) Ui.FillRound(g, new Rectangle(t.X, t.Y, fill, t.Height), t.Height / 2, Theme.Blurple);

        int k = Ui.S(12);
        using (var b = new SolidBrush(Color.White))
            g.FillEllipse(b, t.X + fill - k / 2, t.Y + (t.Height - k) / 2, k, k);

        Ui.Text(g, Format?.Invoke(_value) ?? _value.ToString("0.00"), Theme.Small,
                new Rectangle(Width - LabelW + Ui.S(6), 0, LabelW - Ui.S(6), Height),
                Theme.Faint, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
    }
}

// A live input-level meter, so the sensitivity threshold can be set by watching it rather than
// guessing. Only moves while a call is up — there is no capture device open otherwise.
sealed class LevelMeter : Control
{
    readonly System.Windows.Forms.Timer _tick = new() { Interval = 60 };
    float _level, _peak;

    public LevelMeter()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        _tick.Tick += (_, _) =>
        {
            float v = VoiceClient.Current?.InputLevel ?? 0f;
            _level = v;
            _peak = Math.Max(_peak * 0.92f, v);
            // The notch tracks where the gate will actually open, which moves with the room —
            // a fixed notch would say nothing useful now the threshold is adaptive.
            Threshold = VoiceClient.Current?.OpenThreshold ?? 0f;
            Invalidate();
        };
        _tick.Start();
    }

    /// Where the gate sits, drawn as a notch so the threshold is visible against the level.
    public float Threshold;

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tick.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bar = new Rectangle(0, Height / 2 - Ui.S(3), Width, Ui.S(6));
        Ui.FillRound(g, bar, bar.Height / 2, Theme.Border);

        // Scale is generous: speech sits well under 0.3 RMS, so a full-width bar at 1.0 would
        // never move.
        float shown = Math.Clamp(_level / 0.3f, 0f, 1f);
        int w = (int)(bar.Width * shown);
        if (w > 0)
            Ui.FillRound(g, new Rectangle(bar.X, bar.Y, w, bar.Height), bar.Height / 2,
                         _level >= Threshold ? Theme.Positive : Theme.Muted);

        int tx = bar.X + (int)(bar.Width * Math.Clamp(Threshold / 0.3f, 0f, 1f));
        Ui.Fill(g, new Rectangle(tx, bar.Y - Ui.S(4), Math.Max(1, Ui.S(2)), bar.Height + Ui.S(8)), Theme.Text);
    }
}
