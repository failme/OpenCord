using System.Text.Json;
using System.Text.Json.Serialization;
using Color = System.Drawing.Color;

namespace ClaudeScord;

// What a presence dot means, decoupled from what colour it gets painted. The predecessor returned a
// Theme colour straight out of the model, which is why the protocol layer could not be compiled or
// tested on its own. Theme.Dot() does the mapping now.
enum Presence { Offline, Online, Idle, Dnd, Streaming }

// ── Permissions ──

// Only the bits this client actually reasons about. Discord's field is a 64-bit mask sent as a
// decimal string, so everything here is ulong.
static class Perm
{
    public const ulong Administrator = 1UL << 3;
    public const ulong ViewChannel = 1UL << 10;
    public const ulong SendMessages = 1UL << 11;
    public const ulong ManageMessages = 1UL << 13;
    public const ulong AddReactions = 1UL << 6;
    public const ulong ReadHistory = 1UL << 16;
    public const ulong AttachFiles = 1UL << 15;
    public const ulong Connect = 1UL << 20;
    public const ulong ManageChannels = 1UL << 4;
    public const ulong CreatePublicThreads = 1UL << 35;
    public const ulong KickMembers = 1UL << 1;
    public const ulong BanMembers = 1UL << 2;
    public const ulong ManageNicknames = 1UL << 27;
    public const ulong ManageRoles = 1UL << 28;
    public const ulong ModerateMembers = 1UL << 40;   // timeout
    public const ulong ChangeNickname = 1UL << 26;
}

class UserOverwrite
{
    public ulong Id { get; set; }
    public int Type { get; set; }          // 0 = role, 1 = member
    [JsonPropertyName("allow")] public string AllowRaw { get; set; } = "0";
    [JsonPropertyName("deny")] public string DenyRaw { get; set; } = "0";

    // Without [JsonIgnore] these collide with allow/deny — JsonOpts is case-insensitive.
    [JsonIgnore] public ulong Allow => ulong.TryParse(AllowRaw, out var v) ? v : 0;
    [JsonIgnore] public ulong Deny => ulong.TryParse(DenyRaw, out var v) ? v : 0;
}

// ── Channel types ──

class UserTextChannel
{
    public ulong Id { get; set; }
    public string Name { get; set; } = "";
    public string? Topic { get; set; }
    public ulong GuildId { get; set; }
    public int Position { get; set; }
    public ulong? CategoryId { get; set; }
    public bool IsDm { get; set; }
    public int Type { get; set; }                 // mirrors UserChannelData.Type
    public UserClient Client { get; set; } = null!;

    public Task<IReadOnlyCollection<UserMessage>> GetMessagesAsync(int limit) =>
        Client.Rest.GetMessagesAsync(Id, limit, 0, GuildId);
    public Task<UserMessage> SendMessageAsync(string text) =>
        Client.Rest.SendMessageAsync(Id, text);
    public Task<UserMessage> SendMessageReplyAsync(string text, ulong replyId) =>
        Client.Rest.SendMessageReplyAsync(Id, text, replyId);
    public Task SendFileAsync(string path, string text) =>
        Client.Rest.SendFileAsync(Id, path, text);
    public Task<IReadOnlyCollection<UserMessage>> GetPinnedMessagesAsync() =>
        Client.Rest.GetPinnedMessagesAsync(Id);
    public Task<IReadOnlyCollection<UserThreadChannel>> GetPublicArchivedThreadsAsync(int? limit, DateTimeOffset? before) =>
        Client.Rest.GetThreadsAsync(Id, limit, before);
}

class UserDMChannel
{
    public ulong Id { get; set; }
    public int Type { get; set; } = 1;            // 1 = DM, 3 = group DM
    public UserUser? Recipient { get; set; }      // first recipient (used for 1:1 DMs)
    public List<UserUser> Recipients { get; set; } = new();
    public string? GroupName { get; set; }
    public string? GroupIcon { get; set; }
    public ulong LastMessageId { get; set; }      // for recency ordering
    public UserClient Client { get; set; } = null!;

    public string DisplayName => Type == 3
        ? (!string.IsNullOrEmpty(GroupName) ? GroupName!
            : Recipients.Count > 0 ? string.Join(", ", Recipients.Select(r => r.DisplayName))
            : "Group DM")
        : (Recipient?.DisplayName ?? "Unknown");

    public string? AvatarUrl => Type == 3
        ? (GroupIcon != null ? $"https://cdn.discordapp.com/channel-icons/{Id}/{GroupIcon}.png?size=64" : null)
        : Recipient?.GetAvatarUrl(64);

    public string? LastPreview { get; set; }
    public bool PreviewFetched;                   // don't re-ask for a conversation that has none

    // Discord's DM list is one line per conversation — a second line appears only when the person
    // is doing something ("Playing X"), or for a group, its member count. Message previews are a
    // mobile-only feature and looked wrong here.
    public string Subtitle => Type == 3
        ? $"{Recipients.Count} Members"
        : Recipient?.Activity ?? "";

    /// Format a message the way the DM list does: "You: …" / "Name: …" in a group.
    public string PreviewOf(string? content, int attachments, int stickers, int embeds, ulong authorId, string authorName)
    {
        var body = Markdown.Flatten(content);
        if (body.Length == 0)
            body = attachments > 0 ? "sent an attachment"
                 : stickers > 0 ? "sent a sticker"
                 : embeds > 0 ? "shared a link"
                 : "";
        var who = authorId == Client?.CurrentUser?.Id ? "You" : authorName;
        var line = Type == 3 || authorId == Client?.CurrentUser?.Id ? $"{who}: {body}" : body;
        return line.Length > 100 ? line[..100] + "…" : line;
    }

    public Task<IReadOnlyCollection<UserMessage>> GetMessagesAsync(int limit) =>
        Client.Rest.GetMessagesAsync(Id, limit);
    public Task<UserMessage> SendMessageAsync(string text) =>
        Client.Rest.SendMessageAsync(Id, text);
    public Task<UserMessage> SendMessageReplyAsync(string text, ulong replyId) =>
        Client.Rest.SendMessageReplyAsync(Id, text, replyId);
    public Task SendFileAsync(string path, string text) =>
        Client.Rest.SendFileAsync(Id, path, text);
    public Task<IReadOnlyCollection<UserMessage>> GetPinnedMessagesAsync() =>
        Client.Rest.GetPinnedMessagesAsync(Id);
}

class UserThreadChannel
{
    public ulong Id { get; set; }
    public string Name { get; set; } = "";
    // 11 public thread, 12 private thread. The chat header and composer key off this to draw the
    // thread glyph rather than a channel hash.
    public int Type { get; set; } = 11;
    [JsonPropertyName("guild_id")] public ulong? GuildId { get; set; }
    [JsonPropertyName("parent_id")] public ulong? ParentId { get; set; }
    [JsonPropertyName("message_count")] public int MessageCount { get; set; }
    [JsonPropertyName("member_count")] public int MemberCount { get; set; }
    [JsonPropertyName("owner_id")] public ulong OwnerId { get; set; }
    [JsonPropertyName("thread_metadata")] public UserThreadMeta? Metadata { get; set; }
    [JsonPropertyName("last_message_id")] public ulong? LastMessageId { get; set; }
    [JsonPropertyName("total_message_sent")] public int TotalMessageSent { get; set; }
    [JsonPropertyName("applied_tags")] public List<ulong> AppliedTags { get; set; } = new();
    // Set by the forum loader from the endpoint's parallel first_messages array.
    [JsonIgnore] public UserMessage? FirstMessage { get; set; }
}

// ── Slash commands ──

class UserAppCommand
{
    public ulong Id { get; set; }
    [JsonPropertyName("application_id")] public ulong ApplicationId { get; set; }
    public string Version { get; set; } = "";
    public int Type { get; set; } = 1;                 // 1 = chat input (slash)
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<UserAppCommandOption> Options { get; set; } = new();
    // Discord's own ordering signal; the picker sorts by it so "/beg" beats "/begone".
    [JsonPropertyName("global_popularity_rank")] public int Popularity { get; set; } = int.MaxValue;

    // Set from the search response's parallel applications array.
    [JsonIgnore] public string AppName { get; set; } = "";
    [JsonIgnore] public string? AppIcon { get; set; }

