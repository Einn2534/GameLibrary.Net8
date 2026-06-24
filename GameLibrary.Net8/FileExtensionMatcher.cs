namespace GameLibrary.Net8;

internal static class FileExtensionMatcher
{
    public static bool IsConfiguredExtension(string path, IEnumerable<string> extensions)
    {
        return ContainsExtension(Path.GetExtension(path), extensions);
    }

    public static bool ContainsExtension(string extension, IEnumerable<string> extensions)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        string normalizedExtension = NormalizeExtension(extension);
        return (extensions ?? Enumerable.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(NormalizeExtension)
            .Contains(normalizedExtension, StringComparer.OrdinalIgnoreCase);
    }

    public static HashSet<string> ToExtensionSet(IEnumerable<string> extensions)
    {
        return ToNormalizedExtensions(extensions).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static List<string> ToNormalizedExtensions(IEnumerable<string> extensions)
    {
        return (extensions ?? Enumerable.Empty<string>())
            .Where(extension => !string.IsNullOrWhiteSpace(extension))
            .Select(NormalizeExtension)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string NormalizeExtension(string value)
    {
        return value.StartsWith(".", StringComparison.Ordinal) ? value : "." + value;
    }
}
