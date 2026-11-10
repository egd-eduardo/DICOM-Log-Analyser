using System.Globalization;
using System.Text;

namespace DicomLogAnalyser.Core.Search;

/// <summary>
/// Case- and accent-insensitive comparison by folding every character to a lower-case base letter (é → e, Ç → c,
/// Ő → o, ά → α, đ → d, ı → i). The table is built once; folding a line is a table lookup per character, so a search
/// runs at plain-text speed.
/// </summary>
public static class TextFolding
{
    private static readonly char[] Map = Build();

    private static char[] Build()
    {
        var map = new char[char.MaxValue + 1];
        for (var i = 0; i <= char.MaxValue; i++)
        {
            var c = (char)i;
            map[i] = c;
            if (i < 128)
            {
                map[i] = char.ToLowerInvariant(c);
                continue;
            }

            if (char.IsSurrogate(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.OtherNotAssigned) continue;
            string decomposed;
            try
            {
                decomposed = c.ToString().Normalize(NormalizationForm.FormD);
            }
            catch (ArgumentException)
            {
                // Code points the normaliser rejects (non-characters) are compared as they are.
                continue;
            }

            var b = decomposed[0];
            foreach (var d in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(d) != UnicodeCategory.NonSpacingMark)
                {
                    b = d;
                    break;
                }
            }

            map[i] = char.ToLowerInvariant(b);
        }

        // Letters that are not composed in Unicode but read as accented letters in the supported languages.
        foreach (var (from, to) in new[] { ('đ', 'd'), ('Đ', 'd'), ('ı', 'i'), ('ø', 'o'), ('Ø', 'o'), ('ł', 'l'), ('Ł', 'l'), ('ħ', 'h'), ('Ħ', 'h') })
            map[from] = to;

        return map;
    }

    public static char Fold(char c) => Map[c];

    public static string Fold(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return string.Create(text.Length, text, static (span, s) =>
        {
            for (var i = 0; i < s.Length; i++) span[i] = Map[s[i]];
        });
    }

    /// <summary>Folds <paramref name="source"/> into <paramref name="destination"/> (same length).</summary>
    public static void Fold(ReadOnlySpan<char> source, Span<char> destination)
    {
        for (var i = 0; i < source.Length; i++) destination[i] = Map[source[i]];
    }
}
