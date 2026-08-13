using System.Drawing;
using System.Drawing.Drawing2D;

namespace OpenCord;

// One message in the list: the date divider and reply spine above it, the author line, the body,
// attachments, embeds, and the reaction pills — laid out once per width change and painted from the
// cached result.
//
// Every sub-rectangle is stored relative to the row's own top-left, so painting and hit-testing are
// both "offset by the row's y and compare". Re-deriving geometry inside OnPaint is what makes a
// chat view stutter while scrolling; nothing here measures text during a paint.
sealed class MessageRow
{
    public UserMessage Msg = null!;
    public bool GroupStart;                 // starts a new author block (avatar + name shown)
    public bool Mentioned;                  // highlights the row and paints the amber bar
    public string? DateLabel;               // non-null -> draw a date divider above this row
    public bool UnreadStart;                // draw the red "New messages" rule above this row

    public int Y, Height;                   // position within the list's content, set by the list
    public int Width { get; private set; } = -1;

    // ── laid-out parts, all relative to the row's top-left ───────────────────────────────────────
    public List<RichText.Piece> Body = new();
    int _bodyTop, _bodyH;

    /// A body piece's rectangle in row-local coordinates — the same space the list's hit tests use.
    /// Body is laid out relative to the text column, so the offsets have to be reapplied for
    /// anything outside the row to reason about where a word actually sits.
    public Rectangle BodyPieceBox(int i)
    {
        var b = Body[i].Box;
        return new Rectangle(TextLeft + b.X, _bodyTop + b.Y, b.Width, b.Height);
    }
    public List<Shot> Images = new();
    /// Stickers in a format we cannot draw at all: a named card instead of an image.
    public List<(Rectangle Box, string Name)> StickerCards = new();
    /// Lottie stickers, rasterised on demand by [[Lottie]] and played from a frame sheet.
    public List<(Rectangle Box, string Url)> StickerAnims = new();
    public List<Card> Files = new();
    public List<Emb> Embeds = new();
    public List<Pill> Reactions = new();
    public List<Btn> Buttons = new();
    public List<PollAns> PollAnswers = new();
    Rectangle _replyBox, _nameBox, _stampBox, _avatarBox, _editedBox;
    int _tagW;                       // the author's server-tag chiplet, gap included; 0 if none
    /// The gutter glyph a system message gets in place of an avatar.
    Rectangle _sysIcon;
    string _stamp = "", _editedTag = "";
    string? _replyName, _replyText;
    Color _replyColor = Theme.Muted;

    // ── poll / components (laid out with the rest of the row) ────────────────────────────────────
    public Rectangle PollBox;
    List<RichText.Piece> _pollQ = new();
    int _pollQH;
    public int HotButton = -1;      // hovered button index, set by the list on mouse move
    public int HotPoll = -1;        // hovered poll answer index

    public sealed class Btn
    {
        public required UserComponent C;
        public Rectangle Box;
    }

    public sealed class PollAns
    {
        public required UserPollAnswer A;
        public Rectangle Box;
        public int Count;
        public bool Me;
        public int Pct;
    }

    public sealed class Shot
    {
        public required string Url;
        public Rectangle Box;
        public bool Spoiler;
        public string? OpenUrl;
        public bool Play;                   // video / gifv poster gets a play badge
    }

    public sealed class Card
    {
        public required UserAttachment A;
        public Rectangle Box;
        /// The seek strip inside an audio card, in the same row-local space as Box. Empty for a
        /// plain file card.
        public Rectangle Bar;
    }

    public sealed class Pill
    {
        public required UserReaction R;
        public Rectangle Box;
    }

    public sealed class Emb
    {
        public required UserEmbed E;
        public Rectangle Box;
        public Color Bar;
        public string? Author; public Rectangle AuthorBox; public string? AuthorIcon;
        public List<RichText.Piece> Title = new(); public Point TitleAt;
        public List<RichText.Piece> Desc = new(); public Point DescAt;
        public List<(List<RichText.Piece> Name, Point NameAt, List<RichText.Piece> Val, Point ValAt)> Fields = new();
        public string? ImageUrl; public Rectangle ImageBox;
        public string? ThumbUrl; public Rectangle ThumbBox;
        public string? Footer; public Rectangle FooterBox; public string? FooterIcon;
    }

    // ── metrics ─────────────────────────────────────────────────────────────────────────────────
    /// Compact drops the avatar column entirely and folds the timestamp and author into the start
    /// of the message text, so a message is one line instead of a header plus a body — the head
    /// leading only the first message of a group, exactly like the live client.
    static bool Compact => Prefs.Current.CompactMode;

    static int TextLeft => Compact ? Ui.S(M.MessagePadLeft) : Ui.S(M.MessageTextLeft);
    static int PadLeft => Ui.S(M.MessagePadLeft);
    static int NameLine => Ui.S(22);
    static int DateH => Ui.S(40);
    static int ReplyH => Ui.S(24);

    public void Invalidate() => Width = -1;

    public void Layout(int width, MessageRow? prev)
    {
        if (Width == width) return;
        Width = width;
        Body.Clear(); Images.Clear(); StickerCards.Clear(); StickerAnims.Clear();
        Files.Clear(); Embeds.Clear(); Reactions.Clear();
        Buttons.Clear(); PollAnswers.Clear(); _pollQ.Clear();
        PollBox = default;                     // a row updated away from a poll must not keep its card
        HotButton = HotPoll = -1;

        int avail = Math.Max(Ui.S(80), width - TextLeft - Ui.S(M.MessagePadRight));
        int y = 0;

        if (DateLabel != null) y += DateH;
        if (UnreadStart) y += Ui.S(20);
        if (GroupStart && prev != null) y += Ui.S(M.MessageGroupGap);

        if (GroupStart && ReplyTarget() is { } rt)
        {
            _replyName = rt.Name; _replyText = rt.Text; _replyColor = rt.Color;
            _replyBox = new Rectangle(TextLeft, y, avail, ReplyH);
            y += ReplyH;
        }
        else _replyName = null;

        // A system message has no avatar and no name header at all in the live client: it is one
        // sentence in the gutter-icon style, with the author's name inline and the timestamp after
        // it. Drawing it as a normal message is what made "X started a call." look like something
        // somebody typed.
        if (Msg.IsSystem)
        {
            int ic = Ui.S(16);
            _sysIcon = new Rectangle(TextLeft - Ui.S(44), y + Ui.S(3), ic, ic);
            _avatarBox = _nameBox = _stampBox = Rectangle.Empty;
            _stamp = "";
        }
        else if (GroupStart && !Compact)
        {
            int av = Ui.S(M.Avatar);
            _avatarBox = new Rectangle(PadLeft, y + Ui.S(1), av, av);
            var name = DisplayName();
            int nw = Ui.Measure(name, Theme.BodyMedium).Width;
            _nameBox = new Rectangle(TextLeft, y, nw, NameLine);
            // A server tag sits between the name and the hover timestamp, so it has to be measured
            // here — the stamp is placed off this width, not painted over the chiplet.
            _tagW = Msg.Author?.ServerTag is { Tag: { } tg } ? Ui.S(6) + Ui.TagChipWidth(tg) : 0;
            // The live client prints this as " — 8/8/26, 12:13 AM": the em dash is part of the
            // timestamp beside the name, not a separator this layout adds. Stamp() itself stays
            // bare — the sidebar and search use it for last-message previews, which carry no dash.
            _stamp = "— " + Stamp(Msg.Timestamp);
            _stampBox = new Rectangle(TextLeft + nw + _tagW + Ui.S(8), y, Ui.S(240), NameLine);
            y += NameLine;
        }
        else if (Compact) { _avatarBox = _nameBox = _stampBox = Rectangle.Empty; _stamp = ""; }

        _bodyTop = y;
        var text = Msg.IsForward && Msg.Snapshots[0].Message != null ? Msg.Snapshots[0].Message!.Content
                 : Msg.IsSystem ? Msg.SystemText()
                 : Msg.Content;
        if (Msg.IsSystem)
        {
            // Name in the normal username colour, the rest muted, then the timestamp inline after
            // it — measured off the live client (#ffffff / #999aa1 / 12px #9d9ea5).
            var (nm, rest) = Msg.SystemParts();
            var sys = new List<Run>();
            if (nm.Length > 0) sys.Add(new Run { Text = nm, Color = Theme.Strong });
            sys.Add(new Run { Text = rest, Color = Theme.SystemText });
            sys.Add(new Run { Text = "  —  " + Stamp(Msg.Timestamp), Style = Style.Subtext, Color = Theme.Faint });
            Body = RichText.Layout(sys, avail, out _bodyH);
            y += _bodyH;
        }
        else if (text.Length > 0 || Compact)
        {
            var runs = Markdown.Parse(text);
            // Prepended as real runs rather than drawn separately, so the body wraps around them
            // the way the live client's compact mode does instead of overlapping. GroupStart gates
            // it: Discord leads the FIRST message of a group with the timestamp + sender and lets
            // the messages that follow show only their content. Prepping every row repeated the
            // name on each line, which is what made compact read as cozy-without-avatars.
            if (Compact && !Msg.IsSystem && GroupStart)
            {
                var head = new List<Run>
                {
                    new() { Text = Msg.Timestamp.ToLocalTime().ToString("h:mm tt") + " ",
                            Color = Theme.Faint },
                    new() { Text = DisplayName(), Style = Style.Bold, Color = NameColor() },
                    new() { Text = "  " },
                };
                head.AddRange(runs);
                runs = head;
            }
            Body = RichText.Layout(runs, avail, out _bodyH);
            // Discord turns the text of a message that failed to send red, not just the notice
            // under it. The row is re-laid on the transition, so this runs when the state flips.
            if (Msg.IsFailed) foreach (var p in Body) p.Color = Theme.Danger;
            y += _bodyH;
        }
        else _bodyH = 0;

        _editedTag = Msg.EditedTimestamp.HasValue ? "  (edited)" : "";
        if (_editedTag.Length > 0 && Body.Count > 0)
        {
            var last = Body[^1];
            _editedBox = new Rectangle(TextLeft + last.Box.Right, _bodyTop + last.Box.Y,
                                       Ui.Measure(_editedTag, Theme.Small).Width, last.Box.Height);
        }

        y = LayoutStickers(y, avail);
        y = LayoutAttachments(y, avail);
        y = LayoutEmbeds(y, avail);
        y = LayoutButtons(y, avail);
        y = LayoutPoll(y, avail);
        y = LayoutReactions(y, avail);
        y = LayoutFailure(y);

        Height = y + Ui.S(4);
    }

