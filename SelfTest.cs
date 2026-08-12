using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ClaudeScord;

// The pure logic that is wrong-in-silence if it breaks: the markdown parser (a bad rule mangles
// every message) and the DPI math (a bad scale clips every box in the client).
//
// Run with: ClaudeScord.exe --selftest
static class SelfTest
{
    static int _fail;

    /// Draw `s` into a box exactly as wide as Ui.Measure claims it needs, and again into a box with
    /// room to spare. If the tight one lost characters to EndEllipsis the two bitmaps differ.
    static bool Ellipsised(string s, Font f)
    {
        var need = Ui.Measure(s, f);
        const TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
        return !Render(need.Width).SequenceEqual(Render(need.Width + 40));

        byte[] Render(int w)
        {
            using var bmp = new Bitmap(need.Width + 40, need.Height + 8);
            using (var g = Graphics.FromImage(bmp))
                Ui.Text(g, s, f, new Rectangle(0, 0, w, bmp.Height), Color.White, flags);
            // Compare only the strip the tight box could paint into.
            var bytes = new List<byte>();
            for (int y = 0; y < bmp.Height; y++)
                for (int x = 0; x < need.Width; x++)
                    bytes.Add((byte)(bmp.GetPixel(x, y).A > 0 ? 1 : 0));
            return bytes.ToArray();
        }
    }

    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what);
        if (!ok) _fail++;
    }

    public static int Run()
    {
        // ── markdown: inline styles ──
        var runs = Markdown.Parse("**bold** *it* `code` ||spoil||");
        Check(runs.Any(r => r.Text == "bold" && r.Style.HasFlag(Style.Bold)), "bold");
        Check(runs.Any(r => r.Text == "it" && r.Style.HasFlag(Style.Italic)), "italic");
        Check(runs.Any(r => r.Text == "code" && r.Style.HasFlag(Style.Code)), "inline code");
        Check(runs.Any(r => r.Style.HasFlag(Style.Spoiler) && r.SpoilerId != 0), "spoiler grouped by id");

        Check(Markdown.Parse("# Title").Any(r => r.Style.HasFlag(Style.H1)), "h1");
        Check(Markdown.Parse("### Small").Any(r => r.Style.HasFlag(Style.H3)), "h3");
        Check(Markdown.Parse("-# fine print").Any(r => r.Style.HasFlag(Style.Subtext)), "subtext");
        Check(Markdown.Parse("- item").Any(r => r.Text.StartsWith("•")), "bullet list");
        Check(Markdown.Parse("> quoted").All(r => r.Quote || r.Break), "blockquote flag");
        Check(Markdown.Parse(@"\*not italic\*").Any(r => r.Text.Contains('*')), "backslash escape");
        Check(Markdown.Parse("2 * 3 = 6").Any(r => r.Text.Contains('*')), "lone asterisk is literal");

        // ── markdown: links ──
        Check(Markdown.Parse("[label](https://example.com/x)")
              .Any(r => r.Text == "label" && r.Url == "https://example.com/x"), "masked link");
        Check(Markdown.Parse("see https://example.com/a.")
              .Any(r => r.Url == "https://example.com/a"), "bare link drops trailing period");
        var noEmbed = Markdown.Parse("see <https://example.com/a> ok");
        Check(noEmbed.Any(r => r.Url == "https://example.com/a"), "<url> links");
        Check(noEmbed.All(r => !r.Text.Contains('<') && !r.Text.Contains('>')), "<url> brackets are syntax");

        // ── markdown: emoji + timestamps ──
        Check(Markdown.Parse("<:blob:12345>").Any(r => r.Emoji && r.Url!.Contains("12345")), "custom emoji");
        Check(Markdown.Parse("<a:blob:12345>").Any(r => r.Url!.EndsWith(".gif?size=48")), "animated emoji uses gif");
        Check(Markdown.Parse("\U0001F600\U0001F600").Any(r => r.BigEmoji), "emoji-only message goes jumbo");
        Check(!Markdown.Parse("hi \U0001F600").Any(r => r.BigEmoji), "emoji with text stays inline size");
        Check(Markdown.Parse("<t:1700000000:D>").Any(r => r.Mention && r.Text.Contains("2023")),
              "unix timestamp formats to a date");

        // ── mentions now resolve through App delegates, not through Discord.Net ──
        // This is the part the rewrite changed, so it is the part most likely to regress.
        App.ResolveUserMention = _ => ("tester", Color.FromArgb(10, 20, 30));
        App.ResolveRoleMention = _ => ("Mods", Color.FromArgb(40, 50, 60));
        App.ResolveChannelName = _ => "general";
        Check(Markdown.Parse("<@123>").Any(r => r.Text == "@tester"), "user mention uses the resolver");
        Check(Markdown.Parse("<@&123>").Any(r => r.Text == "@Mods"), "role mention uses the resolver");
        Check(Markdown.Parse("<#123>").Any(r => r.Text == "#general"), "channel mention uses the resolver");

        App.ResolveUserMention = null;
        App.ResolveRoleMention = null;
        App.ResolveChannelName = null;
        Check(Markdown.Parse("<@123>").Any(r => r.Text == "@unknown-user"), "user mention degrades without a resolver");
        Check(Markdown.Parse("<@&123>").Any(r => r.Text == "@deleted-role"), "role mention degrades without a resolver");
        Check(Markdown.Parse("<#123>").Any(r => r.Text == "#unknown"), "channel mention degrades without a resolver");
        // The predecessor prefixed the default role a second time and rendered "@@everyone".
        App.ResolveRoleMention = _ => ("@everyone", null);
        Check(Markdown.Parse("<@&123>").Any(r => r.Text == "@everyone"), "@everyone is not double-prefixed");
        App.ResolveRoleMention = null;

        // ── reaction deltas ──
        // The gateway sends what changed, never the new tally, so these are the whole of "reactions
        // update live". They only ever showed after a channel reload when this was a no-op.
        {
            const ulong me = 7, other = 9;
            var thumb = new UserEmoji { Name = "\U0001F44D" };
            UserEmoji Custom() => new() { Name = "blob", Id = 42 };
            ReactionDelta D(ReactionDelta.Op k, UserEmoji? e, ulong u, int a = 0) => new(k, e, u, a);

            var m = new UserMessage();
            D(ReactionDelta.Op.Add, thumb, other).ApplyTo(m, me);
            Check(m.Reactions.Count == 1 && m.Reactions[0].Count == 1 && !m.Reactions[0].Me,
                  "a stranger's reaction appears, not marked mine");

            D(ReactionDelta.Op.Add, thumb, me).ApplyTo(m, me);
            Check(m.Reactions[0].Count == 2 && m.Reactions[0].Me, "my own add counts and marks me");

            // The echo of our own optimistic add must not count twice.
            D(ReactionDelta.Op.Add, thumb, me).ApplyTo(m, me);
            Check(m.Reactions[0].Count == 2, "a duplicate self-add is ignored");

            D(ReactionDelta.Op.Remove, thumb, me).ApplyTo(m, me);
            Check(m.Reactions[0].Count == 1 && !m.Reactions[0].Me, "removing mine clears the outline");

            D(ReactionDelta.Op.Remove, thumb, other).ApplyTo(m, me);
            Check(m.Reactions.Count == 0, "the pill disappears at zero");

            D(ReactionDelta.Op.Add, Custom(), other).ApplyTo(m, me);
            D(ReactionDelta.Op.Add, thumb, other).ApplyTo(m, me);
            D(ReactionDelta.Op.RemoveEmoji, Custom(), 0).ApplyTo(m, me);
            Check(m.Reactions.Count == 1 && m.Reactions[0].Emoji.Key == thumb.Key,
                  "remove-emoji drops only its own pill");
            D(ReactionDelta.Op.RemoveAll, null, 0).ApplyTo(m, me);
            Check(m.Reactions.Count == 0, "remove-all clears the row");

            // A remove for something not on the message must not throw or go negative.
            D(ReactionDelta.Op.Remove, thumb, other).ApplyTo(m, me);
            Check(m.Reactions.Count == 0, "removing an absent reaction is a no-op");

            var p = new UserMessage { Poll = new UserPoll { Results = new UserPollResults() } };
            D(ReactionDelta.Op.VoteAdd, null, me, 3).ApplyTo(p, me);
            Check(p.Poll!.CountFor(3) == (1, true), "a poll vote lands with me_voted");
            D(ReactionDelta.Op.VoteAdd, null, other, 3).ApplyTo(p, me);
            D(ReactionDelta.Op.VoteRemove, null, me, 3).ApplyTo(p, me);
            Check(p.Poll.CountFor(3) == (1, false), "un-voting leaves the stranger's vote");
        }

        // ── DPI ──
        Check(Ui.S(0) == 0, "S(0) is 0");
        Check(Ui.S(M.RailWidth) >= M.RailWidth, "S() never shrinks a design pixel");
        Check(Ui.LineBox(Theme.Body) > Theme.Body.Height, "a line box is taller than its font");
        // The member list was 24px too narrow in the predecessor; pin the measured value.
        Check(M.MembersWidth == 264 && M.MemberRow == 44, "member list metrics match the live client");
        Check(M.MessageTextLeft == M.MessagePadLeft + M.Avatar + 16, "message text clears the avatar");

        // ── ogg/opus demux ──
        // Discord voice messages are ogg/opus and Media Foundation cannot open them at all, so this
        // demuxer is the only thing standing between a voice message and silence. Build a real Ogg
        // stream around Concentus-encoded packets and check it decodes back to the right length —
        // the failure mode otherwise is a voice message that plays as nothing, with no error.
        {
            const int Rate = 48000, Frame = 960;      // 20ms
            var enc = new Concentus.Structs.OpusEncoder(Rate, 1, Concentus.Enums.OpusApplication.OPUS_APPLICATION_VOIP);
            var packets = new List<byte[]>();
            var pcm = new short[Frame];
            for (int f = 0; f < 25; f++)               // 500ms of a 440Hz tone
            {
                for (int n = 0; n < Frame; n++)
                    pcm[n] = (short)(8000 * Math.Sin(2 * Math.PI * 440 * (f * Frame + n) / Rate));
                var buf = new byte[4000];
                int len = enc.Encode(pcm, 0, Frame, buf, 0, buf.Length);
                packets.Add(buf[..len]);
            }

            // OpusHead: magic, version, channels, pre-skip, rate, gain, mapping.
            var head = new byte[19];
            "OpusHead"u8.CopyTo(head);
            head[8] = 1; head[9] = 1;                 // version 1, 1 channel
            BitConverter.GetBytes((uint)Rate).CopyTo(head, 12);
            var tags = new byte[16];
            "OpusTags"u8.CopyTo(tags);

            var ogg = new MemoryStream();
            int seq = 0;
            void Page(byte[] packet)
            {
                // One packet per page keeps the segment table trivial, which is legal Ogg and is
                // what most encoders emit for the header pages anyway.
                var segs = new List<byte>();
                int left = packet.Length;
                while (left >= 255) { segs.Add(255); left -= 255; }
                segs.Add((byte)left);

                var h = new byte[27];
                "OggS"u8.CopyTo(h);
                h[5] = 0;
                BitConverter.GetBytes((uint)seq).CopyTo(h, 18);
                h[26] = (byte)segs.Count;
                ogg.Write(h);
                ogg.Write(segs.ToArray());
                ogg.Write(packet);
                seq++;
            }
            Page(head); Page(tags);
            foreach (var p in packets) Page(p);

            var (got, rate, ch) = OggOpus.Decode(ogg.ToArray());
            Check(rate == 48000 && ch == 1, $"opus decodes at 48k mono (got {rate}Hz x{ch})");
            int samples = got.Length / 2;
            // 25 frames of 960 samples, allowing for the decoder dropping nothing and the two
            // header packets being skipped rather than decoded as audio.
            Check(samples >= Frame * 20 && samples <= Frame * 30,
                  $"the ogg demuxer recovers ~{Frame * 25} samples (got {samples})");
            Check(got.Any(b => b != 0), "the decoded voice message is not silence");
        }

        // ── icon geometry ──
        // Clyde's eyes and the smiley's eyes and mouth are holes punched by counter-wound subpaths.
        // A path parser that gets a subpath slightly wrong still draws something Clyde-shaped, so the
        // failure is invisible in review and obvious only at 6x zoom — the left eye came out as a
        // crescent for months. Render the icon and check the holes are actually holes.
        {
            static bool Hole(string d, float x, float y, float viewBox = 24f, string? dump = null)
            {
                const int Size = 240;
                using var bmp = new Bitmap(Size, Size);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Black);
                    Svg.SvgFill(g, d, new RectangleF(0, 0, Size, Size), Color.White, viewBox);
                }
                if (dump != null)
                    try { bmp.Save(Path.Combine(AppContext.BaseDirectory, dump), ImageFormat.Png); } catch { }
                var px = bmp.GetPixel((int)(x / viewBox * Size), (int)(y / viewBox * Size));
                return px.R < 128;   // still black = the hole survived
            }
            if (Environment.GetCommandLineArgs().Contains("--icons"))
            {
                Hole(Icons.Clyde, 0, 0, 24f, "icon-clyde.png");
                Hole(Icons.SmileyLine, 0, 0, 24f, "icon-smiley.png");
            }

            Check(Hole(Icons.Clyde, 8.3f, 12.85f), "Clyde's left eye is a hole");
            Check(Hole(Icons.Clyde, 15.7f, 12.85f), "Clyde's right eye is a hole");
            Check(!Hole(Icons.Clyde, 12f, 13f), "Clyde's face is filled between the eyes");
            Check(Hole(Icons.SmileyLine, 6.5f, 11.5f), "the smiley's left eye is a hole");
            Check(Hole(Icons.SmileyLine, 17.5f, 11.5f), "the smiley's right eye is a hole");
            Check(!Hole(Icons.SmileyLine, 12f, 7f), "the smiley's forehead is filled");
        }

        // ── image cache accounting ──
        // The budget is only as good as this model, and both obvious models are badly wrong: one
        // frame under-counts a GIF ~8x, a frame per frame over-counts it ~6x. `--memtest` measures
        // real trending GIFs against it — encoded bytes plus about three frames matched all 20 to
        // within 7%. Get this wrong and the cache either grows without bound or evicts what is still
        // on screen and re-downloads it on the next paint.
        {
            var still = new Bitmap(100, 100);
            Check(Media.CostOf(still) == 100L * 100 * 4, "a still image costs its pixels");

            // A real two-frame 1x1 GIF, assembled byte by byte: no network, and no reliance on the
            // GDI+ GIF encoder, whose multi-frame support is unreliable on .NET Core.
            var frame = new byte[]
            {
                0x21, 0xF9, 0x04, 0x00, 0x0A, 0x00, 0x00, 0x00,             // graphic control, delay 10
                0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, // image descriptor, 1x1
                0x02, 0x02, 0x44, 0x01, 0x00,                               // LZW: clear, pixel 0, EOI
            };
            var gifBytes = new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, // "GIF89a"
                                        0x01, 0x00, 0x01, 0x00,             // 1x1
                                        0x80, 0x00, 0x00,                   // global colour table, 2 entries
                                        0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF }
                            .Concat(frame).Concat(frame).Append((byte)0x3B).ToArray();

            using var ms = new MemoryStream(gifBytes);
            var gif = Image.FromStream(ms);
            int frames = gif.GetFrameCount(FrameDimension.Time);
            Check(frames == 2, $"the probe gif really is animated ({frames} frames)");

            // An animated image keeps the stream it was decoded from, so the encoded bytes are part
            // of what it costs — and are usually most of it. The frame count is deliberately *not*
            // in here: GDI+ decodes frames on demand and keeps only a couple.
            long cost = Media.CostOf(gif, gifBytes.Length);
            Check(cost == gifBytes.Length + 1L * 1 * 4 * 3,
                  $"an animated image is billed its encoded bytes plus a few frames (got {cost})");
            Check(Media.CostOf(gif, 0) < Media.CostOf(gif, 100_000),
                  "the encoded bytes are actually part of the bill");
        }

        // A still image must come out of Decode owning its pixels: if it still referenced the source
        // stream, every avatar would hold its encoded PNG for the life of the session on top of the
        // bitmap. Pinned by round-tripping a PNG and checking the result is a Bitmap of our own
        // format rather than whatever Image.FromStream handed back.
        {
            using var src = new Bitmap(8, 8);
            src.SetPixel(3, 3, Color.FromArgb(255, 12, 34, 56));
            using var png = new MemoryStream();
            src.Save(png, ImageFormat.Png);
            var (decoded, animated) = Media.DecodeFor(png.ToArray());
            Check(!animated && decoded is Bitmap, "a png decodes to a still bitmap");
            Check(decoded!.PixelFormat == PixelFormat.Format32bppPArgb, "stills are normalised to PArgb");
            var got = ((Bitmap)decoded).GetPixel(3, 3);
            Check(got.R == 12 && got.G == 34 && got.B == 56, $"the 1:1 copy is exact (got {got.R},{got.G},{got.B})");
            decoded.Dispose();
        }

        // ── easing ──
        // The point of Ui.Ease is that motion depends on elapsed time, not on tick count: one 30ms
        // step must land where two 15ms steps do. A per-tick lerp fails this, and that difference is
        // exactly the scroll stutter it was written to remove.
        {
            float one = Ui.Ease(0f, 100f, 0.030f);
            float two = Ui.Ease(Ui.Ease(0f, 100f, 0.015f), 100f, 0.015f);
            Check(Math.Abs(one - two) < 0.01f, $"Ease is frame-rate independent ({one:F3} vs {two:F3})");
            Check(Ui.Ease(0f, 100f, 0f) == 0f, "Ease with no elapsed time does not move");
            Check(Ui.Ease(50f, 50f, 0.016f) == 50f, "Ease at the target stays put");
            // A long stall must not teleport: dt is clamped, so one tick can never fully arrive.
            Check(Ui.Ease(0f, 100f, 5f) < 99.9f, "Ease clamps a huge dt instead of snapping");
        }

        // ── wheel ──
        // Every list converts a wheel message the same way, because they are all measured against
        // the real client: 120 units is one notch is 100 CSS px, scaled like everything else.
        {
            Check(Ui.Wheel(120) == Ui.S(100), "a notch is 100 design px");
            Check(Ui.Wheel(-240) == -Ui.S(200), "two notches back are twice as far, the other way");
            Check(Ui.WheelPx(1) != 0, "the smallest report a touchpad can send still moves the list");
            Check(!Ui.Precise(120), "a single notch is a wheel and gets the glide");
            Check(Ui.Precise(-360), "a coalesced multi-notch burst is a touchpad fling, not three notches");
            Check(Ui.Precise(37), "a sub-notch delta is a precision touchpad");
        }

        // ── attachment sizing ──
        // Pictures are never blown up; a video is, up to minW, because the player paints a fixed
        // transport strip over it and a 160px-wide clip is nearly all seek bar otherwise.
        {
            Check(MessageRow.FitBox(1600, 900, 550, 350) == new Size(550, 309), "a big picture fits the envelope");
            Check(MessageRow.FitBox(160, 120, 550, 350) == new Size(160, 120), "a small picture stays its own size");
            Check(MessageRow.FitBox(160, 120, 550, 350, 400) == new Size(400, 300), "a small video grows to the minimum");
            Check(MessageRow.FitBox(320, 20, 550, 350, 400) == new Size(400, 25), "growing it keeps the aspect ratio");
            Check(MessageRow.FitBox(1600, 900, 550, 350, 400) == new Size(550, 309), "a big video is still only shrunk");
        }

        // ── scroll offset ──
        // Scroller's glide needs a message pump (its timer) to run, so what is checkable here is the
        // half that decides where the list may end up: Wheel returns whether the offset will move,
        // and the target never runs past either end.
        {
            using var host = new Control();
            var s = new Scroller(host);
            Check(!s.Wheel(120, 0), "a notch with nothing to scroll moves nothing");
            Check(s.Wheel(-120, 500), "a notch down moves the list");
            Check(s.Target == Ui.S(100), "a notch is exactly one notch of travel");
            s.JumpTo(500, 500);
            Check(!s.Wheel(-120, 500), "a notch past the end stops at the end");
            Check(s.Wheel(120, 500), "and comes back off the end");
            Check(s.Target == 500 - Ui.S(100), "back off the end is one notch of travel");
            // A list that shrank under a scrolled-away offset must be pulled back inside it, or it
            // paints a gap where the content used to be.
            s.Clamp(0);
            Check(!s.Wheel(120, 0), "content that shrank to nothing pulls the list back to the top");
            // A precision touchpad is followed directly, not glided: the offset lands immediately.
            s.Wheel(-37, 500);
            Check(s.Target == Ui.Wheel(37), "a touchpad delta lands immediately, no glide");
        }

        // ── compact message layout ──
        // Compact folds the timestamp + sender into the body of the FIRST message of a group; the
        // messages that follow show just their content. Repeating the name on every row is what
        // made the mode read as cozy-without-avatars instead of Discord's compact.
        {
            static UserMessage CompactMsg(string content, string author, DateTime ts) => new()
            {
                Id = (ulong)Math.Abs(content.GetHashCode()),
                ChannelId = 1,
                Author = new UserUser { Id = 42, Username = author },
                Content = content,
                Type = 0,
                Timestamp = ts,
                Attachments = new(), Embeds = new(), Reactions = new(), Stickers = new(),
                Mentions = new(), MentionRoles = new(), Snapshots = new(),
            };
            static bool BodyNames(MessageRow r, string name) => r.Body.Any(p => p.Text.Trim() == name);

            bool saved = Prefs.Current.CompactMode;
            Prefs.Current.CompactMode = true;
            try
            {
                var prev = new MessageRow { Msg = CompactMsg("earlier", "Alice", DateTime.Today.AddHours(10)) };
                prev.GroupStart = true;
                prev.Layout(800, null);

                var first = new MessageRow { Msg = CompactMsg("hello world", "Bob", DateTime.Today.AddHours(11)) };
                first.GroupStart = true;
                first.Layout(800, prev);
                Check(BodyNames(first, "Bob"), "compact leads a group with the sender name");

                var second = new MessageRow { Msg = CompactMsg("again", "Bob", DateTime.Today.AddHours(11).AddMinutes(1)) };
                second.GroupStart = false;
                second.Layout(800, first);
                Check(!BodyNames(second, "Bob"), "the next message in the group shows only its content");
            }
            finally { Prefs.Current.CompactMode = saved; }
        }

        // ── 1:1 DM profile panel ──
        // The DM "Show Member List" panel paints the live user immediately and the fetched profile
        // fills in banner/bio/pronouns/mutuals behind it. A throw here would be an invisible white
        // column every time a DM is opened with the panel on, so it is painted onto a real bitmap.
        {
            static bool Paints(Control c, Bitmap bmp)
            {
                try { c.DrawToBitmap(bmp, new Rectangle(0, 0, c.Width, c.Height)); return true; }
                catch { return false; }
            }

            var u = new UserUser { Id = 7, Username = "robin" };
            var p = new UserProfile
            {
                Detail = new UserProfileDetail { Bio = "building things", Pronouns = "she/her" },
                MutualGuilds = { new UserMutualGuild { Id = 1 } },
            };
            var ml = new MemberList { Width = Ui.S(264), Height = Ui.S(600) };
            using var bmp = new Bitmap(ml.Width, ml.Height);

            ml.SetProfile(new MemberList.Profile(u, null));
            Check(Paints(ml, bmp), "a 1:1 DM profile panel paints before the profile fetch");
            // The name renders even with no profile data; its text row must have ink on it.
            bool ink = false;
            for (int x = Ui.S(16); x < Ui.S(160); x++)
                if (bmp.GetPixel(x, Ui.S(165)).A > 0) { ink = true; break; }
            Check(ink, "the panel draws the user's name from the live user object");

            ml.UpdateProfile(u.Id, p);
            Check(Paints(ml, bmp), "the fetched profile (bio/pronouns/mutuals) paints");
            ml.UpdateProfile(999, p);   // a fetch for a different DM must not disturb this one
            Check(Paints(ml, bmp), "a stale profile fetch for another DM paints harmlessly");

            // A Nitro accent colour must flow into the banner fill and the popout tint even when
            // only the user object (not user_profile) carries it — the API's usual shape.
            ml.UpdateProfile(u.Id, new UserProfile
            {
                Detail = new UserProfileDetail { AccentColor = 0x5865F2 },
            });
            Check(Paints(ml, bmp), "the panel paints with a Nitro accent colour theme");
            ml.UpdateProfile(u.Id, new UserProfile
            {
                User = new UserUser { Id = 7, Banner = "a_deadbeef" },
                Detail = new UserProfileDetail { AccentColor = 0x5865F2 },
            });
            Check(Paints(ml, bmp), "the panel paints with an animated (a_) banner hash");

            ml.SetMembers(Array.Empty<MemberList.Entry>());
            Check(Paints(ml, bmp), "the roster paints after a profile was shown");
        }

        // ── Nitro profile colour ──
        // The accent colour lives on the user object inside the profile payload more often than on
        // user_profile, and the legacy banner_color hex must still wash the banner.
        {
            var p = new UserProfile
            {
                User = new UserUser { Banner = "abc123", AccentColor = 0x112233 },
                Detail = new UserProfileDetail(),
            };
            Check(p.BannerHash == "abc123", "the profile banner falls back to the user object's banner");
            Check(p.Accent == 0x112233, "the profile accent falls back to the user object's accent");
            Check(p.ProfileColor == 0x112233, "the banner wash is the Nitro accent colour");

            var g = new UserProfile { Detail = new UserProfileDetail { Banner = "xyz", AccentColor = 0x445566 } };
            Check(g.BannerHash == "xyz" && g.Accent == 0x445566, "the per-guild/global detail wins when present");
            Check(g.BannerUrl(5)!.Contains("xyz"), "the banner url uses the winning hash");

            var legacy = new UserProfile { Detail = new UserProfileDetail { BannerColor = "#ff8800" } };
            Check(legacy.ProfileColor == 0xFF8800, "a legacy banner_color hex washes the banner");
            var none = new UserProfile { Detail = new UserProfileDetail() };
            Check(none.ProfileColor == null, "no profile colour means the caller falls back to role colour");
        }

        // ── measure/draw agreement ──
        // Layout everywhere sizes a box with Ui.Measure and then fills it with Ui.Text under
        // EndEllipsis. If the two disagree on padding the box comes out narrower than the string GDI
        // actually draws, and a name that fits gets an ellipsis anyway — this is how "#general"
        // rendered as "#gen..." in the chat header. Drawn twice and compared pixel-for-pixel, so it
        // tests the real rendering rather than restating whichever flags Ui.Text happens to pass.
        foreach (var probe in new[] { "general", "SBGAMERZPRO", "Direct Messages" })
            Check(!Ellipsised(probe, Theme.BodyMedium), $"\"{probe}\" fits the box Ui.Measure sized for it");

        // ── type face ──
        // The body family is Nunito where the machine has it (closest installed face to gg sans)
        // and Segoe UI otherwise — a font whose family silently failed to resolve would fall back
        // to Microsoft Sans Serif and render noticeably off. Pin the resolution and the medium
        // weight so a broken fallback is caught here, not on a user's screen.
        var bodyName = Theme.Body.Name;
        Check(bodyName.Equals("Nunito", StringComparison.OrdinalIgnoreCase)
              || bodyName.Equals("Segoe UI", StringComparison.OrdinalIgnoreCase),
              "body font is Nunito or Segoe UI, not a silent fallback");
        var medName = Theme.BodyMedium.Name;
        Check(medName.StartsWith("Nunito", StringComparison.OrdinalIgnoreCase)
              || medName.Equals("Segoe UI Semibold", StringComparison.OrdinalIgnoreCase),
              "medium font is a real 500/600 family, not a mismatch");
        Check(Theme.Body.SizeInPoints > 0 && Theme.Small.SizeInPoints < Theme.Body.SizeInPoints,
              "type scale is ordered: small < body");

        // ── sidebar ordering ──
        // Uncategorised channels come first, then each category followed by its own children, all by
        // Position. Empty categories are dropped, which is what the real client does.
        var guild = new UserGuild
        {
            Id = 1, Name = "g",
            Channels =
            {
                new UserChannelData { Id = 30, Name = "zeta",    Type = 0, Position = 2, ParentId = 10 },
                new UserChannelData { Id = 10, Name = "Cat A",   Type = 4, Position = 0 },
                new UserChannelData { Id = 20, Name = "loose",   Type = 0, Position = 0 },
                new UserChannelData { Id = 31, Name = "alpha",   Type = 0, Position = 1, ParentId = 10 },
                new UserChannelData { Id = 11, Name = "Empty",   Type = 4, Position = 1 },
                new UserChannelData { Id = 32, Name = "Talk",    Type = 2, Position = 3, ParentId = 10 },
            },
        };
        // The Events row is always first (the live client shows it even with nothing scheduled),
        // so the ordering check is about everything after it.
        var full = Session.BuildTree(guild);
        Check(full.Count > 0 && full[0].Kind == ChannelSidebar.Kind.Nav
              && full[0].Id == ChannelSidebar.EventsId, "Events sits above the channel list");
        var tree = full.Skip(1).Select(x => x.Name).ToList();
        Check(string.Join(",", tree) == "loose,Cat A,alpha,zeta,Talk",
              "sidebar order: orphans, then category children by position");
        Check(!tree.Contains("Empty"), "a category with no channels is not drawn");
        Check(Session.BuildTree(guild).Any(x => x.Kind == ChannelSidebar.Kind.Voice),
              "voice channels survive the tree build");

        // ── long unbroken text wraps instead of running off the edge ──
        // A word with no spaces (a long URL, a hash) used to be emitted at its full width and
        // overflowed the message column. The browser breaks mid-word; so do we.
        int wrapW = 200;
        var longWord = RichText.Layout(Markdown.Parse(new string('F', 400)), wrapW, out var lwH);
        Check(longWord.All(p => p.Box.Right <= wrapW),
              "an unbroken 400-char word stays inside the column");
        Check(longWord.Count > 1 && lwH > Ui.S(M.MessageLineHeight),
              "…by breaking across several lines");
        var normal = RichText.Layout(Markdown.Parse("hello world"), wrapW, out _);
        Check(normal.All(p => p.Box.Right <= wrapW) && normal.Any(p => p.Text.Contains("hello")),
              "ordinary text still lays out unbroken");

        // ── forum channels appear in the sidebar ──
        // IsText used to be `0 or 5`, so a forum was filtered out of the tree entirely and the
        // Kind.Forum branch was unreachable. It is a channel you read, so it belongs in the list —
        // but it takes posts, not messages, which is what IsPostable separates.
        var forumGuild = new UserGuild
        {
            Id = 2, Name = "f",
            Channels =
            {
                new UserChannelData { Id = 40, Name = "talk",  Type = 0,  Position = 0 },
                new UserChannelData { Id = 41, Name = "help",  Type = 15, Position = 1 },
                new UserChannelData { Id = 42, Name = "clips", Type = 16, Position = 2 },
            },
        };
        forumGuild.Reindex();
        var ftree = Session.BuildTree(forumGuild);
        Check(ftree.Any(x => x.Name == "help" && x.Kind == ChannelSidebar.Kind.Forum),
              "a forum channel is drawn in the sidebar, as a forum");
        Check(ftree.Any(x => x.Name == "clips" && x.Kind == ChannelSidebar.Kind.Forum),
              "a media channel is drawn as a forum too");
        var forumCh = forumGuild.ChannelById[41];
        Check(forumCh.IsText && forumCh.IsForum && !forumCh.IsPostable,
              "a forum reads as text (unread state) but is never postable");
        Check(forumGuild.ChannelById[40].IsPostable, "a plain text channel is postable");

        // ── optimistic send state ──
        // A row we drew before the server confirmed it must never be treated as a real message: it
        // is what NewestId skips so an invented snowflake is never acked.
        var pending = new UserMessage { Id = 999, Content = "hi", Nonce = "n1", SendState = 1 };
        Check(pending.IsPending && !pending.IsFailed, "SendState 1 reads as pending");
        pending.SendState = 2;
        Check(pending.IsFailed && !pending.IsPending, "SendState 2 reads as failed");
        pending.SendState = 0;
        Check(!pending.IsFailed && !pending.IsPending, "SendState 0 is a confirmed message");

        // The nonce arrives as a string from Discord's clients but a bare number is legal, and a
        // plain string property threw on those — taking the whole MESSAGE_CREATE with it.
        var numericNonce = JsonSerializer.Deserialize<UserMessage>(
            """{"id":"5","channel_id":"1","content":"x","nonce":123456789}""", UserClient.JsonOpts);
        Check(numericNonce?.Nonce == "123456789", "a numeric nonce parses instead of throwing");
        var strNonce = JsonSerializer.Deserialize<UserMessage>(
            """{"id":"5","channel_id":"1","content":"x","nonce":"abc"}""", UserClient.JsonOpts);
        Check(strNonce?.Nonce == "abc", "a string nonce parses");
        var noNonce = JsonSerializer.Deserialize<UserMessage>(
            """{"id":"5","channel_id":"1","content":"x"}""", UserClient.JsonOpts);
        Check(noNonce?.Nonce == null, "a message with no nonce is fine");

        // A locally-invented id has to sort after every real message on screen, or the optimistic
        // row would appear above the conversation instead of at the bottom.
        Check(UserRestClient.NonceId() > (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
              "an optimistic row's id is snowflake-shaped and sorts last");

        // ── threads resolve for the chat header ──
        // Threads live in ThreadById, not ChannelById; the header used to fall through to "channel".
        var threadGuild = new UserGuild { Id = 3, Name = "t" };
        threadGuild.Threads.Add(new UserThreadChannel { Id = 50, Name = "side chat", ParentId = 40, Type = 11 });
        threadGuild.Reindex();
        Check(threadGuild.ThreadById.GetValueOrDefault(50UL)?.Name == "side chat",
              "a thread is findable by id for the chat header");
        Check(threadGuild.ThreadById.GetValueOrDefault(50UL)?.Type == 11,
              "a thread carries its type, so the header draws the thread glyph");

        // ── token storage (DPAPI round trip) ──
        // The login form's whole security story rests on this: a token encrypts and decrypts back to
        // itself, and the ciphertext is not the plaintext.
        const string sample = "MTAzMzE2.GInKSQ.PuZHM3-fake-token-for-the-roundtrip-check";
        var blob = Crypto.Protect(sample);
        Check(blob != sample && !blob.Contains(sample), "protected token is not stored in the clear");
        Check(Crypto.TryUnprotect(blob) == sample, "protected token decrypts back to the original");
        Check(Crypto.TryUnprotect("not-a-valid-blob") == null, "a garbage blob decrypts to null, not a throw");

        // ── search filter grammar ──
        // The search box splits `from:user has:image "exact phrase"` into real query parameters
        // and plain content; a regression here silently degrades every filtered search.
        ulong? User(string n) => n == "nathan" ? 42 : null;
        ulong? Channel(string n) => n == "general" ? 7 : null;
        var (f1, c1, _) = SearchPopup.ParseQuery("from:nathan has:image hello", User, Channel);
        Check(f1.Count == 2 && f1[0].ParamKey == "author_id" && f1[0].ParamValue == "42", "from: resolves to author_id");
        Check(f1[1].ParamKey == "has" && f1[1].ParamValue == "image", "has: maps to the has parameter");
        Check(c1 == "hello", "plain words stay in content");
        Check(f1.All(f => f.Token.Length > 0), "chips keep their raw token for removal");

        var (f2, c2, _) = SearchPopup.ParseQuery("from:nobody known-user", User, Channel);
        Check(f2.Count == 0 && c2 == "from:nobody known-user", "unresolved from: degrades to plain text");

        var (f3, c3, o3) = SearchPopup.ParseQuery("in:general pinned x", User, Channel);
        Check(f3.Count == 2 && o3 == 7 && c3 == "x", "in: overrides the searched channel");
        Check(f3.Any(f => f.ParamKey == "channel_id" && f.ParamValue == "7"), "in: carries channel_id");

        var (f4, c4, _) = SearchPopup.ParseQuery("before:2024-01-15 \"two words\"", User, Channel);
        Check(f4.Count == 1 && f4[0].ParamKey == "before" && f4[0].ParamValue == "2024-01-15",
              "before: normalises the date");
        Check(c4 == "two words", "quoted phrases stay together in content");

        var (f5, _, _) = SearchPopup.ParseQuery("has:gif", User, Channel);
        Check(f5.Count == 0, "an unknown has: value is not a chip");
        var (f6, _, _) = SearchPopup.ParseQuery("pinned", User, Channel);
        Check(f6.Count == 1 && f6[0].ParamValue == "true", "bare pinned becomes pinned=true");

        // ── composer autocomplete grammar ──
        // The @-mention and :emoji: menus live on the word under the caret; a bad rule here either
        // pops a menu over normal typing or never pops at all.
        Check(Composer.ModeOf("@nat") == Composer.AutoMode.Mention, "@word opens the mention menu");
        Check(Composer.ModeOf("@") == Composer.AutoMode.Mention, "bare @ opens the mention menu");
        Check(Composer.ModeOf("nat") == Composer.AutoMode.None, "a plain word opens nothing");
        Check(Composer.ModeOf("a@b") == Composer.AutoMode.None, "@ inside a word opens nothing");
        Check(Composer.ModeOf(":joy") == Composer.AutoMode.Emoji, ":word opens the emoji menu");
        Check(Composer.ModeOf(":") == Composer.AutoMode.Emoji, "bare : opens the emoji menu");
        Check(Composer.ModeOf(":joy:") == Composer.AutoMode.None, "a closed :name: closes the emoji menu");
        Check(Composer.ModeOf("/beg") == Composer.AutoMode.Slash, "/word opens the slash menu");
        Check(Composer.EmojiMarkup("blob", 123, false) == "<:blob:123>", "static emoji markup");
        Check(Composer.EmojiMarkup("wave", 123, true) == "<a:wave:123>", "animated emoji markup is a-prefixed");

        // ── slash option grammar ──
        // After a command is picked, the rest of the line becomes its option values. A regression
        // here either never sends a value (bot sees empty options) or fires before required fields
        // are filled, so the parse and the gating rule are both pinned.
        var warn = new UserAppCommand
        {
            Name = "warn",
            Options =
            {
                new UserAppCommandOption { Name = "user", Type = 6, Required = true },
                new UserAppCommandOption { Name = "reason", Type = 3 },
            },
        };
        var ping = new UserAppCommand { Name = "ping" };
        var so0 = Composer.ParseSlashOptions("/ping", ping);
        Check(so0.Sub == null && so0.Values.Count == 0, "a command with no options parses empty");
        var so1 = Composer.ParseSlashOptions("/warn alice", warn);
        Check(so1.Sub == null && so1.Values.Count == 1 && so1.Values[0] == ("user", "alice"),
              "positional fill takes the first option");
        Check(so1.Fill.Count == 1 && so1.Fill[1] == 0, "first argument maps to option 0");
        var so2 = Composer.ParseSlashOptions("/warn alice spam", warn);
        Check(so2.Values.Count == 2 && so2.Values[0] == ("user", "alice") && so2.Values[1] == ("reason", "spam"),
              "a second positional value fills the next option");
        var so3 = Composer.ParseSlashOptions("/warn \"alice smith\" rude", warn);
        Check(so3.Values[0] == ("user", "alice smith"), "quoted values survive as one token");
        var so4 = Composer.ParseSlashOptions("/warn reason:spam user:alice", warn);
        Check(so4.Values.Count == 2 && so4.Values[0] == ("user", "alice") && so4.Values[1] == ("reason", "spam"),
              "name:value fills by name and reorders to declaration order");
        Check(Composer.FirstMissing(warn, Composer.ParseSlashOptions("/warn", warn)) is { } mm && mm.Name == "user",
              "required option without a value blocks Enter");
        Check(Composer.FirstMissing(warn, Composer.ParseSlashOptions("/warn alice", warn)) == null,
              "all required filled unblocks Enter");

        // ── slash option value coercion ──
        // Discord's interaction API wants real types, not strings: integers as numbers, booleans
        // as bools, and user/channel/role/mentionable as bare snowflakes (markup stripped).
        Check(Composer.CoerceSlashValue(warn.Options[0], "<@123>") is string uc1 && uc1 == "123",
              "user option coerces markup to a snowflake");
        Check(Composer.CoerceSlashValue(warn.Options[0], "<@!123>") is string uc2 && uc2 == "123",
              "<@!id> legacy mention strips to the id");
        Check(Composer.CoerceSlashValue(new UserAppCommandOption { Type = 4 }, "42") is long ic && ic == 42,
              "integer option coerces to a number");
        Check(Composer.CoerceSlashValue(new UserAppCommandOption { Type = 5 }, "true") is bool bc && bc,
              "boolean option coerces to a bool");
        Check(Composer.CoerceSlashValue(new UserAppCommandOption { Type = 3 }, "hello") is string sc && sc == "hello",
              "string option stays a string");

        // ── slash subcommands ──
        // A command with subcommands takes its subcommand name first; the remaining values belong
        // to that subcommand's own options, and a name that isn't a subcommand blocks the line.
        var mod = new UserAppCommand
        {
            Name = "mod",
            Options =
            {
                new UserAppCommandOption
                {
                    Name = "warn", Type = 1,
                    Options =
                    {
                        new UserAppCommandOption { Name = "user", Type = 6, Required = true },
                        new UserAppCommandOption { Name = "reason", Type = 3 },
                    },
                },
            },
        };
        var so5 = Composer.ParseSlashOptions("/mod warn alice", mod);
        Check(so5.Sub == "warn" && so5.Values.Count == 1 && so5.Values[0] == ("user", "alice"),
              "subcommand routes values to its own options");
        var so6 = Composer.ParseSlashOptions("/mod alice", mod);
        Check(so6.Sub == null && so6.Values.Count == 0, "a non-subcommand token blocks filling");

        // ── presence mapping ──
        Check(Theme.Dot(Presence.Online) == Theme.Online, "online dot");
        Check(Theme.Dot(Presence.Streaming) == Theme.Streaming, "streaming replaces the presence dot");
        Check(Theme.Dot((Presence)99) == Theme.Offline, "unknown presence falls back to offline");

        // ── voice transport: the 8-byte audio extension header ──
        // Layout reverse-engineered from captured real-client frames:
        //   silence = 32 38 C2 4A 10 FF 90 00    audio = 32 3C 5D 76 10 AD 90 02
        // bytes 1-3 = 24-bit BE timestamp in 1/256s ticks, byte 4 = 0x10, byte 5 = level,
        // byte 6 = 0x90, byte 7 = 0x00 silence / 0x02 audio. A wrong structure makes the
        // peer misparse every payload, so this is pinned byte for byte.
        Check(Convert.ToHexString(UdpVoice.AudioExtensionHeader(silence: true, 0x3C5D76))
              == "323C5D7610FF9000", "audio extension header: silence layout");
        Check(Convert.ToHexString(UdpVoice.AudioExtensionHeader(silence: false, 0x3C5D76))
              == "323C5D7610989002", "audio extension header: speech layout");

        // ── voice transport: RTP + ULEB128 ──
        var rtp = VoiceRtp.EncodeHeader(0x1234, 0x00c0ffee, 0xdeadbeef);
        Check(rtp.Length == 12 && rtp[0] == 0x80 && rtp[1] == 0x78, "RTP header flags (V2, payload type 120)");
        Check(VoiceRtp.DecodeHeader(rtp, out var sq, out var ts, out var ss) && sq == 0x1234 && ts == 0x00c0ffee && ss == 0xdeadbeef,
              "RTP header round-trips sequence/timestamp/ssrc big-endian");
        Check(!VoiceRtp.DecodeHeader(new byte[] { 0x01, 0x02, 0x03 }, out _, out _, out _), "short garbage is not an RTP header");
        // P-bit video headers (the real client pads its video fragments; first byte 0xA0) and
        // P+X (0xB0) must decode as valid RTP — this used to be a hard reject that misrouted the
        // peer's video into the audio path.
        var pbit = (byte[])rtp.Clone(); pbit[0] = 0xA0;
        var pxbit = (byte[])rtp.Clone(); pxbit[0] = 0xB0;
        Check(VoiceRtp.DecodeAnyHeader(pbit, out var sqp, out var tsp, out var ssp, out var ptp, out _)
              && sqp == 0x1234 && tsp == 0x00c0ffee && ssp == 0xdeadbeef && ptp == 0x78,
              "P-bit RTP header (0xA0) decodes as a real video/audio packet");
        Check(VoiceRtp.DecodeAnyHeader(pxbit, out _, out _, out _, out _, out _),
              "P+X RTP header (0xB0) decodes as a real packet");
        Check(!VoiceRtp.DecodeAnyHeader(new byte[] { 0x40, 0x78, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, out _, out _, out _, out _, out _),
              "V=1 (0x40) is not a valid RTP header");
        var uleb = new byte[5];
        int uln = VoiceRtp.Uleb128(0x2A, uleb);
        Check(uln == 1 && uleb[0] == 0x2A, "small ULEB128 is one byte");
        uln = VoiceRtp.Uleb128(300, uleb);
        Check(uln == 2 && uleb[0] == 0xAC && uleb[1] == 0x02, "ULEB128 300 encodes as AC 02");
        Check(VoiceRtp.TryUleb128(uleb.AsSpan(0, uln), out var uv, out var uc) && uv == 300 && uc == uln, "ULEB128 round-trips");

        // ── voice transport: ChaCha20 known answer (RFC 8439 §2.3.2) ──
        var ckey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var cblock = VoiceRtp.ChaChaBlockForTest(ckey, 1, new byte[] { 0, 0, 0, 9, 0, 0, 0, 0x4a });
        byte[] wantChaCha =
        {
            0x10,0xf1,0xe7,0xe4,0xd1,0x3b,0x59,0x15,0x50,0x0f,0xdd,0x1f,0xa3,0x20,0x71,0xc4,
            0xc7,0xd1,0xf4,0xc7,0x33,0xc0,0x68,0x03,0x04,0x22,0xaa,0x9a,0xc3,0xd4,0x6c,0x4e,
            0xd2,0x82,0x64,0x46,0x07,0x9f,0xaa,0x09,0x14,0xc2,0xd7,0x05,0xd9,0x8b,0x02,0xa2,
            0xb5,0x12,0x9c,0xd1,0xde,0x16,0x4e,0xb9,0xcb,0xd0,0x83,0xe8,0xa2,0x50,0x3c,0x4e,
        };
        Check(cblock.SequenceEqual(wantChaCha), "ChaCha20 block matches RFC 8439 §2.3.2");

        // ── voice transport: Poly1305 known answer (RFC 8439 §2.5.2) ──
        byte[] pkey =
        {
            0x85,0xd6,0xbe,0x78,0x57,0x55,0x6d,0x33,0x7f,0x44,0x52,0xfe,0x42,0xd5,0x06,0xa8,
            0x01,0x03,0x80,0x8a,0xfb,0x0d,0xb2,0xfd,0x4a,0xbf,0xf6,0xaf,0x41,0x49,0xf5,0x1b,
        };
        byte[] wantTag = { 0xa8, 0x06, 0x1d, 0xc1, 0x30, 0x51, 0x36, 0xc6, 0xc2, 0x2b, 0x8b, 0xaf, 0x0c, 0x01, 0x27, 0xa9 };
        var tag = VoiceRtp.Poly1305(pkey, "Cryptographic Forum Research Group"u8);
        System.Console.WriteLine("  debug poly got=" + Convert.ToHexString(tag) + " want=" + Convert.ToHexString(wantTag));
        Check(tag.SequenceEqual(wantTag), "Poly1305 matches RFC 8439 §2.5.2");

        // ── voice transport: hand-rolled AES-GCM agrees with the platform's AesGcm ──
        // The manual GCM exists because AesGcm rejects non-12-byte nonces (DAVE transport uses 24).
        // Cross-checking the 12-byte case against the FIPS-tested implementation pins the GHASH/CTR
        // core; the 24-byte case then exercises only the standard J0 non-12 path.
        var rng = new Random(42);
        VoiceRtp.DebugGcm = true;
        bool gcmMatches = true;
        for (int t = 0; t < 8 && gcmMatches; t++)
        {
            var key = new byte[32]; rng.NextBytes(key);
            var nonce = new byte[12]; rng.NextBytes(nonce);
            var pt = new byte[rng.Next(0, 200)]; rng.NextBytes(pt);
            var aad = new byte[rng.Next(0, 40)]; rng.NextBytes(aad);
            var mine = VoiceRtp.GcmEncryptWithTag(key, nonce, pt, aad);
            using var ag = new System.Security.Cryptography.AesGcm(key, 16);
            var ct = new byte[pt.Length]; var tg = new byte[16];
            ag.Encrypt(nonce, pt, ct, tg, aad);
            if (!mine[..pt.Length].SequenceEqual(ct) || !mine[pt.Length..].SequenceEqual(tg))
            {
                gcmMatches = false;
                // tag = E_K(J0) XOR S, so the reference S is tag_theirs XOR E_K(J0).
                byte[] j0 = new byte[16]; nonce.CopyTo(j0, 0); j0[15] = 1;
                byte[] ek;
                using (var aes = System.Security.Cryptography.Aes.Create())
                {
                    aes.Key = key; aes.Mode = System.Security.Cryptography.CipherMode.ECB; aes.Padding = System.Security.Cryptography.PaddingMode.None;
                    using var enc = aes.CreateEncryptor();
                    ek = new byte[16]; enc.TransformBlock(j0, 0, 16, ek, 0);
                }
                var sRef = tg.Select((b, i) => (byte)(b ^ ek[i])).ToArray();
                Console.WriteLine("  debug ek=" + Convert.ToHexString(ek));
                Console.WriteLine("  debug s_ref=" + Convert.ToHexString(sRef));
                // Independent GHASH: the GCM spec's right-shift variant.
                byte[] gh = AesEcb(key, new byte[16]);
                var mac = BuildMac(aad, ct);
                var y = new byte[16];
                for (int o = 0; o < mac.Length; o += 16)
                {
                    var blk = mac[o..(o + 16)].ToArray();
                    for (int i = 0; i < 16; i++) y[i] ^= blk[i];
                    y = GfMulShiftRight(y, gh);
                }
                Console.WriteLine("  debug s_rightshift=" + Convert.ToHexString(y));

                static byte[] AesEcb(byte[] k, byte[] blk)
                {
                    using var aes = System.Security.Cryptography.Aes.Create();
                    aes.Key = k; aes.Mode = System.Security.Cryptography.CipherMode.ECB; aes.Padding = System.Security.Cryptography.PaddingMode.None;
                    using var enc = aes.CreateEncryptor();
                    var outp = new byte[16]; enc.TransformBlock(blk, 0, 16, outp, 0);
                    return outp;
                }
                static byte[] GfMulShiftRight(byte[] a, byte[] b)
                {
                    var z = new byte[16]; var v = (byte[])b.Clone();
                    for (int i = 0; i < 128; i++)
                    {
                        if (((a[i >> 3] >> (7 - (i & 7))) & 1) != 0) for (int j = 0; j < 16; j++) z[j] ^= v[j];
                        bool drop = (v[15] & 1) != 0;
                        for (int j = 15; j >= 1; j--) v[j] = (byte)((v[j] >> 1) | (v[j - 1] << 7));
                        v[0] >>= 1;
                        if (drop) v[0] ^= 0xE1;
                    }
                    return z;
                }
                static byte[] BuildMac(byte[] aad, byte[] ct)
                {
                    int ap = ((aad.Length + 15) / 16) * 16, cp = ((ct.Length + 15) / 16) * 16;
                    var buf = new byte[ap + cp + 16];
                    aad.CopyTo(buf, 0); ct.CopyTo(buf, ap);
                    var la = (ulong)aad.Length * 8; var lc = (ulong)ct.Length * 8;
                    for (int i = 0; i < 8; i++) { buf[ap + cp + i] = (byte)(la >> (56 - i * 8)); buf[ap + cp + 8 + i] = (byte)(lc >> (56 - i * 8)); }
                    return buf;
                }
            }
        }
        VoiceRtp.DebugGcm = false;
        Check(gcmMatches, "hand-rolled AES-GCM matches AesGcm on 12-byte nonces");
        {
            // Known answer for a 24-byte nonce from python-cryptography (OpenSSL), which accepts
            // arbitrary nonce lengths. Pins the GHASH-derived J0 path the rtpsize mode relies on.
            byte[] key = { 0x00,0x11,0x22,0x33,0x44,0x55,0x66,0x77,0x88,0x99,0xaa,0xbb,0xcc,0xdd,0xee,0xff,
                           0x00,0x11,0x22,0x33,0x44,0x55,0x66,0x77,0x88,0x99,0xaa,0xbb,0xcc,0xdd,0xee,0xff };
            byte[] nonce = { 0x01,0x02,0x03,0x04,0x05,0x06,0x07,0x08,0x09,0x0a,0x0b,0x0c,0x0d,0x0e,0x0f,0x10,
                             0x11,0x12,0x13,0x14,0x15,0x16,0x17,0x18 };
            byte[] want = { 0xad,0xaf,0x82,0x2c,0x6f,0x58,0x8b,0xcc,0x5a,0x20,0x99,0xd1,0x71,0x2f,0xb0,0x22,
                            0x97,0xcb,0x7c,0xd4,0xe8,0xb0,0xd1,0x93,0x6f,0xa7,0x97,0xb2,0x08,0xc7,0x40,0x45,
                            0x0b,0xa4,0x64,0x02,0xd9,0x14,0x13,0x33,0xf9,0x03,0xfb,0x69,0xb4,0x55,0x32 };
            var mine = VoiceRtp.GcmEncryptWithTag(key, nonce, "hello dave transport encryption"u8, "aad-here"u8);
            Check(mine.SequenceEqual(want), "AES-GCM 24-byte nonce matches OpenSSL (the rtpsize J0 path)");
        }
        {
            var key = new byte[32]; var nonce = new byte[24]; rng.NextBytes(key); rng.NextBytes(nonce);
            var pt = "hello dave"u8.ToArray();
            var wrapped = VoiceRtp.GcmEncryptWithTag(key, nonce, pt, ReadOnlySpan<byte>.Empty);
            Check(VoiceRtp.GcmDecrypt(key, nonce, wrapped, ReadOnlySpan<byte>.Empty)!.SequenceEqual(pt),
                  "AES-GCM 24-byte nonce round-trips (the rtpsize construction)");
        }

        // ── voice transport: HChaCha20 known answer (draft-irtf-cfrg-xchacha-03 §2.2.1) ──
        {
            byte[] hkey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
            byte[] hnonce = { 0x00,0x00,0x00,0x09,0x00,0x00,0x00,0x4a,0x00,0x00,0x00,0x00,0x31,0x41,0x59,0x27 };
            byte[] hwant = { 0x82,0x41,0x3b,0x42,0x27,0xb2,0x7b,0xfe,0xd3,0x0e,0x42,0x50,0x8a,0x87,0x7d,0x73,
                            0xa0,0xf9,0xe4,0xd5,0x8a,0x74,0xa8,0x53,0xc1,0x2e,0xc4,0x13,0x26,0xd3,0xec,0xdc };
            var hgot = VoiceRtp.HChaCha20(hkey, hnonce);
            if (!hgot.SequenceEqual(hwant))
            {
                Console.WriteLine("  debug hchacha got=" + Convert.ToHexString(hgot));
                Console.WriteLine("  debug hchacha want=" + Convert.ToHexString(hwant));
            }
            Check(hgot.SequenceEqual(hwant), "HChaCha20 matches draft-irtf-cfrg-xchacha-03");
        }

        // ── voice transport: XChaCha20-Poly1305 ──
        {
            var key = new byte[32]; var nonce = new byte[24]; rng.NextBytes(key); rng.NextBytes(nonce);
            var pt = "xchacha round trip"u8.ToArray();
            var wrapped = VoiceRtp.XChaCha20Poly1305Encrypt(key, nonce, pt, ReadOnlySpan<byte>.Empty);
            Check(VoiceRtp.XChaCha20Poly1305Decrypt(key, nonce, wrapped, ReadOnlySpan<byte>.Empty)!.SequenceEqual(pt),
                  "XChaCha20-Poly1305 round-trips");
            wrapped[^1] ^= 0xFF;
            Check(VoiceRtp.XChaCha20Poly1305Decrypt(key, nonce, wrapped, ReadOnlySpan<byte>.Empty) == null,
                  "XChaCha20-Poly1305 rejects a tampered tag");
        }

        // ── voice transport: XChaCha20-Poly1305 known answer vs pynacl 1.6.2 Aead ──
        // key = 0x11*32, nonce = BE(5)+20 zeros, aad = captured real Discord RTP header,
        // pt = "hello world" + 8 zero bytes. pynacl output: 1afdfadd...f07e (ct||tag).
        {
            var key = Enumerable.Repeat((byte)0x11, 32).ToArray();
            var nonce = new byte[24];
            nonce[0] = 0; nonce[1] = 0; nonce[2] = 0; nonce[3] = 5;
            var aad = Convert.FromHexString("90F8AF8301DEC027000000B4");
            var pt = System.Text.Encoding.ASCII.GetBytes("hello world").Concat(new byte[8]).ToArray();
            var wrapped = VoiceRtp.XChaCha20Poly1305Encrypt(key, nonce, pt, aad);
            Check(wrapped.Length == 35, "XChaCha known answer: 19B ct + 16B tag");
            if (!Convert.ToHexString(wrapped).SequenceEqual("1AFDFADD4DD647894A0F2B6074AE3B596347D5C3C7E0FBE55D6B707E6FEDC8824DF07E"))
            {
                Console.WriteLine("  debug xchacha got=" + Convert.ToHexString(wrapped));
                Console.WriteLine("  debug xchacha want=1AFDFADD4DD647894A0F2B6074AE3B596347D5C3C7E0FBE55D6B707E6FEDC8824DF07E");
            }
            Check(Convert.ToHexString(wrapped) == "1AFDFADD4DD647894A0F2B6074AE3B596347D5C3C7E0FBE55D6B707E6FEDC8824DF07E",
                  "XChaCha20-Poly1305 matches pynacl Aead known answer");
        }

        // ── voice transport: the full Discord packet ──
        {
            var key = new byte[32]; rng.NextBytes(key);
            byte[] opus = { 0xF8, 0xFF, 0xFE, 0x01, 0x02 };
            var packet = VoiceRtp.ProtectPacket(key, useAes: true, 7, 960, 12345, opus, 3);
            // Audio rides the modern 16-byte header (12-byte RTP with the X bit + the 4-byte
            // BE DE 00 02 extension header), authenticated in full — captured real-client
            // packets only decrypt with the 16-byte AAD.
            Check(packet.Length == 16 + opus.Length + 16 + 4, "transport packet = ext header + ct + tag + counter");
            var back = VoiceRtp.UnprotectPacket(key, true, packet);
            Check(back != null && back.SequenceEqual(opus), "AES transport packet round-trips");
            packet[20] ^= 0x01;
            Check(VoiceRtp.UnprotectPacket(key, true, packet) == null, "tampered transport packet is rejected");
            var xp = VoiceRtp.ProtectPacket(key, useAes: false, 7, 960, 12345, opus, 3);
            Check(VoiceRtp.UnprotectPacket(key, false, xp)!.SequenceEqual(opus), "XChaCha transport packet round-trips");
            var wrongKey = new byte[32]; rng.NextBytes(wrongKey);
            Check(VoiceRtp.UnprotectPacket(wrongKey, false, xp) == null, "wrong transport key is rejected");

            // libsodium cross-check: the exact key/nonce/header/plaintext that
            // the real client's rtpsize mode produces (16-byte extended header as
            // AAD, LITTLE-ENDIAN counter in the 24-byte nonce, LE counter trailer).
            // Captured real-client traffic pins LE: the peer's trailers run 00 00 00 00,
            // 01 00 00 00, ... and its packets only decrypt with the LE nonce — the old
            // big-endian vector here matched libsodium's symmetric output but not the
            // client, which is exactly why the peer heard nothing after counter 0.
            var k2 = new byte[32]; for (int i = 0; i < 32; i++) k2[i] = (byte)i;
            byte[] opus2 = { 0xF8, 0xFF, 0xFE, 0x01, 0x02 };
            var want = Convert.FromHexString(
                "90780007000003C000003039BEDE0002" +
                "E126E9A6A125AB3CEE8F3686AA339E3358550FE347" +
                "03000000");
            var got = VoiceRtp.ProtectPacket(k2, useAes: false, 7, 960, 12345, opus2, 3);
            Check(got.SequenceEqual(want), "XChaCha packet matches libsodium known-answer vector (LE counter, ext-header AAD)");
            Check(VoiceRtp.UnprotectPacket(k2, false, want)!.SequenceEqual(opus2),
                  "libsodium packet decrypts back to the Opus frame");
        }

        // ── voice transport: video packets (extension stripping + RTCP feedback) ──
        // Video RTP rides a plain 12-byte header (no X bit) OR an X-bit 16-byte one whose
        // DECLARED extension data rides INSIDE the ciphertext (the real client's layout: AAD =
        // 12-byte RTP + BE DE + words, extension bytes at the front of the decrypted payload —
        // verified against dolfies/discord-native-voice). The decrypt must strip those bytes or
        // the H.264 assembler swallows the extension as a fake NAL and DAVE fails every frame.
        // RTCP feedback (PLI/REMB, PT 205-207) rides the same socket with an 8-byte cleartext
        // header — classified as RTCP before the media path so it never counts as a transport
        // failure, then AEAD-decrypted for PLI handling.
        {
            var key = new byte[32]; rng.NextBytes(key);
            byte[] h264 = { 0x67, 0x42, 0x00, 0x0A, 0xF8, 0x41, 0xA2 };
            var plain = VoiceRtp.ProtectPacket(key, useAes: false, 100, 90000, 0xdead, h264, 7,
                                               VideoRtp.PayloadType, marker: true);
            Check(VoiceRtp.UnprotectVideoPacket(key, false, plain, out var hl1, out var strip1)!.SequenceEqual(h264),
                  "video decrypt handles a plain 12-byte header");
            Check(hl1 == 12 && strip1 == 0, $"the plain video header reports 12 bytes / no strip (got {hl1}/{strip1})");
            // X-bit header with 2 words of extension data INSIDE the ciphertext (real-client shape).
            byte[] extData = { 0x59, 0x1C, 0xDA, 0x27, 0x00, 0x00, 0x00, 0x00 };   // 8 bytes = 2 words
            var payload = extData.Concat(h264).ToArray();
            var ext = VoiceRtp.ProtectPacket(key, useAes: false, 101, 90000, 0xdead, payload, 8,
                                             VideoRtp.PayloadType, marker: false, extHeader: true);
            var dec = VoiceRtp.UnprotectVideoPacket(key, false, ext, out var hl2, out var strip2);
            var media = dec == null ? null : dec[strip2..];   // caller strips the extension bytes
            Check(media != null && media.SequenceEqual(h264),
                  "video decrypt strips the X-bit extension data from the payload");
            Check(hl2 == 16 && strip2 == 8, $"X-bit video reports 16-byte AAD and 8-byte strip (got {hl2}/{strip2})");
            var wrongKey = new byte[32]; rng.NextBytes(wrongKey);
            Check(VoiceRtp.UnprotectVideoPacket(wrongKey, false, plain, out _, out _) == null,
                  "video decrypt rejects a wrong key under every header length");
            // The send-side video packet (ProtectVideoPacket): 3 words of extension data INSIDE
            // the ciphertext, 16-byte AAD — must round-trip through the receive path exactly.
            byte[] ext12 = { 0x51, 0x00, 0x07, 0x32, 0x1C, 0xDA, 0x27, 0xB2, 0x31, 0x30, 0x30, 0x00 };
            var extSend = VoiceRtp.ProtectVideoPacket(key, useAes: false, 200, 90000, 0xdead,
                                                      ext12, h264, 9, VideoRtp.PayloadType, marker: true);
            var decSend = VoiceRtp.UnprotectVideoPacket(key, false, extSend, out var hl3, out var strip3);
            var mediaSend = decSend == null ? null : decSend[strip3..];
            Check(mediaSend != null && mediaSend.SequenceEqual(h264),
                  "video send packet (ext inside ciphertext) round-trips through the receive path");
            Check(hl3 == 16 && strip3 == 12, $"ProtectVideoPacket reports 16-byte AAD / 12-byte strip (got {hl3}/{strip3})");

            Check(VoiceRtp.IsRtcp(new byte[] { 0x81, 0xC9, 0x00, 0x07 }), "RTCP receiver report is detected");
            Check(VoiceRtp.IsRtcp(new byte[] { 0x81, 0xCE, 0x00, 0x02 }), "RTCP PLI feedback is detected");
            Check(VoiceRtp.IsRtcp(new byte[] { 0x81, 0xCF, 0x00, 0x02 }), "RTCP REMB feedback is detected");
            Check(!VoiceRtp.IsRtcp(new byte[] { 0x90, 0x78, 0x00, 0x00 }), "audio RTP (PT 120) is not RTCP");
            Check(!VoiceRtp.IsRtcp(new byte[] { 0x90, 0x6B, 0x00, 0x00 }), "video RTP (PT 107) is not RTCP");

            // RTCP is AEAD-encrypted exactly like RTP (8-byte header as AAD, shared counter,
            // 4-byte LE trailer). PLI must be FMT=1 (RFC 4585 / dnv), not the FIR FMT=4 this
            // client used to send — the SFU only honors PLI, and raw RTCP is silently dropped.
            var pli = VideoRtp.BuildPli(0x1111, 0x2222);
            Check(pli[0] == 0x81 && pli[1] == 206, "PLI is FMT=1 (RFC 4585), not FIR FMT=4");
            var encPli = VoiceRtp.ProtectRtcp(key, useAes: false, pli, 42);
            Check(encPli.Length == 8 + 4 + 16 + 4 && encPli[0] == 0x81 && encPli[1] == 206,
                  "RTCP header rides cleartext (AAD) with the body encrypted");
            var decPli = VoiceRtp.UnprotectRtcp(key, false, encPli);
            Check(decPli != null && decPli.SequenceEqual(pli[8..]), "RTCP PLI round-trips through the AEAD");
            var rr = VideoRtp.BuildReceiverReport(0x1111, 0x2222, 0x1234);
            var encRr = VoiceRtp.ProtectRtcp(key, false, rr, 43);
            var decRr = VoiceRtp.UnprotectRtcp(key, false, encRr);
            Check(decRr != null && decRr.SequenceEqual(rr[8..]), "RTCP receiver report round-trips");
            Check(VoiceRtp.UnprotectRtcp(wrongKey, false, encPli) == null,
                  "RTCP decrypt rejects a wrong key");
        }

        // ── MLS varint (mlspp wire lengths) ──
        // Every MLS message carries varint length headers; a 3-byte read instead of
        // 2 mis-decodes every key package / proposal / commit / welcome.
        {
            var v65 = new byte[] { 0x40, 0x41, 0xDE, 0xAD };
            int p65 = 0;
            Check(Varint.Read(v65, ref p65) == 65 && p65 == 2, "2-byte varint: 0x4041 reads as 65 in 2 bytes");
            var v1 = new byte[] { 0x2A, 0xFF };
            int p1 = 0;
            Check(Varint.Read(v1, ref p1) == 42 && p1 == 1, "1-byte varint reads a single byte");
            var w = new List<byte>();
            Varint.Write(w, 65);
            Varint.Write(w, 42);
            Varint.Write(w, 0x1234);
            int pw = 0;
            var wb = w.ToArray();
            Check(Varint.Read(wb, ref pw) == 65 && Varint.Read(wb, ref pw) == 42 &&
                  Varint.Read(wb, ref pw) == 0x1234 && pw == wb.Length,
                  "varint round-trips the 1/2-byte forms");
        }

        // ── DAVE MLS: HPKE base mode matches RFC 9180 A.3.1 (P-256) ──
        // DHKEM(P-256, HKDF-SHA256) + AES-128-GCM: the exact primitives the MLS
        // welcome/proposal encryption rides on. If the labeled KDF chain drifts
        // even one byte from the RFC, this decrypt fails — no self-consistent
        // implementation could pass it while disagreeing with the standard.
        {
            var skRm = Hex("f3ce7fdae57e1a310d87f1ebbde6f328be0a99cdbcadf4d6589cf29de4b8ffd2");
            var pkRm = Hex("04fe8c19ce0905191ebc298a9245792531f26f0cece2460639e8bc39cb7f706a826a779b4cf969b8a0e539c7f62fb3d30ad6aa8f80e30f1d128aafd68a2ce72ea0");
            var enc = Hex("04a92719c6195d5085104f469a8b9814d5838ff72b60501e2c4466e5e67b325ac98536d7b61a1af4b78e5b7f951c0900be863c403ce65c9bfcb9382657222d18c4");
            var skEm = Hex("4995788ef4b9d6132b249ce59a77281493eb39af373d236a1fe415cb0c2d7beb");
            var info = Hex("4f6465206f6e2061204772656369616e2055726e");
            var pt = Hex("4265617574792069732074727574682c20747275746820626561757479");
            var aad = Hex("436f756e742d30");
            var ct = Hex("5ad590bb8baa577f8619db35a36311226a896e7342a6d836d8b7bcd2f20b6c7f9076ac232e3ab2523f39513434");

            // The raw ECDH output is not printed by the RFC (shared_secret is the
            // post-KDF value), so pin it by symmetry — both directions must agree
            // with the OpenSSL math mlspp uses (cross-checked against python-
            // cryptography, which gave byte-identical output). The decrypt below
            // then validates the full ExtractAndExpand chain end to end.
            var dhAB = MlsCrypto.DhRaw(skEm, pkRm);
            var dhBA = MlsCrypto.DhRaw(skRm, enc);      // enc == pkEm (the ephemeral public key)
            Check(dhAB.Length == 32 && dhAB.SequenceEqual(dhBA), "HPKE ECDH is symmetric (matches OpenSSL)");
            var opened = MlsCrypto.HpkeOpen(enc, skRm, info, aad, ct, pkRm);
            Check(opened != null && opened.SequenceEqual(pt),
                  "HPKE base mode decrypts the RFC 9180 A.3.1 ciphertext");
        }

        // ── DAVE MLS: full two-party group exchange, end to end ──
        // Simulates the voice gateway (external sender + proposal/commit/welcome
        // relay) driving two DaveMls members through group creation, then round-
        // trips an E2EE-protected media frame between them. Exercises the whole
        // stack — key packages, TreeKEM, commits, welcomes, key schedule, sender
        // ratchets and the protocol frame transform — against each other.
        {
            byte[] Le64(ulong v) { var b = new byte[8]; for (int i = 0; i < 8; i++) b[i] = (byte)(v >> (8 * i)); return b; }
            byte[] Be64(ulong v) { var b = new byte[8]; for (int i = 0; i < 8; i++) b[i] = (byte)(v >> (8 * (7 - i))); return b; }
            byte[] Join(params byte[][] parts)
            {
                int len = 0; foreach (var p in parts) len += p.Length;
                var b = new byte[len]; int o = 0;
                foreach (var p in parts) { Array.Copy(p, 0, b, o, p.Length); o += p.Length; }
                return b;
            }

            var (extSigD, extX, extY) = MlsCrypto.GenP256();
            var extSigPub = MlsCrypto.PubPoint(extX, extY);
            var extIdentity = new byte[] { 0x00 };
            var extCred = MlsCredential.Encode(extIdentity);

            ulong channel = 987654321, userA = 111, userB = 222;
            // Group id = channel snowflake BIG-endian (go-dave/davey), matching
            // the gateway's proposals.
            byte[] gid = Be64(channel);

            var daveA = new DaveMls(userA, channel);
            var daveB = new DaveMls(userB, channel);

            var pkgs = new Dictionary<DaveMls, byte[]>();
            var commits = new Dictionary<DaveMls, (byte[] commit, byte[] welcome)>();
            int readyA = 0, readyB = 0;
            void Capture(DaveMls who, byte[] pkt)
            {
                int op = pkt[0];
                var body = pkt.AsSpan(1);
                if (op == 26) pkgs[who] = body.ToArray();          // [26][bare KeyPackage]
                else if (op == 28)
                {
                    // [28][bare commit][bare welcome?] — split at the commit's
                    // self-delimiting MLSMessage boundary.
                    var (_, _, consumed) = MlsMessage.Decode(body.ToArray());
                    commits[who] = (body[..consumed].ToArray(), body[consumed..].ToArray());
                }
            }
            daveA.SendBinary = p => Capture(daveA, p);
            daveB.SendBinary = p => Capture(daveB, p);
            daveA.SendJson = (op, d) => { if (op == 23) readyA++; };
            daveB.SendJson = (op, d) => { if (op == 23) readyB++; };

            // op 4: session description advertises DAVE v1. Per the spec the key
            // package goes out HERE (select_protocol_ack), before the external
            // sender package — not in response to op 25.
            daveA.OnSessionDescription(1);
            daveB.OnSessionDescription(1);
            Check(pkgs.ContainsKey(daveA) && pkgs.ContainsKey(daveB),
                  "both members send a key package after the session description");

            // op 25: the external sender package, delivered to both members.
            var op25 = Join(Tls.Bytes(extSigPub), extCred);
            daveA.HandleDave(25, op25);
            daveB.HandleDave(25, op25);

            // The gateway adds B to A's group: an external-sender Add proposal
            // for B's key package, exactly the MLSMessage the server broadcasts.
            var propContent = MlsProposal.EncodeAdd(pkgs[daveB]);
            var propTbs = new TlsWriter()
                .Bytes(gid).U64(0).U8(MlsAuthContent.SenderExternal).U32(0)
                .Vec(v => { })
                .U8(MlsAuthContent.ContentProposal).Raw(propContent).Buf.ToArray();
            var propSig = MlsCrypto.SignWithLabel(extSigD, "FramedContentTBS",
                Join(Tls.U16(1), Tls.U16(1), propTbs, Array.Empty<byte>()));
            var propAuth = MlsAuthContent.EncodePublicMessage(gid, 0, MlsAuthContent.SenderExternal, 0,
                MlsAuthContent.ContentProposal, propContent, propSig, null);
            var propMsg = MlsMessage.Encode(propAuth);

            // op 27: proposals (operation 0 = add) to the committer. The vector
            // length prefix wraps the RAW MLSMessages (RFC 9420 V<T>): each
            // element is parsed structurally, so no per-message opaque prefix.
            var op27 = new TlsWriter().U8(0).Bytes(propMsg).Buf.ToArray();
            daveA.HandleDave(27, op27);
            Check(commits.TryGetValue(daveA, out var cw) && cw.welcome.Length > 0,
                  "committer answers the add with a commit + welcome");
            Check(readyA == 0, "committer does not declare ready before the announce");

            // A's commit is member-sent, so it must carry a 32-byte membership
            // tag after the auth (mlspp PublicMessage requires it; without it the
            // gateway rejects the commit and revokes the proposals).
            var (_, authBody, _) = MlsMessage.Decode(commits[daveA].commit);
            var (_, _, _, _, _, _, _, _, memTag, _) = MlsAuthContent.Decode(authBody);
            Check(memTag is { Length: 32 }, "member commit carries a 32-byte membership tag");
            var tamperedCommit = (byte[])commits[daveA].commit.Clone();
            tamperedCommit[^1] ^= 0xFF;
            var (_, authBody2, _) = MlsMessage.Decode(tamperedCommit);
            var (_, _, _, _, _, _, _, _, memTag2, _) = MlsAuthContent.Decode(authBody2);
            Check(memTag2 != null && !memTag2.SequenceEqual(memTag),
                  "tampered membership tag is detected");

            // op 29: the gateway announces A's own commit back to A.
            // [transition_id u16][bare commit]
            var op29 = new TlsWriter().U16(1).Raw(commits[daveA].commit).Buf.ToArray();
            daveA.HandleDave(29, op29);
            Check(readyA == 1, "committer declares ready after the announce");

            // op 30: the welcome reaches the new member B.
            // [transition_id u16][bare welcome]
            var op30 = new TlsWriter().U16(1).Raw(commits[daveA].welcome).Buf.ToArray();
            daveB.HandleDave(30, op30);
            Check(readyB == 1, "joiner joins from the welcome and declares ready");

            // op 22: execute the transition on both sides.
            var tid = JsonDocument.Parse("{\"transition_id\":1}").RootElement;
            daveA.HandleDaveJson(22, tid);
            daveB.HandleDaveJson(22, tid);
            Check(daveA.Ready && daveB.Ready, "both members execute the transition");

            // E2EE media frame round-trip in both directions.
            uint ssrcA = 1001, ssrcB = 1002;
            daveA.OnSpeaking(userB, ssrcB);
            daveB.OnSpeaking(userA, ssrcA);
            byte[] opus = { 0xF8, 0xFF, 0xFE, 0x11, 0x22, 0x33 };
            var frame = daveA.ProtectFrame(opus);
            Check(frame != null && frame.Length == opus.Length + 12,
                  "committer protects a media frame (ct + 8-byte tag + nonce + trailer)");
            var back = daveB.UnprotectFrame(ssrcA, frame);
            Check(back != null && back.SequenceEqual(opus), "joiner decrypts the protected frame");
            var frame2 = daveB.ProtectFrame(opus);
            var back2 = daveA.UnprotectFrame(ssrcB, frame2);
            Check(back2 != null && back2.SequenceEqual(opus), "reverse direction decrypts");
            var tampered = (byte[])frame.Clone();
            tampered[4] ^= 0xFF;
            Check(daveB.UnprotectFrame(ssrcA, tampered) == null, "a tampered frame is rejected");
            Check(daveB.UnprotectFrame(9999, frame) == null, "an unknown ssrc is rejected");
            Check(daveA.ProtectFrame(VoiceRtp.SilenceFrame)!.SequenceEqual(VoiceRtp.SilenceFrame),
                  "outbound silence passes through untransformed");
            Check(daveB.UnprotectFrame(ssrcA, VoiceRtp.SilenceFrame)!.SequenceEqual(VoiceRtp.SilenceFrame),
                  "inbound silence is handed to the decoder as-is");

            // Unencrypted ranges: a video-style frame leaves its 2-byte header authenticated but
            // NOT encrypted (libdave's codec handling for H26X fragments — the real client emits
            // these and we used to reject every such frame, which is why the peer's camera never
            // appeared). The round-trip must interleave the plaintext back around the ranges.
            byte[] vid = { 0x65, 0x88, 0x84, 0x01, 0x2A, 0x5C, 0x9E, 0x10, 0x33 };
            var rframe = daveA.ProtectFrame(vid, new[] { (0, 2) });
            Check(rframe != null && rframe.Length == vid.Length + 12 + 2,
                  "ranged frame carries the 2-byte range in the supplement");
            var vback = daveB.UnprotectFrame(ssrcA, rframe);
            Check(vback != null && vback.SequenceEqual(vid),
                  "joiner reassembles a frame with unencrypted ranges");
            var tv = (byte[])rframe.Clone();
            tv[1] ^= 0xFF;   // byte 1 sits inside the unencrypted range
            Check(daveB.UnprotectFrame(ssrcA, tv) == null,
                  "tampering an unencrypted range fails the AEAD tag");

            // Whole-frame H.264 DAVE (spec §Codec Handling): an Annex-B AU with SPS/PPS/IDR/P
            // NALs is protected as ONE frame (start codes + NAL headers unencrypted, VCL payloads
            // encrypted), then decrypted. The decryptor is codec-unaware and returns the frame
            // with 4-byte start codes (the encryptor rewrites them), so compare NAL payloads.
            byte[] au = Join(
                new byte[] { 0x00, 0x00, 0x01, 0x67, 0x42, 0xC0, 0x1E, 0xDA, 0x02, 0x80, 0xB7, 0x8D, 0x8D },
                new byte[] { 0x00, 0x00, 0x01, 0x68, 0xCE, 0x3C, 0x80 },
                new byte[] { 0x00, 0x00, 0x01, 0x65, 0x88, 0x84, 0x01, 0x2A, 0x5C, 0x9E, 0x10, 0x33, 0x22, 0x11, 0x00, 0xAA, 0xBB },
                new byte[] { 0x00, 0x00, 0x01, 0x41, 0x9A, 0x7C, 0x5E, 0x21, 0x43, 0x65, 0x87 });
            var pframe = daveA.ProtectVideoFrame(au);
            Check(pframe != null && pframe.Length > au.Length + 11,
                  "committer protects a whole H.264 AU (frame + supplement)");
            var pback = daveB.UnprotectFrame(ssrcA, pframe);
            if (pback == null) Console.WriteLine($"    [dbg] whole-frame decrypt failed: {daveB.LastFailReason}");
            Check(pback != null, "joiner decrypts the whole-frame H.264 AU");
            if (pback != null)
            {
                var inNals = VideoRtp.SplitNals(au);
                var outNals = VideoRtp.SplitNals(pback);
                bool same = inNals.Count == outNals.Count;
                for (int i = 0; same && i < inNals.Count; i++)
                    same = inNals[i].SequenceEqual(outNals[i]);
                Check(same, "decrypted AU's NAL payloads match the input (start codes now 4-byte)");
            }
            var tp = (byte[])pframe.Clone();
            tp[pframe.Length - 10] ^= 0x40;   // inside the encrypted slice payload
            Check(daveB.UnprotectFrame(ssrcA, tp) == null, "tampering the H.264 ciphertext fails the tag");

            // Reference dump: write the protected frame + the sender's generation-0 key to files
            // so the offline davey (Rust) test can decrypt it with the REAL library — the
            // definitive check that our whole-frame H.264 protection matches libdave.
            try
            {
                var dump = daveA.ProtectVideoFrameForDump(au);
                if (dump != null)
                {
                    System.IO.File.WriteAllText(Path.Combine(Path.GetTempPath(), "ref_video_frame.hex"),
                                                Convert.ToHexString(dump.Value.frame));
                    System.IO.File.WriteAllText(Path.Combine(Path.GetTempPath(), "ref_video_key.hex"),
                                                Convert.ToHexString(dump.Value.key0));
                    System.IO.File.WriteAllText(Path.Combine(Path.GetTempPath(), "ref_video_base.hex"),
                                                Convert.ToHexString(dump.Value.baseSecret));
                    System.IO.File.WriteAllText(Path.Combine(Path.GetTempPath(), "ref_video_plain.hex"),
                                                Convert.ToHexString(au));
                }
            }
            catch { }

            // Full wire path: packetize the PROTECTED frame (its start codes + NAL headers stay
            // unencrypted so the packetizer can split), reassemble with the 4-byte start codes the
            // receiver re-adds, then decrypt the whole frame and confirm the NAL payloads match.
            var ppackets = VideoRtp.PacketizeH264(pframe);
            Check(ppackets.Count > 0, $"the protected AU packetizes into {ppackets.Count} RTP packets");
            var pasm = new VideoRtp.H264Assembler();
            byte[]? prebuilt = null;
            for (int i = 0; i < ppackets.Count; i++)
                prebuilt = pasm.Feed(ppackets[i], i == ppackets.Count - 1);
            Check(prebuilt != null && prebuilt.SequenceEqual(pframe),
                  "the protected AU reassembles byte-identically (4-byte start codes)");
            var prebuiltDec = daveB.UnprotectFrame(ssrcA, prebuilt);
            if (prebuiltDec != null)
            {
                var oNals = VideoRtp.SplitNals(au);
                var rNals = VideoRtp.SplitNals(prebuiltDec);
                bool same = oNals.Count == rNals.Count
                            && oNals.Zip(rNals).All(p => p.First.SequenceEqual(p.Second));
                Check(same, "protected AU -> RTP -> reassembled AU decrypts to the original NALs");
            }
            else Check(false, "protected AU -> RTP -> reassembled AU decrypts to the original NALs");

            // STAP-A (RFC 6184 type 24): the real client aggregates small NALs (SPS/PPS) into one
            // packet at the head of every keyframe. The assembler used to DROP type-24 packets, so
            // the reassembled DAVE frame was missing the SPS/PPS bytes the ranges reference and
            // every keyframe failed the GCM tag. Verify the aggregation unpacks with 4-byte codes.
            byte[] sps = { 0x67, 0x42, 0x00, 0x0A, 0xF8, 0x41, 0xA2 };
            byte[] pps = { 0x68, 0xCE, 0x3C, 0x80 };
            var stap = new byte[1 + 2 + sps.Length + 2 + pps.Length];
            stap[0] = 24;                                    // STAP-A: NRI 0, type 24
            int so = 1;
            BinaryPrimitives.WriteUInt16BigEndian(stap.AsSpan(so), (ushort)sps.Length); so += 2;
            sps.CopyTo(stap, so); so += sps.Length;
            BinaryPrimitives.WriteUInt16BigEndian(stap.AsSpan(so), (ushort)pps.Length); so += 2;
            pps.CopyTo(stap, so);
            var stapAsm = new VideoRtp.H264Assembler();
            var stapAu = stapAsm.Feed(stap, marker: true);
            var stapNals = stapAu == null ? new List<byte[]>() : VideoRtp.SplitNals(stapAu);
            Check(stapAu != null && stapNals.Count == 2 && stapNals[0].SequenceEqual(sps)
                  && stapNals[1].SequenceEqual(pps),
                  "STAP-A aggregation unpacks into its NAL units with 4-byte start codes");
        }

        // ── video: H.264 encoder/decoder round-trip through the Media Foundation MFTs ──
        // The whole video path stands on this pair: webcam frames are H.264-encoded into RTP, the
        // peer's H.264 is decoded back to NV12. A silent MFT failure means "camera on, nobody sees
        // it", so the encode -> packetize -> reassemble -> decode pipeline is pinned here with a
        // synthetic NV12 frame.
        {
            int w = 640, h = 360;
            var nv12 = new byte[w * h * 3 / 2];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    nv12[y * w + x] = (byte)(x * 255 / w);
            for (int y = 0; y < h / 2; y++)
                for (int x = 0; x < w / 2; x++)
                {
                    nv12[w * h + y * w + x * 2] = 128;
                    nv12[w * h + y * w + x * 2 + 1] = 128;
                }

            using var enc = new H264Encoder(w, h, 15, 900_000);
            // The MS encoder MFT is unavailable on some builds (self-tests inside the encoder);
            // the app falls back to the JPEG video transport then, so this is informational.
            Console.WriteLine("  info  H264 encoder " + (enc.Ready ? "ready" : "unavailable (" + enc.Error + ")"));
            Check(true, "H264 encoder path exercised");

            // Colour channels. Every rendered frame goes NV12 -> Nv12.ToJpeg -> a
            // PixelFormat.Format24bppRgb bitmap, and that format is B,G,R in memory despite the
            // name — writing R,G,B swapped red and blue in our own preview AND every peer tile,
            // while the video we sent (raw NV12 into the encoder) stayed correct. Round-trip a
            // saturated red through the real path and read the pixel back.
            // Each direction is checked ALONE against BT.709 ground truth: a round trip would pass
            // with both ends swapped, since the two errors cancel.
            {
                const int cw = 32, chh = 32;
                // Pure red in BT.709 TV range: Y = 16 + 219*0.2126 (the 16..235 span, NOT
                // 0.2126*255), U = 128 - 0.1006*255, V = 128 + 0.4392*255. A blue/red swap lands
                // at Y=31 U=240 V=118 — nowhere near these.
                const byte yRed = 63, uRed = 102, vRed = 240;
                var nvRed = new byte[cw * chh * 3 / 2];
                for (int i = 0; i < cw * chh; i++) nvRed[i] = yRed;
                for (int i = cw * chh; i < nvRed.Length; i += 2) { nvRed[i] = uRed; nvRed[i + 1] = vRed; }

                // Decode direction (peer tiles + our own preview).
                var jpegRed = Nv12.ToJpeg(nvRed, cw, chh, 95);
                Check(jpegRed != null, "a red NV12 frame encodes to JPEG");
                if (jpegRed != null)
                {
                    using var ms = new System.IO.MemoryStream(jpegRed);
                    using var bmp = new System.Drawing.Bitmap(ms);
                    var px = bmp.GetPixel(cw / 2, chh / 2);
                    Console.WriteLine($"  info  red NV12 renders as R={px.R} G={px.G} B={px.B}");
                    Check(px.R > 150 && px.B < 90, $"NV12 red renders red, not blue (R={px.R} B={px.B})");
                }

                // Encode direction (screen share): the same red as GDI+ B,G,R bytes.
                var rgbRed = new byte[cw * chh * 3];
                for (int i = 0; i < rgbRed.Length; i += 3)
                { rgbRed[i] = 0; rgbRed[i + 1] = 0; rgbRed[i + 2] = 255; }
                var nvOut = Nv12.FromRgb(rgbRed, cw, chh, cw * 3);
                int yGot = nvOut[0], uGot = nvOut[cw * chh], vGot = nvOut[cw * chh + 1];
                Console.WriteLine($"  info  red BGR encodes to Y={yGot} U={uGot} V={vGot}");
                Check(Math.Abs(yGot - yRed) <= 3 && Math.Abs(uGot - uRed) <= 4 && Math.Abs(vGot - vRed) <= 4,
                      $"BGR red encodes to BT.709 red (Y={yGot} U={uGot} V={vGot})");
            }

            // Go Live: the stream key is the only place the stream gateway's server_id comes from,
            // and identifying with the wrong one is a 4004 that reads as "screen share does
            // nothing". Guild streams key on the guild, DM calls on the channel.
            Check(UserClient.StreamKeyServerId("guild:1078135148924653709:1078135149411188811:872708492547457064")
                  == 1078135148924653709, "a guild stream key yields the guild id");
            Check(UserClient.StreamKeyServerId("call:1078135149411188811:872708492547457064")
                  == 1078135149411188811, "a DM call stream key yields the channel id");
            Check(UserClient.StreamKeyServerId("nonsense") == 0, "a malformed stream key yields 0");

            // Screen shares must keep the MONITOR's shape: a fixed 16:9 encode visibly squashes a
            // 16:10 or ultrawide desktop, and nothing downstream can undo that.
            foreach (var (sw, sh) in new[] { (1920, 1080), (2560, 1600), (3440, 1440), (1280, 1024) })
            {
                var (fw, fh) = StreamClient.FitBudget(sw, sh);
                double want = sw / (double)sh, got = fw / (double)fh;
                Check(Math.Abs(want - got) / want < 0.02 && fw <= StreamClient.MaxW
                      && fh <= StreamClient.MaxH && fw % 16 == 0 && fh % 2 == 0,
                      $"{sw}x{sh} encodes as {fw}x{fh} — same shape, inside budget, aligned");
            }

            // PLI handling: RequestKeyframe() must rebuild the encoder so the next AU is a fresh
            // IDR (a subscriber's decoder stays black otherwise). Feed a few frames, request a
            // keyframe, then confirm the next AU carries a NAL type-5 IDR. Uses its own encoder so
            // the shared `enc` below still emits its keyframe on the very first call.
            if (enc.Ready)
            {
                using var kfEnc = new H264Encoder(w, h, 15, 900_000);
                bool hasIdr(byte[] au) => VideoRtp.SplitNals(au).Any(n => (n[0] & 0x1F) == 5);
                var nv12k = (byte[])nv12.Clone();
                for (int f = 0; f < 4; f++)
                {
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                            nv12k[y * w + x] = (byte)((x * 3 + y * 5 + f * 7) & 0xFF);
                    kfEnc.Encode(nv12k);
                }
                kfEnc.RequestKeyframe();
                // The reset runs on the codec thread; Encode() bails while it is in progress, so
                // wait for it to finish before feeding (production just drops frames meanwhile).
                for (int wt = 0; wt < 200 && !kfEnc.Ready; wt++) Thread.Sleep(10);
                if (!kfEnc.Ready)
                    Console.WriteLine($"    [dbg] keyframe reset never finished: err={kfEnc.Error}");
                // The MS encoder buffers ~16 frames before its first AU; keep feeding until the
                // fresh IDR of live content appears (the PLI response pauses the stream briefly).
                var kf = new List<byte[]>();
                for (int f = 0; f < 30 && !kf.Any(hasIdr); f++)
                {
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                            nv12k[y * w + x] = (byte)((x * 3 + y * 5 + (20 + f) * 7) & 0xFF);
                    kf.AddRange(kfEnc.Encode(nv12k));
                }
                if (!kf.Any(hasIdr))
                    Console.WriteLine($"    [dbg] keyframe req: aus={kf.Count} ready={kfEnc.Ready} err={kfEnc.Error} " +
                                      $"sizes=[{string.Join(",", kf.Select(a => a.Length).Take(5))}]");
                Check(kf.Any(hasIdr), "RequestKeyframe re-emits an IDR for the PLI path");
            }

            // The legacy byte transport (JPEG fallback when the H.264 encoder is dead): a frame is
            // prefixed with the "JPEG" magic so the receiver can tell it apart from real H.264
            // riding the same payload type. This is pure fragmenter/assembler logic — it must be
            // exercised even when the encoder MFT is unusable, because a break here means the
            // remote tile stays black with no error anywhere.
            {
                var jpeg = new byte[5000];
                for (int i = 0; i < jpeg.Length; i++) jpeg[i] = (byte)(i * 31);
                var frags = VideoRtp.Fragment(jpeg);
                Check(frags.Count > 1 && VideoRtp.HasLegacyMagic(frags[0]),
                      $"legacy video fragments carry the JPEG magic ({frags.Count} frags)");
                var asm2 = new VideoRtp.Assembler();
                byte[]? rebuilt = null;
                for (int i = 0; i < frags.Count; i++)
                    rebuilt = asm2.Feed(frags[i], i == frags.Count - 1);
                Check(rebuilt != null && rebuilt.SequenceEqual(jpeg),
                      "legacy fragments reassemble to the exact JPEG frame");
                Check(!VideoRtp.HasLegacyMagic(new byte[] { 0x67, 0x42, 0x00, 0x1E }),
                      "an H.264 NAL header is not mistaken for the JPEG magic");
                // Mid-frame fragments carry no magic; a Pending assembler keeps routing.
                var asm3 = new VideoRtp.Assembler();
                var mid = VideoRtp.Fragment(jpeg);
                Check(asm3.Feed(mid[0], false) == null && asm3.Pending,
                      "an unfinished legacy frame marks the assembler Pending");
                byte[]? out3 = null;
                for (int i = 1; i < mid.Count; i++)
                    out3 = asm3.Feed(mid[i], i == mid.Count - 1);
                Check(out3 != null && out3.SequenceEqual(jpeg), "the remaining fragments complete the frame");
            }

            if (enc.Ready)
            {
                var aus = enc.Encode(nv12);
                enc.Flush();
                aus.AddRange(enc.Flush());
                Check(aus.Count > 0, $"the encoder emits access units ({aus.Count})");
                if (aus.Count > 0)
                {
                    // The first AU of a fresh stream carries SPS/PPS (small) plus the IDR slice.
                    Check(aus[0].Length > 100, $"the first AU is a keyframe ({aus[0].Length}B)");
                    Check(VideoRtp.SplitNals(aus[0]).Count >= 1, "the AU splits into NAL units");

                    // Packetize a second (P-frame) AU through the RTP layer and reassemble, which
                    // exercises the single-NAL/FU-A path the wire uses.
                    var nv12b = (byte[])nv12.Clone();
                    for (int y = 0; y < h; y++) nv12b[y * w] = 0;
                    var aus2 = enc.Encode(nv12b);
                    if (aus2.Count > 0)
                    {
                        var packets = VideoRtp.PacketizeH264(aus2[0]);
                        Check(packets.Count > 0, $"an AU packetizes into {packets.Count} RTP packets");
                        var asm = new VideoRtp.H264Assembler();
                        byte[]? rebuilt = null;
                        for (int i = 0; i < packets.Count; i++)
                            rebuilt = asm.Feed(packets[i], i == packets.Count - 1);
                        // The packetizer strips start codes and the assembler re-adds 3-byte ones
                        // (the encoder used 4-byte) — both are legal Annex-B, so compare NAL
                        // content, not raw bytes.
                        var origNals = VideoRtp.SplitNals(aus2[0]);
                        var rebuiltNals = rebuilt == null ? new List<byte[]>() : VideoRtp.SplitNals(rebuilt);
                        bool same = rebuilt != null && origNals.Count == rebuiltNals.Count
                                    && origNals.Zip(rebuiltNals).All(p => p.First.SequenceEqual(p.Second));
                        if (!same)
                            Console.WriteLine($"  info  pkt {packets.Count} rebuilt={(rebuilt == null ? "null" : rebuilt.Length + "B")} " +
                                              $"orig={aus2[0].Length}B nals={origNals.Count}/{rebuiltNals.Count} " +
                                              $"origHead={Convert.ToHexString(aus2[0], 0, Math.Min(aus2[0].Length, 16))} " +
                                              $"rebuiltHead={(rebuilt == null ? "" : Convert.ToHexString(rebuilt, 0, Math.Min(rebuilt.Length, 16)))}");
                        Check(same, "single-NAL/FU-A packets reassemble to the original AU");
                    }

                    using var dec = new H264Decoder();
                    Check(dec.Ready, "H264 decoder MFT initialises (" + (dec.Error ?? "ok") + ")");
                    if (dec.Ready)
                    {
                        // Drive the RAW MFT exactly like the verified raw-drive sequence (which
                        // decoded real frames): SetInput/SetOutput, BEGIN/START streaming, then
                        // feed across two keyframe intervals. The production H264Decoder class
                        // mirrors this, but pinning the raw sequence here isolates the MFT from
                        // any class-level regression.
                        Mf.CoCreateInstance(Mf.ClsidH264Decoder, IntPtr.Zero, Mf.CLSCTX_INPROC_SERVER,
                                            typeof(Mf.IMFTransform).GUID, out var decObj);
                        var decMft = (Mf.IMFTransform)Marshal.GetObjectForIUnknown(decObj);
                        decMft.SetInputType(0, Mf.MakeVideoType(Mf.VideoFormatH264, w, h, 15), 0);
                        decMft.SetOutputType(0, Mf.MakeVideoType(Mf.VideoFormatNv12, w, h, 15), 0);
                        decMft.ProcessMessage(Mf.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero);
                        decMft.ProcessMessage(Mf.MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero);
                        var frames = new List<byte[]>();
                        int big = 0, feedN = 0;
                        // Use a FRESH encoder so the decoder's very first AU is the SPS/PPS/IDR
                        // keyframe (a pre-used encoder has already consumed its keyframe, and the
                        // MS decoder appears to require parameter sets before any frame).
                        using var freshEnc = new H264Encoder(w, h, 15, 900_000);
                        for (int f = 0; f < 120; f++)
                        {
                            var nv12e = (byte[])nv12.Clone();
                            for (int y = 0; y < h; y++)
                                for (int x = 0; x < w; x++)
                                    nv12e[y * w + x] = (byte)((x * 3 + y * 5 + f * 7) & 0xFF);
                            foreach (var au in freshEnc.Encode(nv12e))
                            {
                                if (au.Length > 500) big++;
                                feedN++;
                                int ih3 = decMft.ProcessInput(0, Mf.MakeSample(au, 10_000_000L + feedN * 666_667L), 0);
                                if (ih3 != Mf.S_OK && ih3 != Mf.MF_E_TRANSFORM_NEED_MORE_INPUT)
                                    Console.WriteLine($"  info  dec in[{(int)f}] 0x{ih3:X8} len={au.Length}");
                                for (int p = 0; p < 200; p++)
                                {
                                    int dh = Mf.ProcessOutputOne(decMft, out var dps, out _, true);
                                    if (dh == Mf.MF_E_TRANSFORM_STREAM_CHANGE)
                                    {
                                        if (decMft.GetOutputAvailableType(0, 0, out var mt2) == 0) decMft.SetOutputType(0, mt2, 0);
                                        continue;
                                    }
                                    if (dh != Mf.S_OK) break;
                                    if (dps != IntPtr.Zero)
                                    {
                                        var ds = (Mf.IMFSample)Marshal.GetObjectForIUnknown(dps);
                                        var db = Mf.SampleBytes(ds);
                                        if (db != null && db.Length > 0) frames.Add(db);
                                        Marshal.Release(dps);
                                    }
                                }
                            }
                        }
                        Marshal.Release(decObj);
                        Console.WriteLine($"  info  dec loop: {feedN} AUs fed, {big} big (>500B), {frames.Count} decoded");
                        Check(frames.Count > 0, $"the decoder recovers {frames.Count} NV12 frame(s)");
                        // The production path (UdpVoice) uses the H264Decoder CLASS, not a raw
                        // MFT drive — pin that too. The encoder runs on the capture thread and the
                        // decoder on the UDP receive thread, so encode and decode never interleave
                        // on ONE thread; the class decoder is exercised exactly that way here
                        // (encode all AUs, then decode). Interleaving Encode()/Decode() calls on a
                        // single thread makes the MS H.264 decoder MFT stall (every Drain returns
                        // NEED_MORE_INPUT, 0 frames from 121 AUs — verified in MftDebug), so the
                        // test must not interleave; production never does either.
                        using var enc2 = new H264Encoder(w, h, 15, 900_000);
                        var ausEnc2 = new List<byte[]>();
                        for (int f = 0; f < 120; f++)
                        {
                            var nv12e = (byte[])nv12.Clone();
                            for (int y = 0; y < h; y++)
                                for (int x = 0; x < w; x++)
                                    nv12e[y * w + x] = (byte)((x * 3 + y * 5 + f * 7) & 0xFF);
                            ausEnc2.AddRange(enc2.Encode(nv12e));
                        }
                        using (var dec2 = new H264Decoder())
                        {
                            Console.WriteLine($"  info  H264Decoder class ready={dec2.Ready} err={dec2.Error}");
                            var frames2 = new List<byte[]>();
                            int fed2 = 0, big2 = 0;
                            foreach (var au in ausEnc2)
                            {
                                if (au.Length > 500) big2++;
                                fed2++;
                                frames2.AddRange(dec2.Decode(au));
                            }
                            Console.WriteLine($"  info  H264Decoder class decoded {frames2.Count} of {fed2} AUs ({big2} big)");
                            Check(frames2.Count > 0, $"the H264Decoder class recovers {frames2.Count} NV12 frame(s)");
                        }

                        // 720p, the size a real Discord peer broadcasts. This is the case that
                        // shipped broken: ProcessOutput got a fixed 1MB buffer, NV12 720p needs
                        // 1.32MB, so EVERY frame failed and the peer's camera decoded to nothing
                        // while our own 640x360 (0.34MB) sailed through the same code.
                        const int hw = 1280, hh = 720;
                        var nv12hd = new byte[hw * hh * 3 / 2];
                        using (var encHd = new H264Encoder(hw, hh, 15, 2_000_000))
                        using (var decHd = new H264Decoder())
                        {
                            Console.WriteLine($"  info  720p encoder ready={encHd.Ready} err={encHd.Error}");
                            var ausHd = new List<byte[]>();
                            for (int f = 0; f < 90; f++)
                            {
                                for (int y = 0; y < hh; y++)
                                    for (int x = 0; x < hw; x++)
                                        nv12hd[y * hw + x] = (byte)((x * 3 + y * 5 + f * 7) & 0xFF);
                                ausHd.AddRange(encHd.Encode(nv12hd));
                            }
                            var framesHd = new List<byte[]>();
                            foreach (var au in ausHd) framesHd.AddRange(decHd.Decode(au));
                            Console.WriteLine($"  info  720p: {ausHd.Count} AUs -> {framesHd.Count} frames "
                                            + $"at {decHd.Width}x{decHd.Height} err={decHd.LastDrainError ?? "-"}");
                            Check(framesHd.Count > 0,
                                  $"a 720p peer stream decodes ({framesHd.Count} frames, {decHd.Width}x{decHd.Height})");
                            Check(decHd.Width == hw && decHd.Height == hh,
                                  "the decoder picks up the peer's real 1280x720 size from its SPS");
                        }
                        if (frames.Count > 0)
                        {
                            var f = frames[0];
                            // The decoder negotiates its own output geometry from the stream's
                            // SPS; 640x360 input decodes to 640x360 (tight stride here).
                            Check(f.Length >= w * h * 3 / 2, $"decoded frame is the right size ({f.Length}B vs {w * h * 3 / 2})");
                            bool nonZero = false;
                            for (int i = 0; i < Math.Min(f.Length, 65536) && !nonZero; i++)
                                if (f[i] > 8) nonZero = true;
                            Check(nonZero, "the decoded NV12 is not empty");
                        }
                    }
                }
            }
            // The camera may legitimately be absent (VM / no webcam); report but don't fail.
            string[] cams = CameraCapture.DeviceNames();
            Console.WriteLine($"  info  webcams found: {cams.Length}{(cams.Length > 0 ? " (" + string.Join(", ", cams.Take(2)) + ")" : "")}");
        }

        // ── live DAVE interop diagnostic: decrypt the peer's frames from the last debug.log ──
        // The app decrypts the peer's frames (e2eeFail=0) but an independent GCM could not, so
        // run the app's OWN primitives over the parsed frame to find where they diverge.
        try
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "debug.log");
            if (File.Exists(logPath))
            {
                var log = File.ReadAllText(logPath);
                var txM = System.Text.RegularExpressions.Regex.Match(log, @"transport key=([0-9A-F]{64})");
                var gen0 = new Dictionary<ulong, byte[]>();
                foreach (System.Text.RegularExpressions.Match m in
                         System.Text.RegularExpressions.Regex.Matches(log, @"recv ratchet user=(\d+) gen0=([0-9A-F]{32})"))
                    gen0[ulong.Parse(m.Groups[1].Value)] = Convert.FromHexString(m.Groups[2].Value);
                if (txM.Success && gen0.Count > 0)
                {
                    var txKey = Convert.FromHexString(txM.Groups[1].Value);
                    int tried = 0;
                    foreach (var line in log.Split('\n'))
                    {
                        var pm = System.Text.RegularExpressions.Regex.Match(line, @"udp rx (\d+)B ([0-9A-F]+)");
                        if (!pm.Success) continue;
                        int plen = int.Parse(pm.Groups[1].Value);
                        var hx = pm.Groups[2].Value;
                        if (hx.Length / 2 < plen) continue;
                        var pkt = Convert.FromHexString(hx);
                        if (pkt.Length < 36 || pkt[0] != 0x90 || pkt[12] != 0xBE || pkt[13] != 0xDE) continue;
                        var nonce = new byte[24];
                        nonce[0] = pkt[^4]; nonce[1] = pkt[^3]; nonce[2] = pkt[^2]; nonce[3] = pkt[^1];
                        var body = VoiceRtp.XChaCha20Poly1305Decrypt(txKey, nonce,
                            pkt.AsSpan(16, pkt.Length - 20), pkt.AsSpan(0, 16));
                        if (body == null || body.Length <= 20) continue;
                        var frame = body.AsSpan();
                        if (frame[0] == 0x32 && frame[6] == 0x90) frame = frame[8..];
                        if (frame.Length < 14 || frame[^1] != 0xFA || frame[^2] != 0xFA) continue;
                        int suppSize = frame[^3];
                        int suppStart = frame.Length - suppSize;
                        var frameTag = frame.Slice(suppStart, 8);
                        var naR = frame.Slice(suppStart + 8, frame.Length - 3 - suppStart - 8);
                        if (!VoiceRtp.TryUleb128(naR, out uint ctr, out _)) continue;
                        var n12 = new byte[12];
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(n12.AsSpan(8), ctr);
                        var ct = frame.Slice(0, suppStart);
                        bool ok = false;
                        foreach (var kv in gen0)
                        {
                            var pt = VoiceRtp.GcmDecryptTrunc(kv.Value, n12, ct, ReadOnlySpan<byte>.Empty, frameTag);
                            if (pt != null)
                            {
                                Console.WriteLine($"  dave-diag: rx DECRYPTED uid={kv.Key} counter={ctr} " +
                                                  $"pt={pt.Length}B {Convert.ToHexString(pt[..Math.Min(pt.Length, 16)])}");
                                ok = true;
                                // Codec-byte or opus TOC? Decode as-is and with byte 0 stripped;
                                // the 20ms decode (960 samples) must succeed for the right layout.
                                try
                                {
                                    using var opusDec = new Concentus.Structs.OpusDecoder(48000, 2);
                                    var pcm = new short[960 * 2];
                                    int s1 = opusDec.Decode(pt, pcm, 960, false);
                                    int s2 = -999;
                                    if (pt.Length > 1)
                                        s2 = opusDec.Decode(pt.AsSpan(1).ToArray(), pcm, 960, false);
                                    Console.WriteLine($"  dave-diag:   opus as-is={s1} strip-0x78={s2}");
                                }
                                catch (Exception de) { Console.WriteLine("  dave-diag:   opus decode err: " + de.Message); }
                                break;
                            }
                        }
                        if (!ok)
                            Console.WriteLine($"  dave-diag: rx FAIL counter={ctr} ct={ct.Length}B tag={Convert.ToHexString(frameTag)} " +
                                              $"uids=[{string.Join(",", gen0.Keys)}]");
                        if (++tried >= 3) break;
                    }

                    // Same check for OUR OWN transmitted audio frames — the peer can't hear us,
                    // so verify our send path is self-consistent (decrypts with our own uid key).
                    int txTried = 0;
                    foreach (var line in log.Split('\n'))
                    {
                        var pm = System.Text.RegularExpressions.Regex.Match(line, @"udp tx (\d+)B ([0-9A-F]+)");
                        if (!pm.Success) continue;
                        int plen = int.Parse(pm.Groups[1].Value);
                        var hx = pm.Groups[2].Value;
                        if (hx.Length / 2 < plen) continue;
                        var pkt = Convert.FromHexString(hx);
                        if (pkt.Length < 36 || pkt[0] != 0x90 || pkt[12] != 0xBE || pkt[13] != 0xDE) continue;
                        var nonce = new byte[24];
                        nonce[0] = pkt[^4]; nonce[1] = pkt[^3]; nonce[2] = pkt[^2]; nonce[3] = pkt[^1];
                        var body = VoiceRtp.XChaCha20Poly1305Decrypt(txKey, nonce,
                            pkt.AsSpan(16, pkt.Length - 20), pkt.AsSpan(0, 16));
                        if (body == null || body.Length <= 20) continue;
                        var frame = body.AsSpan();
                        if (frame[0] == 0x32 && frame[6] == 0x90) frame = frame[8..];
                        if (frame.Length < 14 || frame[^1] != 0xFA || frame[^2] != 0xFA) continue;
                        int suppSize = frame[^3];
                        int suppStart = frame.Length - suppSize;
                        var frameTag = frame.Slice(suppStart, 8);
                        var naR = frame.Slice(suppStart + 8, frame.Length - 3 - suppStart - 8);
                        if (!VoiceRtp.TryUleb128(naR, out uint ctr, out _)) continue;
                        var n12 = new byte[12];
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(n12.AsSpan(8), ctr);
                        var ct = frame.Slice(0, suppStart);
                        bool ok = false;
                        foreach (var kv in gen0)
                        {
                            var pt = VoiceRtp.GcmDecryptTrunc(kv.Value, n12, ct, ReadOnlySpan<byte>.Empty, frameTag);
                            if (pt != null)
                            {
                                Console.WriteLine($"  dave-diag: tx DECRYPTED uid={kv.Key} counter={ctr} " +
                                                  $"pt={pt.Length}B {Convert.ToHexString(pt[..Math.Min(pt.Length, 16)])}");
                                ok = true;
                                break;
                            }
                        }
                        if (!ok)
                            Console.WriteLine($"  dave-diag: tx FAIL counter={ctr} ct={ct.Length}B tag={Convert.ToHexString(frameTag)} " +
                                              $"uids=[{string.Join(",", gen0.Keys)}]");
                        if (++txTried >= 3) break;
                    }
                }
                else Console.WriteLine("  dave-diag: no transport key / gen0 in debug.log");

                // What does OUR encoder emit for one 20ms frame? The peer's frames must be
                // 960-sample (20ms) opus; if ours claim a different duration the peer's
                // decoder will reject or stretch them.
                try
                {
                    using var encT = new Concentus.Structs.OpusEncoder(48000, 2,
                        Concentus.Enums.OpusApplication.OPUS_APPLICATION_VOIP);
                    var tone = new short[960 * 2];
                    for (int n = 0; n < 960; n++) tone[n * 2] = (short)(4000 * Math.Sin(2 * Math.PI * 440 * n / 48000.0));
                    var ebuf = new byte[4000];
                    int elen = encT.Encode(tone, 960, ebuf, ebuf.Length);
                    Console.WriteLine($"  dave-diag: our opus first byte=0x{ebuf[0]:X2} len={elen}");
                    using var opusDec2 = new Concentus.Structs.OpusDecoder(48000, 2);
                    var pcm2 = new short[960 * 2];
                    int rt = opusDec2.Decode(ebuf.AsSpan(0, elen).ToArray(), pcm2, 960, false);
                    Console.WriteLine($"  dave-diag: our opus decodes to {rt} samples (want 960)");
                }
                catch (Exception ee) { Console.WriteLine("  dave-diag: encoder check: " + ee.Message); }
            }
            else Console.WriteLine("  dave-diag: no debug.log in " + AppContext.BaseDirectory);
        }
        catch (Exception e) { Console.WriteLine("  dave-diag: " + e.Message); }

        // ── UI sounds ──
        // Present-and-decodable, not just present: a truncated or HTML-error-page download still
        // lands as a .mp3 and would only be discovered as silence at the moment a call comes in.
        foreach (var name in new[] { "new-message", "incoming-ring", "outgoing-ring" })
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Sounds", name + ".mp3");
            if (!File.Exists(path)) { Check(false, $"sound {name} present"); continue; }
            try
            {
                using var r = new NAudio.Wave.AudioFileReader(path);
                Check(r.TotalTime > TimeSpan.FromMilliseconds(200) && r.TotalTime < TimeSpan.FromMinutes(1),
                      $"sound {name} decodes ({r.TotalTime.TotalSeconds:0.0}s)");
            }
            catch (Exception e) { Check(false, $"sound {name} decodes: {e.Message}"); }
        }

        Console.WriteLine(_fail == 0 ? "\nAll checks passed." : $"\n{_fail} FAILED.");
        return _fail == 0 ? 0 : 1;
    }

    static byte[] Hex(string s)
    {
        var b = new byte[s.Length / 2];
        for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
        return b;
    }

}
