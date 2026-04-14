using Newtonsoft.Json.Linq;

namespace GameLibrary.Net8;

public static class AppSettings
{
    private const string SettingsFileName = "appsettings.json";
    private static readonly Lazy<SettingsDocument> Settings = new(LoadSettings);

    public static string GamesDirectory => GetRequiredPathSetting("GamesDirectory");

    public static string GamesJsonPath => GetPathSetting("GamesJsonPath", "games.json");

    public static string DefaultGameDescription => GetSetting("DefaultGameDescription", "Auto-generated game entry");

    public static string UiLanguage => GetSetting("UiLanguage", "ja");

    public static string IconCacheDirectory => GetPathSetting("IconCacheDirectory", Path.Combine("runtime", "icon-cache"));

    public static bool EnableRemoteIconFetch => GetBooleanSetting("EnableRemoteIconFetch", true);

    public static int RemoteIconFetchLimitPerRun => GetIntSetting("RemoteIconFetchLimitPerRun", 15);

    public static string RuntimeDirectory => GetPathSetting("RuntimeDirectory", "runtime");

    public static string ArchiveInboxDirectory => GetPathSetting("ArchiveInboxDirectory", "incoming");

    public static string SevenZipPath => GetPathSetting("SevenZipPath", @"C:\Program Files\7-Zip\7z.exe");

    public static IReadOnlyList<string> ArchivePasswords => GetListSetting("ArchivePasswords", "kimochi.info", "ADHentai");

    public static IReadOnlyList<string> ArchiveExtensions => GetListSetting("ArchiveExtensions", ".zip", ".rar", ".7z");

    private static string GetRequiredPathSetting(string key)
    {
        return ResolvePath(GetRequiredSetting(key));
    }

    private static string GetPathSetting(string key, string defaultValue)
    {
        string value = GetSetting(key, defaultValue);
        return ResolvePath(value);
    }

    private static string GetSetting(string key, string defaultValue)
    {
        string value = Settings.Value.Values.TryGetValue(key, out string configuredValue)
            ? configuredValue
            : null;
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
    }

    private static bool GetBooleanSetting(string key, bool defaultValue)
    {
        string value = Settings.Value.Values.TryGetValue(key, out string configuredValue)
            ? configuredValue
            : null;
        return bool.TryParse(value, out bool parsedValue) ? parsedValue : defaultValue;
    }

    private static int GetIntSetting(string key, int defaultValue)
    {
        string value = Settings.Value.Values.TryGetValue(key, out string configuredValue)
            ? configuredValue
            : null;
        return int.TryParse(value, out int parsedValue) ? parsedValue : defaultValue;
    }

    private static string GetRequiredSetting(string key)
    {
        string value = Settings.Value.Values.TryGetValue(key, out string configuredValue)
            ? configuredValue
            : null;
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("Required app setting is missing: " + key);
        }

        return value;
    }

    private static IReadOnlyList<string> GetListSetting(string key, params string[] defaultValues)
    {
        if (Settings.Value.ListValues.TryGetValue(key, out IReadOnlyList<string> configuredValues) &&
            configuredValues.Count > 0)
        {
            return configuredValues;
        }

        return defaultValues.ToList();
    }

    private static SettingsDocument LoadSettings()
    {
        string path = Path.Combine(AppContext.BaseDirectory, SettingsFileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Settings file was not found.", path);
        }

        JObject root = JObject.Parse(File.ReadAllText(path));
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var listValues = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (JProperty property in root.Properties())
        {
            if (property.Value.Type == JTokenType.Array)
            {
                listValues[property.Name] = property.Value
                    .Values<string>()
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim())
                    .ToList();
                continue;
            }

            values[property.Name] = property.Value.Type == JTokenType.Null
                ? null
                : property.Value.ToString();
        }

        return new SettingsDocument(values, listValues);
    }

    private static string ResolvePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        if (Path.IsPathRooted(path))
        {
            return path;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
    }

    private sealed class SettingsDocument
    {
        public SettingsDocument(
            IReadOnlyDictionary<string, string> values,
            IReadOnlyDictionary<string, IReadOnlyList<string>> listValues)
        {
            Values = values;
            ListValues = listValues;
        }

        public IReadOnlyDictionary<string, string> Values { get; }

        public IReadOnlyDictionary<string, IReadOnlyList<string>> ListValues { get; }
    }
}
