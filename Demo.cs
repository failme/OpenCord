using System.Drawing;

namespace ClaudeScord;

// Placeholder content so the UI can be driven — and screenshotted — with no token and no network.
//
// The predecessor could only be looked at while logged in, which made every visual change a manual
// round trip through a real account and made layout regressions easy to miss. `--demo` is the cheap
// fix: static lists, no fixtures, no mock client.
//
// The message bodies deliberately exercise the awkward cases: wrapping, every inline style, a
// spoiler, a code span, a link, mentions, and a message long enough to need two lines.
static class Demo
{
    public static void Populate(Shell s)
    {
        App.ResolveUserMention = _ => ("nathan", Theme.BrandText);
        App.ResolveRoleMention = _ => ("moderators", Color.FromArgb(235, 69, 158));
        App.ResolveChannelName = _ => "rules";

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
            DemoPoll(10, 101, "nathan", DateTimeOffset.Now.AddMinutes(-60)),
            DemoPoll(11, 101, "nathan", DateTimeOffset.Now.AddMinutes(-55), voted: true),
            DemoButtons(12, 102, "clyde", DateTimeOffset.Now.AddMinutes(-40)),
        });

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
}
