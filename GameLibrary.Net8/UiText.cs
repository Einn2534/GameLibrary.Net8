using System.Globalization;

namespace GameLibrary.Net8;

public static class UiText
{
    public static string Get(string key)
    {
        return UiLocalization.GetString(key);
    }

    public static string Format(string key, params object[] args)
    {
        return string.Format(CultureInfo.CurrentCulture, Get(key), args);
    }
}
