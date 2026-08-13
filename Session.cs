using System.Drawing;

namespace OpenCord;

// Binds the gateway client to the shell: READY fills the rail, picking a guild fills the sidebar,
// picking a channel loads history, and MESSAGE_CREATE appends.
//
// Every UserClient event arrives on a background thread, so everything that touches a control goes
// through Post(). Forgetting that is the classic WinForms crash — it usually *appears* to work until
// a repaint happens to land mid-update.
sealed class Session
{
    readonly Shell _shell;
    readonly UserClient _client;
    UserGuild? _guild;
    ulong _channel;
    int _channelType;
    ulong _oldestLoaded;
    bool _noMoreHistory;
    readonly Dictionary<ulong, DateTime> _typing = new();
    readonly System.Windows.Forms.Timer _typingTick = new() { Interval = 900 };

    // Unread/rail rebuilds are throttled: a burst of messages would otherwise rebuild the whole
    // sidebar and rail once per message. The flag + timer collapse a burst into a single rebuild.
    readonly System.Windows.Forms.Timer _unreadsTick = new() { Interval = 350 };
    bool _unreadsDirty;

    // Idle memory maintenance on a background thread. The gateway churns short-lived JSON and the
    // UI churns layout objects; workstation GC without a nudge only collects gen-2 under pressure,
    // so RSS creeps up and stays. A quiet non-blocking collect while idle keeps the footprint flat.
    readonly System.Threading.Timer _maint;

    public Session(Shell shell, string token)
    {
        _shell = shell;
        _client = new UserClient(token);
        App.Client = _client;

        _client.Ready += () => { Post(OnReady); return Task.CompletedTask; };
        _client.MessageReceived += m => { Post(() => OnMessage(m)); return Task.CompletedTask; };
        _client.MessageUpdated += m => { Post(() => { if (m.ChannelId == _channel) _shell.Chat.Update(m); }); return Task.CompletedTask; };
        _client.MessageDeleted += (id, ch) => { Post(() => { if (ch == _channel) _shell.Chat.Remove(id); }); return Task.CompletedTask; };
        _client.ReactionChanged += (id, ch, d) => Post(() => { if (ch == _channel) RefreshRow(id, d); });
        _client.UserTyping += t => { Post(() => OnTyping(t)); return Task.CompletedTask; };
        _client.MemberListUpdated += g => Post(() => { if (g == _guild) FillMembers(g); });
        _client.VoiceChanged += g => Post(() => { if (g == null || g == _guild) RefreshSidebar(); });
        _client.ThreadsChanged += g => Post(() => { if (g == _guild) RefreshSidebar(); });
        _client.SelfChanged += () => Post(() => _shell.Sidebar.Invalidate());
        _client.SelfMemberLoaded += () => Post(RefreshSidebar);
        _client.ReadStateChanged += () => Post(RefreshUnreads);
        _client.ConnectionChanged += up => Post(() =>
        {
            _shell.SetConnected(up);
            if (!up) return;
            // Coming back from a drop: images that failed mid-outage are stuck showing a default
            // avatar until their retry window expires, and the member column's subscription died
            // with the old session. Both heal here rather than waiting for the user to click away
            // and back.
            Media.RetryFailed();
            _shell.Members.ResetPaging();
            if (_guild is { } g && _channel != 0) _ = Safe(_client.SubscribeMemberRowsAsync(g, _channel, 0, 99));
            _shell.Invalidate(true);
        });
        // A friend request arriving or resolving changes both the sidebar's pending badge and
        // whatever the Friends page is currently listing.
        _client.RelationshipsChanged += () => Post(() =>
        {
            if (_guild == null) RefreshSidebar();
            if (_shell.Friends.Visible) _shell.Friends.Invalidate();
        });
        _client.PresenceChanged += (_, _) => Post(() =>
        {
            if (_guild != null) { FillMembers(_guild); return; }
            RefreshSidebar();
            // A 1:1 DM's profile panel holds the live user object, so a presence move just repaints.
            if (_client.DmById.GetValueOrDefault(_channel) is { Type: 1 }) _shell.Members.Invalidate();
            // A group DM's column is built from presences too, so it has to re-sort when one moves.
            if (_client.DmById.GetValueOrDefault(_channel) is { Type: 3 } gc) FillGroupMembers(gc);
        });
        _client.GuildJoined += _ => Post(RefreshRail);
        _client.GuildLeft += _ => Post(RefreshRail);
        _client.GuildChanged += _ => Post(RefreshRail);
        _client.CallChanged += _ => Post(RebuildCallBanner);
        _client.DmClosed += id => Post(() => OnDmClosed(id));

        // ── voice media engine ──
        // VOICE_SERVER_UPDATE hands us the credentials for the voice websocket. Fires after any
        // op-4 join (answering a call, starting one, joining a server voice channel), so connecting
        // here covers every path. Leaving a channel is the opposite event: VOICE_STATE_UPDATE with
        // a null channel, caught below as a hang-up.
        _client.VoiceServerReady += info => Post(() => _ = ConnectVoiceAsync(info));
        // Go Live: the gateway answers op 18 with a second set of credentials for the screen-share
        // connection. Same shape as the voice one, its own websocket + UDP + MLS group.
        _client.StreamServerReady += info => Post(() => _ = ConnectStreamAsync(info));
        _client.StreamEnded += () => Post(() =>
        {
            _ = Safe(StreamClient.StopAsync());
            RebuildCallBanner();
            RefreshVoiceUi();
        });
        // Watching a peer's Go Live: a second connection we only receive on.
        _client.StreamWatchReady += (owner, info) => Post(() => _ = WatchStreamAsync(owner, info));
        _client.StreamWatchEnded += () => Post(() =>
        {
            _ = Safe(StreamClient.StopWatchAsync());
            _shell.Voice.SetScreenFrame(_watchingUser, null);
            _watchingUser = 0;
            RefreshVoiceUi();
        });
        _client.VoiceChanged += g => Post(() =>
        {
            if (_client.MyVoiceChannel == null && VoiceClient.Current != null)
            {
                _streamPing.Stop();
                _ = Safe(StreamClient.StopWatchAsync());
                _ = Safe(StreamClient.StopAsync());
                _client.StopWatching();
                _ = Safe(_client.StopGoLiveAsync());
                _ = Safe(VoiceClient.HangUpAsync());
                Sfx.Voice("deconnected");
            }
            AnnounceVoiceMembers();
            // Anyone joining, leaving or muting in our channel changes the stage's tiles.
            RefreshVoiceUi();
        });

        _shell.QuickSwitcherShortcut += () => QuickSwitcher.Show(_shell, this);
        _shell.SettingsShortcut += () => SettingsView.Show(_shell, _client);
        _shell.SearchShortcut += () => SearchPopup.Show(_shell, this);
        _shell.SearchAllShortcut += () => SearchPopup.Show(_shell, this, serverWide: true);
        _shell.EmojiShortcut += () => _shell.Chat.Composer.OpenEmojiShortcut();
        _shell.GifShortcut += () => _shell.Chat.Composer.OpenGifShortcut();
        _shell.MembersShortcut += () => Post(ToggleMembers);
        _shell.PinsShortcut += () => _ = PinsPopup.ShowAsync(_shell, this);
        _shell.JoinServerShortcut += () => JoinDialog.Show(_shell, _client);
        _shell.MarkReadShortcut += () => Post(MarkReadCurrent);
        _shell.MarkServerReadShortcut += () => Post(MarkServerRead);
        _shell.MuteShortcut += () => Post(BannerToggleMute);
        _shell.DeafenShortcut += () => Post(BannerToggleDeaf);
        _shell.SlashShortcut += () => _shell.Chat.Composer.BeginSlash();
        _shell.NavChannel += d => Post(() => StepChannel(d));
        _shell.NavGuild += d => Post(() => StepGuild(d));
        _shell.GuildByIndex += i => Post(() =>
        {
            var g = _client.Guilds.Skip(i).FirstOrDefault();
            if (g != null) { _shell.Rail.Select(g.Id); PickGuild(g.Id); }
        });
        _shell.Rail.Picked += id => Post(() => PickGuild(id));
        _shell.Rail.AddServerClicked += () => JoinDialog.Show(_shell, _client);
        _shell.Rail.DiscoverClicked += () => _shell.ShowDiscover();
        // A successful join arrives as GUILD_CREATE on the gateway, so the rail already knows about
        // it by the time this runs — all that is left is to open it.
        _shell.Discover.Joined += id => Post(() => { RefreshRail(); PickGuild(id); });
        _shell.Rail.GuildMenu += (slot, pt) => GuildContextMenu(slot, pt);
        _shell.Sidebar.ChannelPicked += id => _ = OpenChannel(id);
        _shell.Sidebar.QuickSwitcher += () => QuickSwitcher.Show(_shell, this);
        _shell.Tray.SettingsClicked += () => SettingsView.Show(_shell, _client);
        _shell.Sidebar.ChannelMenu += ChannelContextMenu;
        _shell.Sidebar.GuildMenu += () => { if (_guild != null) GuildContextMenu(new GuildRail.Slot(_guild.Id, _guild.Name), Cursor.Position); };
        _shell.Sidebar.InviteRequested += () => { if (_guild != null) _ = CreateInvite(_guild); };
        _shell.Sidebar.NewGroupClicked += NewGroup;
        _shell.InboxRequested += () => InboxPopup.Show(_shell, this);
        // A forum post is a thread: opening one is the same path as clicking a thread in the sidebar.
        _shell.Forum.PostPicked += id => Post(() => { _shell.Sidebar.SelectedChannel = id; _ = OpenChannel(id, asText: true); });
        _shell.Forum.NewPost += () => NewForumPost();
        _shell.Members.MemberMenu += MemberContextMenu;
        // Scrolling the member column subscribes to the rows that came into view.
        _shell.Members.RangeNeeded += (first, last) =>
        {
            if (_guild is { } g && _channel != 0) _ = Safe(_client.SubscribeMemberRowsAsync(g, _channel, first, last));
        };

        // Voice stage + the sidebar's connected strip.
        _shell.Sidebar.VoiceDisconnect += LeaveVoice;
        _shell.Voice.Disconnect += LeaveVoice;
        _shell.Voice.MuteToggled += BannerToggleMute;
        _shell.Voice.DeafenToggled += BannerToggleDeaf;
        _shell.Voice.VideoToggled += () => Post(BannerToggleVideo);
        _shell.Voice.ScreenToggled += () => Post(BannerToggleScreen);
        _shell.Voice.TileMenu += VoiceTileMenu;
        _shell.Voice.ChatRequested += () =>
        {
            // Open the voice channel's own text chat without leaving the call.
            if (_client.MyVoiceChannel is not { } vc) return;
            _voiceStageWanted = false;
            _shell.ShowVoice(false);
            _shell.Sidebar.SelectedChannel = vc;
            _ = OpenChannelText(vc);
        };
        _shell.Chat.Send += (text, replyTo) => _ = SendAsync(text, replyTo, _shell.Chat.Composer.PingReply);
        _shell.Chat.FailedAction += OnFailedAction;
        _shell.Chat.NeedOlder += () => _ = LoadOlder();
        _shell.Chat.Typing += () => { if (_channel != 0) _ = Safe(_client.Rest.TypingAsync(_channel)); };
        _shell.Chat.MembersToggled += ToggleMembers;
        _shell.Chat.SearchRequested += q => SearchPopup.Show(_shell, this);
        _shell.Chat.PinsRequested += () => _ = PinsPopup.ShowAsync(_shell, this);
        _shell.Chat.ThreadsRequested += () => ThreadsPopup.Show(_shell, this);
        _shell.Chat.CallRequested += v => Post(() => StartCall(v));
        _shell.Call.Answer += c => Post(() => AnswerCall(c));
        _shell.Call.Decline += c => Post(() => DeclineCall(c));
        _shell.Call.HangUp += c => Post(() => HangUpCall(c));
        _shell.Call.ToggleMute += () => Post(BannerToggleMute);
        _shell.Call.ToggleDeaf += () => Post(BannerToggleDeaf);
        _shell.Call.ToggleVideo += () => Post(BannerToggleVideo);
        _shell.Call.ToggleScreen += () => Post(BannerToggleScreen);

        App.OpenDm = uid => _ = OpenDmWith(uid);
        App.Relayout = () => Post(() => _shell.Chat.List.Rebuild());

        // Clicking a toast has to bring the window back — it is most useful precisely when the app
        // is hidden in the tray, which is exactly when it used to jump to a channel nobody could see.
        Toast.OnClick = (guildId, channelId) => Post(() =>
        {
            _shell.SurfaceWindow();
            GoToMessage(guildId, channelId, 0);
        });

        // Markdown asks these for mention text and colour; they read whichever guild is on screen.
        App.ResolveUserMention = id =>
        {
            var g = _guild;
            var m = g?.GetMember(id);
            if (m?.User != null) return (m.Nick ?? m.User.DisplayName, g!.NameColor(id) ?? Theme.BrandText);
            var known = _client.NameOf(id);
            return known is { Length: > 0 } and not "unknown-user" ? (known, (Color?)Theme.BrandText) : null;
        };
        App.ResolveRoleMention = id =>
            _guild?.RoleById.GetValueOrDefault(id) is { } r ? (r.Name, r.Rgb ?? Theme.BrandText) : null;
        App.ResolveChannelName = id => _guild?.ChannelById.GetValueOrDefault(id)?.Name
                                    ?? _client.GuildOfChannel(id)?.ChannelById.GetValueOrDefault(id)?.Name;

        // Typing indicators expire on their own after 10s; nothing pushes a "stopped" event.
        _typingTick.Tick += (_, _) => ExpireTyping();
        _typingTick.Start();

        _unreadsTick.Tick += (_, _) => { _unreadsTick.Stop(); if (_unreadsDirty) { _unreadsDirty = false; RefreshUnreads(); } };

        // 90s cadence, collect only once memory has crept meaningfully above the idle floor. The
        // non-blocking overload runs on the BGC thread, so it never stalls painting. Dispose is
        // unnecessary: the timer dies with the process and the callback only calls GC.
        _maint = new System.Threading.Timer(_ =>
        {
            long mem = GC.GetTotalMemory(false);
            if (mem > 192L * 1024 * 1024) { GC.Collect(2, GCCollectionMode.Optimized, false); }
            else if (mem > 128L * 1024 * 1024) { GC.Collect(0, GCCollectionMode.Optimized, false); }
        }, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(90));
    }

