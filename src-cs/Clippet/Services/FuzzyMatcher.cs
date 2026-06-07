namespace Clippet.Services;

/// <summary>Skim/fzf-style fuzzy matcher. Returns a score and the matched character indices
/// in the target string (used to bold the matched runs in the row preview).</summary>
public static class FuzzyMatcher
{
    public sealed record Result(int Score, List<int> Indices);

    private const int SequentialBonus = 15;
    private const int SeparatorBonus = 30;
    private const int CamelBonus = 30;
    private const int FirstLetterBonus = 15;
    private const int MaxGapPenalty = 5;

    /// <summary>Returns null if <paramref name="text"/> does not contain <paramref name="pattern"/>
    /// as a (case-insensitive) subsequence. An empty pattern matches with score 0 and no indices.</summary>
    public static Result? Match(string pattern, string text)
    {
        if (string.IsNullOrEmpty(pattern)) return new Result(0, []);
        if (string.IsNullOrEmpty(text)) return null;

        var indices = new List<int>(pattern.Length);
        int score = 100;
        int sIdx = 0;
        int prevMatch = -2;

        for (int pIdx = 0; pIdx < pattern.Length; pIdx++)
        {
            char pc = char.ToLowerInvariant(pattern[pIdx]);
            int found = -1;
            while (sIdx < text.Length)
            {
                if (char.ToLowerInvariant(text[sIdx]) == pc) { found = sIdx; break; }
                sIdx++;
            }
            if (found < 0) return null;

            indices.Add(found);
            if (found == prevMatch + 1)
                score += SequentialBonus;
            else if (prevMatch >= 0)
                score -= Math.Min(found - prevMatch - 1, MaxGapPenalty);

            if (found == 0)
            {
                score += FirstLetterBonus;
            }
            else
            {
                char prev = text[found - 1];
                if (prev is ' ' or '_' or '-' or '/' or '.' or '\\' or ':')
                    score += SeparatorBonus;
                else if (char.IsLower(prev) && char.IsUpper(text[found]))
                    score += CamelBonus;
            }

            prevMatch = found;
            sIdx = found + 1;
        }

        score -= (text.Length - indices.Count) / 10; // gentle bias toward tighter matches
        return new Result(score, indices);
    }
}