    [JsonIgnore] public bool HasSubcommands => Options.Any(o => o.Type is 1 or 2);
    [JsonIgnore] public string? IconUrl => AppIcon == null
        ? null : $"https://cdn.discordapp.com/app-icons/{ApplicationId}/{AppIcon}.png?size=32";
}

class UserAppCommandOption
{
    public int Type { get; set; }   // 1 sub, 2 subgroup, 3 str, 4 int, 5 bool, 6 user, 7 channel, 8 role, 10 num
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Required { get; set; }
    public List<UserAppCommandOption> Options { get; set; } = new();
}

// ── full user profile (GET /users/{id}/profile) ──
//
// Everything Discord's profile modal shows that a plain user object doesn't: banner, bio, pronouns,
// badges, connected accounts, mutuals, and the per-guild member record.
class UserProfile
{
    public UserUser? User { get; set; }
    [JsonPropertyName("user_profile")] public UserProfileDetail? Detail { get; set; }
    public List<UserBadge> Badges { get; set; } = new();
    [JsonPropertyName("connected_accounts")] public List<UserConnection> Connections { get; set; } = new();
    [JsonPropertyName("premium_type")] public int? PremiumType { get; set; }
    [JsonPropertyName("premium_since")] public DateTimeOffset? PremiumSince { get; set; }
    [JsonPropertyName("premium_guild_since")] public DateTimeOffset? BoostingSince { get; set; }
    [JsonPropertyName("mutual_guilds")] public List<UserMutualGuild> MutualGuilds { get; set; } = new();
    [JsonPropertyName("mutual_friends")] public List<UserUser> MutualFriends { get; set; } = new();
    [JsonPropertyName("mutual_friends_count")] public int? MutualFriendsCount { get; set; }
    [JsonPropertyName("guild_member")] public UserMember? GuildMember { get; set; }
    [JsonPropertyName("guild_member_profile")] public UserProfileDetail? GuildDetail { get; set; }
    [JsonPropertyName("legacy_username")] public string? LegacyUsername { get; set; }

    // Per-guild profile overrides the global one, which is how server-specific bios work. The user
    // object inside the profile payload carries the fallbacks (banner hash, accent colour) that the
    // user_profile block does not always repeat, so it is consulted last.
    public string? Bio => Pick(GuildDetail?.Bio, Detail?.Bio);
    public string? Pronouns => Pick(GuildDetail?.Pronouns, Detail?.Pronouns);
    public string? BannerHash => Pick(GuildDetail?.Banner, Detail?.Banner) ?? User?.Banner;
    public int? Accent => Detail?.AccentColor ?? User?.AccentColor;

    static string? Pick(string? guild, string? global) =>
        !string.IsNullOrWhiteSpace(guild) ? guild : (!string.IsNullOrWhiteSpace(global) ? global : null);

    // The colour to wash the banner with when there is no banner image: the Nitro accent colour,
    // else the legacy banner_color hex. Null when the user has picked neither, so the caller can
    // fall back to the guild role colour.
    public int? ProfileColor => Accent ?? ParseHex(Detail?.BannerColor);

    /// The two colours of a Nitro profile theme, primary first, or null when the user has none.
    /// Kept as ints so the protocol layer stays free of System.Drawing; the panel turns them into
    /// the card's gradient. A per-guild theme wins over the global one, like every other override.
    public (int Primary, int Secondary)? ThemeColors =>
        (GuildDetail?.ThemeColors ?? Detail?.ThemeColors) is { Count: >= 2 } t ? (t[0], t[1]) : null;

    static int? ParseHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try { return System.Drawing.ColorTranslator.FromHtml(hex.StartsWith('#') ? hex : "#" + hex).ToArgb() & 0xFFFFFF; }
        catch { return null; }
    }

    public string? BannerUrl(ulong userId, int size = 480) => BannerHash is { } b
        ? $"https://cdn.discordapp.com/banners/{userId}/{b}.{(b.StartsWith("a_") ? "gif" : "png")}?size={size}"
        : null;

    public string PremiumLabel => PremiumType switch
    {
        1 => "Nitro Classic", 2 => "Nitro", 3 => "Nitro Basic", _ => "",
    };
}

class UserProfileDetail
{
    public string? Bio { get; set; }
    public string? Pronouns { get; set; }
    public string? Banner { get; set; }
    [JsonPropertyName("accent_color")] public int? AccentColor { get; set; }
    // The pre-accent_color field: a hex "rrggbb" the client used to paint the banner wash.
    [JsonPropertyName("banner_color")] public string? BannerColor { get; set; }
    // A Nitro profile theme: [primary, secondary]. The profile card is a vertical gradient between
    // the two, and the body is that same gradient under a 60% black scrim.
    [JsonPropertyName("theme_colors")] public List<int>? ThemeColors { get; set; }
}

class UserBadge
{
    public string Id { get; set; } = "";
    public string Description { get; set; } = "";
    public string? Icon { get; set; }
    public string? Link { get; set; }

    public string? IconUrl => Icon == null ? null : $"https://cdn.discordapp.com/badge-icons/{Icon}.png?size=32";
}

class UserConnection
{
    public string Type { get; set; } = "";
    public string? Id { get; set; }
    public string Name { get; set; } = "";
    public bool Verified { get; set; }

    // Discord shows a friendly service name, not the raw key.
    public string Service => Type switch
    {
        "steam" => "Steam", "spotify" => "Spotify", "youtube" => "YouTube", "twitch" => "Twitch",
        "github" => "GitHub", "reddit" => "Reddit", "twitter" => "X", "x" => "X",
        "instagram" => "Instagram", "tiktok" => "TikTok", "playstation" => "PlayStation",
        "xbox" => "Xbox", "battlenet" => "Battle.net", "epicgames" => "Epic Games",
        "riotgames" => "Riot Games", "domain" => "Website", "ebay" => "eBay",
        "paypal" => "PayPal", "roblox" => "Roblox", "bluesky" => "Bluesky",
        _ => char.ToUpperInvariant(Type[0]) + Type[1..],
    };

    public string? Url => Type switch
    {
        "steam" => $"https://steamcommunity.com/profiles/{Id}",
        "github" => $"https://github.com/{Name}",
        "twitch" => $"https://twitch.tv/{Name}",
        "youtube" => $"https://youtube.com/channel/{Id}",
        "spotify" => $"https://open.spotify.com/user/{Id}",
        "reddit" => $"https://reddit.com/u/{Name}",
        "twitter" or "x" => $"https://x.com/{Name}",
        "instagram" => $"https://instagram.com/{Name}",
        "tiktok" => $"https://tiktok.com/@{Name}",
        "bluesky" => $"https://bsky.app/profile/{Name}",
        "roblox" => $"https://roblox.com/users/{Id}/profile",
        "domain" => $"https://{Name}",
        _ => null,
    };
}

class UserMutualGuild
{
    public ulong Id { get; set; }
    public string? Nick { get; set; }
}

class UserScheduledEvent
{
    public ulong Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    [JsonPropertyName("scheduled_start_time")] public DateTimeOffset? Start { get; set; }
    [JsonPropertyName("channel_id")] public ulong? ChannelId { get; set; }
    [JsonPropertyName("user_count")] public int UserCount { get; set; }
    public int Status { get; set; }                   // 1 scheduled, 2 active, 3 completed, 4 cancelled
    [JsonPropertyName("entity_metadata")] public UserEventMeta? Metadata { get; set; }

    public string Where => Metadata?.Location is { Length: > 0 } l ? l : "In this server";
}

class UserEventMeta
{
    public string? Location { get; set; }
}

class UserForumTag
{
    public ulong Id { get; set; }
    public string Name { get; set; } = "";
    [JsonPropertyName("emoji_name")] public string? EmojiName { get; set; }
    [JsonPropertyName("emoji_id")] public ulong? EmojiId { get; set; }
}

class UserThreadMeta
{
    public bool Archived { get; set; }
    public bool Locked { get; set; }
    [JsonPropertyName("archive_timestamp")] public DateTimeOffset? ArchivedAt { get; set; }
}

// ── Guild ──

