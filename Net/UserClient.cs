using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenCord;

class UserClient
{
    public UserSelfUser? CurrentUser { get; private set; }
    public List<UserGuild> Guilds { get; } = new();
    public List<UserDMChannel> DMChannels { get; } = new();
    public List<UserRelationship> Relationships { get; } = new();
    public UserRestClient Rest { get; }
    public bool IsConnected { get; private set; }
    public string? SessionId => _sessionId;   // interactions must quote the live gateway session

    // channel id -> guild id, so a MESSAGE_CREATE can find its guild without scanning every guild.
    public readonly Dictionary<ulong, ulong> ChannelGuild = new();
    public readonly Dictionary<ulong, UserGuild> GuildById = new();
    public readonly Dictionary<ulong, UserDMChannel> DmById = new();
    // Per-channel read state drives the unread dots and mention badges.
    public readonly Dictionary<ulong, UserReadState> ReadStates = new();
    public readonly HashSet<ulong> MutedChannels = new();
    public readonly HashSet<ulong> MutedGuilds = new();
    // Per-channel message-notification override: 0 all, 1 mentions only, 2 nothing, 3 inherit.
    public readonly Dictionary<ulong, int> ChannelNotifyLevels = new();
    /// Per-guild message_notifications (0 all, 1 mentions, 2 none). Channel overrides inherit it.
    public readonly Dictionary<ulong, int> GuildNotifyLevels = new();

    public event Func<Task>? Ready;
    public event Func<UserMessage, Task>? MessageReceived;
    public event Func<UserMessage, Task>? MessageUpdated;
    public event Func<ulong, ulong, Task>? MessageDeleted; // (msgId, channelId)
    public event Action<ulong, ulong, ReactionDelta>? ReactionChanged;   // (msgId, channelId, what changed)
    public event Func<UserTypingEvent, Task>? UserTyping;
    public event Action<UserGuild>? MemberListUpdated;
    public event Action? ReadStateChanged;
    public event Action? RelationshipsChanged;
    public event Action<ulong, string>? PresenceChanged;   // (userId, status)
    public event Action<UserGuild?>? VoiceChanged;         // someone joined/left a voice channel
    public event Action<UserGuild>? GuildJoined;           // GUILD_CREATE for a guild we didn't have
    public event Action<ulong>? GuildLeft;                 // left / kicked / server deleted
    public event Action<UserGuild>? GuildChanged;          // name or icon edited
    public event Action? SelfChanged;                      // own username / avatar edited

    /// The UI calls this after a local edit (avatar change, custom status) so open panels repaint.
    public void NotifySelfChanged() => SelfChanged?.Invoke();

    /// The UI mutates MutedChannels/ChannelNotifyLevels directly (the notification popup) and calls
    /// this so the sidebar and rail re-read them.
    public void NotifyMutesChanged() => ReadStateChanged?.Invoke();
    public event Action? SelfMemberLoaded;                  // our roles arrived -> channel visibility changed
    public event Action<ulong>? CallChanged;                // (dm channel id) ring started/stopped, someone joined/left
    public event Action<ulong>? DmClosed;                   // (dm channel id) removed via CHANNEL_DELETE
    public event Action<UserGuild>? ThreadsChanged;         // thread list moved (create/archive/delete)
    public Action<string>? OnLog;

