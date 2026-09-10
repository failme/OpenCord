using System.Drawing;

namespace OpenCord;

// Placeholder content so the UI can be driven — and screenshotted — with no token and no network.
//
// The predecessor could only be looked at while logged in, which made every visual change a manual
// round trip through a real account and made layout regressions easy to miss. `--demo` is the cheap
// fix: static lists, no fixtures, no mock client.
//
// Three scenarios, picked to cover what a README screenshot actually wants to show: a server text
// channel (`guild`, the default), a 1:1 DM with its profile panel (`dm`), and a voice call (`voice`).
// All fake names/avatars — nobody's real account shows up in any of them.
static class Demo
{
    public static void Populate(Shell s, string scenario)
    {
        App.ResolveUserMention = _ => ("nathan", Theme.BrandText);
        App.ResolveRoleMention = _ => ("moderators", Color.FromArgb(235, 69, 158));
        App.ResolveChannelName = _ => "rules";

        switch (scenario.ToLowerInvariant())
        {
            case "dm": PopulateDm(s); break;
            case "voice": PopulateVoice(s); break;
            default: PopulateGuild(s); break;
        }
    }

    // ── guild: a server text channel, rich message content, member roster ─────────────────────────
    static void PopulateGuild(Shell s)
    {
        s.Rail.SetGuilds(new GuildRail.Slot[]
        {
            new(1, "failme2's server", Unread: true),
            new(2, "Mac Students Quest"),
            new(3, "Rust Lang"),
            new(4, "Design Review", Unread: true, Mentions: 3),
            new(5, "Woodworking"),
        });
        s.Rail.Select(1);

        s.Sidebar.SetChannels("failme2's server", new ChannelSidebar.Entry[]
        {
            new(ChannelSidebar.Kind.Category, 0, "Text Channels"),
            new(ChannelSidebar.Kind.Text, 10, "general"),
            new(ChannelSidebar.Kind.Thread, 30, "build-troubleshooting", Unread: true),
            new(ChannelSidebar.Kind.Thread, 31, "palette-nerds"),
            new(ChannelSidebar.Kind.Text, 11, "off-topic", Unread: true),
            new(ChannelSidebar.Kind.Text, 12, "links-and-resources"),
            new(ChannelSidebar.Kind.Text, 13, "a-deliberately-long-channel-name-to-check-ellipsis"),
            new(ChannelSidebar.Kind.Category, 0, "Voice Channels"),
            new(ChannelSidebar.Kind.Voice, 20, "General"),
            new(ChannelSidebar.Kind.Voice, 21, "AFK"),
        });
        s.Sidebar.SelectedChannel = 10;

        s.Chat.SetChannel(new ChatView.ChannelInfo(10, "general", 0));
        var t0 = DateTimeOffset.Now.AddMinutes(-95);
        UserMessage DemoMsg(ulong id, ulong authorId, string author, DateTimeOffset t, string content) =>
            new() { Id = id, ChannelId = 100, Author = new UserUser { Id = authorId, Username = author }, Timestamp = t, Content = content };
        s.Chat.SetMessages(new UserMessage[]
        {
            DemoMsg(1, 100, "failme", t0, "hey — got the new build working"),
            DemoMsg(2, 100, "failme", t0.AddSeconds(40),
                "the palette is finally right. turns out `--background-base-lower` is the chat surface, **not** `base-low`."),
            DemoMsg(3, 101, "nathan", t0.AddMinutes(3),
                "nice. did you check the hover states? those are alpha overlays so they composite differently over the rail vs the chat"),
            DemoMsg(4, 100, "failme", t0.AddMinutes(5),
                "yeah — *56,57,63* over the sidebar and *62,63,69* over chat. same rule, two results."),
            DemoMsg(5, 100, "failme", t0.AddMinutes(5).AddSeconds(20),
                "here's the thing I keep forgetting though: this is a deliberately long message so that the wrapping "
                + "logic actually gets exercised, because a chat view that looks perfect with short messages will happily "
                + "fall apart the moment somebody pastes a paragraph into it."),
            DemoMsg(6, 101, "nathan", t0.AddMinutes(12),
                "# Heading one\nand a [link to the docs](https://discord.com/developers/docs) plus <@123> and <@&456> in <#789>"),
            DemoMsg(7, 101, "nathan", t0.AddMinutes(12).AddSeconds(30),
                "the answer is ||hidden until you click it|| by the way"),
            DemoMsg(8, 102, "clyde", t0.AddMinutes(30),
                "> quoted text renders with a bar\n- and bullets work\n- like this"),
            DemoMsg(9, 100, "failme", DateTimeOffset.Now.AddMinutes(-2),
                "~~struck~~ __underlined__ and `inline code` all in one line"),
            DemoMsg(13, 101, "nathan", DateTimeOffset.Now.AddMinutes(-1),
                "here's the palette helper, for the copy button on a code block:\n"
                + "```csharp\nstatic Color Tint(Color into, Color c, float amount)\n"
                + "    => Color.FromArgb((int)(into.R + (c.R - into.R) * amount),\n"
                + "                     (int)(into.G + (c.G - into.G) * amount),\n"
                + "                     (int)(into.B + (c.B - into.B) * amount));\n```"),
            DemoPoll(10, 101, "nathan", DateTimeOffset.Now.AddMinutes(-60)),
            DemoPoll(11, 101, "nathan", DateTimeOffset.Now.AddMinutes(-55), voted: true),
            DemoButtons(12, 102, "clyde", DateTimeOffset.Now.AddMinutes(-40)),
        });

        s.Members.SetMembers(new MemberList.Entry[]
        {
            new(true, "Moderators"),
            new(false, "nathan", null, Presence.Online, Color.FromArgb(235, 69, 158), "Visual Studio Code"),
            new(false, "failme", null, Presence.Idle),
            new(true, "Online — 3"),
            new(false, "clyde", null, Presence.Dnd),
            new(false, "someone-with-a-very-long-display-name", null, Presence.Online),
            new(false, "quiet person", null, Presence.Offline),
        });
    }

