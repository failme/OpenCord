using System.Drawing;

namespace OpenCord;

// Fenced code blocks, coloured. Discord runs highlight.js over ```lang blocks; this is the same
// idea at a fraction of the size — comments, strings, numbers and a keyword set per language.
//
// Deliberately line-at-a-time, because the layout above it hard-wraps a block into display lines
// before anything gets coloured. That means a /* */ spanning lines only colours its first line,
// which is the one real limitation and the price of not carrying lexer state through the wrapper.
static class Syntax
{
    public enum Kind { Text, Keyword, String, Number, Comment, Type, Title, Operator }

    public static Color ColorOf(Kind k) => k switch
    {
        Kind.Keyword => Theme.CodeKeyword,
        Kind.String => Theme.CodeString,
        Kind.Number => Theme.CodeNumber,
        Kind.Comment => Theme.CodeComment,
        Kind.Type => Theme.CodeType,
        Kind.Title => Theme.CodeTitle,
        Kind.Operator => Theme.CodeOperator,
        _ => Theme.CodeText,
    };

    // One shared set per family. Nothing here needs to be exhaustive: a keyword that is missed just
    // renders as plain text, which is exactly what an unknown language does anyway.
    static readonly string[] CLike =
    {
        "if","else","for","while","do","switch","case","break","continue","return","new","class",
        "struct","enum","interface","public","private","protected","static","void","const","using",
        "namespace","try","catch","finally","throw","this","null","true","false","var","let","const",
        "function","async","await","import","export","from","default","extends","implements","super",
        "typeof","instanceof","delete","in","of","yield","readonly","override","virtual","abstract",
        "sealed","internal","record","get","set","where","select","using",
    };
    static readonly string[] Py =
    {
        "def","class","if","elif","else","for","while","return","import","from","as","try","except",
        "finally","raise","with","lambda","yield","pass","break","continue","global","nonlocal",
        "None","True","False","and","or","not","is","in","async","await","assert","del","self",
    };
    static readonly string[] Sh =
    {
        "if","then","else","elif","fi","for","while","do","done","case","esac","function","return",
        "export","local","echo","cd","set","unset","source","exit","read",
    };
    static readonly string[] Sql =
    {
        "select","from","where","insert","into","values","update","delete","join","left","right",
        "inner","outer","on","group","by","order","having","limit","create","table","drop","alter",
        "index","primary","key","foreign","references","and","or","not","null","as","distinct",
    };

    static string[] KeywordsFor(string? lang) => (lang ?? "").ToLowerInvariant() switch
    {
        "py" or "python" => Py,
        "sh" or "bash" or "shell" or "zsh" or "ps1" or "powershell" => Sh,
        "sql" => Sql,
        "" or null => Array.Empty<string>(),
        _ => CLike,
    };

    static string CommentPrefix(string? lang) => (lang ?? "").ToLowerInvariant() switch
    {
        "py" or "python" or "sh" or "bash" or "shell" or "zsh" or "yml" or "yaml" or "toml" or "ini" => "#",
        "sql" => "--",
        "ps1" or "powershell" => "#",
        _ => "//",
    };

    /// Split one display line into coloured spans. Always returns at least one span, and the spans
    /// always concatenate back to the input — the layout measures them in order.
    public static List<(string Text, Kind Kind)> Line(string s, string? lang)
    {
        var outp = new List<(string, Kind)>();
        if (s.Length == 0) { outp.Add((s, Kind.Text)); return outp; }

        var keywords = KeywordsFor(lang);
        var comment = CommentPrefix(lang);
        int i = 0;
        bool declNext = false;      // the previous word was def/class/... so this one is the name
        var buf = new System.Text.StringBuilder();

        void Flush()
        {
            if (buf.Length == 0) return;
            // A run of plain text still has to be split on word boundaries so keywords inside it
            // get their own span.
            var text = buf.ToString();
            buf.Clear();
            int w = 0;
            while (w < text.Length)
            {
                if (!IsWordChar(text[w]))
                {
                    // Discord tints operators a shade off the body text rather than leaving them
                    // plain, which is most of what makes a block read as highlighted.
                    var ch = text[w];
                    outp.Add((ch.ToString(), "+-*/%=<>!&|^~?:".IndexOf(ch) >= 0 ? Kind.Operator : Kind.Text));
                    w++;
                    continue;
                }
                int start = w;
                while (w < text.Length && IsWordChar(text[w])) w++;
                var word = text[start..w];
                // A name immediately followed by "(" is a call or a definition — hljs calls that a
                // title and gives it its own colour; without it every function name was body text.
                bool call = w < text.Length && text[w] == '(';
                Kind k = keywords.Contains(word) ? Kind.Keyword
                       : char.IsDigit(word[0]) ? Kind.Number
                       // The name being declared is a title even before its parameter list —
                       // "def greet" and "class Foo" colour the name, which is how hljs reads them.
                       : declNext ? Kind.Title
                       : call ? Kind.Title
                       : char.IsUpper(word[0]) && word.Length > 1 ? Kind.Type
                       : Kind.Text;
                declNext = k == Kind.Keyword && Decl.Contains(word);
                outp.Add((word, k));
            }
        }

        while (i < s.Length)
        {
            // Line comment: everything from here on.
            if (comment.Length > 0 && i + comment.Length <= s.Length
                && string.CompareOrdinal(s, i, comment, 0, comment.Length) == 0)
            {
                Flush();
                outp.Add((s[i..], Kind.Comment));
                return outp;
            }
            // String literal, single or double quoted, with backslash escapes.
            if (s[i] is '"' or '\'' or '`')
            {
                Flush();
                char q = s[i];
                int j = i + 1;
                while (j < s.Length && s[j] != q) { if (s[j] == '\\') j++; j++; }
                j = Math.Min(j + 1, s.Length);
                outp.Add((s[i..j], Kind.String));
                i = j;
                continue;
            }
            buf.Append(s[i]);
            i++;
        }
        Flush();
        if (outp.Count == 0) outp.Add((s, Kind.Text));
        return outp;
    }

    /// Keywords whose next identifier is the thing being named.
    static readonly string[] Decl = { "def", "class", "function", "fn", "func", "struct", "interface",
                                      "enum", "record", "namespace", "sub", "type" };

    static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