    // ── failed send ─────────────────────────────────────────────────────────────────────────────
    /// Why the post was rejected, verbatim from Discord where it said anything useful.
    public string? FailReason;
    public Rectangle RetryBox, DeleteBox;

    // Discord's failed row: the message stays put in red, with a line underneath reading
    // "Message failed to send. Retry - Delete" — both words being links.
    int LayoutFailure(int y)
    {
        RetryBox = DeleteBox = Rectangle.Empty;
        if (!Msg.IsFailed) return y;

        y += Ui.S(4);
        var lead = FailReason is { Length: > 0 } r ? r + "  " : "Message failed to send.  ";
        int x = TextLeft + Ui.Measure(lead, Theme.Small).Width;
        RetryBox = new Rectangle(x, y, Ui.Measure("Retry", Theme.SmallMedium).Width, Ui.S(18));
        x = RetryBox.Right + Ui.Measure("  -  ", Theme.Small).Width;
        DeleteBox = new Rectangle(x, y, Ui.Measure("Delete", Theme.SmallMedium).Width, Ui.S(18));
        _failLead = lead;
        return y + Ui.S(18);
    }

    string _failLead = "";

    void PaintFailure(Graphics g, int top)
    {
        if (!Msg.IsFailed || RetryBox.IsEmpty) return;
        Ui.Text(g, _failLead, Theme.Small, new Point(TextLeft, top + RetryBox.Y + Ui.S(2)),
                Theme.Danger, TextFormatFlags.NoPadding);
        Ui.Text(g, "Retry", Theme.SmallMedium, new Point(RetryBox.X, top + RetryBox.Y + Ui.S(2)),
                Theme.Link, TextFormatFlags.NoPadding);
        Ui.Text(g, "  -  ", Theme.Small, new Point(RetryBox.Right, top + RetryBox.Y + Ui.S(2)),
                Theme.Danger, TextFormatFlags.NoPadding);
        Ui.Text(g, "Delete", Theme.SmallMedium, new Point(DeleteBox.X, top + DeleteBox.Y + Ui.S(2)),
                Theme.Link, TextFormatFlags.NoPadding);
    }

    // ── author / reply helpers ──────────────────────────────────────────────────────────────────
    public string DisplayName() => Msg.Member?.Nick ?? Msg.Author?.DisplayName ?? "Unknown";
    public Color NameColor() => App.Guild?.NameColor(Msg.Author?.Id ?? 0) ?? Theme.Strong;
    public string? AvatarUrl() => Msg.Member is { } gm && App.Guild is { } g
        ? gm.AvatarUrl(g.Id, 80) : Msg.Author?.GetAvatarUrl(80);

    (string Name, string Text, Color Color)? ReplyTarget()
    {
        if (Msg.Interaction is { } it && it.User != null)
            return (it.User.DisplayName, "used /" + (it.Name ?? "command"), Theme.Muted);
        if (Msg.ReferencedMessage is { } r)
        {
            var body = Markdown.Flatten(r.Content);
            if (body.Length == 0 && r.Attachments.Count > 0) body = "Click to see attachment";
            else if (body.Length == 0 && r.Embeds.Count > 0) body = "Click to see embed";
            return (r.Member?.Nick ?? r.Author?.DisplayName ?? "Unknown", body,
                    App.Guild?.NameColor(r.Author?.Id ?? 0) ?? Theme.Strong);
        }
        if (Msg.MessageReference != null && Msg.Type == 19)
            return ("Original message", "was deleted", Theme.Muted);
        return null;
    }

    // ── layout passes ───────────────────────────────────────────────────────────────────────────
    int LayoutStickers(int y, int avail)
    {
        foreach (var s in Msg.Stickers)
        {
            int side = Math.Min(Ui.S(160), avail);
            var box = new Rectangle(TextLeft, y + Ui.S(4), side, side);
            // Lottie goes through the Skottie rasteriser rather than the image cache; anything else
            // is an ordinary picture. A format we cannot draw at all falls back to a named card so
            // the message does not read as an empty row with just a username.
            if (s.IsLottie) StickerAnims.Add((box, s.Url));
            else if (s.Renderable) Images.Add(new Shot { Url = s.Url, Box = box });
            else StickerCards.Add((box, s.Name));
            y += side + Ui.S(8);
        }
        return y;
    }

    int LayoutAttachments(int y, int avail)
    {
        var atts = Msg.IsForward && Msg.Snapshots[0].Message != null ? Msg.Snapshots[0].Message!.Attachments : Msg.Attachments;
        var pics = atts.Where(a => a.IsImage || a.IsVideo).ToList();
        var files = atts.Where(a => !a.IsImage && !a.IsVideo).ToList();

        if (pics.Count > 0) y += Ui.S(4);
        if (pics.Count == 1)
        {
            var a = pics[0];
            var box = FitBox(a.Width ?? 400, a.Height ?? 300, Math.Min(avail, Ui.S(550)), Ui.S(350),
                             a.IsVideo ? Math.Min(avail, Ui.S(400)) : 0);
            Images.Add(new Shot
            {
                Url = a.ProxyUrl ?? a.Url, OpenUrl = a.Url, Spoiler = a.IsSpoiler, Play = a.IsVideo,
                Box = new Rectangle(TextLeft, y, box.Width, box.Height),
            });
            y += box.Height + Ui.S(4);
        }
        else if (pics.Count > 1)
        {
            // Discord grids multiples; two columns covers 2-4, which is the overwhelming majority.
            int cols = pics.Count == 2 ? 2 : 2;
            int gap = Ui.S(4);
            int cell = Math.Min((Math.Min(avail, Ui.S(550)) - gap * (cols - 1)) / cols, Ui.S(270));
            for (int i = 0; i < pics.Count; i++)
            {
                int cx = TextLeft + (i % cols) * (cell + gap);
                int cy = y + (i / cols) * (cell + gap);
                Images.Add(new Shot
                {
                    Url = pics[i].ProxyUrl ?? pics[i].Url, OpenUrl = pics[i].Url,
                    Spoiler = pics[i].IsSpoiler, Play = pics[i].IsVideo,
                    Box = new Rectangle(cx, cy, cell, cell),
                });
            }
            y += ((pics.Count + cols - 1) / cols) * (cell + gap);
        }

        foreach (var f in files)
        {
            // Audio gets a player rather than a download card. A voice message is narrower than a
            // music file's card because it carries no filename — just the waveform and a clock.
            bool voice = Msg.IsVoiceMessage;
            int w = f.IsAudio ? Math.Min(avail, Ui.S(voice ? 340 : 432)) : Math.Min(avail, Ui.S(432));
            // A download card is 73 tall in the live client — 16px of padding around a 40px icon.
            // The audio player keeps its own height.
            int h = Ui.S(f.IsAudio ? 56 : 73);
            Files.Add(new Card { A = f, Box = new Rectangle(TextLeft, y + Ui.S(4), w, h) });
            y += h + Ui.S(8);
        }
        return y;
    }