    readonly string _token;
    ClientWebSocket? _ws;
    CancellationTokenSource? _cts;
    int? _heartbeatInterval;
    int? _lastSeq;
    string? _sessionId;
    System.Threading.Timer? _heartbeatTimer;
    DateTime _lastHeartbeatAck = DateTime.UtcNow;
    bool _shouldReconnect = true;
    readonly SemaphoreSlim _sendLock = new(1, 1);   // ClientWebSocket allows one send at a time

    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        // Discord nulls fields liberally, including ones its own docs type as non-nullable
        // (`primary_guild.identity_enabled: null` killed the whole READY dispatch once). Treat an
        // explicit null on a non-nullable value type as the default instead of throwing: one bad
        // field must never cost the entire payload.
        Converters = { new NullTolerantValueTypes() },
    };

    sealed class NullTolerantValueTypes : JsonConverterFactory
    {
        public override bool CanConvert(Type t) =>
            t.IsValueType && Nullable.GetUnderlyingType(t) == null && (t.IsPrimitive || t.IsEnum
                || t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(TimeSpan)
                || t == typeof(decimal) || t == typeof(Guid));

        public override JsonConverter CreateConverter(Type t, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(Inner<>).MakeGenericType(t), options)!;

        sealed class Inner<T> : JsonConverter<T> where T : struct
        {
            // Options without this factory, so resolving the real behaviour doesn't recurse.
            readonly JsonSerializerOptions _bare;

            public Inner(JsonSerializerOptions options)
            {
                _bare = new JsonSerializerOptions(options);
                for (int i = _bare.Converters.Count - 1; i >= 0; i--)
                    if (_bare.Converters[i] is NullTolerantValueTypes) _bare.Converters.RemoveAt(i);
            }

            // Deserialize through the *serializer* rather than a raw JsonConverter: a converter
            // invoked directly ignores NumberHandling, and every Discord snowflake arrives as a
            // string, so calling the ulong converter by hand threw on all of them.
            public override T Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) =>
                r.TokenType == JsonTokenType.Null ? default : JsonSerializer.Deserialize<T>(ref r, _bare);

            public override void Write(Utf8JsonWriter w, T v, JsonSerializerOptions o) =>
                JsonSerializer.Serialize(w, v, _bare);
        }
    }

    public UserClient(string token)
    {
        _token = token;
        Rest = new UserRestClient(token, this);
    }

    public async Task ConnectAsync()
    {
        _cts = new CancellationTokenSource();
        await ConnectGatewayAsync();
    }

    public async Task DisconnectAsync()
    {
        _shouldReconnect = false;
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
        try { _ws?.Abort(); } catch { }
        _ws?.Dispose();
        _ws = null;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        IsConnected = false;
        await Task.CompletedTask;
    }

    // ═══════════════════ GATEWAY ═══════════════════

    async Task ConnectGatewayAsync()
    {
        try
        {
            _ws = new ClientWebSocket();
            _ws.Options.SetRequestHeader("Origin", "https://discord.com");
            await _ws.ConnectAsync(new Uri("wss://gateway.discord.gg/?v=9&encoding=json"), _cts!.Token);
            _ = ReadLoop();
        }
        catch (Exception ex) when (!_cts!.IsCancellationRequested)
        {
            OnLog?.Invoke($"Gateway connect failed: {ex.Message}");
            if (_shouldReconnect) { await BackoffAsync(); await ConnectGatewayAsync(); }
        }
    }

    /// Raised with false when the socket drops and true once a session is live again. Drives the
    /// "Connecting…" bar — before this the client reconnected silently and a dead gateway looked
    /// exactly like a quiet channel.
    public event Action<bool>? ConnectionChanged;

    int _retry;

    /// Exponential backoff with jitter, capped. A flat retry hammered the gateway during an
    /// outage and got the session rate-limited, which made the outage last longer.
    async Task BackoffAsync()
    {
        _retry = Math.Min(_retry + 1, 6);
        int baseMs = 1000 * (1 << (_retry - 1));          // 1, 2, 4, 8, 16, 32s
        int jitter = Random.Shared.Next(0, baseMs / 2);
        int wait = Math.Min(baseMs + jitter, 45000);
        OnLog?.Invoke($"Reconnecting in {wait} ms (attempt {_retry})");
        try { await Task.Delay(wait, _cts?.Token ?? default); } catch { }
    }

    /// Called once a session is established, so the next drop starts from a short wait again.
    void ResetBackoff()
    {
        _retry = 0;
        // A new gateway session has none of the old one's op-14 member subscriptions, but the
        // client-side "already asked" flags survived the drop — so the member column stayed blank
        // until you switched channels. Both the per-guild flag and the dedupe key are session
        // state, so both are cleared here, at the one place a session begins.
        foreach (var g in Guilds) g.MemberListRequested = false;
        _memberSubs.Clear();
        ConnectionChanged?.Invoke(true);
    }

    async Task ReadLoop()
    {
        var buffer = new byte[8192];
        var messageBuffer = new MemoryStream();

        try
        {
            while (_ws?.State == WebSocketState.Open && !_cts!.IsCancellationRequested)
            {
                messageBuffer.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                    messageBuffer.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                // Parse straight from the receive buffer. ToArray() copied the whole payload and
                // GetString() then made a UTF-16 copy at 2x the bytes — on a multi-MB READY that
                // was the single biggest allocation spike at login.
                await HandleGatewayMessage(new ReadOnlyMemory<byte>(messageBuffer.GetBuffer(), 0, (int)messageBuffer.Length));

                // READY is multi-MB; the stream never shrinks on its own, so that capacity would be
                // held for the whole session. Every later dispatch is tiny.
                if (messageBuffer.Capacity > 512 * 1024) { messageBuffer.Dispose(); messageBuffer = new MemoryStream(); }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (Exception ex) { OnLog?.Invoke($"Gateway read error: {ex.Message}"); }

        if (_shouldReconnect && !_cts!.IsCancellationRequested)
        {
            ConnectionChanged?.Invoke(false);
            await BackoffAsync();
            await ConnectGatewayAsync();
        }
    }

    async Task HandleGatewayMessage(ReadOnlyMemory<byte> payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            int op = root.GetProperty("op").GetInt32();

            if (root.TryGetProperty("s", out var s) && s.ValueKind != JsonValueKind.Null)
                _lastSeq = s.GetInt32();

            switch (op)
            {
                case 10: // Hello
                    _heartbeatInterval = root.GetProperty("d").GetProperty("heartbeat_interval").GetInt32();
                    _heartbeatTimer?.Dispose();
                    _heartbeatTimer = new System.Threading.Timer(_ => _ = SendHeartbeat(), null, _heartbeatInterval.Value, _heartbeatInterval.Value);
                    if (_sessionId != null) await SendResume(); else await SendIdentify();
                    break;

                case 11: // Heartbeat ACK
                    _lastHeartbeatAck = DateTime.UtcNow;
                    break;

                case 0: // Dispatch
                    var type = root.GetProperty("t").GetString();
                    await HandleDispatch(type ?? "", root.GetProperty("d"));
                    break;

                case 7: // Reconnect
                    OnLog?.Invoke("Gateway requested reconnect");
                    _shouldReconnect = true;
                    try { _ws?.Abort(); } catch { }
                    break;

                case 9: // Invalid Session
                    var resumable = root.GetProperty("d").GetBoolean();
                    if (resumable && _sessionId != null)
                    {
                        await Task.Delay(1000);
                        await SendResume();
                    }
                    else
                    {
                        _sessionId = null;
                        await Task.Delay(2000);
                        await SendIdentify();
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            // Only materialise text on the error path, and only the head of it.
            var truncated = Encoding.UTF8.GetString(payload.Span[..Math.Min(200, payload.Length)]);
            OnLog?.Invoke($"Gateway parse error: {ex.Message} | JSON: {truncated}");
        }
    }

    async Task SendIdentify()
    {
        if (_ws?.State != WebSocketState.Open) return;

        var identify = new
        {
            op = 2,
            d = new
            {
                token = _token,
                capabilities = 16381,
                properties = new
                {
                    os = "Windows",
                    browser = "Chrome",
                    device = "",
                    system_locale = "en-US",
                    browser_user_agent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
                    browser_version = "131.0.0.0",
                    os_version = "10",
                    referrer = "",
                    referring_domain = "",
                    referrer_current = "",
                    referring_domain_current = "",
                    release_channel = "stable",
                    client_build_number = 363492,
                    client_event_source = (string?)null
                },
                // Identify with the status the user last chose, or a reconnect silently snaps them
                // back to Online.
                presence = new { status = Presence, since = 0, activities = Array.Empty<object>(), afk = false },
                compress = false,
                client_state = new
                {
                    guild_versions = new { },
                    highest_last_message_id = "0",
                    read_state_version = 0,
                    user_guild_settings_version = -1,
                    user_settings_version = -1,
                    private_channels_version = "0",
                    api_code_version = 0
                }
            }
        };
        await SendJson(identify);
    }

    async Task SendResume()
    {
        if (_ws?.State != WebSocketState.Open || _sessionId == null) return;
        var resume = new
        {
            op = 6,
            d = new { token = _token, session_id = _sessionId, seq = _lastSeq ?? 0 }
        };
        await SendJson(resume);
    }

    async Task SendHeartbeat()
    {
        if (_ws?.State != WebSocketState.Open) return;
        try
        {
            // Two missed ACKs means the socket is a zombie; drop it so ReadLoop reconnects.
            if (_heartbeatInterval is { } iv && (DateTime.UtcNow - _lastHeartbeatAck).TotalMilliseconds > iv * 2.5)
            {
                OnLog?.Invoke("Heartbeat not acknowledged — reconnecting");
                try { _ws?.Abort(); } catch { }
                return;
            }
            await SendJson(new { op = 1, d = _lastSeq });
        }
        catch { }
    }

    async Task SendJson(object obj)
    {
        if (_ws?.State != WebSocketState.Open) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(obj);
        await _sendLock.WaitAsync();
        try { await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts?.Token ?? default); }
        catch { }
        finally { _sendLock.Release(); }
    }

    // op 14 — "lazy guild request". A user account cannot GET /guilds/{id}/members (that is
    // bot-only, it 403s); the member sidebar is delivered over the gateway in ranges instead.
    public async Task RequestMemberListAsync(UserGuild g, ulong? channelId = null, int upto = 199)
    {
        var ranges = new List<int[]> { new[] { 0, 99 } };
        for (int start = 100; start < upto; start += 100) ranges.Add(new[] { start, Math.Min(start + 99, upto) });
        await SubscribeMemberRangesAsync(g, channelId, ranges);
    }

    /// Subscribe to the 100-row blocks covering `firstRow`..`lastRow`, which is what the real client
    /// does as you scroll the member column. [0,99] is always kept: the top of the list stays
    /// rendered, and a SYNC that omitted it would clear the rows we still need.
    public Task SubscribeMemberRowsAsync(UserGuild g, ulong? channelId, int firstRow, int lastRow)
    {
        var blocks = new SortedSet<int> { 0 };
        for (int b = Math.Max(0, firstRow) / 100 * 100; b <= lastRow; b += 100) blocks.Add(b);
        // Discord's own client sends at most three ranges per subscription.
        var ranges = blocks.Take(3).Select(b => new[] { b, b + 99 }).ToList();
        return SubscribeMemberRangesAsync(g, channelId, ranges);
    }

    // The ranges last sent for this guild, so a scroll that stays inside them costs nothing.
    readonly Dictionary<ulong, string> _memberSubs = new();

    async Task SubscribeMemberRangesAsync(UserGuild g, ulong? channelId, List<int[]> ranges)
    {
        // The member list is per *channel* — Discord scopes it to the one you are looking at, so a
        // channel only some roles can see lists only those roles. Defaulting to the guild's first
        // text channel showed the wrong people in every restricted channel.
        var chan = channelId ?? g.Channels.FirstOrDefault(c => c.IsPostable)?.Id;
        if (chan == null) return;

        var key = chan.Value + ":" + string.Join(",", ranges.Select(r => r[0]));
        if (_memberSubs.GetValueOrDefault(g.Id) == key) return;
        _memberSubs[g.Id] = key;

        g.MemberListRequested = true;
        await SendJson(new
        {
            op = 14,
            d = new
            {
                guild_id = g.Id.ToString(),
                typing = true,
                // threads: true is what makes the gateway answer with THREAD_LIST_SYNC, the only
                // source of the active-thread list for a user account (the REST thread endpoints
                // are bot-only). False leaves the sidebar unable to show threads at all.
                threads = true,
                activities = true,
                channels = new Dictionary<string, object> { [chan.Value.ToString()] = ranges },
            }
        });
    }

    // ═══════════════════ DISPATCH ═══════════════════

    // Discord creates the real activity instance server-side; the id it picks arrives over the
    // gateway. Record anything activity-shaped so the launcher can quote the true instance_id
    // instead of inventing one (an invented id makes the /.proxy/ edge 404).
    // The ids Discord's edge expects in the activity URL. instance_id must be the *composite*
    // form (i-<launch>-<locationKind>-<guild>-<channel>) and location_id the location's own id —
    // a bare channel id or a generated instance makes /.proxy/ 404.
    public sealed record ActivityLaunchInfo(string InstanceId, string? LocationId, string? LaunchId, ulong ApplicationId);
    public ActivityLaunchInfo? LastActivityLaunch { get; private set; }
    public event Action<string, string>? ActivityEvent;   // (dispatch type, raw json)

    async Task HandleDispatch(string type, JsonElement data)
    {
        if (type.Contains("ACTIVIT", StringComparison.OrdinalIgnoreCase) ||
            type.Contains("SESSION", StringComparison.OrdinalIgnoreCase))
        {
            var raw = data.ToString();
            // Log the top-level keys plus the whole body — the instance id sits past where a
            // truncated dump would cut, and its format is not guaranteed to be "i."-prefixed.
            if (data.ValueKind == JsonValueKind.Object)
            {
                string? Str(string k) => data.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var composite = Str("composite_instance_id") ?? Str("instance_id");
                if (composite != null && data.TryGetProperty("application_id", out var app)
                    && ulong.TryParse(app.GetString(), out var appId))
                {
                    string? locId = null;
                    if (data.TryGetProperty("location", out var loc) && loc.ValueKind == JsonValueKind.Object
                        && loc.TryGetProperty("id", out var li)) locId = li.GetString();
                    LastActivityLaunch = new ActivityLaunchInfo(composite, locId, Str("launch_id"), appId);
                    Log.Activity($"activity instance ready: {composite} location={locId}");
                }
            }
            Log.Activity($"GATEWAY {type} FULL: {raw}");
            ActivityEvent?.Invoke(type, raw);
        }

        try
        {
            switch (type)
            {
                case "READY": await HandleReady(data); break;
                // A successful RESUME is just as much "we are back" as a fresh READY — without
                // this the bar stayed up after every resumed reconnect.
                case "RESUMED": ResetBackoff(); break;
                case "MESSAGE_CREATE": await HandleMessageCreate(data); break;
                case "MESSAGE_UPDATE": await HandleMessageUpdate(data); break;
                case "MESSAGE_DELETE": await HandleMessageDelete(data); break;
                // The gateway sends the delta, not the new tally, so each of these carries its own
                // meaning through to the cached message — see ReactionDelta. They used to collapse
                // into one "something changed" ping, which re-laid-out the row against the very
                // object that had not been updated, so nothing moved until the channel reloaded.
                case "MESSAGE_REACTION_ADD": HandleReaction(data, ReactionDelta.Op.Add); break;
                case "MESSAGE_REACTION_REMOVE": HandleReaction(data, ReactionDelta.Op.Remove); break;
                case "MESSAGE_REACTION_REMOVE_ALL": HandleReaction(data, ReactionDelta.Op.RemoveAll); break;
                case "MESSAGE_REACTION_REMOVE_EMOJI": HandleReaction(data, ReactionDelta.Op.RemoveEmoji); break;
                case "MESSAGE_POLL_VOTE_ADD": HandleReaction(data, ReactionDelta.Op.VoteAdd); break;
                case "MESSAGE_POLL_VOTE_REMOVE": HandleReaction(data, ReactionDelta.Op.VoteRemove); break;
                case "TYPING_START": await HandleTyping(data); break;
                case "GUILD_CREATE": HandleGuildCreate(data); break;
                case "GUILD_UPDATE": HandleGuildUpdate(data); break;
                case "GUILD_DELETE": HandleGuildDelete(data); break;
                case "GUILD_MEMBER_UPDATE": HandleMemberUpdate(data); break;
                case "USER_UPDATE": HandleUserUpdate(data); break;
                case "MESSAGE_DELETE_BULK": await HandleBulkDelete(data); break;
                case "CHANNEL_RECIPIENT_ADD": HandleRecipient(data, true); break;
                case "CHANNEL_RECIPIENT_REMOVE": HandleRecipient(data, false); break;
                case "CHANNEL_CREATE": case "CHANNEL_UPDATE": HandleChannelUpdate(data); break;
                case "CHANNEL_DELETE": HandleChannelDelete(data); break;
                case "PRESENCE_UPDATE": HandlePresenceUpdate(data); break;
                case "GUILD_MEMBER_LIST_UPDATE": HandleMemberList(data); break;
                case "GUILD_ROLE_CREATE": case "GUILD_ROLE_UPDATE": HandleRoleUpsert(data); break;
                case "GUILD_ROLE_DELETE": HandleRoleDelete(data); break;
                case "MESSAGE_ACK": HandleAck(data); break;
                case "VOICE_STATE_UPDATE": HandleVoiceState(data); break;
                case "VOICE_SERVER_UPDATE": HandleVoiceServer(data); break;
                case "STREAM_CREATE": HandleStreamCreate(data); break;
                case "STREAM_SERVER_UPDATE": HandleStreamServer(data); break;
                case "STREAM_DELETE": HandleStreamDelete(data); break;
                case "STREAM_UPDATE": break;   // viewer list / paused — informational
                case "CALL_CREATE": case "CALL_UPDATE": HandleCall(data, false); break;
                case "CALL_DELETE": HandleCall(data, true); break;
                case "GUILD_EMOJIS_UPDATE": HandleEmojisUpdate(data); break;
                case "THREAD_LIST_SYNC": HandleThreadListSync(data); break;
                case "THREAD_CREATE": HandleThreadCreate(data); break;
                case "THREAD_UPDATE": HandleThreadUpdate(data); break;
                case "THREAD_DELETE": HandleThreadDelete(data); break;
                case "USER_GUILD_SETTINGS_UPDATE": HandleGuildSettingsUpdate(data); break;
                case "CHANNEL_UNREAD_UPDATE": break;
                case "READY_SUPPLEMENTAL": HandleReadySupplemental(data); break;
                // Fires whenever any of your logged-in clients changes status, so the tray dot keeps
                // up with a change made on your phone.
                case "SESSIONS_REPLACE": ApplySessions(data); break;
                case "RELATIONSHIP_ADD": HandleRelationship(data, true); break;
                case "RELATIONSHIP_REMOVE": HandleRelationship(data, false); break;
                // Echoes of our own slash command. Only the failure is worth surfacing — the bot's
                // actual reply arrives as a normal MESSAGE_CREATE.
                case "INTERACTION_FAILURE":
                    OnLog?.Invoke("The app didn't respond to that command.");
                    break;
                case "INTERACTION_CREATE": case "INTERACTION_SUCCESS": case "INTERACTION_MODAL_CREATE":
                case "USER_SETTINGS_UPDATE":
                case "GUILD_MEMBERS_CHUNK": case "GUILD_BAN_ADD": case "GUILD_BAN_REMOVE":
                case "GUILD_STICKERS_UPDATE": case "GUILD_SOUNDBOARD_SOUNDS_UPDATE":
                case "THREAD_MEMBER_UPDATE": case "THREAD_MEMBERS_UPDATE":
                case "PASSIVE_UPDATE_V2": case "PASSIVE_UPDATE_V1":
                case "CHANNEL_PINS_UPDATE": case "CHANNEL_PINS_ACK": case "GUILD_APPLICATION_COMMAND_INDEX_UPDATE":
                    break; // known but not surfaced in this client
                default:
                    Log.Activity($"GATEWAY (unhandled) {type}");
                    break;
            }
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"Dispatch error [{type}]: {ex.Message}");
        }
    }

    async Task HandleReady(JsonElement data)
    {
        _sessionId = data.GetProperty("session_id").GetString();
        ResetBackoff();

        // Self user
        var user = data.GetProperty("user");
        CurrentUser = user.Deserialize<UserSelfUser>(JsonOpts)!;
        CurrentUser.Client = this;
        // READY's user object carries no status, so UserSelfUser kept its "online" default and the
        // tray dot was green for an account actually set to dnd/idle/invisible. The live value is in
        // the parallel `sessions` array.
        if (data.TryGetProperty("sessions", out var sess)) ApplySessions(sess);

        // Guilds — core fields (name/icon) live under "properties" on the user gateway
        Guilds.Clear();
        GuildById.Clear();
        ChannelGuild.Clear();
        foreach (var g in data.GetProperty("guilds").EnumerateArray())
        {
            var guild = ReadGuild(g);
            if (guild == null) continue;
            Guilds.Add(guild);
            GuildById[guild.Id] = guild;
        }

        // Top-level user objects, referenced by id elsewhere in READY
        var users = new Dictionary<ulong, UserUser>();
        if (data.TryGetProperty("users", out var usersArr))
            foreach (var u in usersArr.EnumerateArray())
            {
                var uu = u.Deserialize<UserUser>(JsonOpts);
                if (uu != null) users[uu.Id] = uu;
            }

        // Private channels (DMs). Recipients arrive as recipient_ids referencing the top-level users.
        DMChannels.Clear();
        DmById.Clear();
        foreach (var pc in data.GetProperty("private_channels").EnumerateArray())
        {
            var type = pc.GetProperty("type").GetInt32();
            if (type != 1 && type != 3) continue; // DM or Group DM only

            var dm = new UserDMChannel
            {
                Id = ulong.Parse(pc.GetProperty("id").GetString()!),
                Type = type,
                Client = this
            };
            if (pc.TryGetProperty("last_message_id", out var lmid) && lmid.ValueKind == JsonValueKind.String
                && ulong.TryParse(lmid.GetString(), out var lm)) dm.LastMessageId = lm;
            if (type == 3)
            {
                if (pc.TryGetProperty("name", out var gn) && gn.ValueKind == JsonValueKind.String) dm.GroupName = gn.GetString();
                if (pc.TryGetProperty("icon", out var gi) && gi.ValueKind == JsonValueKind.String) dm.GroupIcon = gi.GetString();
            }
            // Resolve recipients: full objects if present, else recipient_ids against the users table
            if (pc.TryGetProperty("recipients", out var recipients))
                foreach (var r in recipients.EnumerateArray())
                {
                    var uu = r.Deserialize<UserUser>(JsonOpts);
                    if (uu != null) dm.Recipients.Add(uu);
                }
            else if (pc.TryGetProperty("recipient_ids", out var rids))
                foreach (var ridEl in rids.EnumerateArray())
                    if (ulong.TryParse(ridEl.GetString(), out var rid) && users.TryGetValue(rid, out var ruser))
                        dm.Recipients.Add(ruser);
            dm.Recipient = dm.Recipients.FirstOrDefault();
            DMChannels.Add(dm);
            DmById[dm.Id] = dm;
        }
        // Most-recent first
        DMChannels.Sort((a, b) => b.LastMessageId.CompareTo(a.LastMessageId));

        // Read state — what powers the unread dots and the mention badges.
        ReadStates.Clear();
        if (data.TryGetProperty("read_state", out var rs))
        {
            var entries = rs.ValueKind == JsonValueKind.Object && rs.TryGetProperty("entries", out var e) ? e : rs;
            if (entries.ValueKind == JsonValueKind.Array)
                foreach (var st in entries.EnumerateArray())
                {
                    var parsed = st.Deserialize<UserReadState>(JsonOpts);
                    if (parsed != null) ReadStates[parsed.ChannelId] = parsed;
                }
        }

        // Muted guilds/channels, so a muted channel doesn't shout at you in the sidebar.
        MutedChannels.Clear();
        MutedGuilds.Clear();
        ChannelNotifyLevels.Clear();
        GuildNotifyLevels.Clear();
        if (data.TryGetProperty("user_guild_settings", out var ugs))
        {
            var entries = ugs.ValueKind == JsonValueKind.Object && ugs.TryGetProperty("entries", out var e2) ? e2 : ugs;
            if (entries.ValueKind == JsonValueKind.Array)
                foreach (var gs in entries.EnumerateArray())
                {
                    if (gs.TryGetProperty("muted", out var mu) && mu.ValueKind == JsonValueKind.True
                        && gs.TryGetProperty("guild_id", out var gid) && ulong.TryParse(gid.GetString(), out var gv))
                        MutedGuilds.Add(gv);
                    // The guild's own notification level, which the channel overrides inherit from.
                    if (gs.TryGetProperty("message_notifications", out var gmn) && gmn.TryGetInt32(out var glvl)
                        && gs.TryGetProperty("guild_id", out var gid2) && ulong.TryParse(gid2.GetString(), out var gv2))
                        GuildNotifyLevels[gv2] = glvl;
                    if (!gs.TryGetProperty("channel_overrides", out var co) || co.ValueKind != JsonValueKind.Array) continue;
                    foreach (var ov in co.EnumerateArray())
                    {
                        if (ov.TryGetProperty("muted", out var m2) && m2.ValueKind == JsonValueKind.True
                            && ov.TryGetProperty("channel_id", out var cid) && ulong.TryParse(cid.GetString(), out var cv))
                            MutedChannels.Add(cv);
                        if (ov.TryGetProperty("message_notifications", out var mn) && mn.TryGetInt32(out var lvl)
                            && ov.TryGetProperty("channel_id", out var cid2) && ulong.TryParse(cid2.GetString(), out var cv2))
                            ChannelNotifyLevels[cv2] = lvl;
                    }
                }
        }

        // Friends list.
        Relationships.Clear();
        if (data.TryGetProperty("relationships", out var rels) && rels.ValueKind == JsonValueKind.Array)
            foreach (var r in rels.EnumerateArray())
            {
                var rel = r.Deserialize<UserRelationship>(JsonOpts);
                if (rel == null) continue;
                if (rel.User == null && users.TryGetValue(rel.Id, out var ru)) rel.User = ru;
                Relationships.Add(rel);
            }

        IsConnected = true;
        await (Ready?.Invoke() ?? Task.CompletedTask);
        // Do not compact the process heap here. READY already ran on the gateway thread, but GC is
        // process-wide and a full compaction would still freeze the WinForms thread. The idle
        // maintenance loop reclaims this short-lived allocation burst after the window is quiet.
    }

    // Shared by READY and GUILD_CREATE: both carry the same guild shape, READY nests the display
    // fields under "properties" while GUILD_CREATE puts them at the top level.
    UserGuild? ReadGuild(JsonElement g)
    {
        if (g.TryGetProperty("unavailable", out var un) && un.ValueKind == JsonValueKind.True) return null;
        var guild = g.Deserialize<UserGuild>(JsonOpts);
        if (guild == null) return null;
        guild.Client = this;
        if (g.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            if (props.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) guild.Name = n.GetString()!;
            if (props.TryGetProperty("icon", out var ic) && ic.ValueKind == JsonValueKind.String) guild.Icon = ic.GetString();
            if (props.TryGetProperty("banner", out var bn) && bn.ValueKind == JsonValueKind.String) guild.Banner = bn.GetString();
            if (props.TryGetProperty("owner_id", out var oi) && ulong.TryParse(oi.GetString(), out var ov)) guild.OwnerId = ov;
        }
        if (g.TryGetProperty("member_count", out var mc) && mc.TryGetInt32(out var mcv)) guild.MemberCount = mcv;
        guild.Reindex();
        foreach (var c in guild.Channels) ChannelGuild[c.Id] = guild.Id;
        return guild;
    }

    async Task HandleMessageCreate(JsonElement data)
    {
        var msg = ParseMessage(data);
        if (msg == null) return;
        // Keep the DM ordering and the channel's unread marker fresh without a refetch — the
        // sidebar's unread dot compares this against the read state.
        if (DmById.TryGetValue(msg.ChannelId, out var dm))
        {
            dm.LastMessageId = msg.Id;
            dm.LastPreview = dm.PreviewOf(msg.Content, msg.Attachments.Count, msg.Stickers.Count, msg.Embeds.Count,
                                          msg.Author?.Id ?? 0, msg.Member?.DisplayName ?? msg.Author?.DisplayName ?? "Someone");
            dm.PreviewFetched = true;
        }
        if (GuildOfChannel(msg.ChannelId)?.ChannelById.GetValueOrDefault(msg.ChannelId) is { } ch)
            ch.LastMessageId = msg.Id;
        if (msg.GuildId is { } tgid && GuildById.TryGetValue(tgid, out var tg)
            && tg.ThreadById.TryGetValue(msg.ChannelId, out var th))
            th.LastMessageId = msg.Id;   // keeps the sidebar thread row's ordering + unread fresh
        if (msg.Author?.Id != CurrentUser?.Id) BumpUnread(msg);
        if (MessageReceived != null) await MessageReceived(msg);
    }

    void BumpUnread(UserMessage m)
    {
        // The "last read" marker deliberately stays where it is — the channel is now behind, which
        // is exactly what IsUnread compares against.
        if (!ReadStates.TryGetValue(m.ChannelId, out var st))
            ReadStates[m.ChannelId] = st = new UserReadState { ChannelId = m.ChannelId };
        var myRoles = m.GuildId is { } gid && GuildById.TryGetValue(gid, out var g)
            ? g.GetMember(CurrentUser?.Id ?? 0)?.RoleIds : null;
        if (m.MentionsMe(CurrentUser?.Id ?? 0, myRoles)) st.MentionCount++;
        ReadStateChanged?.Invoke();
    }

    // READY hands us last_message_id for every DM but not the message, so a freshly-launched client
    // has nothing to show under the name. Fetch one message per conversation, on demand and once —
    // the caller only asks for the rows that are actually on screen, and three at a time keeps the
    // login burst off the rate limiter.
    readonly SemaphoreSlim _previewGate = new(3);

    public async Task FillDmPreviewAsync(UserDMChannel dm, Action? then = null)
    {
        if (dm.PreviewFetched || dm.LastMessageId == 0) return;
        dm.PreviewFetched = true;
        await _previewGate.WaitAsync();
        try
        {
            var m = (await Rest.GetMessagesAsync(dm.Id, 1)).FirstOrDefault();
            if (m == null) return;
            dm.LastPreview = dm.PreviewOf(m.Content, m.Attachments.Count, m.Stickers.Count, m.Embeds.Count,
                                          m.Author?.Id ?? 0, m.Member?.DisplayName ?? m.Author?.DisplayName ?? "Someone");
            then?.Invoke();
        }
        catch { }
        finally { _previewGate.Release(); }
    }

    public bool IsUnread(ulong channelId, ulong? lastMessageId)
    {
        if (lastMessageId is not { } last || last == 0) return false;
        return !ReadStates.TryGetValue(channelId, out var st) || st.LastMessageId < last;
    }

    public int MentionCount(ulong channelId) => ReadStates.TryGetValue(channelId, out var st) ? st.MentionCount : 0;

    public void MarkRead(ulong channelId, ulong messageId)
    {
        if (messageId == 0) return;
        if (!ReadStates.TryGetValue(channelId, out var st))
            ReadStates[channelId] = st = new UserReadState { ChannelId = channelId };
        st.LastMessageId = messageId;
        st.MentionCount = 0;
        ReadStateChanged?.Invoke();
        _ = Rest.AckAsync(channelId, messageId);
    }

    void HandleAck(JsonElement data)
    {
        if (!data.TryGetProperty("channel_id", out var c) || !ulong.TryParse(c.GetString(), out var cid)) return;
        if (!data.TryGetProperty("message_id", out var m) || !ulong.TryParse(m.GetString(), out var mid)) return;
        if (!ReadStates.TryGetValue(cid, out var st)) ReadStates[cid] = st = new UserReadState { ChannelId = cid };
        st.LastMessageId = mid;
        st.MentionCount = 0;
        ReadStateChanged?.Invoke();
    }

    async Task HandleMessageUpdate(JsonElement data)
    {
        var msg = ParseMessage(data);
        if (msg != null && MessageUpdated != null)
            await MessageUpdated(msg);
    }

    async Task HandleMessageDelete(JsonElement data)
    {
        var msgId = ulong.Parse(data.GetProperty("id").GetString()!);
        var channelId = ulong.Parse(data.GetProperty("channel_id").GetString()!);
        if (MessageDeleted != null) await MessageDeleted(msgId, channelId);
    }

    void HandleReaction(JsonElement data, ReactionDelta.Op kind)
    {
        if (!data.TryGetProperty("message_id", out var mi) || !ulong.TryParse(mi.GetString(), out var msgId)) return;
        if (!data.TryGetProperty("channel_id", out var ci) || !ulong.TryParse(ci.GetString(), out var chId)) return;

        UserEmoji? emoji = null;
        if (data.TryGetProperty("emoji", out var em) && em.ValueKind == JsonValueKind.Object)
        {
            emoji = new UserEmoji
            {
                Name = em.TryGetProperty("name", out var n) ? n.GetString() : null,
                Animated = em.TryGetProperty("animated", out var a) && a.ValueKind == JsonValueKind.True,
            };
            // Custom emoji ids arrive as strings; a unicode reaction has a null id.
            if (em.TryGetProperty("id", out var idv) && idv.ValueKind == JsonValueKind.String
                && ulong.TryParse(idv.GetString(), out var eid)) emoji.Id = eid;
        }

        ulong userId = data.TryGetProperty("user_id", out var uv) && ulong.TryParse(uv.GetString(), out var u) ? u : 0;
        int answerId = data.TryGetProperty("answer_id", out var av) && av.TryGetInt32(out var ai) ? ai : 0;

        ReactionChanged?.Invoke(msgId, chId, new ReactionDelta(kind, emoji, userId, answerId));
    }

    async Task HandleTyping(JsonElement data)
    {
        var te = new UserTypingEvent
        {
            UserId = ulong.Parse(data.GetProperty("user_id").GetString()!),
            ChannelId = ulong.Parse(data.GetProperty("channel_id").GetString()!),
        };
        if (data.TryGetProperty("guild_id", out var gid) && ulong.TryParse(gid.GetString(), out var gv)) te.GuildId = gv;

        // Prefer the guild nickname, then the cached member/DM recipient, then the raw payload.
        if (te.GuildId is { } g && GuildById.TryGetValue(g, out var guild) && guild.GetMember(te.UserId) is { } mem)
            te.Username = mem.DisplayName;
        else if (data.TryGetProperty("member", out var member) && member.TryGetProperty("user", out var tu))
            te.Username = tu.TryGetProperty("global_name", out var gn) && gn.ValueKind == JsonValueKind.String
                ? gn.GetString()! : tu.GetProperty("username").GetString()!;
        else if (DmById.TryGetValue(te.ChannelId, out var dm))
            te.Username = dm.Recipients.FirstOrDefault(r => r.Id == te.UserId)?.DisplayName ?? "Someone";
        else te.Username = "Someone";

        if (UserTyping != null) await UserTyping(te);
    }

    void HandleGuildCreate(JsonElement data)
    {
        var fresh = ReadGuild(data);
        if (fresh == null) return;
        var existing = GuildById.GetValueOrDefault(fresh.Id);
        if (existing == null)
        {
            Guilds.Add(fresh);
            GuildById[fresh.Id] = fresh;
            // After READY this is a server we just joined, so the rail has to grow a button.
            if (IsConnected) GuildJoined?.Invoke(fresh);
        }
        else
        {
            // Keep the same instance so open UI keeps pointing at live data.
            if (!string.IsNullOrEmpty(fresh.Name)) existing.Name = fresh.Name;
            existing.Icon ??= fresh.Icon;
            if (fresh.OwnerId != 0) existing.OwnerId = fresh.OwnerId;
            if (fresh.Channels.Count > 0) existing.Channels = fresh.Channels;
            if (fresh.Roles.Count > 0) existing.Roles = fresh.Roles;
            if (fresh.Members.Count > 0) existing.Members = fresh.Members;
            if (fresh.Emojis.Count > 0) existing.Emojis = fresh.Emojis;
            if (fresh.Stickers.Count > 0) existing.Stickers = fresh.Stickers;
            if (fresh.VoiceStates.Count > 0) existing.VoiceStates = fresh.VoiceStates;
            if (fresh.MemberCount > 0) existing.MemberCount = fresh.MemberCount;
            existing.Reindex();
            foreach (var c in existing.Channels) ChannelGuild[c.Id] = existing.Id;
        }
        // Leave burst cleanup to the idle maintenance loop; even a throttled gen-2 collection can
        // pause the UI while guild-create events arrive.
    }

    // Name/icon edits. The payload is a full guild object but only the properties are trustworthy —
    // channels/roles arrive separately — so this touches nothing else and keeps the same instance.
    void HandleGuildUpdate(JsonElement data)
    {
        if (!data.TryGetProperty("id", out var idp) || !ulong.TryParse(idp.GetString(), out var id)) return;
        if (!GuildById.TryGetValue(id, out var guild)) return;
        var src = data.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object ? props : data;
        if (src.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } nm) guild.Name = nm;
        if (src.TryGetProperty("icon", out var ic)) guild.Icon = ic.ValueKind == JsonValueKind.String ? ic.GetString() : null;
        if (src.TryGetProperty("banner", out var bn)) guild.Banner = bn.ValueKind == JsonValueKind.String ? bn.GetString() : null;
        GuildChanged?.Invoke(guild);
    }

    // GUILD_DELETE also fires for a server *outage*, flagged unavailable — dropping it then would
    // wipe the rail every time Discord moved a shard.
    void HandleGuildDelete(JsonElement data)
    {
        if (!data.TryGetProperty("id", out var idp) || !ulong.TryParse(idp.GetString(), out var id)) return;
        if (data.TryGetProperty("unavailable", out var un) && un.ValueKind == JsonValueKind.True) return;
        if (!GuildById.Remove(id, out var guild)) return;
        Guilds.Remove(guild);
        foreach (var c in guild.Channels) ChannelGuild.Remove(c.Id);
        GuildLeft?.Invoke(id);
    }

    // Nickname, roles and avatar changes. Without this a rename or a role grant only showed up
    // after a restart, and role colours in chat stayed at the old value.
    void HandleMemberUpdate(JsonElement data)
    {
        if (!data.TryGetProperty("guild_id", out var g) || !ulong.TryParse(g.GetString(), out var gid)) return;
        if (!GuildById.TryGetValue(gid, out var guild)) return;
        var fresh = data.Deserialize<UserMember>(JsonOpts);
        if (fresh?.User == null) return;

        if (guild.MemberById.TryGetValue(fresh.User.Id, out var old))
        {
            old.Nick = fresh.Nick;
            old.Roles = fresh.Roles;
            old.Avatar = fresh.Avatar;
            // Presence isn't in this payload; keep whatever PRESENCE_UPDATE last set.
            old.User.Username = fresh.User.Username;
            old.User.GlobalName = fresh.User.GlobalName;
            old.User.Avatar = fresh.User.Avatar;
        }
        else
        {
            guild.Members.Add(fresh);
            guild.MemberById[fresh.User.Id] = fresh;
        }
        MemberListUpdated?.Invoke(guild);
    }

    void HandleUserUpdate(JsonElement data)
    {
        var fresh = data.Deserialize<UserSelfUser>(JsonOpts);
        if (fresh == null || CurrentUser == null) return;
        CurrentUser.Username = fresh.Username;
        CurrentUser.GlobalName = fresh.GlobalName;
        CurrentUser.Avatar = fresh.Avatar;
        SelfChanged?.Invoke();
    }

    async Task HandleBulkDelete(JsonElement data)
    {
        if (!data.TryGetProperty("channel_id", out var ci) || !ulong.TryParse(ci.GetString(), out var chId)) return;
        if (!data.TryGetProperty("ids", out var ids) || ids.ValueKind != JsonValueKind.Array) return;
        foreach (var idp in ids.EnumerateArray())
            if (ulong.TryParse(idp.GetString(), out var mid) && MessageDeleted != null)
                await MessageDeleted.Invoke(mid, chId);
    }

    // Someone was added to or left a group DM.
    void HandleRecipient(JsonElement data, bool added)
    {
        if (!data.TryGetProperty("channel_id", out var ci) || !ulong.TryParse(ci.GetString(), out var chId)) return;
        if (!DmById.TryGetValue(chId, out var dm)) return;
        var user = data.TryGetProperty("user", out var u) ? u.Deserialize<UserUser>(JsonOpts) : null;
        if (user == null) return;
        dm.Recipients.RemoveAll(r => r.Id == user.Id);
        if (added) dm.Recipients.Add(user);
        ReadStateChanged?.Invoke();   // the DM list rerenders off this
    }

    // The member sidebar for a user account arrives here in ranges, already grouped the way
    // Discord renders it: one group per hoisted role, then "online", then "offline".
    void HandleMemberList(JsonElement data)
    {
        if (!data.TryGetProperty("guild_id", out var gp) || !ulong.TryParse(gp.GetString(), out var gid)) return;
        if (!GuildById.TryGetValue(gid, out var guild)) return;

        if (data.TryGetProperty("member_count", out var mc) && mc.TryGetInt32(out var mcv)) guild.MemberCount = mcv;
        if (data.TryGetProperty("online_count", out var oc) && oc.TryGetInt32(out var ocv)) guild.OnlineCount = ocv;
        if (!data.TryGetProperty("ops", out var ops) || ops.ValueKind != JsonValueKind.Array) return;

        bool changed = false;
        // We subscribe to several 100-row ranges at once, and Discord answers with one SYNC op per
        // range in a single dispatch. Clearing inside the loop threw away every range but the last,
        // which is what capped the member list at its first hundred rows however far you scrolled.
        // Clear once, then append the ranges in the order they arrive.
        bool cleared = false;

        foreach (var op in ops.EnumerateArray())
        {
            var kind = op.TryGetProperty("op", out var k) ? k.GetString() : null;
            if (kind is not ("SYNC" or "UPDATE" or "INSERT")) continue;

            var items = op.TryGetProperty("items", out var it) ? it
                      : op.TryGetProperty("item", out var one) ? one : default;
            if (items.ValueKind == JsonValueKind.Undefined) continue;

            if (kind == "SYNC" && !cleared)
            {
                cleared = true;
                guild.MemberGroups.Clear();
                guild.Members.Clear();
            }

            var arr = items.ValueKind == JsonValueKind.Array ? items.EnumerateArray().ToList() : new List<JsonElement> { items };
            foreach (var entry in arr)
            {
                if (entry.TryGetProperty("group", out var grp))
                {
                    var id = grp.TryGetProperty("id", out var gi) ? gi.ToString() : "";
                    // Discord's visual refresh shows role names in their own case, not upper-cased.
                    var label = id switch
                    {
                        "online" => "Online",
                        "offline" => "Offline",
                        _ => ulong.TryParse(id, out var rid) && guild.RoleById.TryGetValue(rid, out var role)
                                ? role.Name : id,
                    };
                    // A group that spans a range boundary has its header repeated at the top of the
                    // next range. Re-adding it would split "Online" into two headers mid-list.
                    if (guild.MemberGroups.Count == 0 || guild.MemberGroups[^1].Label != label)
                        // The count in the payload is 0 for SYNC ops; the renderer counts the rows.
                        guild.MemberGroups.Add((label, new List<UserMember>()));
                    changed = true;
                }
                else if (entry.TryGetProperty("member", out var mem))
                {
                    var m = ParseListMember(mem);
                    if (m == null) continue;
                    // UPDATE/INSERT ops re-send someone who is already listed — and a member moving
                    // between groups (going online) arrives as an insert into the new group without
                    // a removal from the old. Appending blindly showed the same person twice.
                    foreach (var (_, members) in guild.MemberGroups) members.RemoveAll(x => x.User.Id == m.User.Id);
                    guild.Members.RemoveAll(x => x.User.Id == m.User.Id);
                    guild.Members.Add(m);
                    guild.MemberById[m.User.Id] = m;
                    if (guild.MemberGroups.Count == 0) guild.MemberGroups.Add(("MEMBERS", new List<UserMember>()));
                    guild.MemberGroups[^1].Members.Add(m);
                    changed = true;
                }
            }
        }
        if (changed) MemberListUpdated?.Invoke(guild);
    }

    // A merged member has "user_id" instead of a nested user object, so the user has to be stitched
    // back on from whatever is already cached.
    void ApplyMergedMember(UserGuild guild, JsonElement m)
    {
        if (m.ValueKind != JsonValueKind.Object) return;
        if (!m.TryGetProperty("user_id", out var ui) || !ulong.TryParse(ui.GetString(), out var uid)) return;
        var member = m.Deserialize<UserMember>(JsonOpts);
        if (member == null) return;
        member.User = uid == CurrentUser?.Id
            ? new UserUser
              {
                  Id = uid, Username = CurrentUser.Username,
                  GlobalName = CurrentUser.GlobalName, Avatar = CurrentUser.Avatar,
              }
            : guild.MemberById.GetValueOrDefault(uid)?.User ?? new UserUser { Id = uid, Username = "Unknown" };
        guild.Members.RemoveAll(x => x.User?.Id == uid);
        guild.Members.Add(member);
        guild.MemberById[uid] = member;
    }

    // Fallback for the case where READY_SUPPLEMENTAL didn't carry us: one request per guild, only
    // when that guild is actually opened, and only once.
    public async Task EnsureSelfMemberAsync(UserGuild guild)
    {
        if (CurrentUser == null || guild.MemberById.ContainsKey(CurrentUser.Id)) return;
        var member = await Rest.GetSelfMemberAsync(guild.Id);
        if (member == null) return;
        member.User = new UserUser
        {
            Id = CurrentUser.Id, Username = CurrentUser.Username,
            GlobalName = CurrentUser.GlobalName, Avatar = CurrentUser.Avatar,
        };
        guild.Members.RemoveAll(x => x.User?.Id == CurrentUser.Id);
        guild.Members.Add(member);
        guild.MemberById[CurrentUser.Id] = member;
        SelfMemberLoaded?.Invoke();
    }

    UserMember? ParseListMember(JsonElement mem)
    {
        var m = mem.Deserialize<UserMember>(JsonOpts);
        if (m?.User == null) return null;
        // Presence rides along with the member in this payload — that is the only place a user
        // account learns who is online without a PRESENCE_UPDATE for each of them.
        if (mem.TryGetProperty("presence", out var pres) && pres.ValueKind == JsonValueKind.Object)
        {
            if (pres.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String)
                m.User.Status = st.GetString()!;
            ApplyActivities(m.User, pres);
        }
        return m;
    }

    static void ApplyActivities(UserUser u, JsonElement presence)
    {
        if (!presence.TryGetProperty("activities", out var acts) || acts.ValueKind != JsonValueKind.Array) return;
        u.CustomStatus = null;
        u.Activity = null;
        u.ActivityVerb = null;
        u.Streaming = false;
        foreach (var a in acts.EnumerateArray())
        {
            int type = a.TryGetProperty("type", out var t) && t.TryGetInt32(out var tv) ? tv : 0;
            var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (type == 4)   // custom status
            {
                var state = a.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                var emoji = a.TryGetProperty("emoji", out var e) && e.ValueKind == JsonValueKind.Object
                            && e.TryGetProperty("name", out var en) ? en.GetString() : null;
                u.CustomStatus = string.Join(" ", new[] { emoji, state }.Where(x => !string.IsNullOrEmpty(x)));
                if (u.CustomStatus.Length == 0) u.CustomStatus = null;
            }
            else if (u.Activity == null && name != null)
            {
                u.Activity = name;
                u.ActivityVerb = type switch
                {
                    1 => "Streaming",
                    2 => "Listening to",
                    3 => "Watching",
                    5 => "Competing in",
                    _ => "Playing",
                };
                if (type == 1) u.Streaming = true;
            }
        }
    }

    void HandleChannelUpdate(JsonElement data)
    {
        var guildId = data.TryGetProperty("guild_id", out var gid) && gid.ValueKind != JsonValueKind.Null
            ? ulong.Parse(gid.GetString()!) : (ulong?)null;
        if (guildId == null) return; // skip DM channel updates (no guild_id)
        if (!GuildById.TryGetValue(guildId.Value, out var guild)) return;
        var fresh = data.Deserialize<UserChannelData>(JsonOpts);
        if (fresh == null) return;
        var existing = guild.ChannelById.GetValueOrDefault(fresh.Id);
        if (existing == null) guild.Channels.Add(fresh);
        else
        {
            existing.Name = fresh.Name;
            existing.Topic = fresh.Topic;
            existing.Position = fresh.Position;
            existing.ParentId = fresh.ParentId;
            existing.Nsfw = fresh.Nsfw;
            existing.PermissionOverwrites = fresh.PermissionOverwrites;
        }
        guild.Reindex();
        ChannelGuild[fresh.Id] = guild.Id;
    }

    void HandleChannelDelete(JsonElement data)
    {
        var chId = ulong.Parse(data.GetProperty("id").GetString()!);
        var guildId = data.TryGetProperty("guild_id", out var gid) && gid.ValueKind != JsonValueKind.Null
            ? ulong.Parse(gid.GetString()!) : (ulong?)null;
        if (guildId != null && GuildById.TryGetValue(guildId.Value, out var guild))
        {
            guild.Channels.RemoveAll(c => c.Id == chId);
            guild.Reindex();
            ChannelGuild.Remove(chId);
        }
        else if (guildId == null)
        {
            // A DM or group DM was closed (from this client or another). All mutations of the DM
            // lists happen here on the gateway thread — the UI only ever reads them — so closing
            // a DM can never race an incoming message.
            DMChannels.RemoveAll(d => d.Id == chId);
            DmById.Remove(chId);
            ReadStates.Remove(chId);
            ChannelGuild.Remove(chId);
            DmClosed?.Invoke(chId);
        }
    }

    void HandleRoleUpsert(JsonElement data)
    {
        if (!data.TryGetProperty("guild_id", out var g) || !ulong.TryParse(g.GetString(), out var gid)) return;
        if (!GuildById.TryGetValue(gid, out var guild)) return;
        var role = data.GetProperty("role").Deserialize<UserRole>(JsonOpts);
        if (role == null) return;
        guild.Roles.RemoveAll(r => r.Id == role.Id);
        guild.Roles.Add(role);
        guild.Reindex();
    }

    // ── Voice ──

    // The channel this account is sitting in, so the UI can render "connected" and offer Disconnect.
    public ulong? MyVoiceChannel { get; private set; }
    public ulong? MyVoiceGuild { get; private set; }
    public bool SelfMute { get; private set; }
    public bool SelfDeaf { get; private set; }

    // ── DM / group calls ──
    //
    // A call in a private channel has no guild, so none of the guild voice bookkeeping applies. The
    // gateway describes it with CALL_CREATE/UPDATE/DELETE (who is being rung) plus ordinary
    // guild-less VOICE_STATE_UPDATEs (who has actually picked up).
    public readonly Dictionary<ulong, DmCall> Calls = new();

    public DmCall? GetCall(ulong channelId) => Calls.GetValueOrDefault(channelId);

    // "Someone is ringing *me* right now" — the cue for the incoming-call UI.
    public DmCall? IncomingCall =>
        Calls.Values.FirstOrDefault(c => c.Ringing.Contains(CurrentUser?.Id ?? 0) && !c.Participants.Contains(CurrentUser?.Id ?? 0));

    void HandleCall(JsonElement data, bool deleted)
    {
        if (!data.TryGetProperty("channel_id", out var c) || !ulong.TryParse(c.GetString(), out var cid)) return;
        if (deleted) { Calls.Remove(cid); CallChanged?.Invoke(cid); return; }

        var call = Calls.TryGetValue(cid, out var e) ? e : Calls[cid] = new DmCall { ChannelId = cid };
        if (data.TryGetProperty("ringing", out var r) && r.ValueKind == JsonValueKind.Array)
        {
            call.Ringing.Clear();
            foreach (var x in r.EnumerateArray())
                if (ulong.TryParse(x.GetString(), out var uid)) call.Ringing.Add(uid);
        }
        // CALL_CREATE carries the voice states of whoever is already in; later CALL_UPDATEs don't.
        if (data.TryGetProperty("voice_states", out var vs) && vs.ValueKind == JsonValueKind.Array)
        {
            call.Participants.Clear();
            foreach (var x in vs.EnumerateArray())
                if (x.TryGetProperty("user_id", out var u) && ulong.TryParse(u.GetString(), out var uid))
                    call.Participants.Add(uid);
        }
        CallChanged?.Invoke(cid);
    }

    void HandleVoiceState(JsonElement data)
    {
        var v = data.Deserialize<UserVoiceState>(JsonOpts);
        if (v == null) return;

        // Guild-less voice states are DM/group-call participation. Track them per channel so the
        // call UI knows who is actually on the line, and drop the person from `ringing` — Discord
        // stops showing them as "being called" the moment they answer.
        if (v.GuildId == null)
        {
            foreach (var call in Calls.Values)
                if (call.ChannelId != v.ChannelId && call.Participants.Remove(v.UserId)) CallChanged?.Invoke(call.ChannelId);
            if (v.ChannelId is { } cid)
            {
                var call = Calls.TryGetValue(cid, out var e) ? e : Calls[cid] = new DmCall { ChannelId = cid };
                if (!call.Participants.Contains(v.UserId)) call.Participants.Add(v.UserId);
                call.Ringing.Remove(v.UserId);
                call.States[v.UserId] = v;
                CallChanged?.Invoke(cid);
            }
        }

        // Our own state carries the session id the voice websocket has to identify with.
        if (v.UserId == CurrentUser?.Id)
        {
            if (data.TryGetProperty("session_id", out var sid) && sid.GetString() is { } s)
            {
                VoiceSessionId = s;
                Log.Voice($"VOICE_STATE_UPDATE self channel={v.ChannelId?.ToString() ?? "none"} session captured");
                TryFireVoiceServer();
            }
            MyVoiceChannel = v.ChannelId;
            MyVoiceGuild = v.ChannelId == null ? null : v.GuildId;
            SelfMute = v.SelfMute;
            SelfDeaf = v.SelfDeaf;
        }

        // A member's Go Live shows up ONLY as self_stream on their voice state — STREAM_CREATE is
        // dispatched to the broadcaster, not to the channel, so waiting for it meant we never
        // noticed a peer had started sharing. This is the moment to ask to watch (op 20).
        if (v.UserId != CurrentUser?.Id && MyVoiceChannel is { } mine && v.ChannelId == mine)
        {
            if (v.SelfStream && WatchingStreamKey == null) _ = WatchStreamAsync(v.UserId);
            else if (!v.SelfStream && WatchingStreamKey == StreamKeyFor(v.UserId)) StopWatching();
        }
        // Joining a channel where someone is ALREADY live: their state arrived with the guild, not
        // as an update, so there is no self_stream transition to catch. Scan once on our own join.
        else if (v.UserId == CurrentUser?.Id && WatchingStreamKey == null
                 && MyVoiceChannel is { } joined && MyVoiceGuild is { } jg
                 && GuildById.TryGetValue(jg, out var jGuild))
        {
            foreach (var s in jGuild.VoiceIn(joined))
                if (s.SelfStream && s.UserId != v.UserId) { _ = WatchStreamAsync(s.UserId); break; }
        }

        if (v.GuildId is { } gid && GuildById.TryGetValue(gid, out var guild))
        {
            guild.ApplyVoice(v);
            VoiceChanged?.Invoke(guild);
        }
        else if (v.UserId == CurrentUser?.Id && v.ChannelId == null)
        {
            VoiceChanged?.Invoke(null);
        }
    }

    // ── voice server handshake ──
    //
    // Joining a channel with op 4 makes the gateway answer with two events: VOICE_STATE_UPDATE
    // (which carries our session_id) and VOICE_SERVER_UPDATE (the voice host + a one-shot token).
    // Those four values are the entire credential set the voice websocket needs. They can arrive in
    // either order, so fire only once both halves are in hand.
    public string? VoiceSessionId { get; private set; }
    public event Action<VoiceServerInfo>? VoiceServerReady;

    VoiceServerInfo? _pendingVoiceServer;

    void HandleVoiceServer(JsonElement data)
    {
        var token = data.TryGetProperty("token", out var t) ? t.GetString() : null;
        var endpoint = data.TryGetProperty("endpoint", out var e) && e.ValueKind != JsonValueKind.Null
            ? e.GetString() : null;
        // A null endpoint means Discord is moving us to a different voice server; another
        // VOICE_SERVER_UPDATE follows with the real one.
        if (token == null || endpoint == null) return;

        ulong serverId = 0;
        if (data.TryGetProperty("guild_id", out var g) && g.ValueKind != JsonValueKind.Null)
            ulong.TryParse(g.GetString(), out serverId);
        else if (data.TryGetProperty("channel_id", out var c) && c.ValueKind != JsonValueKind.Null)
            ulong.TryParse(c.GetString(), out serverId);   // DM/group calls key on the channel

        Log.Voice($"VOICE_SERVER_UPDATE endpoint={endpoint} server={serverId} (session={(string.IsNullOrEmpty(VoiceSessionId) ? "pending" : "have")})");
        _pendingVoiceServer = new VoiceServerInfo(endpoint, token, serverId, VoiceSessionId ?? "",
                                                  CurrentUser?.Id ?? 0, MyVoiceChannel ?? 0);
        TryFireVoiceServer();
    }

    void TryFireVoiceServer()
    {
        if (_pendingVoiceServer is not { } info) return;
        if (string.IsNullOrEmpty(VoiceSessionId)) { Log.Voice("voice server held: no session id yet"); return; }
        if (VoiceServerReady == null) { Log.Voice("voice server ready but nothing is listening"); return; }
        _pendingVoiceServer = null;
        Log.Voice("voice credentials complete — starting media engine");
        VoiceServerReady.Invoke(info with { SessionId = VoiceSessionId, ChannelId = MyVoiceChannel ?? info.ChannelId });
    }

    // ── Go Live (screen share) ──────────────────────────────────────────────────────────────────
    // Screen sharing is NOT part of the voice connection. Discord runs it as a second, independent
    // RTC session ("Go Live"): op 18 asks the gateway to create a stream, which answers with a
    // STREAM_CREATE dispatch (the stream key + rtc server) and a STREAM_SERVER_UPDATE (endpoint +
    // its own one-shot token). The screen video then rides that connection on a "screen" stream —
    // adding a screen entry to the voice connection's op 12 is what a client does for a DM call's
    // camera plane, and the SFU simply ignores it in a guild channel. That is why the previous
    // screenshare produced traffic nobody could ever watch.
    //
    // Stream key: "guild:<guild>:<channel>:<user>" for a guild VC, "call:<channel>:<user>" in a DM.
    public string? ActiveStreamKey { get; private set; }      // ours, when broadcasting
    public string? WatchingStreamKey { get; private set; }    // a peer's, when watching
    public event Action<VoiceServerInfo>? StreamServerReady;          // ours to broadcast on
    public event Action<ulong, VoiceServerInfo>? StreamWatchReady;    // (broadcaster, credentials)
    public event Action? StreamEnded;
    public event Action? StreamWatchEnded;

    /// The server_id to retry the stream identify with if the first one is rejected.
    public ulong StreamAltServerId { get; private set; }

    string? _pendingStreamKey;
    // rtc_server_id per stream key. STREAM_CREATE is dispatched to EVERY member of the channel,
    // so a peer going live hands us their rtc id before we ever ask to watch.
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, ulong> _streamRtc = new();

    /// The stream key a member's Go Live in our current channel would use.
    public string? StreamKeyFor(ulong userId)
    {
        if (MyVoiceChannel is not { } ch) return null;
        return MyVoiceGuild is { } g ? $"guild:{g}:{ch}:{userId}" : $"call:{ch}:{userId}";
    }

    /// op 20 STREAM_WATCH — ask to receive a member's screen share. The gateway answers with a
    /// STREAM_SERVER_UPDATE for their key, which is a second voice connection we join as a viewer.
    public async Task WatchStreamAsync(ulong userId)
    {
        var key = StreamKeyFor(userId);
        if (key == null || key == WatchingStreamKey) return;
        WatchingStreamKey = key;
        Log.Voice("STREAM_WATCH " + key);
        await SendJson(new { op = 20, d = new { stream_key = key } });
    }

    public void StopWatching()
    {
        if (WatchingStreamKey == null) return;
        WatchingStreamKey = null;
        StreamWatchEnded?.Invoke();
    }

    /// op 21 STREAM_PING — a viewer's keepalive; the gateway drops the subscription without it.
    public async Task StreamPingAsync()
    {
        if (WatchingStreamKey is not { } key) return;
        await SendJson(new { op = 21, d = new { stream_key = key } });
    }

    public async Task GoLiveAsync()
    {
        if (MyVoiceChannel is not { } ch) return;
        var guild = MyVoiceGuild;
        Log.Voice($"STREAM_CREATE type={(guild == null ? "call" : "guild")} channel={ch}");
        await SendJson(new
        {
            op = 18,
            d = guild == null
                ? new { type = "call", channel_id = ch.ToString(), preferred_region = (string?)null }
                : (object)new { type = "guild", guild_id = guild.Value.ToString(),
                                channel_id = ch.ToString(), preferred_region = (string?)null },
        });
    }

    public async Task StopGoLiveAsync()
    {
        var key = ActiveStreamKey ?? _pendingStreamKey;
        ActiveStreamKey = null;
        _pendingStreamKey = null;
        if (key == null) return;
        Log.Voice("STREAM_DELETE " + key);
        await SendJson(new { op = 19, d = new { stream_key = key } });
        StreamEnded?.Invoke();
    }

    /// The `server_id` the stream gateway's identify needs, read off the stream key: the guild for
    /// "guild:<guild>:<channel>:<user>", the channel for a DM call's "call:<channel>:<user>".
    /// Identifying with the wrong id is rejected with a 4004 and the share never starts.
    public static ulong StreamKeyServerId(string key)
    {
        var parts = key.Split(':');
        return parts.Length >= 2 && ulong.TryParse(parts[1], out var id) ? id : 0;
    }

    void HandleStreamCreate(JsonElement d)
    {
        var key = d.TryGetProperty("stream_key", out var k) ? k.GetString() : null;
        if (key == null) return;
        // rtc_server_id is the id the VOICE server knows this stream session by, and it is what
        // the stream gateway's identify must send as `server_id` — NOT the guild id. Identifying
        // with the guild is rejected 4006 ("session no longer valid"), which on the other end
        // looks like a Go Live tile appearing and vanishing half a second later.
        ulong rtc = 0;
        if (d.TryGetProperty("rtc_server_id", out var r))
        {
            if (r.ValueKind == JsonValueKind.String) ulong.TryParse(r.GetString(), out rtc);
            else if (r.ValueKind == JsonValueKind.Number) rtc = r.GetUInt64();
        }
        if (rtc != 0) _streamRtc[key] = rtc;
        Log.Voice($"STREAM_CREATE key={key} rtc={rtc}");

        // This dispatch reaches every member of the channel, so it is also how we learn a PEER has
        // gone live — and the moment to ask to watch. Discord makes you click "Watch Stream"; with
        // one screen share in the channel there is nothing to choose between, so we just watch it.
        // Ours to broadcast on. (A peer's Go Live does NOT reach us here — that dispatch is only
        // sent to its broadcaster; self_stream on their voice state is the signal we act on.)
        if (StreamKeyOwner(key) == CurrentUser?.Id) _pendingStreamKey = key;
    }

    void HandleStreamDelete(JsonElement d)
    {
        var key = d.TryGetProperty("stream_key", out var k) ? k.GetString() : null;
        if (key != null) _streamRtc.TryRemove(key, out _);
        if (key == null || key == ActiveStreamKey) { ActiveStreamKey = null; StreamEnded?.Invoke(); }
        if (key == null || key == WatchingStreamKey) { WatchingStreamKey = null; StreamWatchEnded?.Invoke(); }
    }

    /// The broadcaster's user id — the last field of the stream key.
    public static ulong StreamKeyOwner(string key)
    {
        var parts = key.Split(':');
        return parts.Length >= 2 && ulong.TryParse(parts[^1], out var id) ? id : 0;
    }

    void HandleStreamServer(JsonElement d)
    {
        var token = d.TryGetProperty("token", out var t) ? t.GetString() : null;
        var endpoint = d.TryGetProperty("endpoint", out var e) && e.ValueKind != JsonValueKind.Null
            ? e.GetString() : null;
        var key = d.TryGetProperty("stream_key", out var k) ? k.GetString() : _pendingStreamKey;
        // A null endpoint is a server move; the real one follows in another update.
        if (token == null || endpoint == null || key == null) return;
        if (string.IsNullOrEmpty(VoiceSessionId)) { Log.Voice("stream server held: no session id"); return; }

        // Prefer the rtc_server_id STREAM_CREATE handed us; the key's own id (guild, or channel for
        // a DM call) is only a fallback for a STREAM_SERVER_UPDATE that arrives without one.
        ulong serverId = _streamRtc.TryGetValue(key, out var rtc) && rtc != 0 ? rtc : StreamKeyServerId(key);
        // The other candidate, for the one-shot retry: which of the two the stream gateway accepts
        // is not something the wire tells us, and guessing wrong costs a 4006 and a dead share.
        StreamAltServerId = serverId == StreamKeyServerId(key) ? rtc : StreamKeyServerId(key);
        ulong owner = StreamKeyOwner(key);
        var info = new VoiceServerInfo(endpoint, token, serverId, VoiceSessionId!,
                                       CurrentUser?.Id ?? 0, MyVoiceChannel ?? 0);
        Log.Voice($"STREAM_SERVER_UPDATE endpoint={endpoint} key={key} server={serverId} owner={owner}");

        // The same dispatch serves both roles; the key's owner says which one this is.
        if (owner == (CurrentUser?.Id ?? 0))
        {
            ActiveStreamKey = key;
            _pendingStreamKey = null;
            StreamServerReady?.Invoke(info);
        }
        else
        {
            WatchingStreamKey = key;
            StreamWatchReady?.Invoke(owner, info);
        }
    }

    // Force the gateway to mint a new voice session + token for the channel we're already in.
    //
    // Re-sending an identical op 4 is a no-op — Discord only answers with VOICE_SERVER_UPDATE when
    // the assignment actually changes. Dropping out and going straight back in is what makes it
    // issue fresh credentials, which is the only cure for a 4006.
    public async Task RefreshVoiceAsync()
    {
        if (MyVoiceChannel is not { } ch) return;
        var guild = MyVoiceGuild;
        Log.Voice("re-joining voice to obtain fresh credentials");
        await SetVoiceStateAsync(null, null, SelfMute, SelfDeaf);
        await Task.Delay(400);                       // let the leave land before rejoining
        await SetVoiceStateAsync(guild, ch, SelfMute, SelfDeaf);
    }

    // The presence this session is advertising. Also replayed on IDENTIFY after a reconnect.
    public string Presence { get; private set; } = "online";

    /// Your own status, from the gateway's `sessions` array (READY and SESSIONS_REPLACE carry the
    /// same shape). Discord aggregates every logged-in client into a synthetic session with
    /// id "all" — that is the one the real client's tray dot reflects, so prefer it and fall back to
    /// this session, then to whatever is first.
    void ApplySessions(JsonElement sessions)
    {
        if (sessions.ValueKind != JsonValueKind.Array || CurrentUser == null) return;

        string? pick = null, mine = null, first = null;
        foreach (var s in sessions.EnumerateArray())
        {
            if (!s.TryGetProperty("status", out var st) || st.ValueKind != JsonValueKind.String) continue;
            var status = st.GetString();
            if (string.IsNullOrEmpty(status)) continue;
            var id = s.TryGetProperty("session_id", out var sid) ? sid.GetString() : null;
            if (id == "all") { pick = status; break; }
            if (id != null && id == _sessionId) mine = status;
            first ??= status;
        }

        var chosen = pick ?? mine ?? first;
        if (chosen == null || chosen == CurrentUser.Status) return;
        CurrentUser.Status = chosen;
        Presence = chosen;
        SelfChanged?.Invoke();
    }

    // op 3 — Update Presence. This is what actually changes your status.
    //
    // The old code only PATCHed /users/@me/settings, which is where the *saved* preference lives.
    // Discord migrated user settings to a protobuf endpoint, so that PATCH no longer takes effect
    // for status, and nothing in the UI ever changed. The gateway op is what the real client sends
    // and it applies immediately; the settings write is kept alongside so the choice survives a
    // restart, and its failure no longer matters.
    public async Task SetPresenceAsync(string status)
    {
        Presence = status;
        if (CurrentUser != null) CurrentUser.Status = status;
        await SendJson(new
        {
            op = 3,
            d = new { status, since = status == "idle" ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : 0, activities = Array.Empty<object>(), afk = status == "idle" },
        });
        _ = Rest.SetStatusAsync(status);   // best-effort persistence; ignored if the endpoint is gone
    }

    // op 4. This is the whole "join a voice channel" protocol as far as *presence* goes: everyone
    // else sees you in the channel immediately. Actually carrying audio is a separate voice
    // websocket + UDP/Opus/E2EE stack that this client does not implement.
    public async Task SetVoiceStateAsync(ulong? guildId, ulong? channelId, bool mute = false, bool deaf = false)
    {
        SelfMute = mute;
        SelfDeaf = deaf;
        if (channelId == null) SelfVideo = false;     // leaving the channel drops the camera flag
        await SendJson(new
        {
            op = 4,
            d = new
            {
                guild_id = guildId?.ToString(),
                channel_id = channelId?.ToString(),
                self_mute = mute,
                self_deaf = deaf,
                self_video = SelfVideo,
            }
        });
        // The gateway echoes a VOICE_STATE_UPDATE back, which is what actually updates the cache.
    }

    /// Whether our camera is on, as announced in the voice state.
    public bool SelfVideo { get; private set; }

    /// Turn the camera flag on/off in our voice state (main gateway op 4).
    ///
    /// This is what makes OTHER clients render a video tile for us. The voice gateway's op 12 only
    /// tells the SFU which ssrcs to relay; the receiving client decides whether a member is
    /// broadcasting from `self_video` on their VOICE_STATE_UPDATE. Without it the real Discord
    /// client shows a plain avatar tile and never subscribes — which is exactly why our camera
    /// went out at ~900 kbps and the other account saw nothing at all.
    public async Task SetSelfVideoAsync(bool on)
    {
        if (SelfVideo == on) return;
        SelfVideo = on;
        if (MyVoiceChannel is not { } ch) return;
        await SetVoiceStateAsync(MyVoiceGuild, ch, SelfMute, SelfDeaf);
    }

    void HandleEmojisUpdate(JsonElement data)
    {
        if (!data.TryGetProperty("guild_id", out var g) || !ulong.TryParse(g.GetString(), out var gid)) return;
        if (!GuildById.TryGetValue(gid, out var guild)) return;
        if (data.TryGetProperty("emojis", out var e))
            guild.Emojis = e.Deserialize<List<UserGuildEmoji>>(JsonOpts) ?? new();
    }

    // Mute/notification edits land here from *other* sessions too, so the popup and sidebar must
    // track the authoritative state rather than assume the local PATCH took.
    void HandleGuildSettingsUpdate(JsonElement data)
    {
        // The event carries the guild's whole override set, so a dropped override (an unmute) must
        // actually clear — otherwise unmuting on another device leaves this client muted until
        // restart. DMs live under the "@me" sentinel guild and are left alone.
        ulong? gid = null;
        if (data.TryGetProperty("guild_id", out var gp) && gp.ValueKind != JsonValueKind.Null
            && ulong.TryParse(gp.GetString(), out var gv)) gid = gv;
        if (gid is { } gg && gg != 0)
        {
            foreach (var ch in MutedChannels.ToList())
                if (GuildOfChannel(ch)?.Id == gg) MutedChannels.Remove(ch);
            foreach (var kv in ChannelNotifyLevels.ToList())
                if (GuildOfChannel(kv.Key)?.Id == gg) ChannelNotifyLevels.Remove(kv.Key);
        }

        if (!data.TryGetProperty("channel_overrides", out var co) || co.ValueKind != JsonValueKind.Array) return;
        bool changed = false;
        foreach (var ov in co.EnumerateArray())
        {
            if (!ov.TryGetProperty("channel_id", out var cid) || !ulong.TryParse(cid.GetString(), out var cv)) continue;
            if (ov.TryGetProperty("muted", out var mu) && mu.ValueKind == JsonValueKind.True) changed |= MutedChannels.Add(cv);
            else changed |= MutedChannels.Remove(cv);
            if (ov.TryGetProperty("message_notifications", out var mn) && mn.TryGetInt32(out var lvl))
            {
                if (ChannelNotifyLevels.GetValueOrDefault(cv, 3) != lvl) { ChannelNotifyLevels[cv] = lvl; changed = true; }
            }
            else if (ChannelNotifyLevels.Remove(cv)) changed = true;
        }
        if (changed) ReadStateChanged?.Invoke();
    }

    // ── Threads ──
    // The sidebar lists active (unarchived) threads under their parent channel. THREAD_LIST_SYNC
    // delivers the full set once per lazy-guild request; the CREATE/UPDATE/DELETE events keep it
    // current. All four are cheap dictionary ops — no full-guild reindex per event.
    void HandleThreadListSync(JsonElement data)
    {
        if (!data.TryGetProperty("guild_id", out var gp) || !ulong.TryParse(gp.GetString(), out var gid)) return;
        if (!GuildById.TryGetValue(gid, out var guild)) return;
        if (!data.TryGetProperty("threads", out var threads) || threads.ValueKind != JsonValueKind.Array) return;
        bool changed = false;
        foreach (var t in threads.EnumerateArray())
        {
            var th = t.Deserialize<UserThreadChannel>(JsonOpts);
            if (th == null || th.ParentId == null) continue;
            var old = guild.ThreadById.GetValueOrDefault(th.Id);
            bool dirty = old == null || old.Name != th.Name || old.Metadata?.Archived != th.Metadata?.Archived
                       || old.LastMessageId != th.LastMessageId;
            if (dirty) { guild.UpsertThread(th); ChannelGuild[th.Id] = gid; changed = true; }
        }
        if (changed) ThreadsChanged?.Invoke(guild);
    }

    void HandleThreadCreate(JsonElement data)
    {
        var th = data.Deserialize<UserThreadChannel>(JsonOpts);
        if (th?.ParentId == null || th.GuildId is not { } gid || !GuildById.TryGetValue(gid, out var guild)) return;
        guild.UpsertThread(th);
        ChannelGuild[th.Id] = gid;   // a thread is a channel; messages in it must resolve their guild
        ThreadsChanged?.Invoke(guild);
    }

    void HandleThreadUpdate(JsonElement data)
    {
        var th = data.Deserialize<UserThreadChannel>(JsonOpts);
        if (th?.ParentId == null || th.GuildId is not { } gid || !GuildById.TryGetValue(gid, out var guild)) return;
        // Archiving arrives as an update with archived: true; the sidebar filter drops it. Keeping
        // the row (rather than deleting) is what lets an unarchive come back without a re-fetch.
        guild.UpsertThread(th);
        ChannelGuild[th.Id] = gid;
        ThreadsChanged?.Invoke(guild);
    }

    void HandleThreadDelete(JsonElement data)
    {
        if (!data.TryGetProperty("id", out var ip) || !ulong.TryParse(ip.GetString(), out var id)) return;
        if (!data.TryGetProperty("guild_id", out var gp) || !ulong.TryParse(gp.GetString(), out var gid)) return;
        if (!GuildById.TryGetValue(gid, out var guild)) return;
        guild.RemoveThread(id);
        ChannelGuild.Remove(id);
        ThreadsChanged?.Invoke(guild);
    }

    void HandleRoleDelete(JsonElement data)
    {
        if (!data.TryGetProperty("guild_id", out var g) || !ulong.TryParse(g.GetString(), out var gid)) return;
        if (!GuildById.TryGetValue(gid, out var guild)) return;
        if (!data.TryGetProperty("role_id", out var r) || !ulong.TryParse(r.GetString(), out var rid)) return;
        guild.Roles.RemoveAll(x => x.Id == rid);
        guild.Reindex();
    }

    // READY tells you who your friends and DM recipients are but not whether they're online — that
    // arrives a beat later in READY_SUPPLEMENTAL, which this client used to throw away. Without it
    // every avatar in the DM list wore a grey "offline" dot forever.
    void HandleReadySupplemental(JsonElement data)
    {
        // merged_members is parallel to READY's guild list and carries *our own* member object for
        // each guild. It is the only place a user account learns its own roles, and without them
        // PermissionsFor() sees no role grants — so every channel whose ViewChannel comes from a role
        // overwrite was hidden from the sidebar, and the whole guild could look half-empty.
        if (data.TryGetProperty("merged_members", out var mm) && mm.ValueKind == JsonValueKind.Array)
        {
            var order = data.TryGetProperty("guilds", out var gl) && gl.ValueKind == JsonValueKind.Array
                ? gl.EnumerateArray()
                    .Select(g => g.TryGetProperty("id", out var i) && ulong.TryParse(i.GetString(), out var v) ? v : 0)
                    .ToList()
                : Guilds.Select(g => g.Id).ToList();
            int idx = 0;
            foreach (var arr in mm.EnumerateArray())
            {
                var gid = idx < order.Count ? order[idx] : 0;
                idx++;
                if (arr.ValueKind != JsonValueKind.Array || !GuildById.TryGetValue(gid, out var guild)) continue;
                foreach (var m in arr.EnumerateArray()) ApplyMergedMember(guild, m);
            }
            SelfMemberLoaded?.Invoke();
        }

        if (!data.TryGetProperty("merged_presences", out var mp) || mp.ValueKind != JsonValueKind.Object) return;

        if (mp.TryGetProperty("friends", out var friends) && friends.ValueKind == JsonValueKind.Array)
            foreach (var p in friends.EnumerateArray()) ApplyMergedPresence(p);

        // Per-guild presences arrive as an array parallel to READY's guild list.
        if (mp.TryGetProperty("guilds", out var guilds) && guilds.ValueKind == JsonValueKind.Array)
            foreach (var g in guilds.EnumerateArray())
                if (g.ValueKind == JsonValueKind.Array)
                    foreach (var p in g.EnumerateArray()) ApplyMergedPresence(p);

        PresenceChanged?.Invoke(0, "");
    }

    void ApplyMergedPresence(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object) return;
        if (!p.TryGetProperty("user_id", out var u) || !ulong.TryParse(u.GetString(), out var uid))
        {
            // Guild entries nest the id under "user" instead.
            if (!p.TryGetProperty("user", out var uo) || !uo.TryGetProperty("id", out var ui)
                || !ulong.TryParse(ui.GetString(), out uid)) return;
        }
        var status = p.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString()! : "offline";

        foreach (var g in Guilds)
            if (g.MemberById.TryGetValue(uid, out var m)) { m.User.Status = status; ApplyActivities(m.User, p); }
        foreach (var dm in DMChannels)
            foreach (var r in dm.Recipients)
                if (r.Id == uid) { r.Status = status; ApplyActivities(r, p); }
        foreach (var rel in Relationships)
            if (rel.User is { } ru && ru.Id == uid) { ru.Status = status; ApplyActivities(ru, p); }
    }

    void HandleRelationship(JsonElement data, bool added)
    {
        if (!data.TryGetProperty("id", out var idp) || !ulong.TryParse(idp.GetString(), out var id)) return;
        Relationships.RemoveAll(r => r.Id == id);
        if (added && data.Deserialize<UserRelationship>(JsonOpts) is { } rel) Relationships.Add(rel);
        RelationshipsChanged?.Invoke();
    }

    void HandlePresenceUpdate(JsonElement data)
    {
        if (!data.TryGetProperty("user", out var user) || !user.TryGetProperty("id", out var idProp))
            return;
        var userId = ulong.Parse(idProp.GetString()!);
        var status = data.TryGetProperty("status", out var s) && s.ValueKind != JsonValueKind.Null
            ? s.GetString()! : "offline";

        // Indexed lookups: the old version scanned every member of every guild on every presence
        // event, which on a few large servers is tens of thousands of comparisons per second.
        if (data.TryGetProperty("guild_id", out var gid) && ulong.TryParse(gid.GetString(), out var gv)
            && GuildById.TryGetValue(gv, out var guild) && guild.GetMember(userId) is { } m)
        {
            m.User.Status = status;
            ApplyActivities(m.User, data);
        }
        else
        {
            foreach (var g in Guilds)
                if (g.MemberById.TryGetValue(userId, out var mm)) { mm.User.Status = status; ApplyActivities(mm.User, data); }
        }

        foreach (var dm in DMChannels)
            foreach (var r in dm.Recipients)
                if (r.Id == userId) { r.Status = status; ApplyActivities(r, data); }

        PresenceChanged?.Invoke(userId, status);
    }

    internal UserMessage? ParseMessage(JsonElement data)
    {
        try
        {
            var msg = data.Deserialize<UserMessage>(JsonOpts);
            if (msg == null) return null;
            msg.Client = this;

            if (data.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.Object)
                msg.Author = author.Deserialize<UserUser>(JsonOpts)!;
            msg.Author ??= new UserUser { Username = "Unknown" };

            if (data.TryGetProperty("channel_id", out var cid) && ulong.TryParse(cid.GetString(), out var cv))
                msg.ChannelId = cv;
            msg.GuildId ??= ChannelGuild.TryGetValue(msg.ChannelId, out var g) ? g : null;

            // A gateway message carries its own member object; a fetched one doesn't, so fall back
            // to the guild's cached member for the nickname and role colour.
            if (data.TryGetProperty("member", out var member) && member.ValueKind == JsonValueKind.Object)
            {
                var mem = member.Deserialize<UserMember>(JsonOpts);
                if (mem != null) { mem.User = msg.Author; msg.Member = mem; }
            }
            if (msg.Member == null && msg.GuildId is { } gid2 && GuildById.TryGetValue(gid2, out var guild))
                msg.Member = guild.GetMember(msg.Author.Id);

            if (data.TryGetProperty("referenced_message", out var refMsg) && refMsg.ValueKind == JsonValueKind.Object)
            {
                msg.ReferencedMessage = ParseMessage(refMsg);
                if (msg.ReferencedMessage != null) msg.ReferencedMessage.ChannelId = msg.ChannelId;
            }

            // A snapshot has no author, id or channel of its own — it inherits the forwarder's, so
            // that attachment URLs and the "jump to source" footer still resolve.
            foreach (var snap in msg.Snapshots)
            {
                if (snap.Message == null) continue;
                snap.Message.Client = this;
                snap.Message.Author ??= msg.Author;
                snap.Message.ChannelId = msg.MessageReference?.ChannelId ?? msg.ChannelId;
                snap.Message.GuildId ??= msg.MessageReference?.GuildId;
            }

            return msg;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"Message parse error: {ex.Message}");
            return null;
        }
    }

    // ═══════════════════ HELPERS ═══════════════════

    public UserGuild? GetGuild(ulong id) => GuildById.GetValueOrDefault(id);
    // CurrentUser is a UserSelfUser (it carries banner/bio the wire never sends for anyone else),
    // but a message row's author is a UserUser. Rebuilt whenever the profile changes rather than
    // cached forever, so renaming yourself shows up on the next message you send.
    UserUser? _selfAsUser;
    ulong _selfAsUserFor;
    string? _selfAsUserAvatar, _selfAsUserName;

    public UserUser? SelfAsUser
    {
        get
        {
            if (CurrentUser is not { } me) return null;
            if (_selfAsUser != null && _selfAsUserFor == me.Id
                && _selfAsUserAvatar == me.Avatar && _selfAsUserName == me.GlobalName + " " + me.Username)
                return _selfAsUser;
            _selfAsUserFor = me.Id;
            _selfAsUserAvatar = me.Avatar;
            _selfAsUserName = me.GlobalName + " " + me.Username;
            return _selfAsUser = new UserUser
            {
                Id = me.Id,
                Username = me.Username,
                GlobalName = me.GlobalName,
                Avatar = me.Avatar,
                Discriminator = me.Discriminator,
                Status = me.Status,
                CustomStatus = me.CustomStatus,
            };
        }
    }

    public UserGuild? GuildOfChannel(ulong channelId) =>
        ChannelGuild.TryGetValue(channelId, out var g) ? GuildById.GetValueOrDefault(g) : null;
    public UserMember? GetMember(ulong guildId, ulong userId) => GetGuild(guildId)?.GetMember(userId);
    public UserDMChannel? GetDMChannel(ulong userId) => DMChannels.FirstOrDefault(d => d.Type == 1 && d.Recipient?.Id == userId);

    public async Task<UserDMChannel> GetOrCreateDMAsync(ulong userId)
    {
        var existing = GetDMChannel(userId);
        if (existing != null) return existing;
        var dm = await Rest.CreateDMAsync(userId);
        DMChannels.Insert(0, dm);
        DmById[dm.Id] = dm;
        return dm;
    }

    // Best-effort display name for a user id across everything currently cached.
    public string NameOf(ulong userId)
    {
        if (userId == CurrentUser?.Id) return CurrentUser.DisplayName;
        foreach (var g in Guilds) if (g.MemberById.TryGetValue(userId, out var m)) return m.DisplayName;
        foreach (var d in DMChannels) foreach (var r in d.Recipients) if (r.Id == userId) return r.DisplayName;
        foreach (var r in Relationships) if (r.Id == userId && r.User != null) return r.User.DisplayName;
        return "Unknown";
    }
}

