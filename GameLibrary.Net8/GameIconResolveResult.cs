namespace GameLibrary.Net8;

public sealed class GameIconResolveResult
{
    public GameIconResolveResult(string iconPath, bool attemptedRemoteFetch)
    {
        IconPath = iconPath ?? string.Empty;
        AttemptedRemoteFetch = attemptedRemoteFetch;
    }

    public string IconPath { get; }

    public bool AttemptedRemoteFetch { get; }
}