class UserGuild
{
    public ulong Id { get; set; }
    public string Name { get; set; } = "";
    public string? Icon { get; set; }
    public string? Banner { get; set; }
    [JsonPropertyName("owner_id")] public ulong OwnerId { get; set; }
    public List<UserChannelData> Channels { get; set; } = new();
    public List<UserMember> Members { get; set; } = new();
    public List<UserRole> Roles { get; set; } = new();
    public List<UserGuildEmoji> Emojis { get; set; } = new();
    public List<UserSticker> Stickers { get; set; } = new();
    // Active threads, delivered by THREAD_LIST_SYNC (requested with `threads: true` in op 14) and
    // kept current by THREAD_CREATE/UPDATE/DELETE. Rendered under their parent channel in the
    // sidebar; a thread opening a channel just works because a thread is a channel id.
    public List<UserThreadChannel> Threads { get; set; } = new();
    [JsonPropertyName("voice_states")] public List<UserVoiceState> VoiceStates { get; set; } = new();
    public UserClient Client { get; set; } = null!;

    // Who is sitting in which voice channel. Rebuilt from VOICE_STATE_UPDATE, so the sidebar can
    // list occupants without a scan of every state on every repaint.
    public readonly Dictionary<ulong, List<UserVoiceState>> VoiceByChannel = new();

    // Indexes. Every message row used to linear-scan Members to colour one username — O(rows ×
    // members) per channel load, which is seconds of CPU on a 5k-member guild.
    public readonly Dictionary<ulong, UserMember> MemberById = new();
    public readonly Dictionary<ulong, UserRole> RoleById = new();
    public readonly Dictionary<ulong, UserChannelData> ChannelById = new();
    public readonly Dictionary<ulong, UserThreadChannel> ThreadById = new();

    // Member-list groups exactly as Discord's sidebar shows them (hoisted roles, then online,
    // then offline), populated from GUILD_MEMBER_LIST_UPDATE.
    public readonly List<(string Label, List<UserMember> Members)> MemberGroups = new();
    public int MemberCount, OnlineCount;
    public bool MemberListRequested;

    public string? IconUrl => Icon != null
        ? $"https://cdn.discordapp.com/icons/{Id}/{Icon}.{(Icon.StartsWith("a_") ? "gif" : "png")}?size=96"
        : null;

    // 480 is enough for the 240px-wide sidebar header at 200% scale without pulling the 4K original.
    public string? BannerUrl => Banner != null
        ? $"https://cdn.discordapp.com/banners/{Id}/{Banner}.{(Banner.StartsWith("a_") ? "gif" : "png")}?size=480"
        : null;

    public void Reindex()
    {
        MemberById.Clear();
        foreach (var m in Members) if (m.User != null) MemberById[m.User.Id] = m;
        RoleById.Clear();
        foreach (var r in Roles) RoleById[r.Id] = r;
        ChannelById.Clear();
        foreach (var c in Channels) ChannelById[c.Id] = c;
        ThreadById.Clear();
        foreach (var t in Threads) ThreadById[t.Id] = t;
        VoiceByChannel.Clear();
        foreach (var v in VoiceStates) if (v.ChannelId is { } cid) AddVoice(cid, v);
    }

    // Keep a thread without a full reindex — reindexing the whole guild on every THREAD_UPDATE
    // would rebuild every member/role/channel dictionary for a name change.
    public void UpsertThread(UserThreadChannel t)
    {
        Threads.RemoveAll(x => x.Id == t.Id);
        Threads.Add(t);
        ThreadById[t.Id] = t;
    }

    public void RemoveThread(ulong id)
    {
        Threads.RemoveAll(x => x.Id == id);
        ThreadById.Remove(id);
    }

    void AddVoice(ulong channelId, UserVoiceState v)
    {
        if (!VoiceByChannel.TryGetValue(channelId, out var list)) VoiceByChannel[channelId] = list = new();
        list.Add(v);
    }

    // A user is only ever in one voice channel per guild, so an update is "drop the old seat,
    // take the new one" — a leave is the same event with a null channel.
    public void ApplyVoice(UserVoiceState v)
    {
        VoiceStates.RemoveAll(x => x.UserId == v.UserId);
        foreach (var list in VoiceByChannel.Values) list.RemoveAll(x => x.UserId == v.UserId);
        if (v.ChannelId is not { } cid) return;
        VoiceStates.Add(v);
        AddVoice(cid, v);
    }

    public IReadOnlyList<UserVoiceState> VoiceIn(ulong channelId) =>
        VoiceByChannel.TryGetValue(channelId, out var l) ? l : Array.Empty<UserVoiceState>();

    public UserMember? GetMember(ulong userId) => MemberById.GetValueOrDefault(userId);

    // Highest-positioned role that actually sets a colour, the same rule Discord uses.
    // Null means "no coloured role": the caller substitutes the default name colour. Returning a
    // theme colour from here is what coupled the model layer to the UI in the predecessor, which
    // meant none of this could be exercised without a Form on screen.
    public Color? NameColor(ulong userId)
    {
        var m = GetMember(userId);
        if (m == null) return null;
        UserRole? best = null;
        foreach (var rid in m.RoleIds)
            if (RoleById.TryGetValue(rid, out var r) && r.Color != 0 && (best == null || r.Position > best.Position))
                best = r;
        return best?.Rgb;
    }

    public UserRole? TopRole(ulong userId)
    {
        var m = GetMember(userId);
        if (m == null) return null;
        UserRole? best = null;
        foreach (var rid in m.RoleIds)
            if (RoleById.TryGetValue(rid, out var r) && (best == null || r.Position > best.Position))
                best = r;
        return best;
    }

    // Effective permission mask for a member in a channel: base role perms, then the channel's
    // overwrites in Discord's documented order (@everyone, role denies, role allows, member).
    public ulong PermissionsFor(ulong userId, UserChannelData? ch)
    {
        if (userId == OwnerId) return ulong.MaxValue;
        var m = GetMember(userId);
        ulong perms = RoleById.TryGetValue(Id, out var everyone) ? everyone.Permissions : 0;
        if (m != null)
            foreach (var rid in m.RoleIds)
                if (RoleById.TryGetValue(rid, out var r)) perms |= r.Permissions;
        if ((perms & Perm.Administrator) != 0) return ulong.MaxValue;
        if (ch == null) return perms;

        ulong allow = 0, deny = 0;
        foreach (var o in ch.PermissionOverwrites)
        {
            if (o.Type == 0 && o.Id == Id) { perms &= ~o.Deny; perms |= o.Allow; }       // @everyone
            else if (o.Type == 0 && m != null && m.RoleIds.Contains(o.Id)) { deny |= o.Deny; allow |= o.Allow; }
            else if (o.Type == 1 && o.Id == userId) { perms &= ~o.Deny; perms |= o.Allow; }
        }
        perms &= ~deny;
        perms |= allow;
        return perms;
    }

    public bool CanView(ulong userId, UserChannelData ch) => (PermissionsFor(userId, ch) & Perm.ViewChannel) != 0;

    public Task DownloadMembersAsync() => Client.RequestMemberListAsync(this);
}

class UserChannelData
{
    public ulong Id { get; set; }
    public string Name { get; set; } = "";
    public int Type { get; set; } // 0=text 2=voice 4=category 5=announce 13=stage 15=forum
    public string? Topic { get; set; }
    public int Position { get; set; }
    public bool Nsfw { get; set; }
    [JsonPropertyName("parent_id")] public ulong? ParentId { get; set; }
    [JsonPropertyName("last_message_id")] public ulong? LastMessageId { get; set; }
    [JsonPropertyName("permission_overwrites")] public List<UserOverwrite> PermissionOverwrites { get; set; } = new();

    [JsonPropertyName("available_tags")] public List<UserForumTag> AvailableTags { get; set; } = new();

    // "Shows in the channel list and carries unread state." A forum is included: it is not a
    // message list, but it is a channel you read, and excluding it hid forums from the sidebar,
    // the unread sweeps and the quick switcher entirely.
    public bool IsText => Type is 0 or 5 or 15 or 16;
    /// Somewhere a message can actually be posted or forwarded to. A forum takes posts, not
    /// messages, so it is deliberately not in here.
    public bool IsPostable => Type is 0 or 5;
    public bool IsVoice => Type is 2 or 13;
    public bool IsCategory => Type == 4;
    // 16 is a media channel — same post-list shape as a forum, only the default layout differs.
    // Its content is threads, so it must never be opened as a chat.
    public bool IsForum => Type is 15 or 16;

