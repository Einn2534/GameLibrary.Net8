using System.Text;
using System.Text.RegularExpressions;
using static GameLibrary.Net8.FileExtensionMatcher;

namespace GameLibrary.Net8;

internal static class DownloadFileInspector
{
    public static bool TryUseExistingDownloadedFile(DownloadEntry record, string saveDirectory, Action<string> log, string sourceName)
    {
        if (record == null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(record.DownloadedFilePath) &&
            IsUsableDownloadedFile(record.DownloadedFilePath))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(record.DownloadedFilePath))
        {
            string invalidPath = record.DownloadedFilePath;
            if (File.Exists(invalidPath))
            {
                string reason = GetInvalidDownloadedFileReason(invalidPath);
                TryDeleteFile(invalidPath);
                log("Discarded invalid " + sourceName + " download for " + record.Name + ": " + reason);
            }

            record.DownloadedFilePath = null;
        }

        string existingPath = FindExistingDownloadedFile(record, saveDirectory);
        if (!string.IsNullOrWhiteSpace(existingPath))
        {
            record.DownloadedFilePath = existingPath;
            log("Found existing " + sourceName + " download for " + record.Name + ": " + Path.GetFileName(existingPath));
            return true;
        }

        return false;
    }

    public static string FindExistingDownloadedFile(DownloadEntry record, string saveDirectory)
    {
        if (record == null)
        {
            return null;
        }

        string downloadedFileName = Path.GetFileName(record.DownloadedFilePath);
        if (!string.IsNullOrWhiteSpace(downloadedFileName))
        {
            foreach (string directory in GetDownloadSearchDirectories(saveDirectory, Path.GetExtension(downloadedFileName)))
            {
                string candidate = Path.Combine(directory, downloadedFileName);
                if (IsUsableDownloadedFile(candidate))
                {
                    return candidate;
                }
            }
        }

        string baseName = SanitizeFileName(record.Name);
        List<string> downloadExtensions = GetDownloadExtensions();

        foreach (string extension in downloadExtensions)
        {
            foreach (string directory in GetDownloadSearchDirectories(saveDirectory, extension))
            {
                string candidate = Path.Combine(directory, baseName + extension);
                if (IsUsableDownloadedFile(candidate))
                {
                    return candidate;
                }
            }
        }

        string productCode = ExtractProductCode(record.Name);
        if (string.IsNullOrWhiteSpace(productCode))
        {
            return null;
        }

        foreach (string directory in GetAllDownloadSearchDirectories(saveDirectory))
        {
            string foundPath = Directory
                .EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
                .Where(path => downloadExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .FirstOrDefault(path =>
                    Path.GetFileNameWithoutExtension(path).IndexOf(productCode, StringComparison.OrdinalIgnoreCase) >= 0 &&
                    IsUsableDownloadedFile(path));
            if (!string.IsNullOrWhiteSpace(foundPath))
            {
                return foundPath;
            }
        }

        return null;
    }

    public static List<string> GetDownloadSearchDirectories(string saveDirectory, string extension)
    {
        var directories = new List<string>();
        AddDownloadSearchDirectory(directories, saveDirectory);

        bool isArchiveExtension = ContainsExtension(extension, AppSettings.ArchiveExtensions);
        bool isMediaExtension = ContainsExtension(extension, AppSettings.MediaExtensions);
        bool isKnownExtension = isArchiveExtension || isMediaExtension;

        if (isArchiveExtension || !isKnownExtension)
        {
            AddDownloadSearchDirectory(directories, AppSettings.ArchiveStorageDirectory);
        }

        if (isMediaExtension || !isKnownExtension)
        {
            AddDownloadSearchDirectory(directories, AppSettings.VideoLibraryDirectory);
        }

        return directories;
    }

    public static List<string> GetAllDownloadSearchDirectories(string saveDirectory)
    {
        var directories = new List<string>();
        AddDownloadSearchDirectory(directories, saveDirectory);
        AddDownloadSearchDirectory(directories, AppSettings.ArchiveStorageDirectory);
        AddDownloadSearchDirectory(directories, AppSettings.VideoLibraryDirectory);
        return directories;
    }

    public static List<string> GetDownloadExtensions()
    {
        return ToNormalizedExtensions(AppSettings.ArchiveExtensions.Concat(AppSettings.MediaExtensions));
    }

    public static string ExtractProductCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        Match match = Regex.Match(
            value,
            "(?<![A-Z0-9])([A-Z]{2}[0-9]{6,})(?![A-Z0-9])",
            RegexOptions.IgnoreCase);
        return match.Success
            ? match.Groups[1].Value.ToUpperInvariant()
            : null;
    }

    public static bool IsUsableDownloadedFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        FileInfo info = new(path);
        return info.Length > 0 && !LooksLikeHtmlFile(path);
    }

    public static string GetInvalidDownloadedFileReason(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "download path is missing";
        }

        if (!File.Exists(path))
        {
            return "download file is missing";
        }

        FileInfo info = new(path);
        if (info.Length == 0)
        {
            return "download file is empty";
        }

        if (LooksLikeHtmlFile(path))
        {
            return "saved content is an HTML page, not a usable download";
        }

        return "download file failed validation";
    }

    public static bool LooksLikeHtmlFile(string path)
    {
        const int SniffBytes = 4096;
        byte[] buffer = new byte[SniffBytes];
        using FileStream stream = File.OpenRead(path);
        int read = stream.Read(buffer, 0, buffer.Length);
        if (read <= 0)
        {
            return false;
        }

        string prefix = Encoding.UTF8.GetString(buffer, 0, read);
        string trimmed = prefix.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        return trimmed.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
            prefix.IndexOf("<title>workupload - Are you a human?</title>", StringComparison.OrdinalIgnoreCase) >= 0 ||
            prefix.IndexOf("Security Check", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static string SanitizeFileName(string name)
    {
        return SanitizeFileName(name, "unknown_title");
    }

    public static string SanitizeFileName(string name, string fallbackName)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            name = string.IsNullOrWhiteSpace(fallbackName) ? "unknown_title" : fallbackName;
        }

        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name.Trim();
    }

    private static void AddDownloadSearchDirectory(List<string> directories, string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        if (!directories.Contains(directory, StringComparer.OrdinalIgnoreCase))
        {
            directories.Add(directory);
        }
    }

    public static void TryDeleteFile(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A stale invalid file is still treated as not downloaded; deletion is best-effort.
        }
    }
}
