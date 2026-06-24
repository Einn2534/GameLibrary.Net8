using Newtonsoft.Json;

namespace GameLibrary.Net8;

public class GameTagStoreService
{
    private readonly string storePath;

    public GameTagStoreService(string storePath)
    {
        this.storePath = storePath ?? throw new ArgumentNullException(nameof(storePath));
    }

    public GameTagStoreStatus LastStatus { get; private set; } = GameTagStoreStatus.None;

    public GameTagStoreStatus ApplyTags(IEnumerable<GameInfo> games)
    {
        TagStoreLoadResult loadResult = LoadStore();
        Dictionary<string, List<string>> store = loadResult.Store;

        foreach (GameInfo game in games ?? Enumerable.Empty<GameInfo>())
        {
            if (game == null)
            {
                continue;
            }

            string key = BuildGameKey(game);
            List<string> defaultTags = NormalizeTags(game.DefaultTags.Count > 0 ? game.DefaultTags : game.Tags);
            List<string> additionalTags = store.TryGetValue(key, out List<string> storedTags)
                ? NormalizeTags(storedTags)
                : new List<string>();
            List<string> effectiveTags = NormalizeTags(defaultTags.Concat(additionalTags));

            game.DefaultTags = defaultTags;
            game.Tags = effectiveTags;
            game.EditableTagsText = string.Join(", ", additionalTags);
        }

        LastStatus = loadResult.Status;
        return LastStatus;
    }

    public IReadOnlyList<string> SaveTags(GameInfo game, IEnumerable<GameInfo> allGames)
    {
        if (game == null)
        {
            return Array.Empty<string>();
        }

        TagStoreLoadResult loadResult = LoadStore();
        Dictionary<string, List<string>> store = loadResult.Store;
        string key = BuildGameKey(game);
        List<string> additionalTags = NormalizeTags(ParseTags(game.EditableTagsText));

        store[key] = additionalTags;
        string backupPath = loadResult.BackupOriginalBeforeSave
            ? BackupUnreadableStore()
            : null;
        SaveStore(store);

        foreach (GameInfo target in allGames ?? Enumerable.Empty<GameInfo>())
        {
            if (target == null || !string.Equals(BuildGameKey(target), key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            List<string> defaultTags = NormalizeTags(target.DefaultTags.Count > 0 ? target.DefaultTags : target.Tags);
            target.DefaultTags = defaultTags;
            target.Tags = NormalizeTags(defaultTags.Concat(additionalTags));
            target.EditableTagsText = string.Join(", ", additionalTags);
        }

        LastStatus = string.IsNullOrWhiteSpace(backupPath)
            ? GameTagStoreStatus.None
            : GameTagStoreStatus.Warning("Status.TagStoreRepaired", backupPath);
        return additionalTags;
    }

    private TagStoreLoadResult LoadStore()
    {
        if (!File.Exists(storePath))
        {
            return TagStoreLoadResult.Success(CreateEmptyStore());
        }

        try
        {
            string json = File.ReadAllText(storePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return TagStoreLoadResult.Warning(
                    CreateEmptyStore(),
                    GameTagStoreStatus.Warning("Status.TagStoreEmpty", storePath));
            }

            var loaded = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(json);
            if (loaded == null)
            {
                return TagStoreLoadResult.Warning(
                    CreateEmptyStore(),
                    GameTagStoreStatus.Warning("Status.TagStoreEmpty", storePath));
            }

            var store = CreateEmptyStore();
            foreach (KeyValuePair<string, List<string>> entry in loaded)
            {
                if (string.IsNullOrWhiteSpace(entry.Key))
                {
                    continue;
                }

                store[entry.Key.Trim()] = NormalizeTags(entry.Value);
            }

            return TagStoreLoadResult.Success(store);
        }
        catch (JsonException ex)
        {
            return TagStoreLoadResult.Warning(
                CreateEmptyStore(),
                GameTagStoreStatus.Warning("Status.TagStoreUnreadable", storePath, ex.Message),
                backupOriginalBeforeSave: true);
        }
        catch (IOException ex)
        {
            return TagStoreLoadResult.Warning(
                CreateEmptyStore(),
                GameTagStoreStatus.Warning("Status.TagStoreUnreadable", storePath, ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            return TagStoreLoadResult.Warning(
                CreateEmptyStore(),
                GameTagStoreStatus.Warning("Status.TagStoreUnreadable", storePath, ex.Message));
        }
    }

    private void SaveStore(Dictionary<string, List<string>> store)
    {
        string directory = Path.GetDirectoryName(storePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonConvert.SerializeObject(store, Formatting.Indented);
        File.WriteAllText(storePath, json);
    }

    private string BackupUnreadableStore()
    {
        string backupBasePath = storePath + ".invalid-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string backupPath = backupBasePath;
        int suffix = 1;
        while (File.Exists(backupPath))
        {
            backupPath = backupBasePath + "-" + suffix;
            suffix++;
        }

        File.Copy(storePath, backupPath, overwrite: false);
        return backupPath;
    }

    private static Dictionary<string, List<string>> CreateEmptyStore()
    {
        return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    }

    private static List<string> ParseTags(string value)
    {
        return (value ?? string.Empty)
            .Split([',', '\u3001', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(tag => tag.Trim())
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .ToList();
    }

    private static List<string> NormalizeTags(IEnumerable<string> tags)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<string>();

        foreach (string tag in tags ?? Enumerable.Empty<string>())
        {
            string trimmed = tag?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || !seen.Add(trimmed))
            {
                continue;
            }

            results.Add(trimmed);
        }

        return results;
    }

    private static string BuildGameKey(GameInfo game)
    {
        string key = game?.IsVideo == true && !string.IsNullOrWhiteSpace(game.MediaPath)
            ? game.MediaPath
            : !string.IsNullOrWhiteSpace(game?.InstallDirectory)
            ? game.InstallDirectory
            : game?.Name ?? string.Empty;

        return key.Trim();
    }

    private sealed class TagStoreLoadResult
    {
        private TagStoreLoadResult(
            Dictionary<string, List<string>> store,
            GameTagStoreStatus status,
            bool backupOriginalBeforeSave)
        {
            Store = store;
            Status = status;
            BackupOriginalBeforeSave = backupOriginalBeforeSave;
        }

        public Dictionary<string, List<string>> Store { get; }

        public GameTagStoreStatus Status { get; }

        public bool BackupOriginalBeforeSave { get; }

        public static TagStoreLoadResult Success(Dictionary<string, List<string>> store)
        {
            return new TagStoreLoadResult(store, GameTagStoreStatus.None, backupOriginalBeforeSave: false);
        }

        public static TagStoreLoadResult Warning(
            Dictionary<string, List<string>> store,
            GameTagStoreStatus status,
            bool backupOriginalBeforeSave = false)
        {
            return new TagStoreLoadResult(store, status, backupOriginalBeforeSave);
        }
    }
}

public sealed class GameTagStoreStatus
{
    private GameTagStoreStatus(string messageKey, object[] args)
    {
        MessageKey = messageKey;
        Args = args ?? Array.Empty<object>();
    }

    public static GameTagStoreStatus None { get; } = new(null, Array.Empty<object>());

    public string MessageKey { get; }

    public object[] Args { get; }

    public bool HasMessage => !string.IsNullOrWhiteSpace(MessageKey);

    public static GameTagStoreStatus Warning(string messageKey, params object[] args)
    {
        return new GameTagStoreStatus(messageKey, args);
    }
}