    // The sidebar glyph Discord draws in front of the name.
    public string Glyph => Type switch
    {
        2 => "", 13 => "", 5 => "", 15 or 16 => "", _ => Nsfw ? "#!" : "#",
    };
}

// ── Users ──

class UserSelfUser
{
    public ulong Id { get; set; }
    public string Username { get; set; } = "";
    [JsonPropertyName("global_name")] public string? GlobalName { get; set; }
    public string? Avatar { get; set; }
    public string? Banner { get; set; }
    public string? Bio { get; set; }
    public string Discriminator { get; set; } = "0";
    public UserClient? Client { get; set; }

    public string Status { get; set; } = "online";
    public string? CustomStatus { get; set; }

    public string DisplayName => GlobalName ?? Username;
    public string Tag => Discriminator is "0" or "" ? "@" + Username : Username + "#" + Discriminator;
    public string GetDisplayAvatarUrl(int size = 128) =>
        Avatar != null
            ? $"https://cdn.discordapp.com/avatars/{Id}/{Avatar}.{(Avatar.StartsWith("a_") ? "gif" : "png")}?size={size}"
            : GetDefaultAvatarUrl();
    public string GetDefaultAvatarUrl() =>
        $"https://cdn.discordapp.com/embed/avatars/{(Discriminator == "0" ? (int)((Id >> 22) % 6) : int.Parse(Discriminator) % 5)}.png";

    // READY hands us a *different* shape for ourselves than for everyone else, so anywhere that
    // lists people — a group DM's member column, a call's participant tiles — needs us in the
    // common shape to sit alongside the others.
    public UserUser AsUser() => new()
    {
        Id = Id, Username = Username, GlobalName = GlobalName, Avatar = Avatar,
        Discriminator = Discriminator, Status = Status, CustomStatus = CustomStatus,
    };
}

class UserUser
{
    public ulong Id { get; set; }
    public string Username { get; set; } = "";
    [JsonPropertyName("global_name")] public string? GlobalName { get; set; }
    public string? Avatar { get; set; }
    [JsonPropertyName("banner")] public string? Banner { get; set; }
    // The Nitro profile colour ("the user's banner color" per the docs). Painted across the profile
    // popout and used as the banner fill when the user has no banner image. Lives on the user
    // object, which is why the profile fetch carries it even when user_profile omits it.
    [JsonPropertyName("accent_color")] public int? AccentColor { get; set; }
    public string Discriminator { get; set; } = "0";
    public bool Bot { get; set; }
    public bool System { get; set; }
    public string Status { get; set; } = "offline";
    public string? CustomStatus { get; set; }     // the "custom status" activity text
    // Discord's member and DM rows print the activity *bare* ("pls play | dankmemer.lol"); only the
    // profile card prefixes the verb. Keeping them joined put "Streaming " in every list row.
    public string? Activity { get; set; }         // "X" — the activity's own name
    public string? ActivityVerb { get; set; }     // "Playing" / "Streaming" / "Listening to" …
    public bool Streaming { get; set; }
    public string? ActivityLine =>
        Activity == null ? null : ActivityVerb is { Length: > 0 } v ? v + " " + Activity : Activity;
    [JsonPropertyName("public_flags")] public int PublicFlags { get; set; }
    // VERIFIED_BOT (1 << 16) — the checkmark Discord puts inside the blurple APP tag.
    public bool VerifiedBot => Bot && (PublicFlags & (1 << 16)) != 0;
    [JsonPropertyName("avatar_decoration_data")] public UserAvatarDecoration? Decoration { get; set; }
    // Discord's server-tag feature: a small pill of another guild's tag next to the name.
    [JsonPropertyName("primary_guild")] public UserPrimaryGuild? PrimaryGuild { get; set; }

    public string? AvatarDecorationUrl => Decoration?.Asset is { } a
        ? $"https://cdn.discordapp.com/avatar-decoration-presets/{a}.png?size=160&passthrough=true"
        : null;

    public string DisplayName => GlobalName ?? Username;
    public string Tag => Discriminator is "0" or "" ? "@" + Username : Username + "#" + Discriminator;
    public bool IsOnline => Status is "online" or "idle" or "dnd";

    /// The server tag to paint beside this name, or null when there is none. Discord *clears* `tag`
    /// when the identity is switched off rather than leaving it set with identity_enabled false, so
    /// a non-empty tag is the whole test — gating on Enabled as well only loses tags on the payloads
    /// that omit the flag.
    [JsonIgnore] public UserPrimaryGuild? ServerTag => PrimaryGuild is { Tag.Length: > 0 } p ? p : null;

    /// When the account was made, decoded from the snowflake — the profile panel's "Member Since".
    [JsonIgnore] public DateTimeOffset CreatedAt =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)((Id >> 22) + 1420070400000UL));

    public string StatusText => Status switch
    {
        "online" => "Online", "idle" => "Idle", "dnd" => "Do Not Disturb", _ => "Offline",
    };

    // Streaming replaces the whole dot with Discord's purple play badge, whatever the presence says.
    public Presence Presence => Streaming && IsOnline ? Presence.Streaming : Status switch
    {
        "online" => Presence.Online, "idle" => Presence.Idle, "dnd" => Presence.Dnd, _ => Presence.Offline,
    };

    public string GetAvatarUrl(int size = 128) =>
        Avatar != null
            ? $"https://cdn.discordapp.com/avatars/{Id}/{Avatar}.{(Avatar.StartsWith("a_") ? "gif" : "png")}?size={size}"
            : GetDefaultAvatarUrl();
    public string GetDefaultAvatarUrl() =>
        $"https://cdn.discordapp.com/embed/avatars/{(Discriminator == "0" ? (int)((Id >> 22) % 6) : int.Parse(Discriminator) % 5)}.png";

    // Badges Discord shows next to a name/profile. Bit positions are from the public flags table.
    public IEnumerable<string> Badges()
    {
        if ((PublicFlags & (1 << 0)) != 0) yield return "🧑‍🚀";   // staff
        if ((PublicFlags & (1 << 1)) != 0) yield return "🛡";     // partner
        if ((PublicFlags & (1 << 2)) != 0) yield return "🎉";     // hypesquad events
        if ((PublicFlags & (1 << 3)) != 0) yield return "🐛";     // bug hunter
        if ((PublicFlags & (1 << 9)) != 0) yield return "🌟";     // early supporter
        if ((PublicFlags & (1 << 17)) != 0) yield return "⚙";     // active developer
        if ((PublicFlags & (1 << 18)) != 0) yield return "✅";     // verified bot developer
    }
}

class UserAvatarDecoration
{
    public string? Asset { get; set; }
    [JsonPropertyName("sku_id")] public ulong? SkuId { get; set; }
}

class UserPrimaryGuild
{
    [JsonPropertyName("identity_guild_id")] public ulong? GuildId { get; set; }
    [JsonPropertyName("identity_enabled")] public bool Enabled { get; set; }
    public string? Tag { get; set; }
    public string? Badge { get; set; }

    public string? BadgeUrl => GuildId is { } g && Badge is { } b
        ? $"https://cdn.discordapp.com/clan-badges/{g}/{b}.png?size=32"
        : null;
}

class UserMember
{
    public UserUser User { get; set; } = null!;
    public string? Nick { get; set; }
    public string? Avatar { get; set; }                       // per-guild avatar override
    // Discord sends role ids as strings; JsonOpts has AllowReadingFromString, so they land here as
    // numbers directly. An earlier version kept a parsed copy alongside and cached it on first
    // access — which went stale the moment a member's roles changed.
    public List<ulong> Roles { get; set; } = new();
    [JsonPropertyName("joined_at")] public DateTimeOffset? JoinedAt { get; set; }
    [JsonPropertyName("premium_since")] public DateTimeOffset? PremiumSince { get; set; }

    [JsonIgnore] public List<ulong> RoleIds => Roles;

    public string DisplayName => Nick ?? User.DisplayName;
    public ulong Id => User.Id;
    public string Status => User.Status;

    public string AvatarUrl(ulong guildId, int size = 64) =>
        Avatar != null
            ? $"https://cdn.discordapp.com/guilds/{guildId}/users/{User.Id}/avatars/{Avatar}.{(Avatar.StartsWith("a_") ? "gif" : "png")}?size={size}"
            : User.GetAvatarUrl(size);
}

