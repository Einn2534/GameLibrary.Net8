using Newtonsoft.Json;

namespace GameLibrary.Net8;

public class GameCatalogService
{
    private readonly string gamesDirectory;
    private readonly string jsonPath;
    private readonly GamesJsonGenerator generator;

    public GameCatalogService(string gamesDirectory, string jsonPath, GamesJsonGenerator generator = null)
    {
        this.gamesDirectory = gamesDirectory ?? throw new ArgumentNullException(nameof(gamesDirectory));
        this.jsonPath = jsonPath ?? throw new ArgumentNullException(nameof(jsonPath));
        this.generator = generator ?? new GamesJsonGenerator();
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
        return games ?? new List<GameInfo>();
    }
}