    public UserClient Client => _client;
    public Task StartAsync() => _client.ConnectAsync();
    public ulong CurrentChannelId => _channel;

    void Post(Action a)
    {
        if (_shell.IsDisposed) return;
        try
        {
            if (_shell.InvokeRequired) _shell.BeginInvoke(a);
            else a();
        }
        catch (ObjectDisposedException) { }   // shutting down mid-dispatch is not an error
    }

    static async Task Safe(Task t)
    {
        try { await t; } catch (Exception e) { Log.Write("session", e.Message); }
    }

    // ── startup ─────────────────────────────────────────────────────────────────────────────────
    void OnReady()
    {
        RefreshRail();

        var last = _client.GuildById.GetValueOrDefault(Prefs.Current.LastGuild);
        _shell.Rail.Select(last?.Id);
        PickGuild(last?.Id);
        _shell.Sidebar.Invalidate();
    }

    void RefreshRail()
    {
        _shell.Rail.SetGuilds(_client.Guilds.Select(g => new GuildRail.Slot(
            g.Id, g.Name, g.IconUrl, GuildUnread(g), GuildMentions(g))));
        _shell.Rail.HomeMentions = _client.DMChannels.Sum(d => _client.MentionCount(d.Id));
        _shell.Rail.Invalidate();
    }

    bool GuildUnread(UserGuild g) =>
        !_client.MutedGuilds.Contains(g.Id) &&
        g.Channels.Any(c => c.IsText && !_client.MutedChannels.Contains(c.Id) && _client.IsUnread(c.Id, c.LastMessageId));

    int GuildMentions(UserGuild g) => g.Channels.Where(c => c.IsText).Sum(c => _client.MentionCount(c.Id));

    // ── guild / channel selection ───────────────────────────────────────────────────────────────
    public void PickGuild(ulong? id)
    {
        _guild = id is { } gid ? _client.GuildById.GetValueOrDefault(gid) : null;
        App.Guild = _guild;
        _shell.Rail.Select(_guild?.Id);

        if (_guild == null) { ShowHome(); return; }

        Prefs.Current.LastGuild = _guild.Id;
        Prefs.Save();

        // Visibility is settled by ApplyMemberColumn once the channel is known — forcing it on here
        // flashed a guild roster over a DM when the remembered channel turned out to be one.
        _shell.SetContext(_guild.Name, _guild.IconUrl);
        _shell.Sidebar.SetChannels(_guild.Name, BuildTree(_guild));
        FillMembers(_guild);
        // The member list is subscribed per channel, so OpenChannel below asks for it — subscribing
        // to the guild's first text channel here listed the wrong people in restricted channels.
        _ = _client.EnsureSelfMemberAsync(_guild);
        _ = LoadEvents(_guild);

        // Land on the remembered channel if it belongs to this guild, else the first readable text one.
        var want = _guild.ChannelById.GetValueOrDefault(Prefs.Current.LastChannel);
        var open = want is { IsText: true } ? want
                 : _guild.Channels.Where(c => c.IsPostable).OrderBy(c => c.Position).FirstOrDefault();
        if (open != null) { _shell.Sidebar.SelectedChannel = open.Id; _ = OpenChannel(open.Id); }
    }

    void ShowHome()
    {
        Prefs.Current.LastGuild = 0;
        Prefs.Save();
        _shell.ShowMembers(false);
        _shell.SetContext("Direct Messages", null, home: true);
        _shell.Sidebar.SetChannels("Direct Messages", BuildDmList(), home: true);

        // Reopen whatever was last on screen in home mode — a DM, or the Friends page — the way the
        // real client does. Falling straight to the top of the list lost your place on every launch.
        ulong last = Prefs.Current.LastChannel;
        ulong open = last == ChannelSidebar.FriendsId || _client.DmById.ContainsKey(last)
                   ? last
                   : _client.DMChannels.FirstOrDefault()?.Id ?? 0;
        if (open != 0) { _shell.Sidebar.SelectedChannel = open; _ = OpenChannel(open); }
    }

    List<ChannelSidebar.Entry> BuildDmList()
    {
        var outp = new List<ChannelSidebar.Entry>
        {
            new(ChannelSidebar.Kind.Nav, ChannelSidebar.FriendsId, "Friends",
                Mentions: _client.Relationships.Count(r => r.Type == 3)),
            new(ChannelSidebar.Kind.Category, ChannelSidebar.DmHeaderId, "Direct Messages"),
        };
        foreach (var d in _client.DMChannels.OrderByDescending(d => d.LastMessageId).Take(60))
        {
            var u = d.Recipient;
            outp.Add(new ChannelSidebar.Entry(
                d.Type == 3 ? ChannelSidebar.Kind.GroupDm : ChannelSidebar.Kind.Dm,
                d.Id, d.DisplayName,
                _client.IsUnread(d.Id, d.LastMessageId), _client.MentionCount(d.Id),
                d.AvatarUrl, u?.Presence ?? Presence.Offline,
                u?.CustomStatus ?? u?.ActivityLine, Tag: u?.ServerTag));
        }
        return outp;
    }

    // Discord's sidebar order: uncategorised channels first, then each category with its children,
    // everything sorted by Position.
    /// Scheduled events per guild, filled in the background by PickGuild. The sidebar shows the
    /// Events row only when there is something to open, exactly like the live client.
    static readonly Dictionary<ulong, List<UserScheduledEvent>> _events = new();

    internal static List<ChannelSidebar.Entry> BuildTree(UserGuild g)
    {
        var outp = new List<ChannelSidebar.Entry>();
        // The live client shows Events unconditionally — it is there with zero events scheduled —
        // so the row is always present and only the count comes and goes.
        outp.Add(new ChannelSidebar.Entry(ChannelSidebar.Kind.Nav, ChannelSidebar.EventsId, "Events",
                                          Mentions: _events.GetValueOrDefault(g.Id)?.Count ?? 0));
        var self = g.Client?.CurrentUser?.Id ?? 0;

        // Client is null for a guild that has not been through READY (and in the selftest), so the
        // unread lookup has to tolerate it rather than take the sidebar down with an NRE.
        ChannelSidebar.Entry Row(UserChannelData c) => new(
            c.IsVoice ? ChannelSidebar.Kind.Voice
            : c.Type == 5 ? ChannelSidebar.Kind.Announcement
            : c.IsForum ? ChannelSidebar.Kind.Forum
            : ChannelSidebar.Kind.Text,
            c.Id, c.Name,
            !c.IsVoice && (g.Client?.IsUnread(c.Id, c.LastMessageId) ?? false)
                       && g.Client?.MutedChannels.Contains(c.Id) != true,
            g.Client?.MentionCount(c.Id) ?? 0,
            Muted: g.Client?.MutedChannels.Contains(c.Id) ?? false);

        bool Visible(UserChannelData c) => self == 0 || g.CanView(self, c);

        IEnumerable<ChannelSidebar.Entry> WithVoice(UserChannelData c)
        {
            yield return Row(c);
            if (!c.IsVoice) yield break;
            foreach (var vs in g.VoiceIn(c.Id))
            {
                var m = g.GetMember(vs.UserId);
                if (m?.User == null) continue;
                yield return new ChannelSidebar.Entry(ChannelSidebar.Kind.VoiceMember, vs.UserId,
                                                      m.DisplayName, AvatarUrl: m.User.GetAvatarUrl(32));
            }
        }

        // Active threads sit under their parent channel, most recently active first, exactly where
        // the real client puts them.
        IEnumerable<ChannelSidebar.Entry> ThreadsOf(ulong parentId) =>
            g.Threads.Where(t => t.ParentId == parentId && t.Metadata?.Archived != true)
                     .OrderByDescending(t => t.LastMessageId ?? 0)
                     .Select(t => new ChannelSidebar.Entry(
                         ChannelSidebar.Kind.Thread, t.Id, t.Name,
                         Unread: (g.Client?.IsUnread(t.Id, t.LastMessageId ?? 0) ?? false)
                              && g.Client?.MutedChannels.Contains(t.Id) != true,
                         Mentions: g.Client?.MentionCount(t.Id) ?? 0,
                         Muted: g.Client?.MutedChannels.Contains(t.Id) ?? false));

        foreach (var c in g.Channels.Where(c => c.ParentId == null && (c.IsText || c.IsVoice) && Visible(c))
                                    .OrderBy(c => c.Position))
        {
            outp.AddRange(WithVoice(c));
            if (c.IsText) outp.AddRange(ThreadsOf(c.Id));
        }

        foreach (var cat in g.Channels.Where(c => c.IsCategory).OrderBy(c => c.Position))
        {
            var kids = g.Channels.Where(c => c.ParentId == cat.Id && (c.IsText || c.IsVoice) && Visible(c))
                                 .OrderBy(c => c.Position).ToList();
            if (kids.Count == 0) continue;
            outp.Add(new ChannelSidebar.Entry(ChannelSidebar.Kind.Category, cat.Id, cat.Name));
            foreach (var k in kids)
            {
                outp.AddRange(WithVoice(k));
                if (k.IsText) outp.AddRange(ThreadsOf(k.Id));
            }
        }
        return outp;
    }

