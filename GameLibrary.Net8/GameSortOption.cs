using System.ComponentModel;

namespace GameLibrary.Net8;

public sealed class GameSortOption
{
    public GameSortOption(string displayName, string propertyName, ListSortDirection direction)
    {
        DisplayName = displayName ?? string.Empty;
        PropertyName = propertyName ?? string.Empty;
        Direction = direction;
    }

    public string DisplayName { get; }

    public string PropertyName { get; }

    public ListSortDirection Direction { get; }

    public override string ToString()
    {
        return DisplayName;
    }
}