    // An open poll in vote mode and one in results mode, plus a bot message with an action row
    // — the message surface that drives polls/buttons must survive the demo too.
    static UserMessage DemoPoll(ulong id, ulong authorId, string author, DateTimeOffset t, bool voted = false) =>
        new()
        {
            Id = id, ChannelId = 100, Author = new UserUser { Id = authorId, Username = author },
            Timestamp = t, Content = "",
            Poll = new UserPoll
            {
                Question = new UserPollMedia { Text = "Where should we order from?" },
                Answers =
                {
                    new UserPollAnswer { AnswerId = 1, Media = new UserPollMedia { Text = "Tacos 🌮" } },
                    new UserPollAnswer { AnswerId = 2, Media = new UserPollMedia { Text = "Pizza 🍕" } },
                    new UserPollAnswer { AnswerId = 3, Media = new UserPollMedia { Text = "Sushi 🍣" } },
                },
                Expiry = DateTimeOffset.Now.AddHours(voted ? 3 : 2),
                Results = new UserPollResults
                {
                    IsFinalized = false,
                    AnswerCounts =
                    {
                        new UserPollAnswerCount { Id = 1, Count = 12, MeVoted = voted },
                        new UserPollAnswerCount { Id = 2, Count = 5 },
                        new UserPollAnswerCount { Id = 3, Count = 3 },
                    },
                },
            },
        };

    static UserMessage DemoButtons(ulong id, ulong authorId, string author, DateTimeOffset t) =>
        new()
        {
            Id = id, ChannelId = 100, Author = new UserUser { Id = authorId, Username = author },
            Timestamp = t, Content = "Pick one:",
            Components =
            {
                new UserComponent
                {
                    Type = UserComponent.Row,
                    Components =
                    {
                        new UserComponent { Type = UserComponent.Button, Style = 1, Label = "Primary", CustomId = "a" },
                        new UserComponent { Type = UserComponent.Button, Style = 2, Label = "Secondary", CustomId = "b" },
                        new UserComponent { Type = UserComponent.Button, Style = 3, Label = "Success", CustomId = "c" },
                        new UserComponent { Type = UserComponent.Button, Style = 4, Label = "Danger", CustomId = "d" },
                        new UserComponent { Type = UserComponent.Button, Style = 5, Label = "Link", Url = "https://discord.com" },
                    },
                },
            },
        };

