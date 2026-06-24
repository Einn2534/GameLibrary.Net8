using System.Diagnostics;
using System.Text.RegularExpressions;
using static GameLibrary.Net8.DownloadFileInspector;
using static GameLibrary.Net8.FileExtensionMatcher;

namespace GameLibrary.Net8;

public class ArchiveImportService
{
    private static readonly HashSet<string> ExcludedExecutableNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "UnityCrashHandler64.exe",
        "notification_helper.exe"
    };

    private readonly string sevenZipPath;
    private readonly IReadOnlyList<string> passwords;
    private readonly IReadOnlyList<string> archiveExtensions;
    private readonly IReadOnlyList<string> mediaExtensions;

    public ArchiveImportService(
        string sevenZipPath,
        IEnumerable<string> passwords,
        IEnumerable<string> archiveExtensions,
        IEnumerable<string> mediaExtensions)
    {
        this.sevenZipPath = sevenZipPath ?? throw new ArgumentNullException(nameof(sevenZipPath));
        this.passwords = (passwords ?? Enumerable.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        this.archiveExtensions = ToNormalizedExtensions(archiveExtensions);
        this.mediaExtensions = ToNormalizedExtensions(mediaExtensions);
    }

    public IReadOnlyList<string> GetArchivePaths(string sourceDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
        {
            return Array.Empty<string>();
        }

        var results = Directory
            .EnumerateFiles(sourceDirectory, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => IsArchivePath(path) || IsMediaPath(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return results;
    }

    public bool IsArchivePath(string path)
    {
        return !string.IsNullOrWhiteSpace(path) &&
            archiveExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }

    public bool IsMediaPath(string path)
    {
        return !string.IsNullOrWhiteSpace(path) &&
            mediaExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }

    public ArchiveImportResult ImportArchive(string archivePath, string libraryDirectory)
    {
        return ImportArchive(archivePath, libraryDirectory, AppSettings.VideoLibraryDirectory);
    }

    public ArchiveImportResult ImportArchive(string archivePath, string libraryDirectory, string videoLibraryDirectory)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            throw new FileNotFoundException("Archive file was not found.", archivePath);
        }

        if (!File.Exists(sevenZipPath))
        {
            throw new FileNotFoundException("7-Zip executable was not found.", sevenZipPath);
        }

        if (string.IsNullOrWhiteSpace(libraryDirectory))
        {
            throw new ArgumentException("Library directory is required.", nameof(libraryDirectory));
        }

        Directory.CreateDirectory(libraryDirectory);

        string archiveName = Path.GetFileNameWithoutExtension(archivePath);
        ArchiveVersion incomingVersion = ParseArchiveVersion(archiveName);
        string installDirectory = Path.Combine(libraryDirectory, archiveName);

        string tempRoot = Path.Combine(Path.GetTempPath(), "GameLibraryImport", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            bool extracted = TryExtractArchive(archivePath, tempRoot, out string extractMessage);
            if (!extracted)
            {
                return new ArchiveImportResult
                {
                    Success = false,
                    Message = extractMessage,
                    InstallDirectory = installDirectory
                };
            }

            string normalizedPath = NormalizeExtractedContent(tempRoot);
            if (IsVideoArchive(normalizedPath))
            {
                return ImportExtractedMedia(normalizedPath, videoLibraryDirectory, archiveName);
            }

            string existingInstallDirectory = ResolveExistingInstallDirectory(libraryDirectory, installDirectory, incomingVersion);
            bool installDirectoryExists = !string.IsNullOrWhiteSpace(existingInstallDirectory);
            if (installDirectoryExists && !ShouldReplaceExistingInstallDirectory(incomingVersion, existingInstallDirectory))
            {
                return new ArchiveImportResult
                {
                    Success = true,
                    Skipped = true,
                    Message = UiText.Get("Archive.StatusSkippedAlreadyExists"),
                    InstallDirectory = existingInstallDirectory
                };
            }

            if (installDirectoryExists)
            {
                GameSaveBackupService.BackupSaves(existingInstallDirectory);
                ReplaceDirectory(normalizedPath, existingInstallDirectory, installDirectory);
            }
            else
            {
                CopyDirectory(normalizedPath, installDirectory);
            }

            GameSaveBackupService.RestoreSaves(installDirectory);

            return new ArchiveImportResult
            {
                Success = true,
                Message = UiText.Get("Archive.StatusImported"),
                InstallDirectory = installDirectory
            };
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                try
                {
                    Directory.Delete(tempRoot, true);
                }
                catch
                {
                }
            }
        }
    }

    public ArchiveImportResult ImportMedia(string mediaPath, string videoLibraryDirectory)
    {
        if (string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath))
        {
            throw new FileNotFoundException("Media file was not found.", mediaPath);
        }

        if (!IsMediaPath(mediaPath))
        {
            throw new ArgumentException("Media file extension is not supported.", nameof(mediaPath));
        }

        if (string.IsNullOrWhiteSpace(videoLibraryDirectory))
        {
            throw new ArgumentException("Video library directory is required.", nameof(videoLibraryDirectory));
        }

        Directory.CreateDirectory(videoLibraryDirectory);
        string destinationPath = GetUniqueFilePath(videoLibraryDirectory, Path.GetFileName(mediaPath));
        File.Move(mediaPath, destinationPath);

        return new ArchiveImportResult
        {
            Success = true,
            Message = UiText.Get("Media.StatusImported"),
            InstallDirectory = videoLibraryDirectory,
            MediaPath = destinationPath,
            MediaPaths = new List<string> { destinationPath }
        };
    }

    private ArchiveImportResult ImportExtractedMedia(string sourceDirectory, string videoLibraryDirectory, string archiveName)
    {
        if (string.IsNullOrWhiteSpace(videoLibraryDirectory))
        {
            throw new ArgumentException("Video library directory is required.", nameof(videoLibraryDirectory));
        }

        Directory.CreateDirectory(videoLibraryDirectory);

        List<string> mediaFiles = EnumerateMediaFiles(sourceDirectory).ToList();
        var destinationPaths = new List<string>();
        for (int i = 0; i < mediaFiles.Count; i++)
        {
            string mediaFile = mediaFiles[i];
            string destinationFileName = BuildExtractedMediaFileName(archiveName, mediaFile, mediaFiles.Count);
            string destinationPath = GetUniqueFilePath(videoLibraryDirectory, destinationFileName);
            File.Copy(mediaFile, destinationPath, overwrite: false);
            destinationPaths.Add(destinationPath);
        }

        return new ArchiveImportResult
        {
            Success = destinationPaths.Count > 0,
            Message = UiText.Format("Media.StatusImportedFromArchive", destinationPaths.Count),
            InstallDirectory = videoLibraryDirectory,
            MediaPath = destinationPaths.FirstOrDefault(),
            MediaPaths = destinationPaths
        };
    }

    private bool TryExtractArchive(string archivePath, string tempRoot, out string message)
    {
        string[] candidatePasswords = passwords.Count == 0 ? new[] { string.Empty } : passwords.ToArray();

        foreach (string password in candidatePasswords)
        {
            ClearDirectory(tempRoot);

            int exitCode = Run7ZipExtract(archivePath, tempRoot, password);
            if (exitCode <= 1)
            {
                message = UiText.Get("Archive.StatusExtracted");
                return true;
            }
        }

        message = UiText.Get("Archive.StatusFailedToExtract");
        return false;
    }

    private int Run7ZipExtract(string archivePath, string outputDirectory, string password)
    {
        string passwordArgument = string.IsNullOrEmpty(password)
            ? "-p"
            : "-p\"" + password.Replace("\"", "\\\"") + "\"";

        var startInfo = new ProcessStartInfo
        {
            FileName = sevenZipPath,
            Arguments = "x -y " + passwordArgument + " \"" + archivePath + "\" -o\"" + outputDirectory + "\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using Process process = Process.Start(startInfo);
        if (process == null)
        {
            throw new InvalidOperationException("7-Zip process could not be started.");
        }

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(standardOutput, standardError);
        return process.ExitCode;
    }

    private static string NormalizeExtractedContent(string tempRoot)
    {
        string[] files = Directory.GetFiles(tempRoot);
        string[] directories = Directory.GetDirectories(tempRoot);

        if (files.Length == 0 && directories.Length == 1)
        {
            return directories[0];
        }

        string normalizedDirectory = Path.Combine(tempRoot, "_normalized");
        if (Directory.Exists(normalizedDirectory))
        {
            Directory.Delete(normalizedDirectory, true);
        }

        Directory.CreateDirectory(normalizedDirectory);

        foreach (string directory in directories)
        {
            string target = Path.Combine(normalizedDirectory, Path.GetFileName(directory));
            Directory.Move(directory, target);
        }

        foreach (string file in files)
        {
            string target = Path.Combine(normalizedDirectory, Path.GetFileName(file));
            File.Move(file, target);
        }

        return normalizedDirectory;
    }

    private static void ClearDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            return;
        }

        foreach (string childDirectory in Directory.GetDirectories(directory))
        {
            Directory.Delete(childDirectory, true);
        }

        foreach (string file in Directory.GetFiles(directory))
        {
            File.Delete(file);
        }
    }

    private bool IsVideoArchive(string extractedDirectory)
    {
        return !HasGameExecutable(extractedDirectory) && EnumerateMediaFiles(extractedDirectory).Any();
    }

    private static bool HasGameExecutable(string directory)
    {
        return Directory
            .EnumerateFiles(directory, "*.exe", SearchOption.AllDirectories)
            .Any(path => !IsExcludedExecutablePath(path));
    }

    private IEnumerable<string> EnumerateMediaFiles(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return Enumerable.Empty<string>();
        }

        return Directory
            .EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
            .Where(IsMediaPath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsExcludedExecutablePath(string exePath)
    {
        string fileName = Path.GetFileName(exePath);
        if (ExcludedExecutableNames.Contains(fileName))
        {
            return true;
        }

        string directoryPath = Path.GetDirectoryName(exePath);
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return false;
        }

        string[] segments = directoryPath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        return segments.Any(segment => string.Equals(segment, "lib", StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildExtractedMediaFileName(string archiveName, string mediaFile, int mediaCount)
    {
        string extension = Path.GetExtension(mediaFile);
        if (mediaCount == 1)
        {
            return SanitizeFileName(archiveName, "video") + extension;
        }

        string mediaName = Path.GetFileNameWithoutExtension(mediaFile);
        return SanitizeFileName(archiveName + " - " + mediaName, "video") + extension;
    }

    private static string GetUniqueFilePath(string directory, string fileName)
    {
        string destinationPath = Path.Combine(directory, fileName);
        string nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        int suffix = 1;

        while (File.Exists(destinationPath))
        {
            destinationPath = Path.Combine(directory, nameWithoutExtension + " (" + suffix + ")" + extension);
            suffix++;
        }

        return destinationPath;
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (string directory in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            string relativePath = directory[sourceDirectory.Length..].TrimStart(Path.DirectorySeparatorChar);
            Directory.CreateDirectory(Path.Combine(destinationDirectory, relativePath));
        }

        foreach (string file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            string relativePath = file[sourceDirectory.Length..].TrimStart(Path.DirectorySeparatorChar);
            string destinationPath = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));
            File.Copy(file, destinationPath, false);
        }
    }

    private static string ResolveExistingInstallDirectory(
        string libraryDirectory,
        string exactInstallDirectory,
        ArchiveVersion incomingVersion)
    {
        if (Directory.Exists(exactInstallDirectory))
        {
            return exactInstallDirectory;
        }

        if (!incomingVersion.HasVersion)
        {
            return null;
        }

        return Directory
            .EnumerateDirectories(libraryDirectory, "*", SearchOption.TopDirectoryOnly)
            .Select(path => new
            {
                Path = path,
                Version = ParseArchiveVersion(new DirectoryInfo(path).Name)
            })
            .Where(candidate => string.Equals(candidate.Version.Identity, incomingVersion.Identity, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(candidate => candidate.Version, ArchiveVersionComparer.Instance)
            .Select(candidate => candidate.Path)
            .FirstOrDefault();
    }

    private static bool ShouldReplaceExistingInstallDirectory(ArchiveVersion incomingVersion, string existingInstallDirectory)
    {
        if (!incomingVersion.HasVersion)
        {
            return false;
        }

        ArchiveVersion existingVersion = ParseArchiveVersion(new DirectoryInfo(existingInstallDirectory).Name);
        return ArchiveVersionComparer.Instance.Compare(incomingVersion, existingVersion) > 0;
    }

    private static void ReplaceDirectory(string sourceDirectory, string existingDirectory, string destinationDirectory)
    {
        string parentDirectory = Path.GetDirectoryName(existingDirectory);
        string backupDirectory = Path.Combine(parentDirectory, Path.GetFileName(existingDirectory) + "_backup_" + Guid.NewGuid().ToString("N"));

        Directory.Move(existingDirectory, backupDirectory);

        try
        {
            CopyDirectory(sourceDirectory, destinationDirectory);
            Directory.Delete(backupDirectory, true);
        }
        catch
        {
            if (Directory.Exists(destinationDirectory))
            {
                Directory.Delete(destinationDirectory, true);
            }

            Directory.Move(backupDirectory, existingDirectory);
            throw;
        }
    }

    private static ArchiveVersion ParseArchiveVersion(string name)
    {
        string safeName = name ?? string.Empty;
        MatchCollection matches = Regex.Matches(
            safeName,
            @"(?ix)
              (?<![A-Za-z0-9])
              (?<label>version|ver\.?|v)
              \s*
              (?<version>\d+(?:[._-]\d+)*)
              (?![A-Za-z0-9])");

        if (matches.Count == 0)
        {
            return new ArchiveVersion(NormalizeArchiveIdentity(safeName), Array.Empty<int>(), hasVersion: false);
        }

        Match match = matches[matches.Count - 1];
        int[] versionParts = Regex
            .Matches(match.Groups["version"].Value, @"\d+")
            .Select(part => int.Parse(part.Value))
            .ToArray();
        string identity = NormalizeArchiveIdentity(safeName.Remove(match.Index, match.Length));

        return new ArchiveVersion(identity, versionParts, hasVersion: true);
    }

    private static string NormalizeArchiveIdentity(string value)
    {
        string normalized = Regex.Replace(value ?? string.Empty, "[\\[\\]\\u3010\\u3011\\uFF08\\uFF09(){}]", " ");
        normalized = Regex.Replace(normalized, @"[\s._-]+", " ").Trim();
        return normalized;
    }

    private sealed class ArchiveVersion
    {
        public ArchiveVersion(string identity, IReadOnlyList<int> parts, bool hasVersion)
        {
            Identity = identity ?? string.Empty;
            Parts = parts ?? Array.Empty<int>();
            HasVersion = hasVersion;
        }

        public string Identity { get; }

        public IReadOnlyList<int> Parts { get; }

        public bool HasVersion { get; }
    }

    private sealed class ArchiveVersionComparer : IComparer<ArchiveVersion>
    {
        public static readonly ArchiveVersionComparer Instance = new();

        public int Compare(ArchiveVersion left, ArchiveVersion right)
        {
            if (left == null && right == null)
            {
                return 0;
            }

            if (left == null)
            {
                return -1;
            }

            if (right == null)
            {
                return 1;
            }

            int maxParts = Math.Max(left.Parts.Count, right.Parts.Count);
            for (int i = 0; i < maxParts; i++)
            {
                int leftPart = i < left.Parts.Count ? left.Parts[i] : 0;
                int rightPart = i < right.Parts.Count ? right.Parts[i] : 0;
                int partComparison = leftPart.CompareTo(rightPart);
                if (partComparison != 0)
                {
                    return partComparison;
                }
            }

            return left.HasVersion.CompareTo(right.HasVersion);
        }
    }
}
