using Newtonsoft.Json;

namespace GameLibrary.Net8;

public class GameCatalogService
{
    private readonly string gamesDirectory;
    private readonly string jsonPath;
    private readonly GamesJsonGenerator generator;
    private readonly GameTagStoreService tagStoreService;

    public GameCatalogService(
        string gamesDirectory,
        string jsonPath,
        GamesJsonGenerator generator = null,
        GameTagStoreService tagStoreService = null)
    {
        this.gamesDirectory = gamesDirectory ?? throw new ArgumentNullException(nameof(gamesDirectory));
        this.jsonPath = jsonPath ?? throw new ArgumentNullException(nameof(jsonPath));
        this.generator = generator ?? new GamesJsonGenerator();
        this.tagStoreService = tagStoreService ?? new GameTagStoreService(AppSettings.TagStorePath);
    }

    public IReadOnlyList<GameInfo> LoadGames()
    {
        generator.Generate(gamesDirectory, jsonPath);

        if (!File.Exists(jsonPath))
        {
            throw new FileNotFoundException("games.json was not found.", jsonPath);
        }

        string jsonContent = File.ReadAllText(jsonPath);
        var games = JsonConvert.DeserializeObject<List<GameInfo>>(jsonContent);
        List<GameInfo> loadedGames = games ?? new List<GameInfo>();
        tagStoreService.ApplyTags(loadedGames);
        return loadedGames;
    }
}