class UserRole
{
    public ulong Id { get; set; }
    public string Name { get; set; } = "";
    public int Color { get; set; }
    public int Position { get; set; }
    public bool Hoist { get; set; }
    public string? Icon { get; set; }
    [JsonPropertyName("permissions")] public string PermissionsRaw { get; set; } = "0";

    [JsonIgnore] public ulong Permissions => ulong.TryParse(PermissionsRaw, out var v) ? v : 0;
    // Null when the role sets no colour (Discord sends 0), so the caller falls back to the default
    // name colour instead of this layer inventing one.
    [JsonIgnore] public System.Drawing.Color? Rgb => Color == 0
        ? null
        : System.Drawing.Color.FromArgb((Color >> 16) & 0xFF, (Color >> 8) & 0xFF, Color & 0xFF);
}

// ── Voice ──

class UserVoiceState
{
    [JsonPropertyName("user_id")] public ulong UserId { get; set; }
    [JsonPropertyName("channel_id")] public ulong? ChannelId { get; set; }
    [JsonPropertyName("guild_id")] public ulong? GuildId { get; set; }
    [JsonPropertyName("self_mute")] public bool SelfMute { get; set; }
    [JsonPropertyName("self_deaf")] public bool SelfDeaf { get; set; }
    [JsonPropertyName("self_video")] public bool SelfVideo { get; set; }
    [JsonPropertyName("self_stream")] public bool SelfStream { get; set; }
    public bool Mute { get; set; }        // server-side mute
    public bool Deaf { get; set; }

    // The glyphs Discord puts after a name in the voice channel list.
    public string Glyphs =>
        (SelfStream ? "🔴" : "") + (SelfVideo ? "📹" : "") +
        (Mute || SelfMute ? "🔇" : "") + (Deaf || SelfDeaf ? "🎧" : "");
}

/// The `call` object on a type-3 message — how a finished call knows its own duration.
class UserCallSummary
{
    [JsonPropertyName("ended_timestamp")] public DateTimeOffset? EndedTimestamp { get; set; }
    public List<ulong> Participants { get; set; } = new();
}

// A live call in a DM or group DM. `Ringing` is who Discord is still calling; `Participants` is who
// has actually joined (including us, once we do).
class DmCall
{
    public ulong ChannelId;
    public readonly List<ulong> Ringing = new();
    public readonly List<ulong> Participants = new();
    public readonly Dictionary<ulong, UserVoiceState> States = new();
}

// ── Custom emoji ──

class UserGuildEmoji
{
    public ulong Id { get; set; }
    public string Name { get; set; } = "";
    public bool Animated { get; set; }
    public bool Available { get; set; } = true;
    [JsonPropertyName("require_colons")] public bool RequireColons { get; set; } = true;

    public string Url => $"https://cdn.discordapp.com/emojis/{Id}.{(Animated ? "gif" : "png")}?size=64";
    // What goes in the message box; the renderer parses this form back into an inline image.
    public string Insert => $"<{(Animated ? "a" : "")}:{Name}:{Id}>";
}

// ── Read state (unread badges) ──

class UserReadState
{
    [JsonPropertyName("id")] public ulong ChannelId { get; set; }
    [JsonPropertyName("last_message_id")] public ulong LastMessageId { get; set; }
    [JsonPropertyName("mention_count")] public int MentionCount { get; set; }
}

// ── Messages ──

// `nonce` is whatever the sending client put there. Discord's own clients send a string, but a
// numeric one is legal and appears in the wild; a plain string property throws on those and takes
// the whole MESSAGE_CREATE with it.
sealed class LooseStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => r.TokenType switch
    {
        JsonTokenType.String => r.GetString(),
        JsonTokenType.Number => r.TryGetInt64(out var l) ? l.ToString() : r.GetDouble().ToString(),
        _ => null,
    };

    public override void Write(Utf8JsonWriter w, string? v, JsonSerializerOptions o) => w.WriteStringValue(v);
}

class UserMessage
{
    public ulong Id { get; set; }
    [JsonPropertyName("channel_id")] public ulong ChannelId { get; set; }
    public UserUser Author { get; set; } = null!;
    public string Content { get; set; } = "";
    public int Type { get; set; }                 // 0 = default, 19 = reply, 1..12 = system events
    public DateTimeOffset Timestamp { get; set; }
    [JsonPropertyName("edited_timestamp")] public DateTimeOffset? EditedTimestamp { get; set; }
    public bool Pinned { get; set; }
    public bool Tts { get; set; }
    public List<UserAttachment> Attachments { get; set; } = new();
    public List<UserEmbed> Embeds { get; set; } = new();
    public List<UserReaction> Reactions { get; set; } = new();
    [JsonPropertyName("sticker_items")] public List<UserSticker> Stickers { get; set; } = new();
    [JsonPropertyName("referenced_message")] public UserMessage? ReferencedMessage { get; set; }
    [JsonPropertyName("message_reference")] public UserMessageRef? MessageReference { get; set; }
    // A forward carries no content of its own — the original lives in message_snapshots[0].
    [JsonPropertyName("message_snapshots")] public List<UserMessageSnapshot> Snapshots { get; set; } = new();
    public UserPoll? Poll { get; set; }
    [JsonPropertyName("mention_everyone")] public bool MentionEveryone { get; set; }
    public List<UserUser> Mentions { get; set; } = new();
    [JsonPropertyName("mention_roles")] public List<ulong> MentionRoles { get; set; } = new();
    // Action rows carrying buttons / select menus (what a bot's "Play" button lives in).
    public List<UserComponent> Components { get; set; } = new();
    [JsonPropertyName("application_id")] public ulong? ApplicationId { get; set; }
    public int Flags { get; set; }
    [JsonPropertyName("guild_id")] public ulong? GuildId { get; set; }
    [JsonPropertyName("webhook_id")] public ulong? WebhookId { get; set; }
    // Present on a bot's reply to a slash command / component. Discord replaces the reply preview
    // with "<user> used </command>" for these.
    [JsonPropertyName("interaction_metadata")] public UserInteractionMeta? Interaction { get; set; }

    // Set by the client after parsing: the guild nickname/colour source for this author.
    [JsonIgnore] public UserMember? Member { get; set; }

    // The client-generated id echoed back on both the REST reply and the gateway's MESSAGE_CREATE.
    // It is what lets an optimistic row be matched to the real message instead of appearing twice.
    // Discord sends it as a string, but its own client has historically used a number — tolerate both.
    [JsonConverter(typeof(LooseStringConverter))]
    public string? Nonce { get; set; }

    /// 0 = confirmed by the server, 1 = posted and waiting, 2 = the send failed.
    /// Only ever non-zero on a locally constructed row; nothing on the wire sets it.
    [JsonIgnore] public int SendState { get; set; }
    [JsonIgnore] public bool IsPending => SendState == 1;
    [JsonIgnore] public bool IsFailed => SendState == 2;

    // Interactions are addressed to the app that owns the message; for most bots that equals the
    // author id, which is the fallback Discord's own client uses when application_id is absent.
    public ulong InteractionAppId => ApplicationId ?? Author?.Id ?? 0;

    // 20 = slash-command reply, 21 = thread starter, 23 = context-menu reply: all carry real content
    // and must not be flattened to a grey one-liner the way a join/boost/pin event is.
    [JsonIgnore] public bool IsSystem => Type is not (0 or 19 or 20 or 21 or 23);
    [JsonIgnore] public bool IsEphemeral => (Flags & 64) != 0;
    /// IS_VOICE_MESSAGE, 1<<13. The single ogg attachment then carries duration_secs and waveform,
    /// and the client draws it as a waveform player rather than a download card.
    [JsonIgnore] public bool IsVoiceMessage => (Flags & (1 << 13)) != 0;
    [JsonIgnore] public bool IsForward => Snapshots.Count > 0;

    public UserClient Client { get; set; } = null!;

    public bool MentionsMe(ulong selfId, IReadOnlyCollection<ulong>? myRoles)
    {
        if (MentionEveryone) return true;
        foreach (var u in Mentions) if (u.Id == selfId) return true;
        if (myRoles != null)
            foreach (var rid in MentionRoles)
                if (myRoles.Contains(rid)) return true;
        return false;
    }

    /// Present on a type-3 (call) message. `ended_timestamp` is null while the call is still up,
    /// which is how the client knows to say "started a call" instead of how long it lasted.
    [JsonPropertyName("call")] public UserCallSummary? Call { get; set; }

