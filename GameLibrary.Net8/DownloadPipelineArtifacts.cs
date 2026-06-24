using Newtonsoft.Json;

namespace GameLibrary.Net8;

internal static class DownloadPipelineArtifacts
{
    public const string ArticleUrlsFile = "article_urls.json";
    public const string DownloadRecordsFile = "download_records.json";
    public const string HostLinkFailuresFile = "host_link_failures.json";
    public const string PrimaryResolvedFile = "primary_resolved.json";
    public const string PrimaryFailuresFile = "primary_failures.json";
    public const string MirrorCandidatesFile = "mirror_candidates.json";
    public const string MirrorResolvedFile = "mirror_resolved.json";
    public const string MirrorFailuresFile = "mirror_failures.json";
    public const string RunStateFile = "downloader_run_state.json";

    public static string GetPath(string runtimeDirectory, string fileName)
    {
        return Path.Combine(runtimeDirectory, fileName);
    }

    public static T LoadJson<T>(string path)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        string json = File.ReadAllText(path);
        return JsonConvert.DeserializeObject<T>(json);
    }

    public static T LoadRuntimeJson<T>(string runtimeDirectory, string fileName)
    {
        return LoadJson<T>(GetPath(runtimeDirectory, fileName));
    }

    public static void SaveJson<T>(string path, T value)
    {
        string directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonConvert.SerializeObject(value, Formatting.Indented);
        File.WriteAllText(path, json);
    }

    public static void SaveRuntimeJson<T>(string runtimeDirectory, string fileName, T value)
    {
        SaveJson(GetPath(runtimeDirectory, fileName), value);
    }
}