    async Task LoadEvents(UserGuild g)
    {
        try
        {
            // Cancelled and finished events are history; the row counts what is still upcoming.
            var list = (await _client.Rest.GetEventsAsync(g.Id))
                       .Where(e => e.Status is 1 or 2)
                       .OrderBy(e => e.Start ?? DateTimeOffset.MaxValue)
                       .ToList();
            int had = _events.GetValueOrDefault(g.Id)?.Count ?? -1;
            _events[g.Id] = list;
            Log.Write("events", $"{g.Name}: {list.Count} upcoming");
            // Only rebuild when the badge would actually change — PickGuild already drew once.
            if (had != list.Count) Post(() => { if (_guild == g) RefreshSidebar(); });
        }
        catch (Exception e) { Log.Write("events", e.Message); }
    }

    void RefreshSidebar()
    {
        if (_guild != null) _shell.Sidebar.Refresh(BuildTree(_guild));
        else _shell.Sidebar.Refresh(BuildDmList());
    }

    void RefreshUnreads() { RefreshSidebar(); RefreshRail(); }

    // Called on the hot path (every MESSAGE_CREATE). Collapses a burst into one rebuild.
    void QueueUnreads()
    {
        if (!_unreadsTick.Enabled) _unreadsTick.Start();
        _unreadsDirty = true;
    }

    void FillMembers(UserGuild g)
    {
        var items = new List<MemberList.Entry>();
        foreach (var (label, members) in g.MemberGroups)
        {
            // "Online — 3". Em dash, and the count comes from the rows we hold: the gateway sends 0
            // in the group payload on a SYNC (see UserClient's member-list handler).
            items.Add(new MemberList.Entry(true, $"{label} — {members.Count}"));
            foreach (var m in members)
            {
                if (m.User == null) continue;
                items.Add(new MemberList.Entry(false,
                    m.Nick ?? m.User.DisplayName,
                    m.AvatarUrl(g.Id, 64),
                    m.User.Presence,
                    g.NameColor(m.User.Id) ?? Theme.Muted,
                    m.User.CustomStatus ?? m.User.ActivityLine,
                    m.User));
            }
        }
        _shell.Members.SetMembers(items);
    }

    // Who the member column belongs to, decided by the open channel rather than by whatever was
    // last filled. Getting this wrong is what left a server's cached roster sitting beside a DM.
    //
    //   guild channel — the roster, shown or hidden by the user's toggle
    //   1:1 DM        — the recipient's profile panel, hidden or shown by the same toggle
    //   group DM      — always the group's recipients, no toggle
    void ApplyMemberColumn(UserDMChannel? dm)
    {
        if (dm == null)
        {
            _shell.ShowMembers(_membersWanted);
            if (_guild is { } g) FillMembers(g);
            return;
        }
        if (dm.Type == 3) { FillGroupMembers(dm); _shell.ShowMembers(true); return; }
        if (_dmProfileWanted)
        {
            _shell.ShowMembers(true);
            SetDmProfile(dm);
        }
        else
        {
            // Still *clear* the rows, not just hide them — the column is re-shown by the next
            // channel and a stale roster would flash before the new one lands.
            _shell.Members.SetMembers(Array.Empty<MemberList.Entry>());
            _shell.ShowMembers(false);
        }
        _shell.Chat.SetMembersActive(_dmProfileWanted);
    }

    /// Fill the column with the DM's recipient and fetch the rest of the profile (banner, bio,
    /// pronouns, mutuals) in the background — the panel paints the live user immediately and the
    /// fetch fills in the rest when it lands.
    async void SetDmProfile(UserDMChannel dm)
    {
        var u = dm.Recipient;
        if (u == null)
        {
            _shell.Members.SetMembers(Array.Empty<MemberList.Entry>());
            _shell.ShowMembers(false);
            return;
        }
        _shell.Members.SetProfile(new MemberList.Profile(u, null));
        // GetProfileAsync swallows errors and returns null — no throw to guard here.
        var p = await _client.Rest.GetProfileAsync(u.Id);
        Post(() => _shell.Members.UpdateProfile(u.Id, p));
    }

    /// A group DM's column: the recipients plus ourselves, split Online / Offline like the client.
    void FillGroupMembers(UserDMChannel dm)
    {
        var all = new List<UserUser>(dm.Recipients);
        if (_client.CurrentUser is { } me && all.All(u => u.Id != me.Id)) all.Add(me.AsUser());

        var items = new List<MemberList.Entry>();
        void Section(string label, IEnumerable<UserUser> users)
        {
            var list = users.OrderBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
            if (list.Count == 0) return;
            items.Add(new MemberList.Entry(true, $"{label} — {list.Count}"));
            foreach (var u in list)
                items.Add(new MemberList.Entry(false, u.DisplayName, u.GetAvatarUrl(64), u.Presence,
                                               Theme.Muted, u.CustomStatus ?? u.ActivityLine, u));
        }
        Section("Online", all.Where(u => u.Presence != Presence.Offline));
        Section("Offline", all.Where(u => u.Presence == Presence.Offline));
        _shell.Members.SetMembers(items);
    }

    // The user's own show/hide choice for the guild roster.
    bool _membersWanted = true;
    // A 1:1 DM's profile panel has its own toggle, like the live client — hiding the guild roster
    // must not come back to haunt a DM, and vice versa.
    bool _dmProfileWanted = true;

    void ToggleMembers()
    {
        if (_channelType == 1)
        {
            _dmProfileWanted = !_dmProfileWanted;
            _shell.Chat.SetMembersActive(_dmProfileWanted);
            if (_client.DmById.GetValueOrDefault(_channel) is { } dm) ApplyMemberColumn(dm);
            return;
        }
        if (_channelType == 3) return;   // a group DM's roster is always on, nothing to toggle
        _membersWanted = !_membersWanted;
        _shell.ShowMembers(_membersWanted);
    }

    // ── channel content ─────────────────────────────────────────────────────────────────────────
    /// Open a voice channel's *text* chat without joining or leaving the call.
    Task OpenChannelText(ulong id) => OpenChannel(id, 0, asText: true);

    async Task OpenChannel(ulong id, ulong around = 0, bool asText = false)
    {
        // The Friends row lives in the same list as the DM channels, so it arrives here as a pick.
        // It is a destination, not a channel: swap the pane and leave _channel alone.
        if (id == ChannelSidebar.FriendsId)
        {
            Prefs.Current.LastChannel = id;
            Prefs.Save();
            Post(() => { _shell.ShowFriends(true); _shell.Sidebar.SelectedChannel = id; });
            return;
        }
        Post(() => _shell.ShowFriends(false));

        // Events is a popup, not a pane — clicking it must not disturb the open channel.
        if (id == ChannelSidebar.EventsId)
        {
            Post(() =>
            {
                _shell.Sidebar.SelectedChannel = _channel;
                if (_guild is { } g) EventsPopup.Show(_shell, _events.GetValueOrDefault(g.Id) ?? new(), g);
            });
            return;
        }

        // A forum is a list of posts, not a message list: opening it as a chat asked for messages
        // in a channel that has none and left an empty pane. It gets its own view.
        var forum = _guild?.ChannelById.GetValueOrDefault(id);
        if (forum is { IsForum: true })
        {
            _channel = id;
            _channelType = forum.Type;
            Prefs.Current.LastChannel = id;
            Prefs.Save();
            Post(() =>
            {
                _voiceStageWanted = false;
                _shell.Forum.SetLoading(forum.Name ?? "forum", forum.Topic);
                _shell.ShowForum(true);
            });
            await LoadForumAsync(forum);
            return;
        }
        Post(() => _shell.ShowForum(false));

        // Clicking a voice channel connects to it, the way the real client does — it does not open
        // the channel's text chat. That chat is still reachable, from the stage's own button and
        // from the channel's context menu; but the click itself has to join, because "click the
        // channel to join the call" is the whole interaction model of a voice channel.
        var voice = _guild?.ChannelById.GetValueOrDefault(id);
        if (!asText && voice is { IsVoice: true })
        {
            _voiceStageWanted = true;
            if (_client.MyVoiceChannel != id)
                await Safe(_client.SetVoiceStateAsync(_guild?.Id, id, _client.SelfMute, _client.SelfDeaf));
            Post(() => { RefreshSidebar(); RefreshVoiceUi(); _shell.ShowVoice(true); });
            return;
        }

        // Re-opening the DM we are *already* on a call in puts the call back on screen, the way
        // clicking a voice channel does. Without this the stage was a one-way door: the Chat button
        // left it and nothing brought it back short of hanging up.
        if (!asText && _client.MyVoiceChannel == id && _client.DmById.ContainsKey(id))
        {
            _voiceStageWanted = true;
            Post(() => { RefreshVoiceUi(); _shell.ShowVoice(true); });
            return;
        }

        _voiceStageWanted = false;
        Post(() => _shell.ShowVoice(false));

        _channel = id;
        _typing.Clear();
        _noMoreHistory = false;
        Prefs.Current.LastChannel = id;
        Prefs.Save();

        // Discord scopes the member list to the channel on screen. Re-subscribe on every switch and
        // start the paging over, so the column reflects who can actually see *this* channel.
        if (_guild is { } mg)
        {
            Post(() => _shell.Members.ResetPaging());
            _ = Safe(_client.SubscribeMemberRowsAsync(mg, id, 0, 99));
        }

        var ch = _guild?.ChannelById.GetValueOrDefault(id);
        var dm = _client.DmById.GetValueOrDefault(id);
        Post(() => ApplyMemberColumn(dm));
        // Threads are not in ChannelById — they live in the guild's own thread map. Without this a
        // thread opened from the sidebar came through as type 0 named "channel", so the header drew
        // a hash instead of the thread glyph and the composer read "Message #channel".
        var th = _guild?.ThreadById.GetValueOrDefault(id)
              ?? _client.GuildOfChannel(id)?.ThreadById.GetValueOrDefault(id);
        _channelType = ch?.Type ?? th?.Type ?? dm?.Type ?? 0;
        var info = new ChatView.ChannelInfo(
            id,
            ch?.Name ?? th?.Name ?? dm?.DisplayName ?? "channel",
            _channelType,
            ch?.Topic,
            dm?.AvatarUrl,
            dm?.Recipient?.Presence ?? Presence.Offline);
        Post(() => { _shell.Chat.SetChannel(info); _shell.Chat.FocusComposer(); });

        ulong lastRead = _client.ReadStates.GetValueOrDefault(id)?.LastMessageId ?? 0;
        try
        {
            // around != 0 is a jump-to-message (search hit, pinned row): fetch a page centred on it.
            var msgs = around != 0
                ? await _client.Rest.GetMessagesAsync(id, 50, guildId: _guild?.Id ?? 0, around: around)
                : await _client.Rest.GetMessagesAsync(id, 50, guildId: _guild?.Id ?? 0);
            if (_channel != id) return;                      // user moved on while this was in flight
            var ordered = msgs.Reverse().ToList();
            _oldestLoaded = ordered.Count > 0 ? ordered[0].Id : 0;
            Post(() =>
            {
                _shell.Chat.SetMessages(ordered, lastRead);
                if (ordered.Count > 0) Ack(id, ordered[^1].Id);
                if (around != 0) _shell.Chat.List.ScrollTo(around);
            });
        }
        catch (Exception e) { Log.Write("chat", "history failed: " + e.Message); }
    }

