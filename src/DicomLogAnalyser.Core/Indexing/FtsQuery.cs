using System.Text;

namespace DicomLogAnalyser.Core.Indexing;

/// <summary>A parsed search: full-text expression for terms of 3+ characters and plain-text conditions for shorter ones.</summary>
public sealed record TextQuery(string? Match, IReadOnlyList<string> ShortIncluded, IReadOnlyList<string> ShortExcluded)
{
    public bool IsEmpty => Match is null && ShortIncluded.Count == 0;
}

/// <summary>
/// Converts what a user types into a safe search. Every term is matched as a <b>substring</b> anywhere in the event's
/// message, continuation lines (stack traces), category or component, ignoring case and accents: <c>840.1136</c>
/// matches inside a DICOM UID, <c>ACC000</c> inside <c>SYNACC0001</c>. Terms of 3 or more characters use the trigram
/// full-text index; shorter terms are matched by a plain-text scan. User text is never passed to FTS5 as syntax: each
/// term becomes a quoted string literal (double quotes doubled). Supported: "quoted phrases" with spaces and a leading
/// <c>-</c> to exclude a term. All terms must match (AND).
/// </summary>
public static class FtsQuery
{
    public const int MaxTerms = 16;
    public const int MaxInputLength = 512;
    public const int MinIndexedLength = 3;

    public static TextQuery Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return new TextQuery(null, [], []);
        var text = input.Length > MaxInputLength ? input[..MaxInputLength] : input;
        var positives = new List<string>();
        var negatives = new List<string>();
        var shortIn = new List<string>();
        var shortOut = new List<string>();
        var count = 0;
        foreach (var (raw, quoted) in SplitTerms(text))
        {
            var term = raw;
            var negate = false;
            if (!quoted && term.Length > 1 && term[0] == '-')
            {
                negate = true;
                term = term[1..];
            }

            if (!quoted) term = term.TrimEnd('*');
            term = new string(term.Where(ch => !char.IsControl(ch)).ToArray());
            if (term.Trim().Length == 0) continue;
            if (term.Length >= MinIndexedLength) (negate ? negatives : positives).Add("\"" + term.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"");
            else (negate ? shortOut : shortIn).Add(term);
            if (++count >= MaxTerms) break;
        }

        string? match = null;
        if (positives.Count > 0)
        {
            var sb = new StringBuilder(string.Join(" AND ", positives));
            foreach (var n in negatives) sb.Append(" NOT ").Append(n);
            match = sb.ToString();
        }
        else if (negatives.Count > 0)
        {
            // FTS5 cannot express "NOT x" alone; long exclusions then use the plain-text scan as well.
            shortOut.AddRange(negatives.Select(n => n[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal)));
        }

        return new TextQuery(match, shortIn, shortOut);
    }

    /// <summary>The full-text expression only (kept for callers that need just the indexed part).</summary>
    public static string? Build(string? input) => Parse(input).Match;

    /// <summary>LIKE pattern for a literal substring (escape character is backslash).</summary>
    public static string LikePattern(string term) =>
        "%" + term.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";

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