    /// Fit `w`x`h` inside `maxW`x`maxH`. A picture is never scaled up — a 40px emote blown up to
    /// 550 is a mess. `minW` is the exception, and only videos ask for it: the player paints a
    /// fixed 34px transport strip over the bottom of whatever box it gets, so a low-resolution
    /// clip at its natural size ends up mostly transport, with a seek bar too short to grab.
    /// Growing it is still aspect-correct and still inside the same envelope.
    internal static Size FitBox(int w, int h, int maxW, int maxH, int minW = 0)
    {
        if (w <= 0 || h <= 0) return new Size(maxW, maxH);
        float s = Math.Min(maxW / (float)w, maxH / (float)h);
        s = s <= 1f ? s : Math.Max(1f, Math.Min(s, minW / (float)w));
        return new Size(Math.Max(1, (int)(w * s)), Math.Max(1, (int)(h * s)));
    }

    int LayoutEmbeds(int y, int avail)
    {
        foreach (var e in Msg.Embeds)
        {
            // A bare image / gifv embed is just a picture, not a card — Discord draws no chrome.
            if (e.IsBareImage || (e.Type == "gifv" && e.Title == null) ||
                (e.Type == "image" && e.Title == null))
            {
                var url = e.AnimatedGifUrl ?? e.PosterUrl ?? e.Image?.Best;
                if (url == null) continue;
                var box = FitBox(e.Image?.Width ?? e.Thumbnail?.Width ?? 400,
                                 e.Image?.Height ?? e.Thumbnail?.Height ?? 300,
                                 Math.Min(avail, Ui.S(400)), Ui.S(300),
                                 e.IsPlayable && e.AnimatedGifUrl == null ? Math.Min(avail, Ui.S(400)) : 0);
                Images.Add(new Shot
                {
                    Url = url, OpenUrl = e.Url ?? url,
                    Play = e.IsPlayable && e.AnimatedGifUrl == null,
                    Box = new Rectangle(TextLeft, y + Ui.S(4), box.Width, box.Height),
                });
                y += box.Height + Ui.S(8);
                continue;
            }

            var card = LayoutEmbed(e, Math.Min(avail, Ui.S(432)), TextLeft, y + Ui.S(4));
            Embeds.Add(card);
            y += card.Box.Height + Ui.S(8);
        }
        return y;
    }

    // Styling has to be applied to the runs *before* layout: RichText picks the font from the run's
    // Style, so restyling the pieces afterwards paints one font at another's measurements.
    static List<RichText.Piece> Lay(string text, int w, Style extra, Color? col, out int h)
    {
        var runs = Markdown.Parse(text);
        foreach (var r in runs)
        {
            r.Style |= extra;
            if (col != null && r.Url == null && r.Color == null) r.Color = col;
        }
        return RichText.Layout(runs, w, out h);
    }

    static Emb LayoutEmbed(UserEmbed e, int maxW, int x0, int y0)
    {
        var c = new Emb { E = e, Bar = e.Color is { } rgb && rgb != 0 ? Color.FromArgb(rgb | unchecked((int)0xFF000000)) : Theme.Border };
        int pad = Ui.S(12), bar = Ui.S(4);
        bool thumb = e.Thumbnail?.Best != null && !e.IsBareImage;
        int inner = maxW - bar - pad * 2 - (thumb ? Ui.S(88) : 0);
        int y = pad;

        if (e.Author is { Name.Length: > 0 })
        {
            c.Author = e.Author.Name;
            c.AuthorIcon = e.Author.Icon;
            c.AuthorBox = new Rectangle(bar + pad, y, inner, Ui.S(22));
            y += Ui.S(24);
        }
        if (!string.IsNullOrEmpty(e.Title))
        {
            c.Title = Lay(e.Title, inner, Style.H3, e.Url != null ? Theme.Link : Theme.Strong, out int th);
            c.TitleAt = new Point(bar + pad, y);
            y += th + Ui.S(4);
        }
        if (!string.IsNullOrEmpty(e.Description))
        {
            c.Desc = Lay(e.Description, inner, Style.None, null, out int dh);
            c.DescAt = new Point(bar + pad, y);
            y += dh + Ui.S(6);
        }
        foreach (var f in e.Fields.Take(12))
        {
            var nm = Lay(f.Name, inner, Style.Bold, Theme.Strong, out int nh);
            var vl = Lay(f.Value, inner, Style.None, null, out int vh);
            c.Fields.Add((nm, new Point(bar + pad, y), vl, new Point(bar + pad, y + nh)));
            y += nh + vh + Ui.S(8);
        }
        if (e.Image?.Best is { } img)
        {
            var box = FitBox(e.Image.Width ?? 400, e.Image.Height ?? 225, maxW - bar - pad * 2, Ui.S(300));
            c.ImageUrl = img;
            c.ImageBox = new Rectangle(bar + pad, y, box.Width, box.Height);
            y += box.Height + Ui.S(8);
        }
        if (e.Footer is { Text.Length: > 0 })
        {
            c.Footer = e.Footer.Text + (e.Timestamp.HasValue ? "  •  " + e.Timestamp.Value.ToLocalTime().ToString("g") : "");
            c.FooterIcon = e.Footer.Icon;
            c.FooterBox = new Rectangle(bar + pad, y, inner, Ui.S(18));
            y += Ui.S(20);
        }
        if (thumb)
        {
            c.ThumbUrl = e.Thumbnail!.Best;
            c.ThumbBox = new Rectangle(maxW - pad - Ui.S(80), pad, Ui.S(80), Ui.S(80));
            y = Math.Max(y, pad + Ui.S(80));
        }

        c.Box = new Rectangle(x0, y0, maxW, y + pad);
        return c;
    }

    // ── message components (buttons) ──
    // One row of 32px buttons per action row, left-aligned at the text column, wrapping when the
    // labels outgrow the available width — the same shape as Discord's.
    int LayoutButtons(int y, int avail)
    {
        if (Msg.Components.Count == 0) return y;
        int h = Ui.S(32), gap = Ui.S(8);
        foreach (var row in Msg.Components)
        {
            if (row.Type != UserComponent.Row) continue;
            int x = TextLeft;
            foreach (var c in row.Components)
            {
                if (c.Type != UserComponent.Button) continue;
                int w = ButtonWidth(c);
                if (x + w > TextLeft + avail && x > TextLeft) { x = TextLeft; y += h + gap; }
                Buttons.Add(new Btn { C = c, Box = new Rectangle(x, y, w, h) });
                x += w + gap;
            }
            y += h + Ui.S(8);
        }
        return y;
    }

    static int ButtonWidth(UserComponent c)
    {
        if (string.IsNullOrEmpty(c.Label)) return Ui.S(32);       // emoji-only square
        int w = Ui.Measure(c.Label, Theme.BodyMedium).Width + Ui.S(24)
              + (c.Emoji != null ? Ui.S(24) : 0);
        return Math.Max(Ui.S(60), w);
    }