    // ── dm: a 1:1 conversation and its profile panel ───────────────────────────────────────────────
    static void PopulateDm(Shell s)
    {
        s.Rail.SetGuilds(new GuildRail.Slot[]
        {
            new(1, "failme2's server"),
            new(2, "Rust Lang", Unread: true),
        });
        s.Rail.Select(null);   // home

        s.Sidebar.SetChannels("Direct Messages", new ChannelSidebar.Entry[]
        {
            new(ChannelSidebar.Kind.Nav, ChannelSidebar.FriendsId, "Friends"),
            new(ChannelSidebar.Kind.Category, 1, "Direct Messages"),
            new(ChannelSidebar.Kind.Dm, 200, "river", true, Presence: Presence.Online, Subtitle: "sent you a GIF"),
            new(ChannelSidebar.Kind.Dm, 201, "nathan", Presence: Presence.Idle, Subtitle: "Visual Studio Code"),
            new(ChannelSidebar.Kind.Dm, 202, "quiet person", Presence: Presence.Offline),
            new(ChannelSidebar.Kind.GroupDm, 203, "band practice 🥁", Mentions: 2, Presence: Presence.Online),
        }, home: true);
        s.Sidebar.SelectedChannel = 200;

        s.Chat.SetChannel(new ChatView.ChannelInfo(200, "river", 1, Presence: Presence.Online));
        var t0 = DateTimeOffset.Now.AddMinutes(-30);
        UserMessage DemoMsg(ulong id, ulong authorId, string author, DateTimeOffset t, string content) =>
            new() { Id = id, ChannelId = 200, Author = new UserUser { Id = authorId, Username = author }, Timestamp = t, Content = content };
        s.Chat.SetMessages(new UserMessage[]
        {
            DemoMsg(1, 300, "river", t0, "did the icon rail ever get the squircle fix"),
            DemoMsg(2, 900, "nathan", t0.AddMinutes(1), "yep — every tile masks the same shape now, not just the selected one"),
            DemoMsg(3, 300, "river", t0.AddMinutes(2), "🎉"),
            DemoMsg(4, 300, "river", t0.AddMinutes(2).AddSeconds(15), "screenshot when you get a sec?"),
        });

        var river = new UserUser
        {
            Id = 300, Username = "river", GlobalName = "river",
            Status = "online", CustomStatus = "shipping pixels",
            PrimaryGuild = new UserPrimaryGuild { GuildId = 1, Enabled = true, Tag = "OSS" },
        };
        var profile = new UserProfile
        {
            Detail = new UserProfileDetail { Bio = "measures things instead of guessing them.", Pronouns = "they/them" },
            MutualGuilds = { new UserMutualGuild { Id = 1 } },
            MutualFriendsCount = 4,
        };
        s.Members.SetProfile(new MemberList.Profile(river, profile));
        s.ShowMembers(true);
        s.Chat.SetMembersActive(true);
    }

    // ── voice: a call in progress ───────────────────────────────────────────────────────────────────
    static void PopulateVoice(Shell s)
    {
        s.Rail.SetGuilds(new GuildRail.Slot[] { new(1, "failme2's server") });
        s.Rail.Select(1);

        s.Sidebar.SetChannels("failme2's server", new ChannelSidebar.Entry[]
        {
            new(ChannelSidebar.Kind.Category, 0, "Voice Channels"),
            new(ChannelSidebar.Kind.Voice, 20, "General"),
            new(ChannelSidebar.Kind.VoiceMember, 100, "failme"),
            new(ChannelSidebar.Kind.VoiceMember, 101, "nathan"),
            new(ChannelSidebar.Kind.VoiceMember, 102, "clyde"),
            new(ChannelSidebar.Kind.Voice, 21, "AFK"),
        });
        s.Sidebar.SelectedChannel = 20;

        s.Voice.Set("General", "failme2's server", new[]
        {
            new VoiceView.Tile(100, "failme", null, Muted: false, Deafened: false, Streaming: false, Video: false),
            new VoiceView.Tile(101, "nathan", null, Muted: true, Deafened: false, Streaming: false, Video: false),
            new VoiceView.Tile(102, "clyde", null, Muted: false, Deafened: true, Streaming: false, Video: false),
        }, muted: false, deaf: false);
        s.Voice.SetSpeaking(100, true);
        s.ShowVoice(true);
    }
}
