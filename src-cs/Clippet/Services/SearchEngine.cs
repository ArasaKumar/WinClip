using Clippet.Models;

namespace Clippet.Services;

/// <summary>One visible row: an index into the history list plus the matched character
/// indices (for highlighting). The list is always addressed through these rows, never by
/// assuming ListView order equals history order.</summary>
public sealed class FilteredRow
{
    public int HistIndex { get; init; }
    public List<int> Indices { get; init; } = [];
}

public static class SearchEngine
{
    /// <summary>Build the filtered/sorted rows for a query against the history.
    /// Empty query → all items. Sort: pinned first, then (score desc for queries) then recency.</summary>
    public static List<FilteredRow> UpdateFilter(string query, IReadOnlyList<ClipItem> history)
    {
        query = query?.Trim() ?? "";
        var rows = new List<(FilteredRow row, int score, ulong id, bool pinned)>();

        if (query.Length == 0)
        {
            for (int i = 0; i < history.Count; i++)
            {
                var it = history[i];
                rows.Add((new FilteredRow { HistIndex = i, Indices = [] }, 0, it.Id, it.Pinned));
            }
        }
        else
        {
            for (int i = 0; i < history.Count; i++)
            {
                var it = history[i];
                var m = FuzzyMatcher.Match(query, it.Preview);
                if (m == null) continue;
                rows.Add((new FilteredRow { HistIndex = i, Indices = m.Indices }, m.Score, it.Id, it.Pinned));
            }
        }

        rows.Sort((a, b) =>
        {
            if (a.pinned != b.pinned) return a.pinned ? -1 : 1;   // pinned first
            if (query.Length > 0 && a.score != b.score) return b.score - a.score; // higher score first
            return a.id < b.id ? 1 : a.id > b.id ? -1 : 0;        // newer (higher id) first
        });

        var result = new List<FilteredRow>(rows.Count);
        foreach (var r in rows) result.Add(r.row);
        return result;
    }
}