    // ── polls ──
    // A card like Discord's: question, then either clickable answer pills (nobody has voted yet) or
    // progress-bar results. "x left" lives in the footer and re-lays on the list's poll tick.
    int LayoutPoll(int y, int avail)
    {
        var poll = Msg.Poll;
        if (poll == null || poll.Question == null) return y;

        int cardW = Math.Min(avail, Ui.S(432));
        int pad = Ui.S(14), inner = cardW - pad * 2;

        var qtext = poll.Question.Text ?? "";
        if (poll.Question.Emoji is { } qe)
            qtext = (qe.Id is { } qid ? $"<{(qe.Animated ? "a" : "")}:{qe.Name}:{qid}>" : qe.Name ?? "") + " " + qtext;
        var qruns = Markdown.Parse(qtext.Trim());
        foreach (var r in qruns) { r.Style |= Style.Bold; if (r.Color == null && r.Url == null) r.Color = Theme.Text; }
        _pollQ = RichText.Layout(qruns, inner, out _pollQH);

        bool results = poll.IVoted || poll.Closed;
        int yy = pad + _pollQH + Ui.S(8);
        foreach (var a in poll.Answers)
        {
            var (cnt, me) = poll.CountFor(a.AnswerId);
            int h = results ? Ui.S(32) : Ui.S(36);
            int pct = results && poll.TotalVotes > 0 ? cnt * 100 / poll.TotalVotes : 0;
            PollAnswers.Add(new PollAns
            {
                A = a,
                Box = new Rectangle(TextLeft + pad, yy, inner, h),
                Count = cnt, Me = me, Pct = pct,
            });
            yy += h + Ui.S(8);
        }

        int footer = Ui.S(20);
        PollBox = new Rectangle(TextLeft, y, cardW, yy + Ui.S(4) + footer + pad);
        return y + PollBox.Height + Ui.S(8);
    }

    /// The "add reaction" pill that trails the row. Empty when the message has no reactions —
    /// Discord only shows it once a row exists to append to.
    public Rectangle AddReactionBox;

    int LayoutReactions(int y, int avail)
    {
        AddReactionBox = Rectangle.Empty;
        if (Msg.Reactions.Count == 0) return y;
        // Measured: 28 tall, 6px of padding either side, a 20px emoji, a 6px gap, then the count.
        int x = TextLeft, h = Ui.S(28), gap = Ui.S(4);
        y += Ui.S(4);
        foreach (var r in Msg.Reactions)
        {
            int w = Ui.S(32) + Ui.Measure(r.Count.ToString(), Theme.BodySemibold).Width + Ui.S(7);
            if (x + w > TextLeft + avail && x > TextLeft) { x = TextLeft; y += h + gap; }
            Reactions.Add(new Pill { R = r, Box = new Rectangle(x, y, w, h) });
            x += w + gap;
        }
        int aw = Ui.S(34);
        if (x + aw > TextLeft + avail && x > TextLeft) { x = TextLeft; y += h + gap; }
        AddReactionBox = new Rectangle(x, y, aw, h);
        return y + h + Ui.S(4);
    }