    /// A system message split for rendering: the author's name is drawn in the body colour of a
    /// normal username and the rest muted, so it reads as one sentence rather than a header plus a
    /// line of text. Returning the two halves keeps that decision out of the row.
    public (string Name, string Tail) SystemParts()   // 'Rest' is disallowed as a tuple element name (CS8126)
    {
        var full = SystemText();
        var name = Member?.Nick ?? Author?.DisplayName ?? "";
        return name.Length > 0 && full.StartsWith(name, StringComparison.Ordinal)
            ? (name, full[name.Length..])
            : ("", full);
    }

    /// "a few seconds" / "a minute" / "5 minutes" / "an hour" — Discord's own phrasing for how
    /// long a finished call ran.
    static string Lasted(TimeSpan d)
    {
        if (d.TotalSeconds < 45) return "a few seconds";
        if (d.TotalSeconds < 90) return "a minute";
        if (d.TotalMinutes < 45) return $"{(int)Math.Round(d.TotalMinutes)} minutes";
        if (d.TotalMinutes < 90) return "an hour";
        if (d.TotalHours < 24) return $"{(int)Math.Round(d.TotalHours)} hours";
        return d.Days == 1 ? "a day" : $"{d.Days} days";
    }

    string CallLine()
    {
        var who = Member?.Nick ?? Author?.DisplayName ?? "Someone";
        if (Call?.EndedTimestamp is { } ended)
            return $"{who} started a call that lasted {Lasted(ended - Timestamp)}.";
        return $"{who} started a call.";
    }

    // The one-line summary Discord shows for a system (join / boost / pin) message.
    public string SystemText() => Type switch
    {
        1 => $"{Author?.DisplayName} added {(Mentions.Count > 0 ? Mentions[0].DisplayName : "someone")} to the group.",
        2 => Mentions.Count > 0 && Mentions[0].Id == Author?.Id
             ? $"{Author?.DisplayName} left the group."
             : $"{Author?.DisplayName} removed {(Mentions.Count > 0 ? Mentions[0].DisplayName : "someone")} from the group.",
        3 => CallLine(),
        4 => $"{Author?.DisplayName} changed the channel name: {Content}",
        5 => $"{Author?.DisplayName} changed the channel icon.",
        6 => $"{Author?.DisplayName} pinned a message to this channel.",
        7 => JoinLine(),
        8 or 9 or 10 or 11 => Content.Length > 0
             ? $"{Author?.DisplayName} just boosted the server {Content} times!"
             : $"{Author?.DisplayName} just boosted the server!",
        12 => $"{Author?.DisplayName} added this server to a channel.",
        14 => $"{Author?.DisplayName} added a guild discovery requirement.",
        15 => $"{Author?.DisplayName} removed a guild discovery requirement.",
        18 => $"{Author?.DisplayName} started a thread.",
        22 => "Invite your friends to this server.",
        24 => "AutoMod blocked a message.",
        25 or 26 or 27 => $"{Author?.DisplayName} joined a role subscription.",
        31 => $"{Author?.DisplayName} started an interaction.",
        32 => "A poll ended.",
        36 => $"{Author?.DisplayName} started an activity session.",
        44 => $"{Author?.DisplayName} purchased a product.",
        46 => "A poll ended.",
        _ => Content.Length > 0 ? Content : "Unsupported message.",
    };

    static readonly string[] JoinLines =
    {
        "{0} joined the party.", "{0} is here.", "Welcome, {0}. We hope you brought pizza.",
        "A wild {0} appeared.", "{0} just landed.", "{0} just slid into the server.",
        "{0} just showed up!", "Welcome {0}. Say hi!", "{0} hopped into the server.",
        "Everyone welcome {0}!", "Glad you're here, {0}.", "Good to see you, {0}.", "Yay you made it, {0}!",
    };

    // Discord picks the greeting deterministically from the message timestamp, so the same join
    // message always reads the same way on every client.
    string JoinLine() => string.Format(
        JoinLines[(int)(Timestamp.ToUnixTimeMilliseconds() % JoinLines.Length)], Author?.DisplayName);

    public Task DeleteAsync() => Client.Rest.DeleteMessageAsync(ChannelId, Id);
    public Task<UserMessage> ModifyAsync(string newContent) => Client.Rest.EditMessageAsync(ChannelId, Id, newContent);
    public Task AddReactionAsync(string emoji) => Client.Rest.AddReactionAsync(ChannelId, Id, emoji);
    public Task RemoveReactionAsync(string emoji) => Client.Rest.RemoveReactionAsync(ChannelId, Id, emoji);
    public Task PinAsync(bool on) => Client.Rest.PinAsync(ChannelId, Id, on);
    public Task VoteAsync(IEnumerable<int> answerIds) => Client.Rest.VotePollAsync(ChannelId, Id, answerIds);

    public string JumpLink => $"https://discord.com/channels/{(GuildId?.ToString() ?? "@me")}/{ChannelId}/{Id}";

    public bool HasReactionFromMe(string emoji) => Reactions.Any(r => r.Emoji.Key == emoji && r.Me);
}

class UserInteractionMeta
{
    public ulong Id { get; set; }
    public int Type { get; set; }                 // 2 = application command, 3 = component
    public string? Name { get; set; }             // the command name, when Discord sends it
    public UserUser? User { get; set; }
    [JsonPropertyName("original_response_message_id")] public ulong? OriginalResponseId { get; set; }
}

class UserMessageRef
{
    public int Type { get; set; }                 // 0 = reply, 1 = forward
    [JsonPropertyName("message_id")] public ulong? MessageId { get; set; }
    [JsonPropertyName("channel_id")] public ulong? ChannelId { get; set; }
    [JsonPropertyName("guild_id")] public ulong? GuildId { get; set; }
}

// The frozen copy of a forwarded message. Discord only sends the display-relevant subset — there is
// no author and no id, which is why a forward cannot be jumped to through the snapshot alone.
class UserMessageSnapshot
{
    public UserMessage? Message { get; set; }
}

// ---------- polls ----------

class UserPoll
{
    public UserPollMedia? Question { get; set; }
    public List<UserPollAnswer> Answers { get; set; } = new();
    public DateTimeOffset? Expiry { get; set; }
    [JsonPropertyName("allow_multiselect")] public bool AllowMultiselect { get; set; }
    public UserPollResults? Results { get; set; }

    public bool Closed => Results?.IsFinalized == true || (Expiry.HasValue && Expiry.Value < DateTimeOffset.UtcNow);
    public int TotalVotes => Results?.AnswerCounts.Sum(a => a.Count) ?? 0;
    public bool IVoted => Results?.AnswerCounts.Any(a => a.MeVoted) == true;

    public (int Count, bool Me) CountFor(int answerId)
    {
        var c = Results?.AnswerCounts.FirstOrDefault(a => a.Id == answerId);
        return c == null ? (0, false) : (c.Count, c.MeVoted);
    }

    // Discord shows "2h left" / "Poll closed" under the answers.
    public string TimeLeft()
    {
        if (Closed) return "Poll closed";
        if (!Expiry.HasValue) return "";
        var d = Expiry.Value - DateTimeOffset.UtcNow;
        if (d.TotalDays >= 1) return $"{(int)d.TotalDays}d left";
        if (d.TotalHours >= 1) return $"{(int)d.TotalHours}h left";
        if (d.TotalMinutes >= 1) return $"{(int)d.TotalMinutes}m left";
        return "less than a minute left";
    }
}

class UserPollMedia
{
    public string? Text { get; set; }
    public UserEmoji? Emoji { get; set; }
}

class UserPollAnswer
{
    [JsonPropertyName("answer_id")] public int AnswerId { get; set; }
    [JsonPropertyName("poll_media")] public UserPollMedia? Media { get; set; }
}

class UserPollResults
{
    [JsonPropertyName("is_finalized")] public bool IsFinalized { get; set; }
    [JsonPropertyName("answer_counts")] public List<UserPollAnswerCount> AnswerCounts { get; set; } = new();
}

class UserPollAnswerCount
{
    public int Id { get; set; }
    public int Count { get; set; }
    [JsonPropertyName("me_voted")] public bool MeVoted { get; set; }
}

