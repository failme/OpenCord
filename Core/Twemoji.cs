namespace OpenCord;

// Discord does not render emoji with the system font — it substitutes Twemoji images, which is why
// 😀 looks the same on every platform there and looks like Segoe UI Emoji here. This finds emoji
// sequences in a run of text and maps each to its Twemoji sprite; RichContent then draws them
// through the same inline-image path custom emoji already use.
static class Twemoji
{
    // Pinned rather than @latest: jsDelivr caches a pinned tag forever, and an unpinned tag would
    // silently change every sprite under us.
    public const string Cdn = "https://cdn.jsdelivr.net/gh/jdecked/twemoji@15.1.0/assets/72x72/";

    public static bool IsTwemojiUrl(string url) => url.StartsWith(Cdn, StringComparison.Ordinal);

    // ── sequence detection ──

    const int ZWJ = 0x200D, VS16 = 0xFE0F, KEYCAP = 0x20E3;

    static bool IsModifier(int cp) => cp >= 0x1F3FB && cp <= 0x1F3FF;   // skin tones
    static bool IsTag(int cp) => cp >= 0xE0020 && cp <= 0xE007F;        // subdivision flags

    // Codepoints that can begin an emoji. Deliberately a little broad: a false positive resolves to
    // a sprite that does not exist, and the 404 path falls back to drawing the glyph as text.
    static bool IsEmojiBase(int cp) =>
        (cp >= 0x1F000 && cp <= 0x1FAFF) ||   // pictographs, faces, flags, symbols
        (cp >= 0x2600 && cp <= 0x27BF) ||     // misc symbols + dingbats
        (cp >= 0x2300 && cp <= 0x23FF) ||     // watch, hourglass, media controls
        (cp >= 0x2B00 && cp <= 0x2BFF) ||     // stars, arrows, geometric
        (cp >= 0x25A0 && cp <= 0x25FF) ||     // geometric shapes
        (cp >= 0x2190 && cp <= 0x21FF) ||     // arrows
        cp is 0x00A9 or 0x00AE or 0x2122 or 0x3030 or 0x303D or 0x3297 or 0x3299 or 0x2934 or 0x2935;

    // #, * and 0-9 are only emoji when a keycap follows.
    static bool IsKeycapBase(int cp) => cp == 0x23 || cp == 0x2A || (cp >= 0x30 && cp <= 0x39);

    /// Length in chars of the emoji sequence starting at `i`, or 0 if there isn't one.
    public static int SequenceLength(string s, int i)
    {
        if (i >= s.Length) return 0;
        int start = i;
        int cp = char.ConvertToUtf32(s, i);
        int size = char.IsSurrogatePair(s, i) ? 2 : 1;

        if (IsKeycapBase(cp))
        {
            int j = i + size;
            if (j < s.Length && s[j] == VS16) j++;
            if (j < s.Length && s[j] == KEYCAP) return j + 1 - start;
            return 0;
        }
        if (!IsEmojiBase(cp)) return 0;

        i += size;
        while (true)
        {
            // trailing modifiers on the current base
            while (i < s.Length && (s[i] == VS16 || IsModifier(char.ConvertToUtf32(s, i))
                                    || IsTag(char.ConvertToUtf32(s, i)) || s[i] == KEYCAP))
                i += char.IsSurrogatePair(s, i) ? 2 : 1;

            // a ZWJ continues the same glyph (👨‍👩‍👧, 🏳️‍🌈 …)
            if (i < s.Length && s[i] == ZWJ && i + 1 < s.Length)
            {
                int next = i + 1;
                int ncp = char.ConvertToUtf32(s, next);
                if (IsEmojiBase(ncp)) { i = next + (char.IsSurrogatePair(s, next) ? 2 : 1); continue; }
            }
            break;
        }
        return i - start;
    }

    /// Twemoji's filename for a sequence: lowercase hex codepoints joined by '-'. VS16 is dropped
    /// unless the sequence is a ZWJ sequence, which is the rule twemoji.js itself uses.
    public static string FileName(string seq)
    {
        bool zwj = seq.Contains((char)ZWJ);
        var parts = new List<string>(4);
        for (int i = 0; i < seq.Length;)
        {
            int cp = char.ConvertToUtf32(seq, i);
            i += char.IsSurrogatePair(seq, i) ? 2 : 1;
            if (cp == VS16 && !zwj) continue;
            parts.Add(cp.ToString("x"));
        }
        return string.Join("-", parts);
    }

    public static string Url(string seq) => Cdn + FileName(seq) + ".png";

    /// Split text into runs of (segment, isEmoji), so a caller can emit one Run per piece.
    public static IEnumerable<(string Text, bool Emoji)> Split(string text)
    {
        int plain = 0;
        for (int i = 0; i < text.Length;)
        {
            int len = SequenceLength(text, i);
            if (len == 0) { i += char.IsSurrogatePair(text, i) ? 2 : 1; continue; }
            if (i > plain) yield return (text[plain..i], false);
            yield return (text.Substring(i, len), true);
            i += len;
            plain = i;
        }
        if (plain < text.Length) yield return (text[plain..], false);
    }

    /// True when the text contains at least one emoji — lets callers skip the split entirely,
    /// which is the common case for ordinary messages.
    public static bool Any(string text)
    {
        for (int i = 0; i < text.Length;)
        {
            int len = SequenceLength(text, i);
            if (len > 0) return true;
            i += char.IsSurrogatePair(text, i) ? 2 : 1;
        }
        return false;
    }
}
