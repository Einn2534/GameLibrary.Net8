using System.Windows;

namespace GameLibrary.Net8;

public static class UiLocalization
{
    private const string DictionaryPrefix = "Resources/Strings.";
    private static IReadOnlyDictionary<string, string> currentStrings =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public static void ApplyLanguage(string languageCode)
    {
        string normalizedCode = NormalizeLanguage(languageCode);
        ResourceDictionary dictionary = LoadDictionary(normalizedCode);

        if (Application.Current != null)
        {
            List<ResourceDictionary> existingDictionaries = Application.Current.Resources.MergedDictionaries
                .Where(dictionaryItem => dictionaryItem.Source?.OriginalString.StartsWith(DictionaryPrefix, StringComparison.OrdinalIgnoreCase) == true)
                .ToList();

            foreach (ResourceDictionary existingDictionary in existingDictionaries)
            {
                Application.Current.Resources.MergedDictionaries.Remove(existingDictionary);
            }

            Application.Current.Resources.MergedDictionaries.Add(dictionary);
        }

        currentStrings = dictionary.Keys
            .OfType<object>()
            .Select(key => new KeyValuePair<string, string>(key.ToString(), dictionary[key] as string))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Key) && entry.Value != null)
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
    }

    public static string GetString(string key)
    {
        return currentStrings.TryGetValue(key, out string value) ? value : key;
    }

    private static string NormalizeLanguage(string languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return "ja";
        }

        return languageCode.Trim().StartsWith("ja", StringComparison.OrdinalIgnoreCase) ? "ja" : "en";
    }

    private static ResourceDictionary LoadDictionary(string normalizedCode)
    {
        return new ResourceDictionary
        {
            Source = new Uri($"Resources/Strings.{normalizedCode}.xaml", UriKind.Relative)
        };
    }
}