/// One MESSAGE_REACTION_* / MESSAGE_POLL_VOTE_* event, reduced to what it changes.
///
/// The gateway sends the *delta*, never the message's new tally, and this client keeps no message
/// store of its own — the open chat list is the only copy. So the event has to carry enough to
/// mutate that copy in place. Refetching the message over REST instead (which an old comment on the
/// dispatch claimed happened, but nothing ever did) would be one HTTP round trip per reaction on
/// every message in view.
///
/// Pure logic, no UI, no network: exercised by --selftest.
sealed record ReactionDelta(ReactionDelta.Op Kind, UserEmoji? Emoji, ulong UserId, int AnswerId)
{
    public enum Op { Add, Remove, RemoveAll, RemoveEmoji, VoteAdd, VoteRemove }

    /// Applies this event to a cached message. `me` is our own user id, which is the only way to
    /// know whether the blue "you reacted" outline should come or go.
    public void ApplyTo(UserMessage m, ulong me)
    {
        bool mine = UserId == me;
        var key = Emoji?.Key;

        switch (Kind)
        {
            case Op.RemoveAll:
                m.Reactions.Clear();
                break;

            case Op.RemoveEmoji:
                if (key != null) m.Reactions.RemoveAll(r => r.Emoji.Key == key);
                break;

            case Op.Add when key != null:
            {
                var hit = m.Reactions.FirstOrDefault(r => r.Emoji.Key == key);
                if (hit == null) m.Reactions.Add(new UserReaction { Emoji = Emoji!, Count = 1, Me = mine });
                // Our own add already showed optimistically if it came from this client, so a
                // duplicate must not double-count it.
                else if (!(mine && hit.Me)) { hit.Count++; hit.Me |= mine; }
                break;
            }

            case Op.Remove when key != null:
            {
                var hit = m.Reactions.FirstOrDefault(r => r.Emoji.Key == key);
                if (hit == null) break;
                hit.Count--;
                if (mine) hit.Me = false;
                if (hit.Count <= 0) m.Reactions.Remove(hit);
                break;
            }

            case Op.VoteAdd or Op.VoteRemove when m.Poll?.Results is { } res:
            {
                var a = res.AnswerCounts.FirstOrDefault(x => x.Id == AnswerId);
                if (a == null)
                {
                    if (Kind == Op.VoteRemove) break;
                    res.AnswerCounts.Add(new UserPollAnswerCount { Id = AnswerId, Count = 1, MeVoted = mine });
                    break;
                }
                a.Count += Kind == Op.VoteAdd ? 1 : -1;
                if (mine) a.MeVoted = Kind == Op.VoteAdd;
                if (a.Count < 0) a.Count = 0;
                break;
            }
        }
    }
}

/// One card on the server-discovery page (GET /discoverable-guilds).
///
/// Not a UserGuild: this is the public preview of a server you are *not* in, so it carries counts
/// and cover art but no channels, roles or members.
class UserDiscoverGuild
{
    public ulong Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public string? Splash { get; set; }
    [JsonPropertyName("discovery_splash")] public string? DiscoverySplash { get; set; }
    [JsonPropertyName("approximate_member_count")] public int MemberCount { get; set; }
    [JsonPropertyName("approximate_presence_count")] public int OnlineCount { get; set; }
    public List<string> Features { get; set; } = new();

    public bool Verified => Features.Contains("VERIFIED");
    public bool Partnered => Features.Contains("PARTNERED");

    public string? IconUrl => Icon is { } i
        ? $"https://cdn.discordapp.com/icons/{Id}/{i}.{(i.StartsWith("a_") ? "gif" : "png")}?size=128"
        : null;

    /// The wide art across the top of a card. Discovery splashes live on their own CDN path; a
    /// server that has only an invite splash falls back to that.
    public string? CoverUrl => DiscoverySplash is { } d
        ? $"https://cdn.discordapp.com/discovery-splashes/{Id}/{d}.jpg?size=512"
        : Splash is { } s ? $"https://cdn.discordapp.com/splashes/{Id}/{s}.jpg?size=512" : null;

    public static string Compact(int n) => n >= 1_000_000 ? (n / 1_000_000d).ToString("0.#") + "M"
                                         : n >= 1_000 ? (n / 1_000d).ToString("0.#") + "K"
                                         : n.ToString();
}

class UserSticker
{
    public ulong Id { get; set; }
    public string Name { get; set; } = "";
    [JsonPropertyName("format_type")] public int FormatType { get; set; }   // 1=png 2=apng 3=lottie 4=gif

    /// Lottie is a vector animation with no raster form on the CDN — it is fetched as JSON and
    /// rasterised by [[Lottie]] instead of going through Media's image cache.
    public bool IsLottie => FormatType == 3;
    public bool Renderable => FormatType is 1 or 2 or 3 or 4;
    public string Url => FormatType switch
    {
        3 => $"https://cdn.discordapp.com/stickers/{Id}.json",
        4 => $"https://media.discordapp.net/stickers/{Id}.gif",
        _ => $"https://media.discordapp.net/stickers/{Id}.png?size=160",
    };
}

class UserAttachment
{
    public ulong Id { get; set; }
    public string Filename { get; set; } = "";
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string Url { get; set; } = "";
    [JsonPropertyName("proxy_url")] public string? ProxyUrl { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    [JsonPropertyName("content_type")] public string? ContentType { get; set; }
    public long Size { get; set; }
    public bool Ephemeral { get; set; }

    public bool IsImage => Width.HasValue && (ContentType == null || ContentType.StartsWith("image/"));
    // Discord does not always send content_type (older uploads, some bots), so fall back to the
    // extension — otherwise an .mp3 came through as a generic "open in browser" file card.
    public bool IsVideo => ContentType?.StartsWith("video/") == true || Ext is ".mp4" or ".webm" or ".mov" or ".mkv" or ".m4v" or ".avi";
    public bool IsAudio => ContentType?.StartsWith("audio/") == true || Ext is ".mp3" or ".ogg" or ".oga" or ".opus" or ".wav" or ".m4a" or ".flac" or ".aac" or ".weba";
    public bool IsSpoiler => Filename.StartsWith("SPOILER_");
    // A Discord voice message: one ogg attachment on a message flagged 1<<13.
    [JsonPropertyName("duration_secs")] public double? DurationSecs { get; set; }
    [JsonPropertyName("waveform")] public string? Waveform { get; set; }

    [JsonIgnore] public string Ext
    {
        get
        {
            int dot = Filename.LastIndexOf('.');
            return dot < 0 ? "" : Filename[dot..].ToLowerInvariant();
        }
    }

    public string PrettySize => Size >= 1024 * 1024
        ? $"{Size / 1024.0 / 1024.0:0.0} MB"
        : Size >= 1024 ? $"{Size / 1024.0:0.0} KB" : $"{Size} B";

    // Shown under the filename on the card: duration for media, size for everything else.
    public string SubLine => DurationSecs is { } d && d > 0
        ? $"{TimeSpan.FromSeconds(d):m\\:ss}  ·  {PrettySize}"
        : PrettySize;

    public string Glyph => IsVideo ? "🎬" : IsAudio ? "🎵"
        : Ext switch
        {
            ".pdf" => "📕",
            ".zip" or ".rar" or ".7z" or ".gz" or ".tar" => "🗜",
            ".txt" or ".md" or ".log" => "📝",
            ".doc" or ".docx" or ".odt" => "📘",
            ".xls" or ".xlsx" or ".csv" => "📊",
            ".ppt" or ".pptx" => "📙",
            ".json" or ".xml" or ".yml" or ".yaml" => "🧾",
            ".cs" or ".js" or ".ts" or ".py" or ".rs" or ".go" or ".java" or ".cpp" or ".c" or ".h" or ".html" or ".css" or ".sh" => "💻",
            ".exe" or ".msi" or ".dll" => "⚙",
            ".ttf" or ".otf" or ".woff" or ".woff2" => "🔤",
            _ => "📄",
        };
}

class UserEmbed
{
    public string? Type { get; set; }              // rich | image | video | gifv | link | article
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Url { get; set; }
    public int? Color { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public UserEmbedFooter? Footer { get; set; }
    public UserEmbedImage? Image { get; set; }
    public UserEmbedImage? Thumbnail { get; set; }
    public UserEmbedVideo? Video { get; set; }
    public UserEmbedProvider? Provider { get; set; }
    public UserEmbedAuthor? Author { get; set; }
    public List<UserEmbedField> Fields { get; set; } = new();

    // gifv/video embeds (Tenor, YouTube…) carry no inline player here — we show the poster frame
    // plus a play affordance and hand the link to the browser.
    public bool IsPlayable => Type is "video" or "gifv" || Video?.Url != null;
    public string? PosterUrl => Thumbnail?.Best ?? Image?.Best;

    // Tenor and Giphy ship a "gifv" embed whose video is an mp4 — which is why a posted GIF showed
    // up here as a still frame with a play badge instead of playing. Both hosts serve the same
    // asset as a real .gif at the same path, and an animated GIF is something this client *can*
    // render, so prefer it. Anything else (YouTube et al) stays a poster + link.
    public string? AnimatedGifUrl
    {
        get
        {
            if (Type != "gifv") return null;
            foreach (var u in new[] { Video?.Url, Video?.ProxyUrl, Image?.Url, Thumbnail?.Url })
            {
                if (string.IsNullOrEmpty(u)) continue;
                if (u.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)) return u;
                if (u.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) && IsGifHost(u))
                    return u[..^4] + ".gif";
            }
            return null;
        }
    }