    // ── paint ───────────────────────────────────────────────────────────────────────────────────
    public void Paint(Graphics g, int top, int width, Control host, HashSet<int> revealed,
                      HashSet<ulong> shownSpoilers, bool hovered)
    {
        _host = host;
        _rowHovered = hovered;
        int y = top;

        if (DateLabel != null)
        {
            int my = y + DateH / 2;
            var sz = Ui.Measure(DateLabel, Theme.SmallSemibold);
            Ui.Fill(g, new Rectangle(PadLeft, my, width - PadLeft * 2, 1), Theme.Border);
            int bx = (width - sz.Width) / 2;
            Ui.Fill(g, new Rectangle(bx - Ui.S(6), y + Ui.S(8), sz.Width + Ui.S(12), DateH - Ui.S(16)), Theme.Chat);
            // Measured 12px/600 in --text-muted, not the fainter timestamp grey.
            Ui.Text(g, DateLabel, Theme.SmallSemibold, new Rectangle(bx, y, sz.Width, DateH), Theme.Muted,
                    TextFormatFlags.VerticalCenter);
            y += DateH;
        }

        if (UnreadStart)
        {
            // Discord's unread divider: a red rule across the chat with a centred "NEW MESSAGES"
            // pill reading the exact label the web client uses.
            int uy = y + Ui.S(10);
            Ui.Fill(g, new Rectangle(0, uy, width, 1), Theme.Danger);
            const string lbl = "NEW MESSAGES";
            var sz = Ui.Measure(lbl, Theme.SmallMedium);
            var pill = new Rectangle((width - sz.Width - Ui.S(14)) / 2, uy - Ui.S(7), sz.Width + Ui.S(14), Ui.S(15));
            Ui.FillRound(g, pill, Ui.S(4), Theme.Danger);
            Ui.Text(g, lbl, Theme.SmallMedium,
                    new Rectangle(pill.X + Ui.S(7), uy - Ui.S(7), sz.Width, Ui.S(15)), Color.White);
        }

        // Row highlight. A mention paints amber with a bar; plain hover is a flat grey.
        int hlTop = top + (DateLabel != null ? DateH : 0) + (UnreadStart ? Ui.S(20) : 0);
        int hlH = Height - (hlTop - top);
        if (Mentioned)
        {
            Ui.Fill(g, new Rectangle(0, hlTop, width, hlH), Theme.MentionRow);
            Ui.Fill(g, new Rectangle(0, hlTop, Ui.S(2), hlH), Theme.MentionRowBar);
        }
        else if (hovered) Ui.Fill(g, new Rectangle(0, hlTop, width, hlH), Theme.RowHover);

        if (_replyName != null) PaintReply(g, top, width);

        // A system message: just the gutter glyph. The sentence itself is in Body.
        if (Msg.IsSystem)
        {
            if (!_sysIcon.IsEmpty)
            {
                var ib = Offset(_sysIcon, top);
                // The call glyph is the live client's green handset; everything else is neutral.
                bool call = Msg.Type == 3;
                Svg.SvgFill(g, call ? Icons.PhoneCall : Icons.Hash,
                            new RectangleF(ib.X, ib.Y, ib.Width, ib.Height),
                            call ? Theme.CallGreen : Theme.SystemText, call ? 18f : 24f);
            }
        }
        // Compact folds the author into the body runs, so there is no header block to paint.
        else if (GroupStart && !Compact)
        {
            var ab = Offset(_avatarBox, top);
            Ui.Avatar(g, Media.Get(AvatarUrl(), host), ab, Theme.Surface, host);

            Ui.Text(g, DisplayName(), Theme.BodyMedium, Offset(_nameBox, top), NameColor(),
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            if (_tagW > 0 && Msg.Author?.ServerTag is { Tag: { } stag } pg)
                Ui.TagChip(g, _nameBox.Right + Ui.S(6), top + _nameBox.Y + _nameBox.Height / 2,
                           stag, Media.Get(pg.BadgeUrl, host),
                           Mentioned ? Theme.MentionRow : hovered ? Theme.RowHover : Theme.Chat);
            if (Msg.Author?.Bot == true)
            {
                // A *verified* app carries a checkmark inside the tag, ahead of the word. The plain
                // blurple "APP" is what an unverified one gets.
                bool check = Msg.Author.VerifiedBot;
                var tag = new Rectangle(_nameBox.Right + Ui.S(6), top + _nameBox.Y + Ui.S(4),
                                        Ui.S(check ? 41 : 30), Ui.S(15));
                Ui.FillRound(g, tag, Ui.S(3), Theme.Blurple);
                if (check)
                    Svg.SvgFill(g, Icons.CheckLine,
                                  new RectangleF(tag.X + Ui.S(3), tag.Y + Ui.S(4), Ui.S(8), Ui.S(8)),
                                  Color.White);
                Ui.Text(g, "APP", Theme.IconSmall,
                        new Rectangle(tag.X + (check ? Ui.S(11) : 0), tag.Y, tag.Width - (check ? Ui.S(11) : 0), tag.Height),
                        Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            // The timestamp next to the author name appears on hover, exactly like the web client;
            // it stays hidden while the row is idle so the message reads as one clean line.
            if (hovered)
                Ui.Text(g, _stamp, Theme.Small,
                        Offset(_stampBox, top).WithX(_stampBox.X + (Msg.Author?.Bot == true
                            ? Ui.S(Msg.Author.VerifiedBot ? 49 : 38) : 0)),
                        Theme.Faint, TextFormatFlags.VerticalCenter);
        }

        if (Body.Count > 0)
        {
            var origin = new Point(TextLeft, top + _bodyTop);
            RichText.Paint(g, Body, origin, host, revealed);
            if (_editedTag.Length > 0)
                Ui.Text(g, _editedTag, Theme.Small, new Point(_editedBox.X, top + _editedBox.Y + Ui.S(4)),
                        Theme.Faint, TextFormatFlags.NoPadding);
        }

        foreach (var (box, url) in StickerAnims)
        {
            var r = new Rectangle(box.X, top + box.Y, box.Width, box.Height);
            if (!Lottie.Draw(g, url, r, host)) Ui.FillRound(g, r, Ui.S(8), Theme.EmbedBg);   // loading
        }
        foreach (var (box, name) in StickerCards)
        {
            var r = new Rectangle(box.X, top + box.Y, box.Width, box.Height);
            Ui.FillRound(g, r, Ui.S(8), Theme.EmbedBg);
            Svg.SvgFill(g, Icons.StickerLine,
                        new RectangleF(r.X + r.Width / 2f - Ui.S(20), r.Y + r.Height / 2f - Ui.S(28),
                                       Ui.S(40), Ui.S(40)), Theme.Faint);
            Ui.Text(g, name, Theme.Small,
                    new Rectangle(r.X + Ui.S(8), r.Y + r.Height / 2 + Ui.S(16), r.Width - Ui.S(16), Ui.S(20)),
                    Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
        }
        foreach (var s in Images) PaintShot(g, s, top, host, shownSpoilers);
        foreach (var f in Files) PaintFile(g, f, top);
        foreach (var e in Embeds) PaintEmbed(g, e, top, host, revealed);
        for (int i = 0; i < Buttons.Count; i++) PaintButton(g, Buttons[i], i, top);
        if (Msg.Poll != null && PollBox.Width > 0) PaintPoll(g, top, host);
        for (int i = 0; i < Reactions.Count; i++) PaintPill(g, Reactions[i], top, host, HotPill == i);
        PaintAddReaction(g, top);

        // A message that is still in flight is drawn at half strength, the way Discord fades one
        // until the server confirms it. Blending the whole row toward the background covers the
        // attachments and the avatar too, which recolouring the text alone would miss.
        if (Msg.IsPending)
            Ui.Fill(g, new Rectangle(0, hlTop, width, hlH), Color.FromArgb(128, Theme.Chat));

        // Drawn after the fade: a failed row is fully opaque, and red.
        PaintFailure(g, top);
    }

    // ── component buttons ──
    void PaintButton(Graphics g, Btn b, int idx, int top)
    {
        var c = b.C;
        var box = Offset(b.Box, top);
        bool hot = _rowHovered && HotButton == idx;
        Color fill = c.Style switch
        {
            1 => Theme.Blurple,
            3 => Theme.Positive,
            4 => Theme.Danger,
            _ => Theme.Surface,
        };
        if (c.Disabled) fill = Color.FromArgb(96, fill);
        else if (hot && c.Clickable) fill = ControlPaint.Light(fill, 0.12f);

        Ui.FillRound(g, box, Ui.S(8), fill);
        if (!c.Disabled && c.Style is not (1 or 3 or 4))
        {
            using var pen = new Pen(Theme.Border);
            using var path = Ui.RoundRect(new Rectangle(box.X, box.Y, box.Width - 1, box.Height - 1), Ui.S(8));
            g.DrawPath(pen, path);
        }

        var fg = c.Disabled ? Color.FromArgb(110, Theme.Text)
                : c.Style == 5 ? Theme.Link
                : c.Style is 1 or 3 or 4 ? Color.White
                : hot ? Color.White : Theme.Text;

        int x = box.X + Ui.S(10);
        if (c.Emoji != null)
        {
            int e = Ui.S(16);
            var eb = new Rectangle(x, box.Y + (box.Height - e) / 2, e, e);
            var url = c.Emoji.ImageUrl ?? (c.Emoji.Name is { } n ? Twemoji.Url(n) : null);
            var img = Media.Get(url, _host);
            if (img != null) { if (Media.IsAnimated(img)) Media.Animate(img, _host); g.DrawImage(img, eb); }
            else Ui.Text(g, c.Emoji.Glyph, Theme.Emoji, eb, fg, TextFormatFlags.HorizontalCenter);
            x += e + Ui.S(6);
        }

        if (!string.IsNullOrEmpty(c.Label))
        {
            int lw = Ui.Measure(c.Label, Theme.BodyMedium).Width;
            Ui.Text(g, c.Label, Theme.BodyMedium,
                    new Rectangle(x, box.Y, Math.Max(1, box.Right - x - Ui.S(8)), box.Height),
                    fg, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    bool _rowHovered;

    // ── polls ──
    void PaintPoll(Graphics g, int top, Control host)
    {
        var poll = Msg.Poll!;
        var box = Offset(PollBox, top);
        Ui.FillRound(g, box, Ui.S(8), Theme.EmbedBg);
        using (var pen = new Pen(Theme.Border))
        using (var path = Ui.RoundRect(new Rectangle(box.X, box.Y, box.Width - 1, box.Height - 1), Ui.S(8)))
            g.DrawPath(pen, path);

        int pad = Ui.S(14);
        RichText.Paint(g, _pollQ, new Point(box.X + pad, box.Y + pad), host, null);

        bool results = poll.IVoted || poll.Closed;
        for (int i = 0; i < PollAnswers.Count; i++)
        {
            var a = PollAnswers[i];
            var ab = Offset(a.Box, top);
            bool hot = _rowHovered && HotPoll == i;

            if (results)
            {
                // Track with a progress fill; my vote gets the saturated bar and a check.
                Ui.FillRound(g, ab, Ui.S(8), Theme.Field);
                if (a.Pct > 0)
                {
                    int fw = Math.Max(Ui.S(6), ab.Width * a.Pct / 100);
                    var fill = a.Me ? Color.FromArgb(82, 97, 178) : Theme.MentionBg;
                    var st = g.Save();
                    using (var clip = Ui.RoundRect(ab, Ui.S(8)))
                    {
                        g.SetClip(clip, CombineMode.Replace);
                        Ui.Fill(g, new Rectangle(ab.X, ab.Y, fw, ab.Height), fill);
                    }
                    g.Restore(st);
                }

                int em = Ui.S(16);
                int tx = ab.X + Ui.S(10);
                if (AnswerEmoji(a.A, out var eurl, out var eglyph))
                {
                    var eb = new Rectangle(tx, ab.Y + (ab.Height - em) / 2, em, em);
                    var img = Media.Get(eurl, host);
                    if (img != null) g.DrawImage(img, eb);
                    else Ui.Text(g, eglyph, Theme.Emoji, eb, Theme.Text, TextFormatFlags.HorizontalCenter);
                    tx += em + Ui.S(6);
                }
                var label = AnswerText(a.A);
                Ui.Text(g, label, Theme.Body,
                        new Rectangle(tx, ab.Y, Math.Max(1, ab.Width - tx + ab.X - Ui.S(96)), ab.Height),
                        a.Me ? Theme.Strong : Theme.Text,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

                string right = (a.Me ? "✓  " : "") + a.Count + (a.Pct > 0 ? $" ({a.Pct}%)" : "");
                Ui.Text(g, right, Theme.SmallMedium,
                        new Rectangle(ab.Right - Ui.S(84), ab.Y, Ui.S(78), ab.Height),
                        a.Me ? Theme.BrandText : Theme.Faint,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
            }
            else
            {
                // Clickable answer pill: surface + border, blurple when hovered.
                var fill = hot ? Theme.Blurple : Theme.Surface;
                Ui.FillRound(g, ab, Ui.S(8), fill);
                if (!hot)
                {
                    using var pen = new Pen(Theme.Border);
                    using var path = Ui.RoundRect(new Rectangle(ab.X, ab.Y, ab.Width - 1, ab.Height - 1), Ui.S(8));
                    g.DrawPath(pen, path);
                }
                int em = Ui.S(18);
                int tx = ab.X + Ui.S(10);
                if (AnswerEmoji(a.A, out var eurl2, out var eglyph2))
                {
                    var eb = new Rectangle(tx, ab.Y + (ab.Height - em) / 2, em, em);
                    var img = Media.Get(eurl2, host);
                    if (img != null) g.DrawImage(img, eb);
                    else Ui.Text(g, eglyph2, Theme.Emoji, eb, hot ? Color.White : Theme.Text,
                                 TextFormatFlags.HorizontalCenter);
                    tx += em + Ui.S(6);
                }
                Ui.Text(g, AnswerText(a.A), Theme.Body,
                        new Rectangle(tx, ab.Y, Math.Max(1, ab.Right - tx - Ui.S(8)), ab.Height),
                        hot ? Color.White : Theme.Text,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }

        // Footer: total votes left, time left right — the same split as the web client.
        var fr = new Rectangle(box.X + pad, box.Bottom - Ui.S(26), box.Width - pad * 2, Ui.S(18));
        Ui.Text(g, "Total votes: " + poll.TotalVotes, Theme.Small, fr, Theme.Faint,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        var tl = poll.TimeLeft();
        if (tl.Length > 0)
            Ui.Text(g, tl, Theme.Small, fr, Theme.Faint,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
    }

    static string AnswerText(UserPollAnswer a) => a.Media?.Text ?? "";

    static bool AnswerEmoji(UserPollAnswer a, out string? url, out string glyph)
    {
        url = null; glyph = "";
        var em = a.Media?.Emoji;
        if (em == null) return false;
        if (em.Id is { } id) { url = $"https://cdn.discordapp.com/emojis/{id}.{(em.Animated ? "gif" : "png")}?size=32"; return true; }
        glyph = em.Name ?? "";
        return glyph.Length > 0;
    }

    void PaintReply(Graphics g, int top, int width)
    {
        var box = Offset(_replyBox, top);
        // The spine: up from the avatar, then a rounded elbow into the preview line.
        using (var pen = new Pen(Theme.Border, Math.Max(1, Ui.S(2))))
        {
            int sx = PadLeft + Ui.S(M.Avatar) / 2, sy = box.Bottom + Ui.S(6);
            int ex = TextLeft - Ui.S(6), ey = box.Y + box.Height / 2;
            using var path = new GraphicsPath();
            path.AddArc(sx, ey, Ui.S(12), Ui.S(12), 180, 90);
            path.AddLine(sx + Ui.S(6), ey, ex, ey);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawLine(pen, sx, sy, sx, ey + Ui.S(6));
            g.DrawPath(pen, path);
        }

        int x = box.X;
        int av = Ui.S(16);
        var rm = Msg.ReferencedMessage;
        if (rm?.Author != null)
        {
            Ui.Avatar(g, Media.Get(rm.Author.GetAvatarUrl(32), _host!), new Rectangle(x, box.Y + Ui.S(4), av, av), Theme.Surface, _host);
            x += av + Ui.S(6);
        }
        int nw = Ui.Measure(_replyName, Theme.SmallMedium).Width;
        Ui.Text(g, _replyName, Theme.SmallMedium, new Rectangle(x, box.Y, nw, box.Height), _replyColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        x += nw + Ui.S(6);
        Ui.Text(g, _replyText, Theme.Small, new Rectangle(x, box.Y, box.Right - x, box.Height), Theme.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    Control? _host;

    void PaintShot(Graphics g, Shot s, int top, Control host, HashSet<ulong> shown)
    {
        _host = host;
        var box = Offset(s.Box, top);
        bool hide = s.Spoiler && !shown.Contains(Msg.Id);

        // A video that is playing paints its live frame here instead of the poster, with the
        // transport along the bottom — the same surface, so the layout never moves.
        if (s.Play && !hide && Video.Current == (s.OpenUrl ?? s.Url))
        {
            PaintVideo(g, s, box, host);
            return;
        }
        var img = hide ? null : Media.Get(s.Url, host);

        using (var path = Ui.RoundRect(box, Ui.S(8)))
        {
            var st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.SetClip(path, CombineMode.Replace);
            if (img != null)
            {
                if (Media.IsAnimated(img)) Media.Animate(img, host);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(img, box);
            }
            // A hidden spoiler is a LIGHT grey scrim in the live client, not a dark one — measured
            // #7d7e87. Filling with the dark embed colour made it read as a failed image load.
            else if (hide) Ui.Fill(g, box, Theme.SpoilerScrim);
            else Ui.Fill(g, box, Theme.EmbedBg);
            g.Restore(st);
        }

        if (hide)
        {
            // 15px semibold, uppercase, 0.5px tracking, in a fully-round 8x12 pill.
            var lbl = "SPOILER";
            var sz = Ui.Measure(lbl, Theme.SpoilerPill);
            int pw = sz.Width + Ui.S(24), ph = Ui.S(31);
            var pillR = new Rectangle(box.X + (box.Width - pw) / 2, box.Y + (box.Height - ph) / 2, pw, ph);
            Ui.FillRound(g, pillR, ph / 2, Theme.SpoilerPillBg);
            Ui.Text(g, lbl, Theme.SpoilerPill, pillR, Theme.SpoilerPillText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        else if (s.Play)
        {
            int d = Ui.S(48);
            var pb = new Rectangle(box.X + (box.Width - d) / 2, box.Y + (box.Height - d) / 2, d, d);
            using (var b = new SolidBrush(Color.FromArgb(180, 0, 0, 0))) g.FillEllipse(b, pb);
            Svg.SvgFill(g, Icons.PlayTriangle, new RectangleF(pb.X + d * 0.28f, pb.Y + d * 0.25f, d * 0.5f, d * 0.5f), Color.White);
        }
    }

    /// The playing surface: frame, then Discord's transport row — play/pause, elapsed/total, and a
    /// seek bar. `VideoBar` is the same rect the list hit-tests for scrubbing.
    void PaintVideo(Graphics g, Shot s, Rectangle box, Control host)
    {
        using (var path = Ui.RoundRect(box, Ui.S(8)))
        {
            var st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.SetClip(path, CombineMode.Replace);
            Ui.Fill(g, box, Color.Black);
            if (!Video.DrawFrame(g, box))
                Ui.Text(g, Video.IsLoading(s.OpenUrl ?? s.Url) ? "Loading…" : "", Theme.Body, box,
                        Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            g.Restore(st);
        }

        int barH = Ui.S(34);
        var strip = new Rectangle(box.X, box.Bottom - barH, box.Width, barH);
        using (var b = new SolidBrush(Color.FromArgb(170, 0, 0, 0))) g.FillRectangle(b, strip);

        int d = Ui.S(18);
        var play = new Rectangle(strip.X + Ui.S(10), strip.Y + (barH - d) / 2, d, d);
        if (Video.IsPlaying)
        {
            // Two bars: there is no pause glyph in the extracted icon set and this is unambiguous.
            using var w = new SolidBrush(Color.White);
            g.FillRectangle(w, play.X + Ui.S(2), play.Y, Ui.S(5), d);
            g.FillRectangle(w, play.X + Ui.S(11), play.Y, Ui.S(5), d);
        }
        else Svg.SvgFill(g, Icons.PlayTriangle, new RectangleF(play.X + Ui.S(3), play.Y, d, d), Color.White);

        string time = $"{Fmt(Video.Position)} / {Fmt(Video.Duration)}";
        var tw = Ui.Measure(time, Theme.Small).Width;
        Ui.Text(g, time, Theme.Small,
                new Rectangle(strip.Right - tw - Ui.S(10), strip.Y, tw, barH), Color.White,
                TextFormatFlags.VerticalCenter);

        VideoBar = new Rectangle(play.Right + Ui.S(10) - box.X + s.Box.X,
                                 strip.Y + (barH - Ui.S(4)) / 2 - box.Y + s.Box.Y,
                                 strip.Right - play.Right - tw - Ui.S(30), Ui.S(4));
        var barOnScreen = new Rectangle(box.X + VideoBar.X - s.Box.X, box.Y + VideoBar.Y - s.Box.Y,
                                        VideoBar.Width, VideoBar.Height);
        Ui.FillRound(g, barOnScreen, Ui.S(2), Color.FromArgb(90, 255, 255, 255));
        Ui.FillRound(g, new Rectangle(barOnScreen.X, barOnScreen.Y,
                                      (int)(barOnScreen.Width * Video.Progress), barOnScreen.Height),
                     Ui.S(2), Color.White);
        VideoPlayBox = new Rectangle(play.X - box.X + s.Box.X, play.Y - box.Y + s.Box.Y, d, d);
    }

    /// Row-local rects for the playing clip's transport, so the list can hit-test them.
    public Rectangle VideoBar, VideoPlayBox;

    static string Fmt(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    void PaintFile(Graphics g, Card c, int top)
    {
        if (c.A.IsAudio) { PaintAudio(g, c, top); return; }

        // Measured off a non-previewable attachment: --background-surface-high at radius 8 with a
        // 1px #97979f-at-12% rule, 16px of padding, a 30x40 document glyph, then a 16px/400 link
        // and a 12px/400 --text-muted size line. It used to sit on the darker embed colour with a
        // 500-weight name, which read as a different component entirely.
        var box = Offset(c.Box, top);
        Ui.FillRound(g, box, Ui.S(8), Theme.Field);
        using (var pen = new Pen(Theme.Tint(Theme.Field, Color.FromArgb(151, 151, 159), 0.1216f)))
        using (var path = Ui.RoundRect(new Rectangle(box.X, box.Y, box.Width - 1, box.Height - 1), Ui.S(8)))
            g.DrawPath(pen, path);

        int iw = Ui.S(30), ih = Ui.S(40);
        Svg.SvgStroke(g, Icons.FileLine, new RectangleF(box.X + Ui.S(16), box.Y + (box.Height - ih) / 2f, iw, ih),
                      Theme.Muted, 1.6f);
        int tx = box.X + Ui.S(55);
        int tw = box.Width - Ui.S(55) - Ui.S(40);
        Ui.Text(g, c.A.Filename, Theme.Body, new Rectangle(tx, box.Y + Ui.S(17), tw, Ui.S(21)),
                Theme.Link, TextFormatFlags.EndEllipsis);
        Ui.Text(g, c.A.SubLine, Theme.Small, new Rectangle(tx, box.Y + Ui.S(39), tw, Ui.S(16)),
                Theme.Muted);
        Svg.SvgFill(g, Icons.DownloadLine,
                      new RectangleF(box.Right - Ui.S(34), box.Y + (box.Height - Ui.S(20)) / 2f, Ui.S(20), Ui.S(20)),
                      Theme.Muted);
    }

    // An audio attachment plays inline instead of downloading. Two shapes share this card: a voice
    // message, which Discord draws as a waveform, and an uploaded track, which gets a plain seek bar
    // with its filename above it.
    void PaintAudio(Graphics g, Card c, int top)
    {
        var box = Offset(c.Box, top);
        bool voice = Msg.IsVoiceMessage;
        Ui.FillRound(g, box, Ui.S(8), Theme.EmbedBg);
        using (var pen = new Pen(Theme.Border))
        using (var path = Ui.RoundRect(new Rectangle(box.X, box.Y, box.Width - 1, box.Height - 1), Ui.S(8)))
            g.DrawPath(pen, path);

        string url = c.A.ProxyUrl ?? c.A.Url;
        bool mine = Audio.Current == url;
        bool playing = mine && Audio.IsPlaying;
        float progress = mine ? Audio.Progress : 0f;

        // Play/pause disc.
        int d = Ui.S(32);
        var disc = new Rectangle(box.X + Ui.S(12), box.Y + (box.Height - d) / 2, d, d);
        Ui.FillRound(g, disc, d / 2, Theme.Blurple);
        if (Audio.IsLoading(url))
            Ui.Text(g, "…", Theme.BodyMedium, disc, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        else if (playing)
        {
            int bw = Ui.S(4), bh = Ui.S(12);
            Ui.Fill(g, new Rectangle(disc.X + d / 2 - bw - Ui.S(2), disc.Y + (d - bh) / 2, bw, bh), Color.White);
            Ui.Fill(g, new Rectangle(disc.X + d / 2 + Ui.S(2), disc.Y + (d - bh) / 2, bw, bh), Color.White);
        }
        else
            Svg.SvgFill(g, Icons.PlayTriangle,
                        new RectangleF(disc.X + Ui.S(9), disc.Y + Ui.S(8), Ui.S(16), Ui.S(16)), Color.White);

        int tx = disc.Right + Ui.S(10);
        int right = box.Right - Ui.S(12);
        var total = c.A.DurationSecs is { } secs && secs > 0 ? TimeSpan.FromSeconds(secs)
                  : mine ? Audio.Duration : TimeSpan.Zero;
        var at = mine ? Audio.Position : TimeSpan.Zero;
        string clock = total > TimeSpan.Zero ? $"{Fmt(at)} / {Fmt(total)}" : Fmt(at);
        var cw = Ui.Measure(clock, Theme.Small);
        int barRight = right - cw.Width - Ui.S(8);

        if (!voice)
        {
            Ui.Text(g, c.A.Filename, Theme.SmallMedium,
                    new Rectangle(tx, box.Y + Ui.S(8), barRight - tx, Ui.S(18)),
                    Theme.Link, TextFormatFlags.EndEllipsis);
        }

        var bar = new Rectangle(tx, voice ? box.Y + (box.Height - Ui.S(24)) / 2 : box.Y + Ui.S(30),
                                Math.Max(Ui.S(20), barRight - tx), Ui.S(voice ? 24 : 6));
        c.Bar = new Rectangle(bar.X, bar.Y - box.Y + c.Box.Y, bar.Width, bar.Height);

        if (voice) PaintWaveform(g, bar, c.A.Waveform, progress);
        else
        {
            Ui.FillRound(g, bar, bar.Height / 2, Theme.Surface);
            if (progress > 0)
                Ui.FillRound(g, new Rectangle(bar.X, bar.Y, Math.Max(Ui.S(4), (int)(bar.Width * progress)), bar.Height),
                             bar.Height / 2, Theme.Blurple);
        }

        Ui.Text(g, clock, Theme.Small, new Rectangle(barRight + Ui.S(8), box.Y, cw.Width + Ui.S(4), box.Height),
                Theme.Faint, TextFormatFlags.VerticalCenter);

        static string Fmt(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    // Discord's waveform is one 0-255 amplitude per bucket. Bars already played take the brand
    // colour; the rest stay muted, which is the whole read-at-a-glance of a voice message.
    void PaintWaveform(Graphics g, Rectangle bar, string? waveform, float progress)
    {
        var w = Audio.DecodeWaveform(waveform);
        int bw = Ui.S(2), gap = Ui.S(2);
        int bars = Math.Max(1, bar.Width / (bw + gap));
        int played = (int)(bars * progress);

        for (int i = 0; i < bars; i++)
        {
            // Resample whatever bucket count Discord sent onto the bars that fit this width.
            byte amp = w.Length == 0 ? (byte)90 : w[Math.Min(w.Length - 1, i * w.Length / bars)];
            int h = Math.Max(Ui.S(2), (int)(bar.Height * (amp / 255f)));
            var r = new Rectangle(bar.X + i * (bw + gap), bar.Y + (bar.Height - h) / 2, bw, h);
            Ui.FillRound(g, r, bw / 2, i < played ? Theme.BrandText : Theme.Muted);
        }
    }

    void PaintEmbed(Graphics g, Emb c, int top, Control host, HashSet<int> revealed)
    {
        var box = Offset(c.Box, top);
        // --background-surface-high, same as a file card — not the darker embed colour this used.
        // Measured off a live embed: #393a41 at radius 4 with a 4px accent bar down the left.
        Ui.FillRound(g, box, Ui.S(4), Theme.Field);
        // The colour bar is the card's left edge, so it has to keep the card's own corner radius.
        var st = g.Save();
        using (var clip = Ui.RoundRect(box, Ui.S(4)))
        {
            g.SetClip(clip, CombineMode.Replace);
            Ui.Fill(g, new Rectangle(box.X, box.Y, Ui.S(4), box.Height), c.Bar);
        }
        g.Restore(st);

        if (c.Author != null)
        {
            var ab = Offset(c.AuthorBox, top).WithX(c.AuthorBox.X + box.X);
            int x = ab.X;
            if (c.AuthorIcon != null)
            {
                Ui.Avatar(g, Media.Get(c.AuthorIcon, host), new Rectangle(x, ab.Y + Ui.S(2), Ui.S(20), Ui.S(20)), Theme.Surface);
                x += Ui.S(26);
            }
            Ui.Text(g, c.Author, Theme.SmallMedium, new Rectangle(x, ab.Y, ab.Right - x, ab.Height), Theme.Strong,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        if (c.Title.Count > 0) RichText.Paint(g, c.Title, new Point(box.X + c.TitleAt.X, box.Y + c.TitleAt.Y), host, revealed);
        if (c.Desc.Count > 0) RichText.Paint(g, c.Desc, new Point(box.X + c.DescAt.X, box.Y + c.DescAt.Y), host, revealed);
        foreach (var (nm, na, vl, va) in c.Fields)
        {
            RichText.Paint(g, nm, new Point(box.X + na.X, box.Y + na.Y), host, revealed);
            RichText.Paint(g, vl, new Point(box.X + va.X, box.Y + va.Y), host, revealed);
        }
        if (c.ImageUrl != null)
        {
            var ib = new Rectangle(box.X + c.ImageBox.X, box.Y + c.ImageBox.Y, c.ImageBox.Width, c.ImageBox.Height);
            var img = Media.Get(c.ImageUrl, host);
            using var path = Ui.RoundRect(ib, Ui.S(4));
            var s2 = g.Save();
            g.SetClip(path, CombineMode.Replace);
            if (img != null) { if (Media.IsAnimated(img)) Media.Animate(img, host); g.DrawImage(img, ib); }
            else Ui.Fill(g, ib, Theme.Surface);
            g.Restore(s2);
        }
        if (c.ThumbUrl != null)
        {
            var tb = new Rectangle(box.X + c.ThumbBox.X, box.Y + c.ThumbBox.Y, c.ThumbBox.Width, c.ThumbBox.Height);
            var img = Media.Get(c.ThumbUrl, host);
            using var path = Ui.RoundRect(tb, Ui.S(4));
            var s2 = g.Save();
            g.SetClip(path, CombineMode.Replace);
            if (img != null) g.DrawImage(img, tb);
            else Ui.Fill(g, tb, Theme.Surface);
            g.Restore(s2);
        }
        if (c.Footer != null)
        {
            var fb = new Rectangle(box.X + c.FooterBox.X, box.Y + c.FooterBox.Y, c.FooterBox.Width, c.FooterBox.Height);
            int x = fb.X;
            if (c.FooterIcon != null)
            {
                Ui.Avatar(g, Media.Get(c.FooterIcon, host), new Rectangle(x, fb.Y, Ui.S(18), Ui.S(18)), Theme.Surface);
                x += Ui.S(24);
            }
            Ui.Text(g, c.Footer, Theme.Small, new Rectangle(x, fb.Y, fb.Right - x, fb.Height), Theme.Faint,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    /// Index of the reaction pill under the pointer, set by the list so the pill can light up.
    public int HotPill = -1;
    public bool HotAddReaction;

    void PaintAddReaction(Graphics g, int top)
    {
        if (AddReactionBox.IsEmpty) return;
        var box = Offset(AddReactionBox, top);
        Ui.FillRound(g, box, Ui.S(8), HotAddReaction ? Theme.SurfaceHigh : Theme.Field);
        Icons.Draw(g, Icons.SmileyLine, Rectangle.Inflate(box, -Ui.S(9), -Ui.S(9)),
                   HotAddReaction ? Theme.Text : Theme.Muted, 1.9f);
    }

    void PaintPill(Graphics g, Pill p, int top, Control host, bool hot)
    {
        var box = Offset(p.Box, top);
        Ui.FillRound(g, box, Ui.S(8),
                     p.R.Me ? (hot ? Theme.MentionBgHover : Theme.MentionBg)
                            : (hot ? Theme.SurfaceHigh : Theme.Field));
        if (p.R.Me)
        {
            using var pen = new Pen(Theme.Blurple);
            using var path = Ui.RoundRect(new Rectangle(box.X, box.Y, box.Width - 1, box.Height - 1), Ui.S(8));
            g.DrawPath(pen, path);
        }

        int e = Ui.S(20);
        var eb = new Rectangle(box.X + Ui.S(6), box.Y + (box.Height - e) / 2, e, e);
        var url = p.R.Emoji.ImageUrl ?? (p.R.Emoji.Name is { } n ? Twemoji.Url(n) : null);
        var img = Media.Get(url, host);
        if (img != null) { if (Media.IsAnimated(img)) Media.Animate(img, host); g.DrawImage(img, eb); }
        else Ui.Text(g, p.R.Emoji.Glyph, Theme.Emoji, eb, Theme.Text, TextFormatFlags.HorizontalCenter);

        Ui.Text(g, p.R.Count.ToString(), Theme.BodySemibold,
                new Rectangle(eb.Right + Ui.S(6), box.Y, box.Right - eb.Right - Ui.S(6), box.Height),
                p.R.Me ? Theme.ReactionMeText : Theme.Muted, TextFormatFlags.VerticalCenter);
    }

    static Rectangle Offset(Rectangle r, int top) => new(r.X, r.Y + top, r.Width, r.Height);

    // ── hit testing (all take a point in list coordinates with the row's top already subtracted) ──
    public bool OverAvatar(Point p) => GroupStart && _avatarBox.Contains(p);
    public bool OverName(Point p) => GroupStart && _nameBox.Contains(p);
    public bool OverReply(Point p) => _replyName != null && _replyBox.Contains(p);
    public Shot? ShotAt(Point p) => Images.FirstOrDefault(s => s.Box.Contains(p));
    public Card? FileAt(Point p) => Files.FirstOrDefault(f => f.Box.Contains(p));
    public Pill? PillAt(Point p) => Reactions.FirstOrDefault(r => r.Box.Contains(p));
    public int PillIndexAt(Point p) => Reactions.FindIndex(r => r.Box.Contains(p));
    public bool OverAddReaction(Point p) => !AddReactionBox.IsEmpty && AddReactionBox.Contains(p);
    public string? LinkAt(Point p) => RichText.LinkAt(Body, new Point(TextLeft, _bodyTop), p)
                                   ?? Embeds.Select(e => EmbedLink(e, p)).FirstOrDefault(u => u != null);
    public int SpoilerAt(Point p) => RichText.SpoilerAt(Body, new Point(TextLeft, _bodyTop), p);

    /// The index of the button under a point, or -1.
    public int ButtonAt(Point p) => Buttons.FindIndex(b => b.Box.Contains(p));

    /// Whether anything under a point is interactive (button, live poll answer) — the cursor cue.
    public bool InteractiveAt(Point p) => ButtonAt(p) >= 0 || PollAt(p) >= 0;

    /// The button component at an already-resolved index, or null.
    public UserComponent? ButtonC(int i) => i >= 0 && i < Buttons.Count ? Buttons[i].C : null;

    /// The poll answer under a point, or -1. Locked down once a single-select poll is decided; a
    /// multiselect keeps accepting toggles, matching Discord.
    public int PollAt(Point p)
    {
        var poll = Msg.Poll;
        if (poll == null || poll.Closed) return -1;
        if (poll.IVoted && !poll.AllowMultiselect) return -1;
        return PollAnswers.FindIndex(a => a.Box.Contains(p));
    }

    static string? EmbedLink(Emb e, Point p)
    {
        if (e.E.Url == null) return null;
        var tb = new Rectangle(e.Box.X + e.TitleAt.X, e.Box.Y + e.TitleAt.Y, e.Box.Width, Ui.S(24));
        return tb.Contains(p) ? e.E.Url : null;
    }

    public static string Stamp(DateTimeOffset t)
    {
        var local = t.ToLocalTime();
        var d = local.Date;
        if (d == DateTime.Today) return "Today at " + local.ToString("h:mm tt");
        if (d == DateTime.Today.AddDays(-1)) return "Yesterday at " + local.ToString("h:mm tt");
        // Older messages read "8/3/26, 10:13 AM" in the live client — no leading zeros, a two-digit
        // year, and a comma before the time.
        return local.ToString("M/d/yy, h:mm tt");
    }
}

static class RectExt
{
    public static Rectangle WithX(this Rectangle r, int x) => new(x, r.Y, r.Width, r.Height);
}
