namespace GameLibrary.Net8;

public class GameFilterState
{
    public static string AllTagsLabel => UiText.Get("Filter.AllTags");

    public string SearchText { get; set; } = string.Empty;

    public string SelectedTag { get; set; } = UiText.Get("Filter.AllTags");

    public bool Matches(GameInfo game)
    {
        if (game == null)
        {
            return false;
        }

        string searchText = SearchText ?? string.Empty;
        bool searchMatch =
            string.IsNullOrWhiteSpace(searchText) ||
            Contains(game.Name, searchText) ||
            Contains(game.Description, searchText);

        bool tagMatch =
            string.Equals(SelectedTag, AllTagsLabel, StringComparison.OrdinalIgnoreCase) ||
            (game.Tags != null &&
             game.Tags.Any(tag => string.Equals(tag, SelectedTag, StringComparison.OrdinalIgnoreCase)));

        return searchMatch && tagMatch;
    }

    public static IReadOnlyList<string> BuildAvailableTags(IEnumerable<GameInfo> games)
    {
        var tagSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (GameInfo game in games ?? Enumerable.Empty<GameInfo>())
        {
            if (game?.Tags == null)
            {
                continue;
            }

            foreach (string tag in game.Tags)
            {
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    tagSet.Add(tag);
                }
            }
        }

        var tags = new List<string> { AllTagsLabel };
        tags.AddRange(tagSet.OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase));
        return tags;
    }

    private static bool Contains(string source, string value)
    {
        return !string.IsNullOrEmpty(source) &&
               source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