    static bool IsGifHost(string url) =>
        url.Contains("tenor.com", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("giphy.com", StringComparison.OrdinalIgnoreCase);

    // A bare image embed (someone pasted a .png link) is drawn as a plain picture, not a card.
    public bool IsBareImage => Type == "image" && Title == null && Description == null;
}

class UserEmbedFooter
{
    public string Text { get; set; } = "";
    [JsonPropertyName("icon_url")] public string? IconUrl { get; set; }
    [JsonPropertyName("proxy_icon_url")] public string? ProxyIconUrl { get; set; }
    public string? Icon => IconUrl ?? ProxyIconUrl;
}

class UserEmbedImage
{
    public string? Url { get; set; }
    [JsonPropertyName("proxy_url")] public string? ProxyUrl { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    // Discord's own client always renders the proxied copy: it is cached, re-encoded, and served
    // without the hotlink protection some origins apply. Fall back to the origin only if absent.
    [JsonIgnore] public string? Best => ProxyUrl ?? Url;
}

class UserEmbedVideo
{
    public string? Url { get; set; }
    [JsonPropertyName("proxy_url")] public string? ProxyUrl { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
}

class UserEmbedProvider { public string? Name { get; set; } public string? Url { get; set; } }

class UserEmbedAuthor
{
    public string Name { get; set; } = "";
    public string? Url { get; set; }
    [JsonPropertyName("icon_url")] public string? IconUrl { get; set; }
    [JsonPropertyName("proxy_icon_url")] public string? ProxyIconUrl { get; set; }
    public string? Icon => IconUrl ?? ProxyIconUrl;
}

class UserEmbedField
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public bool Inline { get; set; }
}

// ── Message components (action rows, buttons, select menus) ──

// One node of the component tree. Type 1 is an action row and only carries children; the rest are
// leaves. Kept as a single shape because Discord's payload is a uniform recursive array.
class UserComponent
{
    public int Type { get; set; }                    // 1=row 2=button 3=string-select 5..8=entity selects
    public List<UserComponent> Components { get; set; } = new();
    public int Style { get; set; }                   // button: 1=primary 2=secondary 3=success 4=danger 5=link 6=premium
    public string? Label { get; set; }
    public UserEmoji? Emoji { get; set; }
    [JsonPropertyName("custom_id")] public string? CustomId { get; set; }
    public string? Url { get; set; }                 // style 5 only
    public bool Disabled { get; set; }
    public string? Placeholder { get; set; }
    public List<UserSelectOption> Options { get; set; } = new();

    // ── Components V2 ──
    // A message with flag 1<<15 carries its whole body here instead of in content/embeds: a Container
    // of TextDisplays, Separators, Sections and media. Bots have been migrating to it, and a client
    // that only knows ActionRows renders those messages completely blank.
    public string? Content { get; set; }                    // TextDisplay (10): markdown
    public UnfurledMedia? Media { get; set; }               // Thumbnail (11) / File (13)
    public List<MediaItem> Items { get; set; } = new();     // MediaGallery (12)
    public UserComponent? Accessory { get; set; }           // Section (9): the control on the right
    public int Spacing { get; set; } = 1;                   // Separator (14): 1 small, 2 large
    public bool Divider { get; set; } = true;               // Separator (14): draw the line
    [JsonPropertyName("accent_color")] public int? AccentColor { get; set; }   // Container (17)
    public bool Spoiler { get; set; }

    public const int Row = 1, Button = 2, StringSelect = 3;
    public const int Section = 9, TextDisplay = 10, Thumbnail = 11, MediaGallery = 12,
                     File = 13, Separator = 14, Container = 17;

    // Does this component hold message *body* rather than controls? Those are the ones a V2 message
    // needs rendered even though there is nothing clickable in them.
    public bool IsV2Layout => Type is Section or TextDisplay or Thumbnail or MediaGallery
                                   or File or Separator or Container;
    public bool IsLink => Type == Button && Style == 5 && !string.IsNullOrEmpty(Url);
    // Premium/subscription buttons carry no custom_id and can't be actioned by a client.
    public bool Clickable => !Disabled && (IsLink || !string.IsNullOrEmpty(CustomId));
}

// Components V2 wraps every media reference in an "unfurled media" object.
class UnfurledMedia
{
    public string? Url { get; set; }
    [JsonPropertyName("proxy_url")] public string? ProxyUrl { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    [JsonPropertyName("content_type")] public string? ContentType { get; set; }
    [JsonIgnore] public string? Best => ProxyUrl ?? Url;
}

class MediaItem
{
    public UnfurledMedia? Media { get; set; }
    public string? Description { get; set; }
    public bool Spoiler { get; set; }
}

class UserSelectOption
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
    public string? Description { get; set; }
    public UserEmoji? Emoji { get; set; }
    public bool Default { get; set; }
}

class UserReaction
{
    public UserEmoji Emoji { get; set; } = null!;
    public int Count { get; set; }
    public bool Me { get; set; }
}

class UserEmoji
{
    public ulong? Id { get; set; }
    public string? Name { get; set; }
    public bool Animated { get; set; }

    public string Glyph => Name ?? "?";
    public string? ImageUrl => Id is { } id
        ? $"https://cdn.discordapp.com/emojis/{id}.{(Animated ? "gif" : "png")}?size=32"
        : null;

    // The form the REST reaction endpoints want: "name:id" for custom, the raw glyph otherwise.
    public string Key => Id is { } id ? $"{Name}:{id}" : Name ?? "";

    /// How the emoji reads inside a sentence — a tooltip cannot draw the custom image, so it names
    /// it the way Discord does.
    public string Display => Id is null ? Glyph : ":" + Name + ":";

    /// Discord's own markup form, minus the angle brackets: "a:name:id", "name:id", or the raw
    /// glyph. Unlike Key it keeps the animated flag, which is what makes it safe to store.
    public string Markup => Id is { } id ? $"{(Animated ? "a:" : "")}{Name}:{id}" : Name ?? "";

    /// The inverse of Markup — rebuilds the emoji from a stored suggestion.
    public static UserEmoji Parse(string markup)
    {
        bool anim = markup.StartsWith("a:", StringComparison.Ordinal);
        var body = anim ? markup[2..] : markup;
        int colon = body.LastIndexOf(':');
        if (colon > 0 && ulong.TryParse(body[(colon + 1)..], out var id))
            return new UserEmoji { Name = body[..colon], Id = id, Animated = anim };
        return new UserEmoji { Name = markup };
    }
}

// ── Typing ──

class UserTypingEvent
{
    [JsonPropertyName("user_id")] public ulong UserId { get; set; }
    [JsonPropertyName("channel_id")] public ulong ChannelId { get; set; }
    [JsonPropertyName("guild_id")] public ulong? GuildId { get; set; }
    public string Username { get; set; } = "";
}

// ── Friends / relationships ──

class UserRelationship
{
    public ulong Id { get; set; }
    public int Type { get; set; }        // 1=friend 2=blocked 3=incoming 4=outgoing
    public UserUser? User { get; set; }
    public string? Nickname { get; set; }

    public string Bucket => Type switch
    {
        1 => "All", 2 => "Blocked", 3 => "Pending", 4 => "Pending", _ => "Other",
    };
}