// ═══════════════════ REST CLIENT ═══════════════════

class UserRestClient
{
    readonly HttpClient _http;
    readonly UserClient _client;
    DateTime _lastTyping;

    // The picker needs to distinguish a genuine empty result from a failed Discord GIF request.
    public string? LastGifError { get; private set; }

    public UserRestClient(string token, UserClient client)
    {
        _client = client;
        _http = new HttpClient { BaseAddress = new Uri("https://discord.com/api/v9/") };
        _http.DefaultRequestHeaders.Add("Authorization", token);
        _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        _http.DefaultRequestHeaders.Add("Accept", "*/*");
        _http.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
    }

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");

    // Discord rate-limits per route and answers 429 with retry_after; retrying once keeps a burst
    // of scroll-loads from silently dropping messages.
    async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build)
    {
        for (int attempt = 0; ; attempt++)
        {
            var resp = await _http.SendAsync(build());
            if ((int)resp.StatusCode != 429 || attempt >= 2) return resp;
            var body = await resp.Content.ReadAsStringAsync();
            double wait = 1;
            try
            {
                using var d = JsonDocument.Parse(body);
                if (d.RootElement.TryGetProperty("retry_after", out var ra)) wait = ra.GetDouble();
            }
            catch { }
            _client.OnLog?.Invoke($"Rate limited, retrying in {wait:0.0}s");
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(wait, 5)));
        }
    }

    async Task<string?> GetStringOrNull(string url)
    {
        try
        {
            var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url));
            if (!resp.IsSuccessStatusCode)
            {
                _client.OnLog?.Invoke($"GET {url} → {(int)resp.StatusCode}");
                return null;
            }
            return await resp.Content.ReadAsStringAsync();
        }
        catch (Exception ex) { _client.OnLog?.Invoke($"GET {url} failed: {ex.Message}"); return null; }
    }

    // ── Embedded activities ──

    // Mint an OAuth2 code for an activity's client_id. The activity's own backend exchanges this
    // for a token using its client_secret, so we never need (and never see) the secret.
    // Returns the code, or an error string describing exactly why Discord refused — the caller
    // logs it, because a bare null here is impossible to debug from the activity's side.
    public async Task<(string? Code, string? Error)> AuthorizeActivityAsync(
        string clientId, IEnumerable<string> scopes, string? state = null,
        ulong? guildId = null, ulong? channelId = null)
    {
        var list = scopes.ToList();
        var (code, err) = await AuthorizeOnce(clientId, list, state, guildId, channelId);
        // "applications.commands" can only be granted against a real server. In a DM there isn't
        // one, so drop that scope and retry — the activity still gets identify + activities.write.
        if (code == null && err != null && err.Contains("OAUTH2_GUILD_REQUIRED") && list.Remove("applications.commands"))
            return await AuthorizeOnce(clientId, list, state, guildId, channelId);
        return (code, err);
    }

    async Task<(string? Code, string? Error)> AuthorizeOnce(
        string clientId, List<string> scopes, string? state, ulong? guildId, ulong? channelId)
    {
        try
        {
            // "10000" is the sentinel the web client sends when there is genuinely no guild/channel
            // context; when we do have one it must be the real id or scopes like
            // applications.commands are refused with OAUTH2_GUILD_REQUIRED.
            var body = new Dictionary<string, object?>
            {
                ["permissions"] = "0",
                ["authorize"] = true,
                ["integration_type"] = 0,
                ["location_context"] = new Dictionary<string, object?>
                {
                    ["guild_id"] = guildId?.ToString() ?? "10000",
                    ["channel_id"] = channelId?.ToString() ?? "10000",
                    ["channel_type"] = guildId != null ? 0 : 10000,
                },
            };
            if (guildId is { } gid) body["guild_id"] = gid.ToString();

            var payload = JsonSerializer.Serialize(body);
            var q = $"oauth2/authorize?client_id={clientId}&response_type=code&scope={Uri.EscapeDataString(string.Join(' ', scopes))}";
            if (!string.IsNullOrEmpty(state)) q += "&state=" + Uri.EscapeDataString(state!);
            var resp = await _http.PostAsync(q, new StringContent(payload, Encoding.UTF8, "application/json"));
            var respBody = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                return (null, $"HTTP {(int)resp.StatusCode}: {respBody}");

            using var doc = JsonDocument.Parse(respBody);
            if (doc.RootElement.TryGetProperty("location", out var loc) && loc.GetString() is { } url)
            {
                var m = System.Text.RegularExpressions.Regex.Match(url, @"[?&]code=([^&#]+)");
                if (m.Success) return (Uri.UnescapeDataString(m.Groups[1].Value), null);
                return (null, "no code in location: " + url);
            }
            if (doc.RootElement.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String)
                return (c.GetString(), null);
            return (null, "unexpected authorize response: " + respBody);
        }
        catch (Exception ex) { return (null, "exception: " + ex.Message); }
    }

    // Application metadata — used to confirm an app actually ships an embedded activity.
    public async Task<JsonElement?> GetApplicationAsync(ulong appId)
    {
        var json = await GetStringOrNull($"applications/{appId}/public");
        if (json == null) return null;
        try { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); }
        catch { return null; }
    }

    // ── Interactions (clicking a button / choosing from a select menu) ──

    // Discord answers 204 and the bot then edits the message; the resulting MESSAGE_UPDATE arrives
    // over the gateway, so there is nothing to render from the response here.
    public async Task<bool> SendComponentInteractionAsync(UserMessage msg, UserComponent c, IReadOnlyList<string>? values = null)
    {
        try
        {
            if (_client.SessionId == null) { _client.OnLog?.Invoke("Interaction skipped: no gateway session yet."); return false; }
            object data = c.Type == UserComponent.StringSelect
                ? new { component_type = c.Type, custom_id = c.CustomId, values = values ?? Array.Empty<string>() }
                : new { component_type = c.Type, custom_id = c.CustomId };

            var payload = JsonSerializer.Serialize(new
            {
                type = 3,                                   // MESSAGE_COMPONENT
                nonce = ((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1420070400000UL) << 22,
                guild_id = msg.GuildId?.ToString(),
                channel_id = msg.ChannelId.ToString(),
                message_flags = msg.Flags,
                message_id = msg.Id.ToString(),
                application_id = msg.InteractionAppId.ToString(),
                session_id = _client.SessionId,
                data,
            });
            var resp = await _http.PostAsync("interactions", new StringContent(payload, Encoding.UTF8, "application/json"));
            if (!resp.IsSuccessStatusCode)
                _client.OnLog?.Invoke($"Interaction failed: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { _client.OnLog?.Invoke($"Interaction error: {ex.Message}"); return false; }
    }

    // ── Messages ──

    // guildId is stamped in because the REST message payload omits it, and component interactions
    // must quote it. Gateway MESSAGE_CREATE carries it already, so this only fills the fetch path.
    public async Task<IReadOnlyCollection<UserMessage>> GetMessagesAsync(ulong channelId, int limit = 50, ulong before = 0, ulong guildId = 0, ulong after = 0, ulong around = 0)
    {
        var url = $"channels/{channelId}/messages?limit={limit}"
                + (before != 0 ? $"&before={before}" : "")
                + (after != 0 ? $"&after={after}" : "")
                + (around != 0 ? $"&around={around}" : "");
        var json = await GetStringOrNull(url);
        if (json == null)
            throw new InvalidOperationException("Unable to load messages. Please try again.");
        try
        {
            var msgs = JsonSerializer.Deserialize<List<UserMessage>>(json, UserClient.JsonOpts) ?? new();
            foreach (var m in msgs) Hydrate(m, channelId, guildId);
            LinkReplies(msgs);
            return msgs;
        }
        catch (Exception ex)
        {
            _client.OnLog?.Invoke($"REST GetMessages parse: {ex.Message}");
            throw new InvalidOperationException("The message history response was invalid. Please try again.", ex);
        }
    }

    // Discord stopped inlining `referenced_message` on the history endpoint for user accounts, so
    // every reply rendered as "Original message was deleted or is unavailable". The target is
    // almost always in the same page (or one already read this session), so resolve it locally
    // instead of paying a request per reply.
    readonly Dictionary<ulong, UserMessage> _seen = new();

    void LinkReplies(List<UserMessage> page)
    {
        foreach (var m in page) _seen[m.Id] = m;
        // Bounded: this only has to cover the pages still on screen.
        if (_seen.Count > 600)
            foreach (var k in _seen.Keys.OrderBy(k => k).Take(_seen.Count - 400).ToList()) _seen.Remove(k);

        foreach (var m in page)
            if (m.ReferencedMessage == null && m.MessageReference?.MessageId is { } r && r != 0
                && _seen.TryGetValue(r, out var target) && !ReferenceEquals(target, m))
                m.ReferencedMessage = target;
    }

    // Deserialized messages arrive without the client reference or the guild member that gives
    // them a nickname/colour; fill both in one place so every fetch path behaves the same.
    void Hydrate(UserMessage m, ulong channelId, ulong guildId)
    {
        m.Client = _client;
        if (m.ChannelId == 0) m.ChannelId = channelId;
        m.GuildId ??= guildId != 0 ? guildId : _client.ChannelGuild.TryGetValue(m.ChannelId, out var g) ? g : null;
        if (m.GuildId is { } gid && _client.GuildById.TryGetValue(gid, out var guild) && m.Author != null)
            m.Member ??= guild.GetMember(m.Author.Id);
        if (m.ReferencedMessage != null) Hydrate(m.ReferencedMessage, m.ChannelId, m.GuildId ?? 0);
    }

    // nonce is supplied by the caller when it has already drawn an optimistic row, so the reply and
    // the gateway echo can both be matched back to it. Omitted, one is generated as before.
    public async Task<UserMessage> SendMessageAsync(ulong channelId, string content, string? nonce = null) =>
        await PostMessage(channelId, new { content, tts = false, nonce = nonce ?? Nonce() });

    // pingReply mirrors the @ toggle on Discord's reply bar. The API's default for a reply with no
    // allowed_mentions is to ping, so the field is only worth sending when the user turned it off.
    public async Task<UserMessage> SendMessageReplyAsync(ulong channelId, string content, ulong replyId,
                                                         string? nonce = null, bool pingReply = true) =>
        await PostMessage(channelId, pingReply
            ? new
            {
                content,
                tts = false,
                nonce = nonce ?? Nonce(),
                message_reference = new { message_id = replyId.ToString() },
            }
            : (object)new
            {
                content,
                tts = false,
                nonce = nonce ?? Nonce(),
                message_reference = new { message_id = replyId.ToString() },
                allowed_mentions = new { parse = new[] { "users", "roles", "everyone" }, replied_user = false },
            });

    // A forward is a message whose reference has type 1 and whose content is empty: the server
    // snapshots the original into message_snapshots so it survives the source being deleted.
    public async Task<UserMessage> ForwardMessageAsync(ulong toChannelId, ulong fromChannelId, ulong messageId) =>
        await PostMessage(toChannelId, new
        {
            content = "",
            tts = false,
            nonce = Nonce(),
            message_reference = new { type = 1, message_id = messageId.ToString(), channel_id = fromChannelId.ToString() },
        });

    // A snowflake-shaped id built from the current time, so an optimistic row keyed on it also sorts
    // after every message already on screen.
    public static string Nonce() => NonceId().ToString();

    public static ulong NonceId() =>
        ((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1420070400000UL) << 22;

    async Task<UserMessage> PostMessage(ulong channelId, object body)
    {
        var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"channels/{channelId}/messages") { Content = Json(body) });
        var json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(ErrorText(resp, json));
        var msg = JsonSerializer.Deserialize<UserMessage>(json, UserClient.JsonOpts)!;
        Hydrate(msg, channelId, 0);
        return msg;
    }

    // Discord's error bodies are JSON; surfacing the "message" field turns "Send failed: 403"
    // into something the user can act on ("Missing Permissions").
    static string ErrorText(HttpResponseMessage resp, string body)
    {
        try
        {
            using var d = JsonDocument.Parse(body);
            if (d.RootElement.TryGetProperty("message", out var m) && m.GetString() is { } s && s.Length > 0)
                return s;
        }
        catch { }
        return $"HTTP {(int)resp.StatusCode}";
    }

    public async Task<UserMessage> EditMessageAsync(ulong channelId, ulong messageId, string content)
    {
        var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Patch, $"channels/{channelId}/messages/{messageId}") { Content = Json(new { content }) });
        var json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(ErrorText(resp, json));
        var msg = JsonSerializer.Deserialize<UserMessage>(json, UserClient.JsonOpts)!;
        Hydrate(msg, channelId, 0);
        return msg;
    }

    public async Task DeleteMessageAsync(ulong channelId, ulong messageId)
    {
        try { await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, $"channels/{channelId}/messages/{messageId}")); }
        catch (Exception ex) { _client.OnLog?.Invoke($"REST DeleteMessage error: {ex.Message}"); }
    }

    /// One message carrying up to ten attachments plus its caption — which is what the composer's
    /// tray sends. `progress` is called with 0..1 as the body is written, so the tray can show a
    /// bar instead of the window appearing to hang on a big upload.
    public async Task SendFilesAsync(ulong channelId, IReadOnlyList<(string Path, string Name)> files,
                                     string? text, ulong replyTo, Action<float>? progress = null)
    {
        if (files.Count == 0) return;
        using var form = new MultipartFormDataContent();
        var streams = new List<FileStream>();
        try
        {
            long total = 0;
            foreach (var f in files)
            {
                var fs = File.OpenRead(f.Path);
                streams.Add(fs);
                total += fs.Length;
            }
            var seen = new Counter();
            for (int i = 0; i < files.Count; i++)
            {
                // Streamed, and wrapped so the bytes actually leaving the socket drive the bar —
                // reporting per whole file makes a single large attachment sit at 0% then jump.
                var content = new ProgressContent(streams[i], total, seen, progress);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                form.Add(content, $"files[{i}]", files[i].Name);
            }

            object payload = replyTo != 0
                ? new
                {
                    content = text ?? "", nonce = Nonce(),
                    message_reference = new { message_id = replyTo.ToString() },
                    attachments = files.Select((f, i) => new { id = i.ToString(), filename = f.Name }).ToArray(),
                }
                : new
                {
                    content = text ?? "", nonce = Nonce(),
                    attachments = files.Select((f, i) => new { id = i.ToString(), filename = f.Name }).ToArray(),
                };
            form.Add(new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
                     "payload_json");

            var resp = await _http.PostAsync($"channels/{channelId}/messages", form);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(ErrorText(resp, await resp.Content.ReadAsStringAsync()));
            progress?.Invoke(1f);
        }
        finally { foreach (var s in streams) s.Dispose(); }
    }

    /// Bytes written so far, shared by every part of one batch so the bar measures the whole
    /// upload rather than restarting per file.
    sealed class Counter { public long Value; }

    // Reports upload progress across the whole batch as its stream is copied out.
    sealed class ProgressContent : HttpContent
    {
        readonly Stream _src;
        readonly long _total;
        readonly Counter _seen;
        readonly Action<float>? _cb;

        public ProgressContent(Stream src, long total, Counter seen, Action<float>? cb)
        {
            _src = src; _total = Math.Max(1, total); _seen = seen; _cb = cb;
        }

        protected override async Task SerializeToStreamAsync(Stream target, System.Net.TransportContext? ctx)
        {
            var buf = new byte[81920];
            int n;
            while ((n = await _src.ReadAsync(buf)) > 0)
            {
                await target.WriteAsync(buf.AsMemory(0, n));
                long done = Interlocked.Add(ref _seen.Value, n);
                _cb?.Invoke(Math.Min(1f, done / (float)_total));
            }
        }

        protected override bool TryComputeLength(out long length) { length = _src.Length; return true; }
    }

    public async Task SendFileAsync(ulong channelId, string filePath, string? text = null)
    {
        try
        {
            using var form = new MultipartFormDataContent();
            // Stream the file instead of File.ReadAllBytes: a 25MB upload was a 25MB pinned array
            // in the large object heap for the whole request.
            var stream = File.OpenRead(filePath);
            var fileContent = new StreamContent(stream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(fileContent, "files[0]", Path.GetFileName(filePath));
            form.Add(new StringContent(JsonSerializer.Serialize(new
            {
                content = text ?? "",
                nonce = Nonce(),
                attachments = new[] { new { id = "0", filename = Path.GetFileName(filePath) } },
            }), Encoding.UTF8, "application/json"), "payload_json");
            var resp = await _http.PostAsync($"channels/{channelId}/messages", form);
            stream.Dispose();
            if (!resp.IsSuccessStatusCode)
                _client.OnLog?.Invoke("Upload failed: " + ErrorText(resp, await resp.Content.ReadAsStringAsync()));
        }
        catch (Exception ex) { _client.OnLog?.Invoke($"REST SendFile error: {ex.Message}"); }
    }

    // ── Typing ──

    // Discord's own client sends this at most every 8s while you keep typing.
    public async Task TypingAsync(ulong channelId)
    {
        if ((DateTime.UtcNow - _lastTyping).TotalSeconds < 8) return;
        _lastTyping = DateTime.UtcNow;
        try { await _http.PostAsync($"channels/{channelId}/typing", null); } catch { }
    }

    // ── Reactions ──

    // Custom emoji are addressed as "name:id"; unicode ones as the raw glyph. Both need escaping.
    public async Task AddReactionAsync(ulong channelId, ulong messageId, string emoji)
    {
        try { await SendAsync(() => new HttpRequestMessage(HttpMethod.Put, $"channels/{channelId}/messages/{messageId}/reactions/{Uri.EscapeDataString(emoji)}/@me")); }
        catch (Exception ex) { _client.OnLog?.Invoke($"AddReaction error: {ex.Message}"); }
    }

    public async Task RemoveReactionAsync(ulong channelId, ulong messageId, string emoji)
    {
        try { await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, $"channels/{channelId}/messages/{messageId}/reactions/{Uri.EscapeDataString(emoji)}/@me")); }
        catch (Exception ex) { _client.OnLog?.Invoke($"RemoveReaction error: {ex.Message}"); }
    }

    // ── Pins ──

    /// Who reacted with one emoji. Used for the hover tooltip, so it only ever needs the first
    /// page — Discord's own tooltip names three and counts the rest.
    public async Task<List<UserUser>> GetReactionUsersAsync(ulong channelId, ulong messageId, string emojiKey)
    {
        var enc = Uri.EscapeDataString(emojiKey);
        var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get,
            $"channels/{channelId}/messages/{messageId}/reactions/{enc}?limit=25"));
        if (!resp.IsSuccessStatusCode) return new();
        var json = await resp.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<List<UserUser>>(json, UserClient.JsonOpts) ?? new();
    }

    public async Task<IReadOnlyCollection<UserMessage>> GetPinnedMessagesAsync(ulong channelId)
    {
        var json = await GetStringOrNull($"channels/{channelId}/pins");
        if (json == null) return Array.Empty<UserMessage>();
        try
        {
            // Newer API revisions wrap the list in { items: [ { message: {...} } ] }.
            using var probe = JsonDocument.Parse(json);
            List<UserMessage> msgs;
            if (probe.RootElement.ValueKind == JsonValueKind.Object && probe.RootElement.TryGetProperty("items", out var items))
                msgs = items.EnumerateArray()
                            .Select(i => (i.TryGetProperty("message", out var mm) ? mm : i).Deserialize<UserMessage>(UserClient.JsonOpts))
                            .Where(m => m != null).ToList()!;
            else
                msgs = JsonSerializer.Deserialize<List<UserMessage>>(json, UserClient.JsonOpts) ?? new();
            foreach (var m in msgs) Hydrate(m, channelId, 0);
            return msgs;
        }
        catch (Exception ex) { _client.OnLog?.Invoke($"REST Pins parse: {ex.Message}"); return Array.Empty<UserMessage>(); }
    }

    public async Task PinAsync(ulong channelId, ulong messageId, bool on)
    {
        try
        {
            await SendAsync(() => new HttpRequestMessage(
                on ? HttpMethod.Put : HttpMethod.Delete, $"channels/{channelId}/pins/{messageId}"));
        }
        catch (Exception ex) { _client.OnLog?.Invoke($"Pin error: {ex.Message}"); }
    }

    // ── Profiles ──

    // The whole profile modal's worth of data in one request. guildId scopes the member record and
    // any server-specific bio/banner; pass 0 for a DM where there is no guild context.
    public async Task<UserProfile?> GetProfileAsync(ulong userId, ulong guildId = 0)
    {
        var url = $"users/{userId}/profile?with_mutual_guilds=true&with_mutual_friends=true"
                + (guildId != 0 ? $"&guild_id={guildId}" : "");
        var json = await GetStringOrNull(url);
        if (json == null) return null;
        try
        {
            var p = JsonSerializer.Deserialize<UserProfile>(json, UserClient.JsonOpts);
            return p;
        }
        catch (Exception ex) { _client.OnLog?.Invoke($"Profile parse: {ex.Message}"); return null; }
    }

    // Bio and pronouns live on the profile endpoint, not on the user object.
    public Task<string?> SetProfileAsync(string bio, string pronouns) =>
        Act(HttpMethod.Patch, "users/@me/profile", new { bio = bio.Trim(), pronouns = pronouns.Trim() });

    // ── Scheduled events ──

    public async Task<List<UserScheduledEvent>> GetEventsAsync(ulong guildId)
    {
        var json = await GetStringOrNull($"guilds/{guildId}/scheduled-events?with_user_count=true");
        if (json == null) return new();
        try { return JsonSerializer.Deserialize<List<UserScheduledEvent>>(json, UserClient.JsonOpts) ?? new(); }
        catch (Exception ex) { _client.OnLog?.Invoke($"Events parse: {ex.Message}"); return new(); }
    }

    // ── Slash commands ──

    // Every command available in one place, guild-wide (or "@me" in a DM). The per-channel
    // /application-commands/search endpoint answers with an empty list for a user account — this is
    // the one the desktop client's "/" popup is actually backed by, and it returns the commands *and*
    // the owning applications, so the picker can name the bot without extra requests.
    public async Task<List<UserAppCommand>> GetCommandIndexAsync(ulong? guildId)
    {
        var json = await GetStringOrNull(guildId is { } g
            ? $"guilds/{g}/application-command-index"
            : "users/@me/application-command-index");
        if (json == null) return new();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var cmds = doc.RootElement.TryGetProperty("application_commands", out var ac)
                ? ac.Deserialize<List<UserAppCommand>>(UserClient.JsonOpts) ?? new() : new();
            if (doc.RootElement.TryGetProperty("applications", out var apps) && apps.ValueKind == JsonValueKind.Array)
                foreach (var a in apps.EnumerateArray())
                {
                    if (!a.TryGetProperty("id", out var ip) || !ulong.TryParse(ip.GetString(), out var aid)) continue;
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var icon = a.TryGetProperty("icon", out var ic) && ic.ValueKind == JsonValueKind.String ? ic.GetString() : null;
                    foreach (var c in cmds)
                        if (c.ApplicationId == aid) { c.AppName = name; c.AppIcon = icon; }
                }
            return cmds;
        }
        catch (Exception ex) { _client.OnLog?.Invoke($"Command search: {ex.Message}"); return new(); }
    }

    // Firing a command is an interaction, not a message: Discord answers 204 and the bot's reply
    // arrives over the gateway. The session id is required — without it the interaction is dropped
    // with no error, which looks exactly like the bot ignoring you.
    public async Task<string?> InvokeCommandAsync(UserAppCommand cmd, ulong channelId, ulong? guildId, object options)
    {
        if (_client.SessionId == null) return "Not connected.";
        var body = new
        {
            type = 2,
            application_id = cmd.ApplicationId.ToString(),
            guild_id = guildId?.ToString(),
            channel_id = channelId.ToString(),
            session_id = _client.SessionId,
            data = new
            {
                version = cmd.Version,
                id = cmd.Id.ToString(),
                name = cmd.Name,
                type = cmd.Type,
                options,
                application_command = cmd,
                attachments = Array.Empty<object>(),
            },
            nonce = Nonce(),
            analytics_location = "slash_ui",
        };
        // Plain JSON, the same shape SendComponentInteractionAsync already uses successfully — the
        // multipart form is only needed when the command carries an attachment option.
        var payload = JsonSerializer.Serialize(body);
        try
        {
            var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "interactions")
            { Content = new StringContent(payload, Encoding.UTF8, "application/json") });
            if (resp.IsSuccessStatusCode) return null;
            return ErrorText(resp, await resp.Content.ReadAsStringAsync());
        }
        catch (Exception ex) { return ex.Message; }
    }

    // ── Moderation ──
    // All five report their Discord error text rather than swallowing it: "Missing Permissions" is
    // the normal outcome of trying any of these and the user needs to see that, not silence.

    public Task<string?> KickAsync(ulong guildId, ulong userId) =>
        Act(HttpMethod.Delete, $"guilds/{guildId}/members/{userId}");

    public Task<string?> BanAsync(ulong guildId, ulong userId, int deleteMessageSeconds = 0) =>
        Act(HttpMethod.Put, $"guilds/{guildId}/bans/{userId}", new { delete_message_seconds = deleteMessageSeconds });

    public Task<string?> UnbanAsync(ulong guildId, ulong userId) =>
        Act(HttpMethod.Delete, $"guilds/{guildId}/bans/{userId}");

    // A null duration lifts the timeout; Discord caps it at 28 days.
    public Task<string?> TimeoutAsync(ulong guildId, ulong userId, TimeSpan? duration) =>
        Act(HttpMethod.Patch, $"guilds/{guildId}/members/{userId}", new
        {
            communication_disabled_until = duration is { } d ? DateTimeOffset.UtcNow.Add(d).ToString("o") : null,
        });

    public Task<string?> SetNicknameAsync(ulong guildId, ulong userId, string? nick) =>
        Act(HttpMethod.Patch,
            userId == _client.CurrentUser?.Id ? $"guilds/{guildId}/members/@me" : $"guilds/{guildId}/members/{userId}",
            new { nick = string.IsNullOrWhiteSpace(nick) ? null : nick });

    public Task<string?> SetMemberRoleAsync(ulong guildId, ulong userId, ulong roleId, bool on) =>
        Act(on ? HttpMethod.Put : HttpMethod.Delete, $"guilds/{guildId}/members/{userId}/roles/{roleId}");

    // Returns null on success, Discord's own message on failure. The body is serialised once and a
    // fresh request built per attempt, because SendAsync retries and a request can only be sent once.
    async Task<string?> Act(HttpMethod method, string url, object? body = null)
    {
        var payload = body == null ? null : JsonSerializer.Serialize(body);
        try
        {
            var resp = await SendAsync(() => new HttpRequestMessage(method, url)
            {
                Content = payload == null ? null : new StringContent(payload, Encoding.UTF8, "application/json"),
            });
            if (resp.IsSuccessStatusCode) return null;
            return ErrorText(resp, await resp.Content.ReadAsStringAsync());
        }
        catch (Exception ex) { return ex.Message; }
    }

    public async Task<UserMember?> GetSelfMemberAsync(ulong guildId)
    {
        var json = await GetStringOrNull($"users/@me/guilds/{guildId}/member");
        if (json == null) return null;
        try { return JsonSerializer.Deserialize<UserMember>(json, UserClient.JsonOpts); }
        catch { return null; }
    }

    // ── Polls ──

    // A user account votes through the same endpoint the web client uses; an empty list clears the
    // vote, which is how Discord implements "remove vote" on a single-select poll.
    public async Task VotePollAsync(ulong channelId, ulong messageId, IEnumerable<int> answerIds)
    {
        try
        {
            await SendAsync(() => new HttpRequestMessage(HttpMethod.Put, $"channels/{channelId}/polls/{messageId}/answers/@me")
            { Content = Json(new { answer_ids = answerIds.Select(i => i.ToString()).ToArray() }) });
        }
        catch (Exception ex) { _client.OnLog?.Invoke($"Poll vote error: {ex.Message}"); }
    }

    public async Task ExpirePollAsync(ulong channelId, ulong messageId)
    {
        try { await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"channels/{channelId}/polls/{messageId}/expire")); }
        catch (Exception ex) { _client.OnLog?.Invoke($"Poll expire error: {ex.Message}"); }
    }

    // ── Channels & threads ──

    public async Task CreateChannelAsync(ulong guildId, string name, int type, ulong? parentId)
    {
        var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"guilds/{guildId}/channels")
        { Content = Json(new { name, type, parent_id = parentId?.ToString() }) });
        if (!resp.IsSuccessStatusCode) throw new Exception(await resp.Content.ReadAsStringAsync());
    }

    public async Task DeleteChannelAsync(ulong channelId)
    {
        var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, $"channels/{channelId}"));
        if (!resp.IsSuccessStatusCode) throw new Exception(await resp.Content.ReadAsStringAsync());
    }

    public async Task ModifyChannelAsync(ulong channelId, object patch)
    {
        var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Patch, $"channels/{channelId}") { Content = Json(patch) });
        if (!resp.IsSuccessStatusCode) throw new Exception(await resp.Content.ReadAsStringAsync());
    }

    /// Start a thread. With a messageId it hangs off that message (Discord's "Create Thread" on a
    /// message); without one it is a standalone public thread in the channel.
    public async Task<UserThreadChannel?> CreateThreadAsync(ulong channelId, string name, ulong messageId = 0)
    {
        var route = messageId == 0 ? $"channels/{channelId}/threads" : $"channels/{channelId}/messages/{messageId}/threads";
        object body = messageId == 0
            ? new { name, type = 11, auto_archive_duration = 1440 }
            : (object)new { name, auto_archive_duration = 1440 };
        var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, route) { Content = Json(body) });
        if (!resp.IsSuccessStatusCode) throw new Exception(await resp.Content.ReadAsStringAsync());
        return JsonSerializer.Deserialize<UserThreadChannel>(await resp.Content.ReadAsStringAsync(), UserClient.JsonOpts);
    }

    // ── DM calls ──
    //
    // Joining the channel with op 4 is what puts you *in* the call; ringing is a separate REST call
    // that makes the other end's client actually make a noise. `recipients: null` rings everyone.
    public async Task RingAsync(ulong channelId, IEnumerable<ulong>? recipients = null)
    {
        try
        {
            await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"channels/{channelId}/call/ring")
            { Content = Json(new { recipients = recipients?.Select(r => r.ToString()).ToArray() }) });
        }
        catch (Exception ex) { _client.OnLog?.Invoke($"Ring error: {ex.Message}"); }
    }

    public async Task StopRingingAsync(ulong channelId, IEnumerable<ulong>? recipients = null)
    {
        try
        {
            await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"channels/{channelId}/call/stop-ringing")
            { Content = Json(new { recipients = recipients?.Select(r => r.ToString()).ToArray() }) });
        }
        catch (Exception ex) { _client.OnLog?.Invoke($"Stop-ringing error: {ex.Message}"); }
    }

    // Adding a recipient to a 1:1 DM is how Discord promotes it to a group DM; the gateway answers
    // with a CHANNEL_CREATE for the new group.
    public async Task AddDmRecipientAsync(ulong channelId, ulong userId)
    {
        var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Put, $"channels/{channelId}/recipients/{userId}"));
        if (!resp.IsSuccessStatusCode) throw new Exception(await resp.Content.ReadAsStringAsync());
    }

    public async Task RemoveDmRecipientAsync(ulong channelId, ulong userId)
    {
        var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, $"channels/{channelId}/recipients/{userId}"));
        if (!resp.IsSuccessStatusCode) throw new Exception(await resp.Content.ReadAsStringAsync());
    }

    /// Rename a group DM (or set its icon). A 1:1 DM has no name and rejects this.
    public Task RenameGroupAsync(ulong channelId, string name) =>
        ModifyChannelAsync(channelId, new { name });

    /// Open a group DM with several people at once — POST with more than one recipient is what
    /// makes the server create a group rather than a 1:1.
    public async Task<UserDMChannel> CreateGroupAsync(IEnumerable<ulong> userIds)
    {
        var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "users/@me/channels")
        {
            Content = Json(new { recipients = userIds.Select(u => u.ToString()).ToArray() }),
        });
        var json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(ErrorText(resp, json));
        var dm = JsonSerializer.Deserialize<UserDMChannel>(json, UserClient.JsonOpts)!;
        dm.Client = _client;
        return dm;
    }

    // ── Read state ──

    public async Task AckAsync(ulong channelId, ulong messageId)
    {
        try { await _http.PostAsync($"channels/{channelId}/messages/{messageId}/ack", Json(new { token = (string?)null })); }
        catch { }
    }

    // "Mark Unread" is the same ack endpoint with manual:true, acking the id *before* the clicked
    // message so that message becomes the first unread one. Read state is just a snowflake
    // watermark, so messageId-1 works without having to look up the preceding row.
    public async Task MarkUnreadAsync(ulong channelId, ulong messageId)
    {
        try
        {
            await _http.PostAsync($"channels/{channelId}/messages/{messageId - 1}/ack",
                Json(new { manual = true, mention_count = 0 }));
        }
        catch (Exception ex) { _client.OnLog?.Invoke($"MarkUnread error: {ex.Message}"); }
    }

    // ── Search ──

    // Discord indexes asynchronously and answers 202 while a channel is still being indexed;
    // one retry covers the common case of searching a channel you just opened.
    //
    // `extra` carries the structured search filters (author_id, mentions, has, before, after,
    // pinned) that the search box parses out of Discord's `from:`/`mentions:`/`has:` syntax — the
    // endpoint only honours them as real query parameters, never as content text.
    //
    // serverWide omits the channel_id restriction (Ctrl+Shift+F searches the whole server).
    public async Task<IReadOnlyCollection<UserMessage>> SearchAsync(ulong? guildId, ulong channelId, string query, int offset = 0,
        IReadOnlyDictionary<string, string>? extra = null, bool serverWide = false)
    {
        var scope = guildId is { } g ? $"guilds/{g}" : $"channels/{channelId}";
        var url = $"{scope}/messages/search?content={Uri.EscapeDataString(query)}&offset={offset}";
        if (guildId != null && !serverWide) url += $"&channel_id={channelId}";
        if (extra != null)
            foreach (var (k, v) in extra)
                url += $"&{Uri.EscapeDataString(k)}={Uri.EscapeDataString(v)}";
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url));
            var body = await resp.Content.ReadAsStringAsync();
            if ((int)resp.StatusCode == 202) { await Task.Delay(1500); continue; }
            if (!resp.IsSuccessStatusCode) { _client.OnLog?.Invoke("Search failed: " + ErrorText(resp, body)); return Array.Empty<UserMessage>(); }
            try
            {
                using var doc = JsonDocument.Parse(body);
                var outp = new List<UserMessage>();
                if (!doc.RootElement.TryGetProperty("messages", out var groups)) return outp;
                // Each hit is a small array: the match plus surrounding context. The match is the
                // element flagged with "hit", falling back to the first.
                foreach (var grp in groups.EnumerateArray())
                {
                    var chosen = grp.EnumerateArray().FirstOrDefault(e => e.TryGetProperty("hit", out var h) && h.ValueKind == JsonValueKind.True);
                    if (chosen.ValueKind == JsonValueKind.Undefined) chosen = grp[0];
                    var m = chosen.Deserialize<UserMessage>(UserClient.JsonOpts);
                    if (m == null) continue;
                    Hydrate(m, m.ChannelId, guildId ?? 0);
                    outp.Add(m);
                }
                return outp;
            }
            catch (Exception ex) { _client.OnLog?.Invoke($"Search parse: {ex.Message}"); return Array.Empty<UserMessage>(); }
        }
        return Array.Empty<UserMessage>();
    }

    // ── Inbox ──

    // Recent messages that mention you, across every guild — what the Inbox's Mentions tab shows.
    // Unlike search this is a flat array of message objects, and each carries its own guild_id, so
    // there is nothing to correlate against the guild list.
    public async Task<IReadOnlyCollection<UserMessage>> GetRecentMentionsAsync(int limit = 25)
    {
        var json = await GetStringOrNull($"users/@me/mentions?limit={limit}&roles=true&everyone=true");
        if (json == null) return Array.Empty<UserMessage>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<UserMessage>();
            var outp = new List<UserMessage>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var m = el.Deserialize<UserMessage>(UserClient.JsonOpts);
                if (m == null) continue;
                ulong gid = el.TryGetProperty("guild_id", out var g) && g.ValueKind == JsonValueKind.String
                            && ulong.TryParse(g.GetString(), out var pg) ? pg : 0;
                Hydrate(m, m.ChannelId, gid);
                outp.Add(m);
            }
            return outp;
        }
        catch (Exception ex) { _client.OnLog?.Invoke("Mentions parse: " + ex.Message); return Array.Empty<UserMessage>(); }
    }

    // ── Threads ──

    public async Task<IReadOnlyCollection<UserThreadChannel>> GetThreadsAsync(ulong channelId, int? limit, DateTimeOffset? before)
    {
        var url = $"channels/{channelId}/threads/archived/public?limit={limit ?? 25}";
        if (before != null) url += $"&before={before.Value.ToUnixTimeMilliseconds()}";
        var json = await GetStringOrNull(url);
        if (json == null) return Array.Empty<UserThreadChannel>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("threads").Deserialize<List<UserThreadChannel>>(UserClient.JsonOpts) ?? new();
        }
        catch { return Array.Empty<UserThreadChannel>(); }
    }

    // Threads that are still active live on the guild, not the channel.
    // Forum posts. /threads/search is the endpoint the web client uses: it returns the threads
    // *and* their opening messages in one round trip, which the archived/active endpoints don't, so
    // a post card can show its preview without one fetch per post.
    public async Task<List<UserThreadChannel>> GetForumPostsAsync(ulong channelId, ulong guildId, bool archived = false)
    {
        var json = await GetStringOrNull(
            $"channels/{channelId}/threads/search?archived={(archived ? "true" : "false")}&sort_by=last_message_time&sort_order=desc&limit=25");
        if (json == null) return new();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var threads = doc.RootElement.TryGetProperty("threads", out var t)
                ? t.Deserialize<List<UserThreadChannel>>(UserClient.JsonOpts) ?? new() : new();
            if (doc.RootElement.TryGetProperty("first_messages", out var fm) && fm.ValueKind == JsonValueKind.Array)
                foreach (var m in fm.EnumerateArray())
                {
                    var msg = _client.ParseMessage(m);
                    if (msg == null) continue;
                    // The opening post's id equals its thread's id.
                    var owner = threads.FirstOrDefault(x => x.Id == msg.ChannelId || x.Id == msg.Id);
                    if (owner != null) owner.FirstMessage = msg;
                }
            return threads;
        }
        catch (Exception ex) { _client.OnLog?.Invoke($"Forum posts parse: {ex.Message}"); return new(); }
    }

    // A forum post is created as a thread on the forum channel whose body is the opening message.
    // Returns null on success, Discord's own error text otherwise.
    public async Task<string?> CreateForumPostAsync(ulong forumId, string title, string body)
    {
        try
        {
            var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"channels/{forumId}/threads")
            {
                Content = Json(new { name = title, message = new { content = body, nonce = Nonce() } }),
            });
            if (resp.IsSuccessStatusCode) return null;
            return ErrorText(resp, await resp.Content.ReadAsStringAsync());
        }
        catch (Exception ex) { return ex.Message; }
    }

    // `guilds/{id}/threads/active` and `channels/{id}/threads/active` are both **bot-only** and
    // answer 403 for a user account — which surfaced as a "GET … → 403" toast and an empty panel.
    // The endpoint the real client uses is the per-channel thread search.
    public async Task<IReadOnlyCollection<UserThreadChannel>> GetActiveThreadsAsync(ulong guildId, ulong channelId)
    {
        var json = await GetStringOrNull(
            $"channels/{channelId}/threads/search?archived=false&sort_by=last_message_time&sort_order=desc&limit=25");
        if (json == null) return Array.Empty<UserThreadChannel>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var all = doc.RootElement.GetProperty("threads").Deserialize<List<UserThreadChannel>>(UserClient.JsonOpts) ?? new();
            return all;
        }
        catch { return Array.Empty<UserThreadChannel>(); }
    }

    // ── Guilds & profiles ──

    public async Task<List<UserChannelData>> GetGuildChannelsAsync(ulong guildId)
    {
        var json = await GetStringOrNull($"guilds/{guildId}/channels");
        if (json == null) return new();
        try { return JsonSerializer.Deserialize<List<UserChannelData>>(json, UserClient.JsonOpts) ?? new(); }
        catch { return new(); }
    }

    // The full profile card data: banner, bio, badges, mutual servers. Guild context makes Discord
    // include the per-guild nickname and roles.
    public async Task<JsonElement?> GetProfileAsync(ulong userId, ulong? guildId)
    {
        var url = $"users/{userId}/profile?with_mutual_guilds=true";
        if (guildId is { } g) url += $"&guild_id={g}";
        var json = await GetStringOrNull(url);
        if (json == null) return null;
        try { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); }
        catch { return null; }
    }

    // ── DMs ──

    // The full DM list, used by the headless API smoke test (and handy for anything that needs the
    // conversations without a gateway). Recipients resolve the same way READY does.
    public async Task<List<UserDMChannel>> GetDmChannelsAsync()
    {
        var json = await GetStringOrNull("users/@me/channels");
        if (json == null) return new();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var outp = new List<UserDMChannel>();
            foreach (var pc in doc.RootElement.EnumerateArray())
            {
                int type = pc.TryGetProperty("type", out var t) && t.TryGetInt32(out var tv) ? tv : 0;
                if (type != 1 && type != 3) continue;   // DM or group DM only
                var dm = new UserDMChannel
                {
                    Id = ulong.Parse(pc.GetProperty("id").GetString()!),
                    Type = type,
                    Client = _client,
                };
                if (pc.TryGetProperty("last_message_id", out var lmid) && lmid.ValueKind == JsonValueKind.String
                    && ulong.TryParse(lmid.GetString(), out var lm)) dm.LastMessageId = lm;
                if (type == 3)
                {
                    if (pc.TryGetProperty("name", out var gn) && gn.ValueKind == JsonValueKind.String) dm.GroupName = gn.GetString();
                    if (pc.TryGetProperty("icon", out var gi) && gi.ValueKind == JsonValueKind.String) dm.GroupIcon = gi.GetString();
                }
                if (pc.TryGetProperty("recipients", out var recipients))
                    foreach (var r in recipients.EnumerateArray())
                        if (r.Deserialize<UserUser>(UserClient.JsonOpts) is { } u) dm.Recipients.Add(u);
                dm.Recipient = dm.Recipients.FirstOrDefault();
                outp.Add(dm);
            }
            return outp;
        }
        catch (Exception ex) { _client.OnLog?.Invoke($"DMs parse: {ex.Message}"); return new(); }
    }

    public async Task<UserDMChannel> CreateDMAsync(ulong userId)
    {
        var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "users/@me/channels")
        {
            Content = Json(new { recipients = new[] { userId.ToString() } }),
        });
        var json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(ErrorText(resp, json));
        using var doc = JsonDocument.Parse(json);
        var dm = new UserDMChannel
        {
            Id = ulong.Parse(doc.RootElement.GetProperty("id").GetString()!),
            Client = _client,
        };
        if (doc.RootElement.TryGetProperty("recipients", out var recipients))
            foreach (var r in recipients.EnumerateArray())
                if (r.Deserialize<UserUser>(UserClient.JsonOpts) is { } u) dm.Recipients.Add(u);
        dm.Recipient = dm.Recipients.FirstOrDefault();
        return dm;
    }

    public async Task CloseChannelAsync(ulong channelId)
    {
        try { await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, $"channels/{channelId}")); }
        catch (Exception ex) { _client.OnLog?.Invoke("Close DM failed: " + ex.Message); }
    }

    // ── Relationships ──
    // One route does everything: PUT with no body accepts/sends a friend request, PUT {type:2}
    // blocks, DELETE removes a friend, declines a request, or unblocks.

    public async Task<string?> RelateAsync(ulong userId, int? type)
    {
        var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Put, $"users/@me/relationships/{userId}")
        {
            Content = type is int t ? Json(new { type = t }) : Json(new { }),
        });
        return resp.IsSuccessStatusCode ? null : ErrorText(resp, await resp.Content.ReadAsStringAsync());
    }

    public async Task<string?> UnrelateAsync(ulong userId)
    {
        var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, $"users/@me/relationships/{userId}"));
        return resp.IsSuccessStatusCode ? null : ErrorText(resp, await resp.Content.ReadAsStringAsync());
    }

    // Discord's "Add Friend" box takes a plain username (post-discriminator migration).
    public async Task<string?> AddFriendAsync(string username)
    {
        string name = username.Trim(), disc = "0";
        int hash = name.LastIndexOf('#');
        if (hash > 0) { disc = name[(hash + 1)..]; name = name[..hash]; }
        var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "users/@me/relationships")
        {
            Content = Json(new { username = name, discriminator = disc }),
        });
        return resp.IsSuccessStatusCode ? null : ErrorText(resp, await resp.Content.ReadAsStringAsync());
    }

    // ── User ──

    public async Task<UserSelfUser?> GetSelfAsync()
    {
        var json = await GetStringOrNull("users/@me");
        if (json == null) return null;
        try { return JsonSerializer.Deserialize<UserSelfUser>(json, UserClient.JsonOpts); }
        catch { return null; }
    }

    // The account's guild list (used by the apitest to reach a real server's command index). The
    // gateway carries richer data, but this is the light REST form when only ids/names are needed.
    public async Task<List<UserGuild>> GetMyGuildsAsync()
    {
        var json = await GetStringOrNull("users/@me/guilds?with_counts=true");
        if (json == null) return new();
        try { return JsonSerializer.Deserialize<List<UserGuild>>(json, UserClient.JsonOpts) ?? new(); }
        catch { return new(); }
    }

    // Username / display name / avatar edit from User Settings. Username is always required by the
    // endpoint, so the caller passes the current value when only the avatar changed. avatar is a
    // data:image/png;base64 URI — Discord's avatar upload format.
    public async Task<(bool Ok, string? Error)> UpdateSelfAsync(string username, string globalName, string? avatarDataUri = null)
    {
        try
        {
            var body = new Dictionary<string, object> { ["username"] = username, ["global_name"] = globalName };
            if (avatarDataUri != null) body["avatar"] = avatarDataUri;
            var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Patch, "users/@me")
            {
                Content = Json(body),
            });
            if (resp.IsSuccessStatusCode) return (true, null);
            return (false, ErrorText(resp, await resp.Content.ReadAsStringAsync()));
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    // Presence/status change from the account tray.
    public async Task SetStatusAsync(string status)
    {
        try { await _http.PatchAsync("users/@me/settings", Json(new { status })); }
        catch (Exception ex) { _client.OnLog?.Invoke($"Status change failed: {ex.Message}"); }
    }

    // The "what's on your mind" line under your name. An empty text clears it.
    public async Task<bool> SetCustomStatusAsync(string? text)
    {
        try
        {
            object? custom = string.IsNullOrWhiteSpace(text) ? null : new { text = text!.Trim() };
            var resp = await _http.PatchAsync("users/@me/settings", Json(new { custom_status = custom }));
            if (!resp.IsSuccessStatusCode)
                _client.OnLog?.Invoke($"Custom status failed: {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { _client.OnLog?.Invoke($"Custom status failed: {ex.Message}"); return false; }
    }

    // ── Invites / membership ──

    // Create an invite for a channel. Discord's defaults: 7 days, unlimited uses.
    public async Task<string?> CreateInviteAsync(ulong channelId, int maxAgeSeconds = 604800, int maxUses = 0)
    {
        try
        {
            var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"channels/{channelId}/invites")
            {
                Content = Json(new { max_age = maxAgeSeconds, max_uses = maxUses, temporary = false, unique = false }),
            });
            if (!resp.IsSuccessStatusCode)
            {
                _client.OnLog?.Invoke($"Couldn't create an invite ({(int)resp.StatusCode}) — you may not have Create Invite here.");
                return null;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty("code", out var c) ? "https://discord.gg/" + c.GetString() : null;
        }
        catch (Exception ex) { _client.OnLog?.Invoke("Invite failed: " + ex.Message); return null; }
    }

    // Accepts "https://discord.gg/abc", "discord.com/invite/abc" or a bare "abc".
    public static string InviteCode(string raw)
    {
        var s = raw.Trim();
        int slash = s.LastIndexOf('/');
        if (slash >= 0) s = s[(slash + 1)..];
        int q = s.IndexOfAny(new[] { '?', '#' });
        return q >= 0 ? s[..q] : s;
    }

    // Look at an invite without accepting it, so the join dialog can name the server first.
    public async Task<(string? Guild, int Members, string? Error)> PreviewInviteAsync(string code)
    {
        var json = await GetStringOrNull($"invites/{Uri.EscapeDataString(code)}?with_counts=true");
        if (json == null) return (null, 0, "That invite is invalid or expired.");
        try
        {
            using var doc = JsonDocument.Parse(json);
            var g = doc.RootElement.TryGetProperty("guild", out var gg) ? gg : default;
            var name = g.ValueKind == JsonValueKind.Object && g.TryGetProperty("name", out var n) ? n.GetString() : null;
            int count = doc.RootElement.TryGetProperty("approximate_member_count", out var c) && c.TryGetInt32(out var cv) ? cv : 0;
            return (name, count, null);
        }
        catch { return (null, 0, "Couldn't read that invite."); }
    }

    // Everything the in-chat invite card shows. Discord resolves posted discord.gg links this way —
    // there is no embed for them, the client special-cases the URL itself.
    public sealed record InviteInfo(string Code, ulong GuildId, string Name, string? IconUrl,
                                    int Members, int Online, string? ChannelName);

    readonly Dictionary<string, InviteInfo?> _inviteCache = new();

    public async Task<InviteInfo?> GetInviteAsync(string code)
    {
        lock (_inviteCache) if (_inviteCache.TryGetValue(code, out var hit)) return hit;
        var json = await GetStringOrNull($"invites/{Uri.EscapeDataString(code)}?with_counts=true&with_expiration=true");
        InviteInfo? info = null;
        if (json != null)
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("guild", out var g) && g.ValueKind == JsonValueKind.Object)
                {
                    ulong gid = g.TryGetProperty("id", out var gi) && ulong.TryParse(gi.GetString(), out var gv) ? gv : 0;
                    var icon = g.TryGetProperty("icon", out var ic) ? ic.GetString() : null;
                    info = new InviteInfo(
                        code, gid,
                        g.TryGetProperty("name", out var n) ? n.GetString() ?? "Unknown" : "Unknown",
                        icon == null ? null : $"https://cdn.discordapp.com/icons/{gid}/{icon}.{(icon.StartsWith("a_") ? "gif" : "png")}?size=128",
                        root.TryGetProperty("approximate_member_count", out var mc) && mc.TryGetInt32(out var mv) ? mv : 0,
                        root.TryGetProperty("approximate_presence_count", out var pc) && pc.TryGetInt32(out var pv) ? pv : 0,
                        root.TryGetProperty("channel", out var ch) && ch.ValueKind == JsonValueKind.Object
                            && ch.TryGetProperty("name", out var cn) ? cn.GetString() : null);
                }
            }
            catch { }
        lock (_inviteCache) _inviteCache[code] = info;
        return info;
    }

    // ── server discovery ────────────────────────────────────────────────────────────────────────
    // Endpoints captured off the live client rather than guessed: browsing is a plain paged GET,
    // and joining is a PUT to the guild's own member collection. The discovery *preview* (what the
    // web client does the moment you click a card) is the same PUT with `lurker=true`, which is a
    // peek that leaves no membership — dropping it is what makes this a real join.

    public async Task<(List<UserDiscoverGuild> Guilds, int Total)> DiscoverGuildsAsync(int offset = 0, int limit = 30)
    {
        var json = await GetStringOrNull($"discoverable-guilds?offset={offset}&limit={limit}");
        if (json == null) return (new(), 0);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var total = doc.RootElement.TryGetProperty("total", out var t) ? t.GetInt32() : 0;
            var list = doc.RootElement.TryGetProperty("guilds", out var g)
                ? JsonSerializer.Deserialize<List<UserDiscoverGuild>>(g.GetRawText(), UserClient.JsonOpts) ?? new()
                : new();
            return (list, total);
        }
        catch { return (new(), 0); }
    }

    /// Joins a discoverable server for real. GUILD_CREATE follows on the gateway, so the rail picks
    /// it up the same way it picks up an accepted invite.
    public async Task<(bool Ok, string? Error)> JoinDiscoverableGuildAsync(ulong guildId)
    {
        try
        {
            var url = $"guilds/{guildId}/members/@me"
                    + $"?session_id={Uri.EscapeDataString(_client.SessionId ?? "")}"
                    + "&location=Guild%20Discovery";
            var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Put, url));
            if (resp.IsSuccessStatusCode) return (true, null);
            var body = await resp.Content.ReadAsStringAsync();
            return (false, ErrorText(resp, body));
        }
        catch (Exception e) { return (false, e.Message); }
    }

    // POST with an empty body is the accept. GUILD_CREATE follows on the gateway.
    public async Task<(bool Ok, string? Error)> AcceptInviteAsync(string code)
    {
        try
        {
            var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"invites/{Uri.EscapeDataString(code)}")
            {
                Content = Json(new { session_id = _client.SessionId }),
            });
            var body = await resp.Content.ReadAsStringAsync();
            return resp.IsSuccessStatusCode ? (true, null) : (false, ErrorText(resp, body));
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<bool> LeaveGuildAsync(ulong guildId)
    {
        try
        {
            var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, $"users/@me/guilds/{guildId}")
            {
                Content = Json(new { lurking = false }),
            });
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { _client.OnLog?.Invoke($"Leave failed: {ex.Message}"); return false; }
    }

    // ── Per-guild notification settings (mute) ──

    public async Task<bool> SetGuildMutedAsync(ulong guildId, bool muted) =>
        await PatchGuildSettings(guildId, new { muted });

    public async Task<bool> SetChannelMutedAsync(ulong guildId, ulong channelId, bool muted) =>
        await PatchGuildSettings(guildId, new
        {
            channel_overrides = new Dictionary<string, object> { [channelId.ToString()] = new { muted } },
        });

    // 0 = all messages, 1 = only mentions, 2 = nothing, 3 = inherit from the server (channels only).
    public async Task<bool> SetGuildNotifyLevelAsync(ulong guildId, int level) =>
        await PatchGuildSettings(guildId, new { message_notifications = level });

    public async Task<bool> SetChannelNotifyLevelAsync(ulong guildId, ulong channelId, int level) =>
        await PatchGuildSettings(guildId, new
        {
            channel_overrides = new Dictionary<string, object>
            { [channelId.ToString()] = new { message_notifications = level } },
        });

    async Task<bool> PatchGuildSettings(ulong guildId, object body)
    {
        try
        {
            // DMs live under the sentinel guild id "@me" in this endpoint.
            var resp = await _http.PatchAsync($"users/@me/guilds/{(guildId == 0 ? "@me" : guildId.ToString())}/settings", Json(body));
            if (!resp.IsSuccessStatusCode) _client.OnLog?.Invoke($"Notification settings failed: {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { _client.OnLog?.Invoke($"Notification settings failed: {ex.Message}"); return false; }
    }

    // ── GIFs (Discord proxies Tenor) ──

    public record GifResult(string Url, string Preview, int Width, int Height);

    // No media_format filter: the picker needs `gif_src` to preview with, and asking for one
    // format makes Discord drop the others.
    // Discord dropped Tenor for Klipy: `provider=tenor` is now a 400, and `limit` must be >= 20.
    // `media_format=gif` matters — the default hands back mp4/webp, neither of which GDI+ decodes.
    public Task<List<GifResult>> TrendingGifsAsync() =>
        Gifs("gifs/trending-gifs?media_format=gif&limit=40&locale=en-US");

    public Task<List<GifResult>> SearchGifsAsync(string query) =>
        Gifs($"gifs/search?q={Uri.EscapeDataString(query)}&media_format=gif&limit=40&locale=en-US");

    async Task<List<GifResult>> Gifs(string url)
    {
        var list = new List<GifResult>();
        LastGifError = null;
        string json;
        try
        {
            var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url));
            json = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                // Say what actually went wrong. This used to read "check the token", which sent
                // people hunting for a Giphy/Tenor key that this client has never needed — the GIF
                // search is Discord's own proxy and rides the same session token as everything else.
                LastGifError = $"Discord's GIF service returned {(int)resp.StatusCode}.";
                return list;
            }
        }
        catch (Exception ex)
        {
            LastGifError = "Couldn't reach Discord's GIF service: " + ex.Message;
            return list;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            IEnumerable<JsonElement> items = root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray()
                : FindArray(root, "gifs", "results", "data", "items");

            foreach (var g in items)
            {
                if (g.ValueKind != JsonValueKind.Object) continue;
                // `src` is the media; `url` is the provider's *web page* for it, which posts as a
                // bare link instead of a GIF. Media first, page only as a last resort.
                var src = StringProperty(g, "src", "gif_url", "content_url", "source", "url");
                // Preview must be something GDI+ can decode, so a webp `gif_src` is not usable here.
                var prev = src;
                if (string.IsNullOrWhiteSpace(src)) continue;
                int w = IntProperty(g, 200, "width", "w");
                int h = IntProperty(g, 200, "height", "h");
                list.Add(new GifResult(src!, prev!, Math.Clamp(w, 1, 4096), Math.Clamp(h, 1, 4096)));
            }

            if (list.Count == 0 && root.ValueKind != JsonValueKind.Array &&
                !FindArray(root, "gifs", "results", "data", "items").Any())
                LastGifError = "Discord returned an unexpected GIF response.";
        }
        catch (Exception ex)
        {
            LastGifError = "Could not read GIF results: " + ex.Message;
        }
        return list;

        static IEnumerable<JsonElement> FindArray(JsonElement root, params string[] names)
        {
            if (root.ValueKind != JsonValueKind.Object) return Enumerable.Empty<JsonElement>();
            foreach (var name in names)
                if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array)
                    return value.EnumerateArray();
            return Enumerable.Empty<JsonElement>();
        }

        static string? StringProperty(JsonElement item, params string[] names)
        {
            foreach (var name in names)
                if (item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(value.GetString())) return value.GetString();
            return null;
        }

        static int IntProperty(JsonElement item, int fallback, params string[] names)
        {
            foreach (var name in names)
            {
                if (!item.TryGetProperty(name, out var value)) continue;
                if (value.TryGetInt32(out var n)) return n;
                if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out n)) return n;
            }
            return fallback;
        }
    }

    // ── Stickers ──

    // The free "standard" packs every account has. Guild stickers come down with GUILD_CREATE.
    public async Task<List<(string Pack, List<UserSticker> Stickers)>> GetStickerPacksAsync()
    {
        var packs = new List<(string, List<UserSticker>)>();
        var json = await GetStringOrNull("sticker-packs");
        if (json == null) return packs;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var arr = doc.RootElement.TryGetProperty("sticker_packs", out var sp) ? sp : doc.RootElement;
            if (arr.ValueKind != JsonValueKind.Array) return packs;
            foreach (var p in arr.EnumerateArray())
            {
                var name = p.TryGetProperty("name", out var n) ? n.GetString() ?? "Pack" : "Pack";
                var items = p.TryGetProperty("stickers", out var s)
                    ? s.Deserialize<List<UserSticker>>(UserClient.JsonOpts) ?? new() : new();
                if (items.Count > 0) packs.Add((name, items));
            }
        }
        catch { }
        return packs;
    }

    public async Task<UserMessage?> SendStickerAsync(ulong channelId, ulong stickerId, string? text = null)
    {
        try
        {
            return await PostMessage(channelId, new
            {
                content = text ?? "",
                sticker_ids = new[] { stickerId.ToString() },
                nonce = Nonce(),
                tts = false,
            });
        }
        catch (Exception ex) { _client.OnLog?.Invoke($"Sticker send failed: {ex.Message}"); return null; }
    }

    // ── Threads ──

    // Reading a thread works without this; posting into one you are not a member of does not.
    public async Task JoinThreadAsync(ulong threadId)
    {
        try { await SendAsync(() => new HttpRequestMessage(HttpMethod.Put, $"channels/{threadId}/thread-members/@me")); }
        catch { }
    }

    /// Leave a thread. It stays readable — leaving only drops it out of your sidebar and stops it
    /// notifying you, which is what the real client's "Leave Thread" does.
    public async Task LeaveThreadAsync(ulong threadId)
    {
        try { await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, $"channels/{threadId}/thread-members/@me")); }
        catch { }
    }
}
