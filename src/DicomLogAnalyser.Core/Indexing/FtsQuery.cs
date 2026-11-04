using System.Text;

namespace DicomLogAnalyser.Core.Indexing;

/// <summary>
/// Converts what a user types into a safe SQLite FTS5 expression. User text is never passed to FTS5 as syntax: every
/// term becomes a quoted phrase of its letter/digit tokens, so identifiers such as <c>1.2.840.10008.5.1.4</c>,
/// <c>192.0.2.10</c> or <c>DOE^JANE</c> match their exact token sequence. Supported: "quoted phrases", a trailing
/// <c>*</c> for prefix search, and a leading <c>-</c> to exclude a term. All terms must match (AND).
/// </summary>
public static class FtsQuery
{
    public const int MaxTerms = 16;
    public const int MaxInputLength = 512;

    public static string? Build(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var text = input.Length > MaxInputLength ? input[..MaxInputLength] : input;
        var positives = new List<string>();
        var negatives = new List<string>();
        foreach (var (raw, quoted) in SplitTerms(text))
        {
            var term = raw;
            var negate = false;
            if (!quoted && term.StartsWith('-') && term.Length > 1)
            {
                negate = true;
                term = term[1..];
            }

            var prefix = !quoted && term.EndsWith('*');
            var tokens = Tokens(term);
            if (tokens.Count == 0) continue;
            var phrase = "\"" + string.Join(' ', tokens) + "\"" + (prefix ? "*" : "");
            (negate ? negatives : positives).Add(phrase);
            if (positives.Count + negatives.Count >= MaxTerms) break;
        }

        if (positives.Count == 0) return null;
        var sb = new StringBuilder(string.Join(" AND ", positives));
        foreach (var n in negatives) sb.Append(" NOT ").Append(n);
        return sb.ToString();
    }

    /// <summary>Letter/digit runs, lower-cased; everything else is a separator (as in the unicode61 tokenizer).</summary>
    internal static List<string> Tokens(string term)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        foreach (var ch in term)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
            else if (sb.Length > 0)
            {
                tokens.Add(sb.ToString());
                sb.Clear();
            }
        }

        if (sb.Length > 0) tokens.Add(sb.ToString());
        return tokens.Count > 64 ? tokens[..64] : tokens;
    }

    private static IEnumerable<(string Term, bool Quoted)> SplitTerms(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length) yield break;
            if (text[i] == '"')
            {
                var end = text.IndexOf('"', i + 1);
                if (end < 0) end = text.Length;
                yield return (text[(i + 1)..end], true);
                i = end + 1;
                continue;
            }

            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            yield return (text[start..i], false);
        }
    }
}