    // The forum post list. `threads/search` returns the posts *and* their opening messages in one
    // call, so a card can show its preview without a fetch per post.
    async Task LoadForumAsync(UserChannelData forum)
    {
        try
        {
            var posts = await _client.Rest.GetForumPostsAsync(forum.Id, _guild?.Id ?? 0);
            if (_channel != forum.Id) return;                 // user moved on while this was in flight
            var tagNames = forum.AvailableTags.ToDictionary(t => t.Id, t => t.Name);
            var cards = posts.Select(p => new ForumView.Post(
                p.Id,
                p.Name,
                p.FirstMessage?.Member?.DisplayName ?? p.FirstMessage?.Author?.DisplayName ?? "Unknown",
                p.FirstMessage?.Author?.GetAvatarUrl(32),
                Markdown.Flatten(p.FirstMessage?.Content ?? ""),
                Math.Max(0, p.TotalMessageSent - 1),           // the opening post is not a reply
                p.LastMessageId is { } l ? MessageRow.Stamp(SnowflakeTime(l)) : "",
                p.AppliedTags.Select(t => tagNames.GetValueOrDefault(t)).Where(n => n != null).ToList()!))
                .ToList();
            Post(() => _shell.Forum.Set(forum.Name ?? "forum", forum.Topic, cards));
        }
        catch (Exception e) { Log.Write("forum", e.Message); }
    }

    // Discord's forum composer is a title plus a first message. Two prompts is not the real
    // two-field dialog, but it creates the same post through the same endpoint.
    void NewForumPost()
    {
        if (_guild?.ChannelById.GetValueOrDefault(_channel) is not { IsForum: true } forum) return;
        var title = Prompt.Ask(_shell, "New Post", $"Give your post in #{forum.Name} a title.", null, "Next");
        if (string.IsNullOrWhiteSpace(title)) return;
        var body = Prompt.Ask(_shell, "New Post", "What's on your mind?", null, "Post");
        if (body == null) return;
        _ = PostForumAsync(forum, title.Trim(), body);
    }

    async Task PostForumAsync(UserChannelData forum, string title, string body)
    {
        var err = await _client.Rest.CreateForumPostAsync(forum.Id, title, body);
        if (err != null) { _shell.Sidebar.FlashInvite(err); return; }
        if (_channel == forum.Id) await LoadForumAsync(forum);
    }

    static DateTimeOffset SnowflakeTime(ulong id) =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)((id >> 22) + 1420070400000UL));

    async Task LoadOlder()
    {
        // Every path out of here has to release the list's in-flight latch. It is only cleared by a
        // successful prepend, so a throw or a mid-flight channel switch used to wedge the channel:
        // it never asked for older messages again.
        if (_noMoreHistory || _oldestLoaded == 0) { Post(() => _shell.Chat.OlderDone()); return; }
        ulong ch = _channel, before = _oldestLoaded;
        try
        {
            var msgs = await _client.Rest.GetMessagesAsync(ch, 50, before: before, guildId: _guild?.Id ?? 0);
            if (_channel != ch) { Post(() => _shell.Chat.OlderDone()); return; }
            var ordered = msgs.Reverse().ToList();
            if (ordered.Count == 0) { _noMoreHistory = true; Post(() => _shell.Chat.OlderDone()); return; }
            _oldestLoaded = ordered[0].Id;
            Post(() => _shell.Chat.PrependOlder(ordered));
        }
        catch (Exception e)
        {
            Log.Write("chat", "older failed: " + e.Message);
            Post(() => _shell.Chat.OlderDone());
        }
    }

    void OnMessage(UserMessage m)
    {
        if (m.ChannelId == _channel)
        {
            bool wasAtBottom = _shell.Chat.AtBottom;
            _shell.Chat.Append(m);
            _typing.Remove(m.Author?.Id ?? 0);
            PushTyping();
            if (wasAtBottom) Ack(m.ChannelId, m.Id);
        }
        QueueUnreads();
        MaybeNotify(m);
    }

    // The desktop-notification rule, the same one the real client uses: only fire while the window
    // doesn't have focus, skip your own messages, honour mutes, and honour "mentions only".
    void MaybeNotify(UserMessage m)
    {
        if (_shell.IsDisposed) return;
        if (m.Author?.Id == _client.CurrentUser?.Id) return;
        if (_client.MutedChannels.Contains(m.ChannelId)) return;
        if (m.GuildId is { } gid && _client.MutedGuilds.Contains(gid)) return;

        bool isDm = _client.DmById.ContainsKey(m.ChannelId);
        if (Prefs.Current.NotifyMentionsOnly && !isDm)
        {
            var self = _client.CurrentUser?.Id ?? 0;
            var myRoles = m.GuildId is { } g2 && _client.GuildById.TryGetValue(g2, out var g)
                ? g.GetMember(self)?.RoleIds : null;
            if (self == 0 || !m.MentionsMe(self, myRoles)) return;
        }

        // Discord splits the two alerts, and the focus rule is not the same for both. The ping
        // plays whenever the message is not already in front of you — a DM landing while you read
        // another channel is audible even though the app has focus. The toast is the stricter one:
        // it only appears when the window is in the background.
        bool focused = _shell.Focused || _shell.ContainsFocus;
        bool watching = focused && m.ChannelId == _channel;
        if (!watching) Sfx.Play("new-message");
        if (focused) return;
        if (!Prefs.Current.NotifyEnabled) return;

        var author = m.Member?.DisplayName ?? m.Author?.DisplayName ?? "Someone";
        string title, icon;
        if (isDm)
        {
            var dm = _client.DmById[m.ChannelId];
            title = dm.Type == 3 ? author + "  ·  " + dm.DisplayName : author;
            icon = dm.Recipient?.GetAvatarUrl(64) ?? "";
        }
        else
        {
            var ch = _client.GuildOfChannel(m.ChannelId)?.ChannelById.GetValueOrDefault(m.ChannelId);
            title = "#" + (ch?.Name ?? "channel");
            icon = m.Author?.GetAvatarUrl(64) ?? "";
        }
        var body = Markdown.Flatten(m.Content);
        if (body.Length == 0)
            body = m.Attachments.Count > 0 ? "📎 " + m.Attachments[0].Filename
                 : m.Stickers.Count > 0 ? "sent a sticker"
                 : m.Embeds.Count > 0 ? "shared a link"
                 : "new message";
        Toast.Show(title, body, icon, m.GuildId ?? 0, m.ChannelId);
    }

    /// A reaction or poll vote moved on a message that is on screen: apply the delta to the cached
    /// message, then re-lay-out the row. Without the first half the second is a no-op, which is why
    /// reactions only ever appeared after reopening the channel.
    void RefreshRow(ulong id, ReactionDelta? delta = null)
    {
        var m = _shell.Chat.List.MessageById(id);
        if (m == null) return;
        delta?.ApplyTo(m, _client.CurrentUser?.Id ?? 0);
        _shell.Chat.Update(m);
    }

    void Ack(ulong channel, ulong message)
    {
        _client.MarkRead(channel, message);
        _ = Safe(_client.Rest.AckAsync(channel, message));
        QueueUnreads();
    }

    // ── typing ──────────────────────────────────────────────────────────────────────────────────
    void OnTyping(UserTypingEvent t)
    {
        if (t.ChannelId != _channel || t.UserId == _client.CurrentUser?.Id) return;
        _typing[t.UserId] = DateTime.UtcNow;
        PushTyping();
    }

    void ExpireTyping()
    {
        if (_typing.Count == 0) return;
        var cutoff = DateTime.UtcNow.AddSeconds(-9);
        var stale = _typing.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList();
        if (stale.Count == 0) return;
        foreach (var k in stale) _typing.Remove(k);
        PushTyping();
    }

    void PushTyping() =>
        _shell.Chat.SetTyping(_typing.Keys.Select(id => _client.NameOf(id)).ToList());

    // ── sending ─────────────────────────────────────────────────────────────────────────────────
    // Discord draws your message the instant you press Enter, faded, and only then talks to the
    // server. Waiting for the round trip made the client feel laggy on a good connection and lose
    // the message outright on a bad one — a 403 used to vanish into the log.
    Task SendAsync(string text, ulong replyTo, bool pingReply = true) =>
        SendAsync(text, replyTo, pingReply, null);

    async Task SendAsync(string text, ulong replyTo, bool pingReply, UserMessage? resend)
    {
        ulong channel = _channel;
        if (channel == 0) return;

        // A retry keeps the original row's nonce so the server still dedups it, and so the row we
        // just removed and the one we are about to draw are the same message to everyone involved.
        string nonce = resend?.Nonce ?? UserRestClient.Nonce();
        var local = resend ?? new UserMessage
        {
            Id = UserRestClient.NonceId(),   // sorts after everything on screen until the real id lands
            ChannelId = channel,
            GuildId = _guild?.Id,
            Content = text,
            Timestamp = DateTimeOffset.Now,
            Author = _client.SelfAsUser!,
            Member = _guild?.GetMember(_client.CurrentUser?.Id ?? 0),
            Client = _client,
            Nonce = nonce,
            Type = replyTo != 0 ? 19 : 0,
            ReferencedMessage = replyTo != 0 ? _shell.Chat.List.MessageById(replyTo) : null,
        };
        local.SendState = 1;
        _shell.Chat.Append(local);
        _shell.Chat.ScrollToBottom();

        try
        {
            // Posting into a thread you have not joined is rejected. The real client joins you
            // silently on your first message, which is why you never see the error there.
            if (_channelType is 11 or 12) await _client.Rest.JoinThreadAsync(channel);

            var sent = replyTo != 0
                ? await _client.Rest.SendMessageReplyAsync(channel, text, replyTo, nonce, pingReply)
                : await _client.Rest.SendMessageAsync(channel, text, nonce);
            // Swap the optimistic row for the server's copy. If the gateway echo beat us here the
            // id already matches and Append is a no-op, which is exactly what we want.
            Post(() => { if (_channel == channel) _shell.Chat.Append(sent); });
        }
        catch (Exception e)
        {
            Log.Write("chat", "send failed: " + e.Message);
            Post(() => { if (_channel == channel) _shell.Chat.FailPending(nonce, e.Message); });
        }
    }

    // Retry re-posts the same row; Delete just drops it. Both are the links Discord puts under a
    // message that failed.
    void OnFailedAction(UserMessage m, bool retry)
    {
        if (m.Nonce is not { Length: > 0 } nonce) return;
        var taken = _shell.Chat.TakeFailed(nonce);
        if (!retry || taken == null) return;
        taken.SendState = 1;
        _ = SendAsync(taken.Content, taken.ReferencedMessage?.Id ?? 0, true, taken);
    }

    public async Task OpenDmWith(ulong userId)
    {
        try
        {
            var dm = await _client.GetOrCreateDMAsync(userId);
            Post(() =>
            {
                PickGuild(null);
                _shell.Sidebar.SelectedChannel = dm.Id;
                _shell.Sidebar.Refresh(BuildDmList());
                _ = OpenChannel(dm.Id);
            });
        }
        catch (Exception e) { Log.Write("dm", e.Message); }
    }

    /// Used by the quick switcher: jump to any channel in any guild.
    public void GoTo(ulong guildId, ulong channelId)
    {
        if (guildId != 0 && _guild?.Id != guildId) PickGuild(guildId);
        else if (guildId == 0 && _guild != null) PickGuild(null);
        _shell.Sidebar.SelectedChannel = channelId;
        _shell.Sidebar.Invalidate();
        _ = OpenChannel(channelId);
    }

    /// Jump to a specific message (search hit / pinned row): open the channel around it and scroll.
    public void GoToMessage(ulong guildId, ulong channelId, ulong messageId)
    {
        if (guildId != 0 && _guild?.Id != guildId) PickGuild(guildId);
        else if (guildId == 0 && _guild != null) PickGuild(null);
        _shell.Sidebar.SelectedChannel = channelId;
        _shell.Sidebar.Invalidate();
        _ = OpenChannel(channelId, messageId);
    }

    // ── DM calls ────────────────────────────────────────────────────────────────────────────────
    // The banner reflects three states, read straight off the gateway: someone ringing us (show
    // Answer/Decline), a call we started that hasn't connected (show Ringing/Hang Up), and a call we
    // are in (show In Call + mute/deafen/hang up). All of it is driven by CallChanged, so no other
    // code path has to know the banner exists.
    ulong _ringingChannel;   // the DM we started a call in, until someone picks up
    bool _callIsVideo;

    void StartCall(bool video)
    {
        if (_channel == 0 || _guild != null) return;   // DM calls only, same as the header buttons
        if (_client.MyVoiceChannel == _channel)
        {
            // Already connected: the header button becomes a leave control, like Discord.
            HangUpCall(_channel);
            return;
        }
        _ringingChannel = _channel;
        _callIsVideo = video;
        _voiceStageWanted = true;      // land on the stage as soon as the connection comes up
        // Ringing ALSO joins the call's voice channel — Discord connects you during ringing so the
        // recipient joining is instant. The op 4 triggers VOICE_SERVER_UPDATE -> VoiceClient.
        _ = Safe(_client.Rest.RingAsync(_channel));
        _ = Safe(_client.SetVoiceStateAsync(null, _channel));
        if (video) _pendingVideo = true;
        RebuildCallBanner();
    }

    void AnswerCall(ulong channel)
    {
        _ringingChannel = 0;
        _voiceStageWanted = true;
        // Joining the channel with op 4 is what actually connects you to the call.
        _ = Safe(_client.SetVoiceStateAsync(null, channel));
        _ = Safe(_client.Rest.StopRingingAsync(channel));
        RebuildCallBanner();
    }

    // A video call has to turn the camera on once the transport exists, not at click time — the
    // VoiceClient is only created after VOICE_SERVER_UPDATE comes back.
    bool _pendingVideo;

    void DeclineCall(ulong channel)
    {
        _ringingChannel = 0;
        _ = Safe(_client.Rest.StopRingingAsync(channel));
        RebuildCallBanner();
    }

    void HangUpCall(ulong channel)
    {
        _ringingChannel = 0;
        if (_client.MyVoiceChannel == channel)
            _ = Safe(_client.SetVoiceStateAsync(null, null));   // leave the call
        else
            _ = Safe(_client.Rest.StopRingingAsync(channel));
        RebuildCallBanner();
    }

    /// Leave whatever voice channel we are in and drop the stage.
    void LeaveVoice()
    {
        _voiceStageWanted = false;
        _voicePeers.Clear();
        _voiceBaselined = false;      // the next call re-baselines instead of announcing everyone
        _ = Safe(_client.SetVoiceStateAsync(null, null));
        _shell.ShowVoice(false);
        _shell.Sidebar.SetVoiceStatus(null, null, false);
        RefreshSidebar();
    }

    void BannerToggleMute()
    {
        var c = _client;
        bool mute = !c.SelfMute;
        _ = Safe(c.SetVoiceStateAsync(c.MyVoiceGuild, c.MyVoiceChannel, mute, c.SelfDeaf));
        if (VoiceClient.Current is { } v) v.SetMuted(mute);
        Sfx.Voice(mute ? "muted" : "non-muted");
        RebuildCallBanner();
    }

    void BannerToggleDeaf()
    {
        var c = _client;
        bool on = !c.SelfDeaf;
        _ = Safe(c.SetVoiceStateAsync(c.MyVoiceGuild, c.MyVoiceChannel, on || c.SelfMute, on));
        if (VoiceClient.Current is { } v) v.SetDeafened(on);
        Sfx.Voice(on ? "deaf" : "non-deaf");
        RebuildCallBanner();
    }

    void BannerToggleVideo()
    {
        // The camera button toggles our broadcast (op 12 active) + capture. It ALSO has to flip
        // self_video in our voice state on the main gateway: op 12 only tells the SFU which ssrcs
        // to relay, while every other client decides whether to render a video tile for us from
        // self_video. Without it our camera streamed to nobody.
        if (VoiceClient.Current is not { } v) return;
        bool on = !v.VideoOn;
        v.SetVideoEnabled(on);
        _ = Safe(_client.SetSelfVideoAsync(v.VideoOn));   // read back: the camera may have failed to open
        RebuildCallBanner();
        RefreshVoiceUi();
    }

    void BannerToggleScreen()
    {
        // The screenshare button is independent from the camera, exactly like Discord — and unlike
        // the camera it is a whole separate Go Live connection, so it goes through the main gateway
        // (op 18/19) rather than the voice one.
        if (_client.MyVoiceChannel == null) return;
        if (StreamClient.Current != null || _client.ActiveStreamKey != null)
        {
            _ = Safe(StreamClient.StopAsync());
            _ = Safe(_client.StopGoLiveAsync());
        }
        else _ = Safe(_client.GoLiveAsync());
        RebuildCallBanner();
        RefreshVoiceUi();
    }

    // Whose screen share we are currently rendering, so its tile can be cleared when it stops.
    ulong _watchingUser;
    readonly System.Windows.Forms.Timer _streamPing = new() { Interval = 20000 };

    async Task WatchStreamAsync(ulong owner, VoiceServerInfo info)
    {
        try
        {
            await StreamClient.WatchAsync(owner, info, _client.StreamAltServerId);
            _watchingUser = owner;
            if (StreamClient.Watcher is { } w)
                w.VideoFrame += (uid, jpeg) => Post(() =>
                    // Their share gets its own tile beside their camera, not instead of it.
                    _shell.Voice.SetScreenFrame(uid != 0 ? uid : owner, jpeg.Length == 0 ? null : jpeg));
            // op 21 keepalive: the gateway retires a viewer that stops pinging.
            _streamPing.Tick -= StreamPingTick;
            _streamPing.Tick += StreamPingTick;
            _streamPing.Start();
            Post(RefreshVoiceUi);
        }
        catch (Exception e) { Log.Write("voice", "stream watch: " + e.Message); }
    }

    void StreamPingTick(object? s, EventArgs e)
    {
        if (_client.WatchingStreamKey == null) { _streamPing.Stop(); return; }
        _ = Safe(_client.StreamPingAsync());
    }

    async Task ConnectStreamAsync(VoiceServerInfo info)
    {
        try
        {
            await StreamClient.StartAsync(info, _client.StreamAltServerId);
            if (StreamClient.Current is { } s)
            {
                // Our own share goes to the SCREEN tile — routing it to the camera tile is what
                // made the two feeds fight over one slot.
                s.SelfVideoFrame += jpeg => Post(() =>
                {
                    _shell.Call.SetSelfFrame(jpeg);
                    _shell.Voice.SetScreenFrame(_client.CurrentUser?.Id ?? 0, jpeg);
                });
                // The stream connection dropping (or the capture failing to open) must put the
                // button back and tell the gateway to retire the stream key, or the next click
                // tries to stop a share that is already gone.
                s.Ended += () => Post(() =>
                {
                    _ = Safe(StreamClient.StopAsync());
                    _ = Safe(_client.StopGoLiveAsync());
                    RebuildCallBanner();
                    RefreshVoiceUi();
                });
            }
            Post(() => { RebuildCallBanner(); RefreshVoiceUi(); });
        }
        catch (Exception e) { Log.Write("voice", "stream connect: " + e.Message); }
    }

    void RebuildCallBanner()
    {
        var me = _client.CurrentUser?.Id ?? 0;
        var inc = _client.IncomingCall;
        if (inc != null)
        {
            var u = _client.DmById.GetValueOrDefault(inc.ChannelId)?.Recipient;
            _shell.Call.Show(CallBanner.State.Incoming, inc.ChannelId,
                             u?.DisplayName ?? "Someone", u?.GetAvatarUrl(128));
            Sfx.Loop("incoming-ring");
            return;
        }
        // Ring back only until the transport is up — once we are actually connected the call is
        // live even if nobody has answered, and Discord drops the tone at that point.
        if (_ringingChannel != 0 && _client.GetCall(_ringingChannel) != null)
        {
            var u = _client.DmById.GetValueOrDefault(_ringingChannel)?.Recipient;
            _shell.Call.Show(CallBanner.State.Ringing, _ringingChannel,
                             u?.DisplayName ?? "Someone", u?.GetAvatarUrl(128), _callIsVideo);
            var call = _client.GetCall(_ringingChannel);
            bool alone = call != null && call.Participants.All(p => p == me);
            if (alone) Sfx.Loop("outgoing-ring"); else Sfx.StopLoop();
            return;
        }
        // Every other path — answered, declined, hung up, connected — is silence.
        Sfx.StopLoop();
        // Once connected the overlay gets out of the way: the call runs on the voice stage, which
        // leaves the rail, the sidebar and the channel list live underneath it. Dimming the whole
        // window for the duration is what made a connected DM call impossible to actually use.
        _shell.Call.Hide();
    }

    // ── video frames ────────────────────────────────────────────────────────────────────────────
    // VoiceClient decodes frames on the UDP receive loop; every frame lands here on the UI thread
    // and is handed to whichever view is showing the call (banner for DM calls, stage for guild VC).
    void WireVideoFrames()
    {
        var v = VoiceClient.Current;
        if (v == null) return;
        v.VideoFrame += (uid, jpeg) => Post(() =>
        {
            // An empty payload is the "camera off" clear signal from VoiceClient.
            byte[]? frame = jpeg.Length == 0 ? null : jpeg;
            _shell.Call.SetPeerFrame(frame);
            _shell.Voice.SetVideoFrame(uid, frame);
            // A peer camera coming on turns the call into a video call (Discord's banner flips
            // its title the same way); the frame handler is the only signal we get.
            if (frame != null && !_callIsVideo)
            {
                _callIsVideo = true;
                RebuildCallBanner();
            }
        });
        v.SelfVideoFrame += jpeg => Post(() =>
        {
            _shell.Call.SetSelfFrame(jpeg);
            _shell.Voice.SetVideoFrame(_client.CurrentUser?.Id ?? 0, jpeg);
        });
    }

    async Task ConnectVoiceAsync(VoiceServerInfo info)
    {
        try
        {
            await VoiceClient.ConnectAsync(info);
            WireVideoFrames();
            WireSpeaking();
            // "Start Video Call" asked for the camera before there was a transport to put it on.
            if (_pendingVideo)
            {
                _pendingVideo = false;
                Post(BannerToggleVideo);
            }
            Post(() => { RefreshVoiceUi(); RebuildCallBanner(); });
        }
        catch (Exception e) { Log.Write("voice", "connect: " + e.Message); }
    }

    // The green ring. Fires off the audio receive loop, so it is marshalled like everything else —
    // and it fires often, which is why the views keep their own speaker set rather than having the
    // whole tile list rebuilt on each transition.
    void WireSpeaking()
    {
        if (VoiceClient.Current is not { } v) return;
        v.SpeakingChanged += (uid, on) => Post(() =>
        {
            _shell.Voice.SetSpeaking(uid, on);
            _shell.Sidebar.SetSpeaking(uid, on);
            _shell.Call.SetSpeaking(uid, on);
        });
    }

    // ── context menus ───────────────────────────────────────────────────────────────────────────
    void GuildContextMenu(GuildRail.Slot slot, Point pt)
    {
        var g = _client.GuildById.GetValueOrDefault(slot.Id);
        if (g == null) return;
        bool muted = _client.MutedGuilds.Contains(g.Id);
        ulong me = _client.CurrentUser?.Id ?? 0;
        bool owner = g.OwnerId == me;

        var items = new List<ToolStripItem>
        {
            Menu.Item("Mark As Read", () => { foreach (var c in g.Channels.Where(c => c.IsText && c.LastMessageId is > 0))
                                                  _client.MarkRead(c.Id, c.LastMessageId!.Value); RefreshUnreads(); }),
            Menu.Sep(),
            Menu.Item(muted ? "Unmute Server" : "Mute Server",
                      () => _ = Safe(_client.Rest.SetGuildMutedAsync(g.Id, !muted))),
            // Server-level notification level. The REST call existed but nothing ever reached it,
            // so "All Messages / Only @mentions / Nothing" was unreachable for a whole server.
            Menu.Sub("Notification Settings", new ToolStripItem[]
            {
                Menu.Toggle("All Messages", GuildNotify(g.Id) == 0, () => SetGuildNotify(g.Id, 0)),
                Menu.Toggle("Only @mentions", GuildNotify(g.Id) == 1, () => SetGuildNotify(g.Id, 1)),
                Menu.Toggle("Nothing", GuildNotify(g.Id) == 2, () => SetGuildNotify(g.Id, 2)),
            }),
            Menu.Item("Invite People", () => _ = CreateInvite(g)),
        };

        if (CanManage(g, null))
        {
            items.Add(Menu.Sep());
            items.Add(Menu.Item("Create Channel", () => CreateChannel(g, null, 0)));
            items.Add(Menu.Item("Create Voice Channel", () => CreateChannel(g, null, 2)));
            items.Add(Menu.Item("Create Category", () => CreateChannel(g, null, 4)));
        }

        items.Add(Menu.Sep());
        items.Add(Menu.Item("Copy Server ID", () => { try { Clipboard.SetText(g.Id.ToString()); } catch { } }));
        // The owner cannot leave their own server; Discord greys the entry out rather than
        // offering a click that always fails.
        if (!owner)
            items.Add(Menu.Item("Leave Server", () => LeaveGuild(g), danger: true));
        Menu.Show(_shell, pt, items.ToArray());
    }

    // Esc on an idle composer: mark the open channel read and snap back to present, like Discord.
    void MarkReadCurrent()
    {
        var ch = _channel;
        if (ch == 0) return;
        if (_client.MentionCount(ch) > 0 || _client.IsUnread(ch, _shell.Chat.NewestId))
        {
            _client.MarkRead(ch, _shell.Chat.NewestId);
            RefreshUnreads();
        }
        _shell.Chat.ScrollToBottom();
    }

    /// Every unmuted text channel in every guild, plus every DM — the Inbox's "Mark All as Read".
    public void MarkAllRead()
    {
        foreach (var g in _client.Guilds)
            foreach (var c in g.Channels.Where(c => c.IsText && c.LastMessageId is > 0))
                _client.MarkRead(c.Id, c.LastMessageId!.Value);
        foreach (var d in _client.DMChannels.Where(d => d.LastMessageId > 0))
            _client.MarkRead(d.Id, d.LastMessageId);
        RefreshUnreads();
    }

    /// Every unmuted channel that still has something unread, newest guild first. Drives the Inbox's
    /// Unreads tab; muted guilds and channels are excluded exactly as the sidebar excludes them.
    public IEnumerable<(ulong Guild, ulong Channel, string GuildName, string ChannelName, int Mentions)> UnreadChannels()
    {
        foreach (var g in _client.Guilds)
        {
            if (_client.MutedGuilds.Contains(g.Id)) continue;
            foreach (var c in g.Channels)
            {
                if (!c.IsText || _client.MutedChannels.Contains(c.Id)) continue;
                if (!_client.IsUnread(c.Id, c.LastMessageId)) continue;
                yield return (g.Id, c.Id, g.Name, c.Name ?? "unknown", _client.MentionCount(c.Id));
            }
        }
        foreach (var d in _client.DMChannels)
        {
            if (_client.MutedChannels.Contains(d.Id)) continue;
            if (!_client.IsUnread(d.Id, d.LastMessageId)) continue;
            yield return (0, d.Id, "Direct Messages", d.DisplayName, _client.MentionCount(d.Id));
        }
    }

    // Shift+Esc: mark every channel in the current server read.
    void MarkServerRead()
    {
        var g = _guild;
        if (g == null) return;
        foreach (var c in g.Channels.Where(c => c.IsText && c.LastMessageId is > 0))
            _client.MarkRead(c.Id, c.LastMessageId!.Value);
        RefreshUnreads();
    }

    // Copying with no acknowledgement looks like the button did nothing — and when the account
    // cannot create invites here, silence is indistinguishable from success.
    async Task CreateInvite(UserGuild g, ulong channelId = 0)
    {
        // "Invite to Channel" invites to the channel you right-clicked; the server-level entry
        // still falls back to the first channel you can actually post in.
        var ch = channelId != 0 ? g.ChannelById.GetValueOrDefault(channelId) : null;
        ch ??= g.Channels.FirstOrDefault(c => c.IsPostable);
        if (ch == null) return;
        var code = await _client.Rest.CreateInviteAsync(ch.Id);
        if (code == null) { _shell.Sidebar.FlashInvite("Couldn't create an invite here."); return; }
        try { Clipboard.SetText(code); _shell.Sidebar.FlashInvite("Invite link copied"); }
        catch { _shell.Sidebar.FlashInvite(code); }
    }

    // Right-click on a member. Everything below the separator is gated on our own permissions in
    // this guild, so a normal member sees the same short menu they see in the real client rather
    // than a row of buttons that answer 403.
    void MemberContextMenu(UserUser user, Point pt)
    {
        var g = _guild;
        bool self = user.Id == _client.CurrentUser?.Id;
        var items = new List<ToolStripItem>
        {
            Menu.Item("Profile", () => ProfileCard.Show(_shell, user, pt)),
            Menu.Item("Mention", () => _shell.Chat.Composer.Insert($"<@{user.Id}>")),
        };
        if (!self) items.Add(Menu.Item("Message", () => App.OpenDm?.Invoke(user.Id)));

        if (g != null)
        {
            ulong me = _client.CurrentUser?.Id ?? 0;
            ulong perms = g.PermissionsFor(me, null);
            bool admin = (perms & Perm.Administrator) != 0;
            bool Can(ulong p) => admin || (perms & p) != 0;

            // You can always rename yourself if the guild allows it; renaming anyone else needs
            // Manage Nicknames.
            bool canNick = self ? Can(Perm.ChangeNickname) : Can(Perm.ManageNicknames);
            var mod = new List<ToolStripItem>();
            if (canNick)
                mod.Add(Menu.Item("Change Nickname", () => ChangeNickname(g, user)));
            if (!self && Can(Perm.ModerateMembers))
                mod.Add(Menu.Item("Timeout", () => TimeoutMember(g, user)));
            if (!self && Can(Perm.KickMembers))
                mod.Add(Menu.Item($"Kick {user.DisplayName}", () => KickMember(g, user), danger: true));
            if (!self && Can(Perm.BanMembers))
                mod.Add(Menu.Item($"Ban {user.DisplayName}", () => BanMember(g, user), danger: true));

            if (Can(Perm.ManageRoles) && RoleMenu(g, user) is { } roles) mod.Add(roles);

            if (mod.Count > 0) { items.Add(Menu.Sep()); items.AddRange(mod); }
        }

        items.Add(Menu.Sep());
        items.Add(Menu.Item("Copy User ID", () => { try { Clipboard.SetText(user.Id.ToString()); } catch { } }));
        Menu.Show(_shell, pt, items.ToArray());
    }

    // Discord's role hierarchy: you may only grant or take a role that sits *below* your own highest
    // one, and @everyone is never assignable. Offering the rest would just produce 403s.
    ToolStripMenuItem? RoleMenu(UserGuild g, UserUser user)
    {
        ulong me = _client.CurrentUser?.Id ?? 0;
        bool admin = (g.PermissionsFor(me, null) & Perm.Administrator) != 0;
        var mine = g.GetMember(me)?.RoleIds ?? new List<ulong>();
        int ceiling = admin
            ? int.MaxValue
            : mine.Select(r => g.RoleById.GetValueOrDefault(r)?.Position ?? 0).DefaultIfEmpty(0).Max();

        var has = g.GetMember(user.Id)?.RoleIds ?? new List<ulong>();
        var assignable = g.Roles
            .Where(r => r.Id != g.Id && r.Position < ceiling)
            .OrderByDescending(r => r.Position)
            .ToList();
        if (assignable.Count == 0) return null;

        return Menu.Sub("Roles", assignable.Select(r =>
            Menu.Toggle(r.Name, has.Contains(r.Id), () =>
                _ = Moderate(() => _client.Rest.SetMemberRoleAsync(g.Id, user.Id, r.Id, !has.Contains(r.Id)),
                             (has.Contains(r.Id) ? "Removed " : "Added ") + r.Name))));
    }

    void ChangeNickname(UserGuild g, UserUser user)
    {
        var current = g.GetMember(user.Id)?.Nick ?? "";
        var nick = Prompt.Ask(_shell, "Change Nickname",
                              $"Enter a new nickname for {user.DisplayName}.", current, "Save");
        if (nick == null) return;
        _ = Moderate(() => _client.Rest.SetNicknameAsync(g.Id, user.Id, nick), "Nickname updated");
    }

    void KickMember(UserGuild g, UserUser user)
    {
        if (Prompt.Ask(_shell, $"Kick {user.DisplayName}",
                       $"{user.DisplayName} will be removed from {g.Name}. They can rejoin with a new invite.",
                       null, "Kick", danger: true) == null) return;
        _ = Moderate(() => _client.Rest.KickAsync(g.Id, user.Id), $"Kicked {user.DisplayName}");
    }

    void BanMember(UserGuild g, UserUser user)
    {
        if (Prompt.Ask(_shell, $"Ban {user.DisplayName}",
                       $"{user.DisplayName} will be permanently banned from {g.Name}.",
                       null, "Ban", danger: true) == null) return;
        _ = Moderate(() => _client.Rest.BanAsync(g.Id, user.Id), $"Banned {user.DisplayName}");
    }

    void TimeoutMember(UserGuild g, UserUser user)
    {
        var mins = Prompt.Ask(_shell, "Timeout",
                              $"How many minutes should {user.DisplayName} be timed out for? Discord caps this at 28 days; 0 lifts it.",
                              "60", "Apply");
        if (mins == null) return;
        if (!int.TryParse(mins.Trim(), out var m) || m < 0)
        {
            Toast.Show("Timeout", "Enter a whole number of minutes.", null, 0, 0);
            return;
        }
        TimeSpan? d = m == 0 ? null : TimeSpan.FromMinutes(Math.Min(m, 28 * 24 * 60));
        _ = Moderate(() => _client.Rest.TimeoutAsync(g.Id, user.Id, d),
                     m == 0 ? "Timeout removed" : $"Timed out for {m} min");
    }

    // Every moderation call answers null on success or Discord's own message on failure, so the
    // failure text is worth surfacing verbatim — "Missing Permissions" tells you far more than a
    // generic "that didn't work".
    async Task Moderate(Func<Task<string?>> act, string ok)
    {
        string? err;
        try { err = await act(); }
        catch (Exception e) { err = e.Message; }
        _shell.Sidebar.FlashInvite(err ?? ok);
    }

    void ChannelContextMenu(ChannelSidebar.Entry e, Point pt)
    {
        bool isDm = e.Kind is ChannelSidebar.Kind.Dm or ChannelSidebar.Kind.GroupDm;
        var items = new List<ToolStripItem>();
        if (isDm)
        {
            var dm = _client.DmById.GetValueOrDefault(e.Id);
            items.Add(Menu.Item("Mark As Read", () =>
            {
                if (dm?.LastMessageId is { } l) { _client.MarkRead(e.Id, l); RefreshUnreads(); }
            }));
            items.Add(Menu.Sep());
            if (dm?.Type == 3)
            {
                // A group has the extras a 1:1 does not: a name, a roster you can add to, and
                // "leave" rather than "close".
                items.Add(Menu.Item("Rename Group", () => RenameGroup(dm)));
                items.Add(Menu.Item("Add Friends", () => AddToGroup(dm)));
                items.Add(Menu.Sep());
                items.Add(Menu.Item("Leave Group", () => _ = CloseDm(e.Id), danger: true));
            }
            else items.Add(Menu.Item("Close DM", () => _ = CloseDm(e.Id), danger: true));
        }
        else
        {
            var ch = _guild?.ChannelById.GetValueOrDefault(e.Id);
            bool muted = _client.MutedChannels.Contains(e.Id);
            bool isVoice = ch?.IsVoice == true;
            bool isCategory = e.Kind == ChannelSidebar.Kind.Category;

            bool isThread = e.Kind == ChannelSidebar.Kind.Thread;
            var th = isThread ? _guild?.ThreadById.GetValueOrDefault(e.Id) : null;

            items.Add(Menu.Item("Mark As Read", () =>
            {
                if (isCategory) MarkCategoryRead(e.Id);
                else if (isThread && th?.LastMessageId is { } tl) { _client.MarkRead(e.Id, tl); RefreshUnreads(); }
                else if (ch?.LastMessageId is { } l) { _client.MarkRead(e.Id, l); RefreshUnreads(); }
            }));
            items.Add(Menu.Sep());

            string link = $"https://discord.com/channels/{_guild?.Id}/{e.Id}";
            if (isCategory)
            {
                // The live client's category menu: collapse-all, then mute, then the levels.
                items.Add(Menu.Item("Collapse All Categories", () => _shell.Sidebar.CollapseAll(true)));
                items.Add(Menu.Item("Expand All Categories", () => _shell.Sidebar.CollapseAll(false)));
            }
            else if (isThread)
            {
                // A thread cannot be invited to; leaving it is the entry that belongs here.
                items.Add(Menu.Item("Copy Link", () => TryCopy(link)));
                items.Add(Menu.Item("Leave Thread", () => _ = LeaveThread(e.Id)));
            }
            else
            {
                items.Add(Menu.Item("Invite to Channel", () => _ = CreateInvite(_guild!, e.Id)));
                items.Add(Menu.Item("Copy Link", () => TryCopy(link)));
            }

            if (isVoice)
                items.Add(Menu.Item(_client.MyVoiceChannel == e.Id ? "Disconnect" : "Connect",
                                    () => ToggleVoice(e.Id), icon: Icons.Speaker));

            items.Add(Menu.Item(muted ? (isCategory ? "Unmute Category" : "Unmute Channel")
                                      : (isCategory ? "Mute Category" : "Mute Channel"),
                                () => _ = Safe(_client.Rest.SetChannelMutedAsync(_guild?.Id ?? 0, e.Id, !muted))));

            // Per-channel notification override, which the live client puts right here rather than
            // only behind the header's bell.
            int lvl = _client.ChannelNotifyLevels.GetValueOrDefault(e.Id, 3);
            items.Add(Menu.Sub("Notification Settings", new ToolStripItem[]
            {
                Menu.Toggle("Use Server Default", lvl == 3, () => SetChannelNotify(e.Id, 3)),
                Menu.Toggle("All Messages", lvl == 0, () => SetChannelNotify(e.Id, 0)),
                Menu.Toggle("Only @mentions", lvl == 1, () => SetChannelNotify(e.Id, 1)),
                Menu.Toggle("Nothing", lvl == 2, () => SetChannelNotify(e.Id, 2)),
            }));

            // Management, gated on the permission for THIS channel — offering it to everyone just
            // produces 403s, and Discord hides the entries the same way.
            if (_guild is { } g && CanManage(g, ch))
            {
                var admin = new List<ToolStripItem>();
                if (isCategory)
                {
                    admin.Add(Menu.Item("Create Channel", () => CreateChannel(g, e.Id, 0)));
                    admin.Add(Menu.Item("Create Voice Channel", () => CreateChannel(g, e.Id, 2)));
                }
                admin.Add(Menu.Item(isCategory ? "Edit Category" : "Rename Channel",
                                    () => RenameChannel(e.Id, e.Name)));
                if (!isVoice && !isCategory)
                    admin.Add(Menu.Item("Edit Topic", () => EditTopic(e.Id, ch?.Topic)));
                admin.Add(Menu.Item(isCategory ? "Delete Category" : "Delete Channel",
                                    () => DeleteChannel(e.Id, e.Name, isCategory), danger: true));
                items.Add(Menu.Sep());
                items.AddRange(admin);
            }
        }
        items.Add(Menu.Item("Copy Channel ID", () => { try { Clipboard.SetText(e.Id.ToString()); } catch { } }));
        Menu.Show(_shell, pt, items.ToArray());
    }

    static void TryCopy(string s) { try { Clipboard.SetText(s); } catch { } }

    /// Alt+Up / Alt+Down. Walks the *visible* rows the sidebar is showing, skipping the ones that
    /// are not destinations (category headers, voice members), so it lands where a click would.
    void StepChannel(int dir)
    {
        var rows = (_guild != null ? BuildTree(_guild) : BuildDmList())
                   .Where(r => r.Kind is ChannelSidebar.Kind.Text or ChannelSidebar.Kind.Voice
                                      or ChannelSidebar.Kind.Announcement or ChannelSidebar.Kind.Forum
                                      or ChannelSidebar.Kind.Thread or ChannelSidebar.Kind.Dm
                                      or ChannelSidebar.Kind.GroupDm)
                   .ToList();
        if (rows.Count == 0) return;
        int at = rows.FindIndex(r => r.Id == _channel);
        int next = at < 0 ? 0 : ((at + dir) % rows.Count + rows.Count) % rows.Count;
        _shell.Sidebar.SelectedChannel = rows[next].Id;
        _shell.Sidebar.Invalidate();
        _ = OpenChannel(rows[next].Id);
    }

    /// Ctrl+Alt+Up / Down, wrapping through Home the way the real rail does.
    void StepGuild(int dir)
    {
        var ids = new List<ulong?> { null };
        ids.AddRange(_client.Guilds.Select(g => (ulong?)g.Id));
        int at = ids.FindIndex(x => x == _guild?.Id);
        if (at < 0) at = 0;
        int next = ((at + dir) % ids.Count + ids.Count) % ids.Count;
        _shell.Rail.Select(ids[next]);
        PickGuild(ids[next]);
    }

    /// The "+" beside Direct Messages: pick friends, then open a group with them. Discord's own
    /// flow is a multi-select dialog; a checkable menu is the same choice in far less chrome.
    void NewGroup()
    {
        var friends = _client.Relationships.Where(r => r.Type == 1 && r.User != null)
                             .Select(r => r.User!)
                             .OrderBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase)
                             .ToList();
        if (friends.Count == 0) { _shell.Sidebar.FlashInvite("Add a friend first."); return; }

        var picked = new HashSet<ulong>();
        var items = new List<ToolStripItem>();
        foreach (var u in friends.Take(20))
        {
            var user = u;
            ToolStripMenuItem? entry = null;
            entry = Menu.Toggle(user.DisplayName, false, () =>
            {
                // Keep the menu open while choosing: a group needs more than one pick.
                if (!picked.Add(user.Id)) picked.Remove(user.Id);
                if (entry != null) entry.Checked = picked.Contains(user.Id);
            });
            entry.CheckOnClick = false;
            items.Add(entry);
        }
        items.Add(Menu.Sep());
        items.Add(Menu.Item("Create Group", () =>
        {
            if (picked.Count == 0) { _shell.Sidebar.FlashInvite("Pick at least one friend."); return; }
            _ = CreateGroup(picked.ToList());
        }));
        Menu.Show(_shell, Cursor.Position, items.ToArray());
    }

    async Task CreateGroup(List<ulong> ids)
    {
        try
        {
            var dm = await _client.Rest.CreateGroupAsync(ids);
            Post(() =>
            {
                // CHANNEL_CREATE also arrives over the gateway; inserting here just means the row
                // is there the instant the call returns.
                if (!_client.DmById.ContainsKey(dm.Id)) { _client.DMChannels.Insert(0, dm); _client.DmById[dm.Id] = dm; }
                PickGuild(null);
                _shell.Sidebar.SelectedChannel = dm.Id;
                _shell.Sidebar.Refresh(BuildDmList());
                _ = OpenChannel(dm.Id);
            });
        }
        catch (Exception e) { _shell.Sidebar.FlashInvite(e.Message); }
    }

    void RenameGroup(UserDMChannel dm)
    {
        var name = Prompt.Ask(_shell, "Rename Group", "Leave it empty to use the members' names.",
                              dm.GroupName ?? "", "Save");
        if (name == null) return;
        _ = Admin(() => _client.Rest.RenameGroupAsync(dm.Id, name.Trim()), "Group renamed");
    }

    /// Add a friend to a group. Picks from the friend list rather than asking for an id — the id
    /// is not something anyone has to hand.
    void AddToGroup(UserDMChannel dm)
    {
        var friends = _client.Relationships.Where(r => r.Type == 1 && r.User != null)
                             .Select(r => r.User!)
                             .Where(u => dm.Recipients.All(x => x.Id != u.Id))
                             .OrderBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase)
                             .ToList();
        if (friends.Count == 0) { _shell.Sidebar.FlashInvite("No friends left to add."); return; }
        Menu.Show(_shell, Cursor.Position, friends.Take(20).Select(u =>
            (ToolStripItem)Menu.Item(u.DisplayName,
                () => _ = Admin(() => _client.Rest.AddDmRecipientAsync(dm.Id, u.Id), "Added " + u.DisplayName)))
            .ToArray());
    }

    async Task LeaveThread(ulong id)
    {
        await Safe(_client.Rest.LeaveThreadAsync(id));
        // The gateway confirms with THREAD_MEMBER_UPDATE, but the sidebar should not sit there
        // still listing it until that lands.
        if (_channel == id) Post(() => { _channel = 0; RefreshSidebar(); });
        else Post(RefreshSidebar);
    }

    void SetChannelNotify(ulong id, int level) =>
        _ = Safe(_client.Rest.SetChannelNotifyLevelAsync(_guild?.Id ?? 0, id, level));

    /// Mark every channel under a category read — what the live client's category "Mark As Read" does.
    void MarkCategoryRead(ulong categoryId)
    {
        if (_guild is not { } g) return;
        foreach (var c in g.Channels.Where(c => c.ParentId == categoryId && c.IsText && c.LastMessageId is > 0))
            _client.MarkRead(c.Id, c.LastMessageId!.Value);
        RefreshUnreads();
    }

    int GuildNotify(ulong id) => _client.GuildNotifyLevels.GetValueOrDefault(id, 0);

    void SetGuildNotify(ulong id, int level) =>
        _ = Safe(_client.Rest.SetGuildNotifyLevelAsync(id, level));

    void LeaveGuild(UserGuild g)
    {
        if (Prompt.Ask(_shell, $"Leave {g.Name}",
                       $"You will not be able to rejoin {g.Name} unless you are re-invited.",
                       null, "Leave", danger: true) == null) return;
        _ = Safe(_client.Rest.LeaveGuildAsync(g.Id));
    }

    // ── channel management ──────────────────────────────────────────────────────────────────────
    bool CanManage(UserGuild g, UserChannelData? ch)
    {
        ulong me = _client.CurrentUser?.Id ?? 0;
        if (me == 0) return false;
        ulong p = g.PermissionsFor(me, ch);
        return (p & (Perm.ManageChannels | Perm.Administrator)) != 0;
    }

    void CreateChannel(UserGuild g, ulong? parentId, int type)
    {
        var name = Prompt.Ask(_shell, type == 2 ? "Create Voice Channel" : "Create Text Channel",
                              "Channel names are lowercase, without spaces.", "new-channel", "Create");
        if (string.IsNullOrWhiteSpace(name)) return;
        // Discord slugs a text channel's name server-side anyway; doing it here means the name in
        // the prompt is the name you get.
        var clean = type == 2 ? name.Trim()
                  : name.Trim().ToLowerInvariant().Replace(' ', '-');
        _ = Admin(() => _client.Rest.CreateChannelAsync(g.Id, clean, type, parentId), "Channel created");
    }

    void RenameChannel(ulong id, string current)
    {
        var name = Prompt.Ask(_shell, "Rename", "Enter a new name.", current, "Save");
        if (string.IsNullOrWhiteSpace(name)) return;
        _ = Admin(() => _client.Rest.ModifyChannelAsync(id, new { name = name.Trim() }), "Renamed");
    }

    void EditTopic(ulong id, string? current)
    {
        var topic = Prompt.Ask(_shell, "Edit Topic", "Shown beside the channel name in the header.",
                               current ?? "", "Save");
        if (topic == null) return;
        _ = Admin(() => _client.Rest.ModifyChannelAsync(id, new { topic = topic.Trim() }), "Topic updated");
    }

    void DeleteChannel(ulong id, string name, bool isCategory)
    {
        if (Prompt.Ask(_shell, $"Delete {(isCategory ? "Category" : "Channel")}",
                       $"\"{name}\" will be deleted permanently. This cannot be undone.",
                       null, "Delete", danger: true) == null) return;
        _ = Admin(() => _client.Rest.DeleteChannelAsync(id), "Deleted");
    }

    /// The channel REST calls throw on failure rather than returning Discord's message, so this
    /// mirrors Moderate() and surfaces whatever came back in the sidebar's flash strip.
    async Task Admin(Func<Task> act, string ok)
    {
        string? err = null;
        try { await act(); }
        catch (Exception e) { err = Trim(e.Message); }
        _shell.Sidebar.FlashInvite(err ?? ok);

        // Discord's error bodies are JSON; the useful part is the "message" field.
        static string Trim(string s)
        {
            try
            {
                using var d = System.Text.Json.JsonDocument.Parse(s);
                if (d.RootElement.TryGetProperty("message", out var m) && m.GetString() is { Length: > 0 } t)
                    return t;
            }
            catch { }
            return s.Length > 80 ? s[..80] : s;
        }
    }

    // Join/leave a voice channel with op 4.
    void ToggleVoice(ulong channel)
    {
        var c = _client;
        bool on = c.MyVoiceChannel != channel;
        _ = Safe(c.SetVoiceStateAsync(_guild?.Id, on ? channel : null, c.SelfMute, c.SelfDeaf));
        RefreshSidebar();
        RefreshVoiceUi();
    }

    /// Rebuild the voice stage and the sidebar's connected strip from the gateway's voice states.
    /// Called on every VOICE_STATE_UPDATE, so it must be cheap and must not assume we are connected.
    /// Right-click a participant on the stage: their playback volume, plus the moderation actions
    /// when we hold the permissions for them. Discord's own tile menu, minus what this client
    /// cannot do.
    void VoiceTileMenu(ulong userId, Point pt)
    {
        ulong me = _client.CurrentUser?.Id ?? 0;
        var items = new List<ToolStripItem>();

        if (userId != me)
        {
            float cur = Prefs.UserVolume(userId);
            ToolStripMenuItem Vol(string label, float g) =>
                Menu.Toggle(label, Math.Abs(cur - g) < 0.01f, () =>
                {
                    Prefs.SetUserVolume(userId, g);
                    _shell.Sidebar.FlashInvite($"Volume {(int)(g * 100)}%");
                });
            items.Add(Menu.Sub("User Volume", new ToolStripItem[]
            {
                Vol("0%  (mute)", 0f), Vol("50%", 0.5f), Vol("100%", 1f),
                Vol("150%", 1.5f), Vol("200%", 2f),
            }));
        }

        var user = _guild?.GetMember(userId)?.User
                ?? _client.DmById.GetValueOrDefault(_client.MyVoiceChannel ?? 0)?.Recipients
                       .FirstOrDefault(r => r.Id == userId);
        if (user != null)
        {
            items.Add(Menu.Sep());
            items.Add(Menu.Item("Profile", () => ProfileCard.Show(_shell, user, pt)));
            if (userId != me) items.Add(Menu.Item("Message", () => App.OpenDm?.Invoke(userId)));
        }
        items.Add(Menu.Sep());
        items.Add(Menu.Item("Copy User ID", () => TryCopy(userId.ToString())));
        Menu.Show(_shell, pt, items.ToArray());
    }

    // Who was in our channel last time, so a join or a leave can be told apart from a mute.
    readonly HashSet<ulong> _voicePeers = new();

    /// The chirps Discord plays when someone else joins or leaves the call you are in. Only for
    /// other people — your own arrival is not announced to you.
    void AnnounceVoiceMembers()
    {
        var c = _client;
        ulong me = c.CurrentUser?.Id ?? 0;
        if (c.MyVoiceChannel is not { } vc || VoiceClient.Current == null)
        {
            _voicePeers.Clear();
            return;
        }

        var now = new HashSet<ulong>();
        if (c.MyVoiceGuild is { } gid && c.GuildById.GetValueOrDefault(gid) is { } g)
            foreach (var vs in g.VoiceIn(vc)) { if (vs.UserId != me) now.Add(vs.UserId); }
        else if (c.GetCall(vc) is { } call)
            foreach (var p in call.Participants) { if (p != me) now.Add(p); }

        // First rebuild after connecting is the baseline — otherwise joining a busy channel
        // plays a join sound for everyone already sitting in it.
        if (_voicePeers.Count == 0 && now.Count > 0 && !_voiceBaselined)
        {
            _voiceBaselined = true;
            _voicePeers.UnionWith(now);
            return;
        }
        _voiceBaselined = true;

        foreach (var uid in now) if (!_voicePeers.Contains(uid)) Sfx.Voice("incoming-user");
        foreach (var uid in _voicePeers) if (!now.Contains(uid)) Sfx.Voice("user-leave");
        _voicePeers.Clear();
        _voicePeers.UnionWith(now);
    }

    bool _voiceBaselined;

    void RefreshVoiceUi()
    {
        var c = _client;
        if (c.MyVoiceChannel is not { } vc)
        {
            _shell.Sidebar.SetVoiceStatus(null, null, false);
            if (_shell.Voice.Visible) _shell.ShowVoice(false);
            return;
        }

        var guild = c.MyVoiceGuild is { } gid ? c.GuildById.GetValueOrDefault(gid) : null;
        var ch = guild?.ChannelById.GetValueOrDefault(vc);
        string chName = ch?.Name ?? c.DmById.GetValueOrDefault(vc)?.DisplayName ?? "Voice";
        _shell.Sidebar.SetVoiceStatus(chName, guild?.Name, VoiceClient.Current == null);

        // Our camera stopped: drop the last frame so our tile falls back to the avatar instead of
        // freezing on whatever it saw last. (The share has its own tile and its own frame.)
        ulong me = c.CurrentUser?.Id ?? 0;
        if (!(VoiceClient.Current?.VideoOn ?? false)) _shell.Voice.SetVideoFrame(me, null);
        if (!(StreamClient.Current?.IsLive ?? false)) _shell.Voice.SetScreenFrame(me, null);

        // Two tiles for someone who is both on camera and sharing, exactly like Discord: the
        // person, then their screen. One tile fed by both feeds just flickers between them.
        var tiles = new List<VoiceView.Tile>();

        void AddTiles(ulong uid, string name, string? avatar,
                      bool muted, bool deaf, bool stream, bool video)
        {
            tiles.Add(new VoiceView.Tile(uid, name, avatar, muted, deaf, stream, video));
            if (stream)
                tiles.Add(new VoiceView.Tile(uid, name + "'s screen", null,
                                             false, false, true, false, Screen: true));
        }

        if (guild != null)
        {
            foreach (var vs in guild.VoiceIn(vc))
            {
                var m = guild.GetMember(vs.UserId);
                AddTiles(vs.UserId, m?.DisplayName ?? c.NameOf(vs.UserId), m?.User?.GetAvatarUrl(64),
                         vs.SelfMute || vs.Mute, vs.SelfDeaf || vs.Deaf, vs.SelfStream, vs.SelfVideo);
            }
        }
        else
        {
            // A DM or group call is the same stage, built from the call's participants instead of a
            // guild's voice states. Before this, a DM call had only the modal overlay — which dimmed
            // the whole window, so there was no way to read the conversation, switch channels, or do
            // anything with the call except hang it up.
            var call = c.GetCall(vc);
            var dmc = c.DmById.GetValueOrDefault(vc);
            var ids = call?.Participants.ToList() ?? new List<ulong>();
            if (ids.Count == 0 && VoiceClient.Current != null) ids.Add(me);   // alone on the line

            UserUser? Who(ulong uid) => uid == me ? c.CurrentUser?.AsUser()
                : dmc?.Recipients.FirstOrDefault(r => r.Id == uid) ?? dmc?.Recipient;

            foreach (var uid in ids)
            {
                var st = call?.States.GetValueOrDefault(uid);
                var u = Who(uid);
                AddTiles(uid, u?.DisplayName ?? c.NameOf(uid), u?.GetAvatarUrl(64),
                         st?.SelfMute ?? false, st?.SelfDeaf ?? false,
                         st?.SelfStream ?? false, st?.SelfVideo ?? false);
            }

            // Everyone still being rung gets a dimmed tile. Without this an outgoing call showed
            // only our own face, with no sign of who was being called.
            var pending = call?.Ringing.Where(r => !ids.Contains(r)).ToList() ?? new List<ulong>();
            if (pending.Count == 0 && ids.Count == 1 && ids[0] == me && dmc is { Type: 1, Recipient: { } r0 })
                pending.Add(r0.Id);   // 1:1 call placed before the server echoed a ringing list
            foreach (var uid in pending)
            {
                var u = Who(uid);
                tiles.Add(new VoiceView.Tile(uid, u?.DisplayName ?? c.NameOf(uid), u?.GetAvatarUrl(64),
                                             false, false, false, false, Pending: true));
            }
        }

        _shell.Voice.Set(chName, guild?.Name ?? "Direct Message", tiles, c.SelfMute, c.SelfDeaf,
                         VoiceClient.Current?.VideoOn ?? false,
                         StreamClient.Current?.IsLive ?? false);
        if (_voiceStageWanted && !_shell.Voice.Visible) _shell.ShowVoice(true);
    }

    // Clicking a voice channel puts you on the stage; opening its text chat from there clears this
    // so the message list stays put until you click the channel again.
    bool _voiceStageWanted;

    async Task CloseDm(ulong id)
    {
        // The server confirms with CHANNEL_DELETE, which removes the DM on the gateway thread and
        // fires DmClosed; the UI never mutates the client's DM lists itself.
        try { await _client.Rest.CloseChannelAsync(id); }
        catch (Exception e) { Log.Write("dm", e.Message); }
    }

    // Runs on the UI thread after the gateway has already removed the DM from its lists.
    void OnDmClosed(ulong id)
    {
        // RefreshUnreads also re-totals the rail's home-mentions, which change when a DM closes.
        if (_channel == id) { _channel = 0; ShowHome(); }
        else RefreshUnreads();
    }
}
