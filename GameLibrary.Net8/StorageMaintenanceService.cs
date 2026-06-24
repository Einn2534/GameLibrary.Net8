using System.Diagnostics;
using System.Text.RegularExpressions;
using static GameLibrary.Net8.DownloadFileInspector;
using static GameLibrary.Net8.FileExtensionMatcher;

namespace GameLibrary.Net8;

public class StorageMaintenanceService
{
    private const long BytesPerGb = 1024L * 1024L * 1024L;

    private readonly ArchiveImportService archiveImportService;

    public StorageMaintenanceService(ArchiveImportService archiveImportService)
    {
        this.archiveImportService = archiveImportService ?? throw new ArgumentNullException(nameof(archiveImportService));
    }

    public IReadOnlyList<StoredArchiveItem> LoadStoredArchives()
    {
        Directory.CreateDirectory(AppSettings.ArchiveStorageDirectory);
        return archiveImportService
            .GetArchivePaths(AppSettings.ArchiveStorageDirectory)
            .Select(path => new StoredArchiveItem(path))
            .ToList();
    }

    public ArchiveImportResult RestoreArchive(string storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath) || !File.Exists(storedPath))
        {
            throw new FileNotFoundException("Stored archive was not found.", storedPath);
        }

        if (archiveImportService.IsMediaPath(storedPath))
        {
            return RestoreStoredMedia(storedPath);
        }

        return archiveImportService.ImportArchive(
            storedPath,
            AppSettings.GamesDirectory,
            AppSettings.VideoLibraryDirectory);
    }

    public StorageActionResult DeleteExpandedInstall(GameInfo game)
    {
        string installDirectory = ValidateExpandedInstall(game);
        long bytes = GetDirectorySize(installDirectory);
        GameSaveTransferResult saveBackupResult = GameSaveBackupService.BackupSaves(installDirectory);

        ClearReadOnlyAttributes(installDirectory);
        Directory.Delete(installDirectory, recursive: true);

        long bytesFreed = Math.Max(0, bytes - saveBackupResult.BytesCopied);
        return new StorageActionResult
        {
            Success = true,
            ItemCount = 1,
            BytesChanged = bytesFreed,
            Message = BuildDeleteExpandedInstallMessage(game, saveBackupResult),
            Paths = BuildDeleteExpandedInstallPaths(installDirectory, saveBackupResult)
        };
    }

    public async Task<StorageActionResult> CompressExpandedInstallAsync(GameInfo game, CancellationToken cancellationToken)
    {
        string installDirectory = ValidateExpandedInstall(game);
        long beforeBytes = GetDirectorySize(installDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = "compact.exe",
            Arguments = "/c /s /i /exe:lzx *",
            WorkingDirectory = installDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using Process process = Process.Start(startInfo);
        if (process == null)
        {
            throw new InvalidOperationException("compact.exe could not be started.");
        }

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        string error = await standardError;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? "compact.exe failed with exit code " + process.ExitCode + "."
                    : error.Trim());
        }

        await standardOutput;
        long afterBytes = GetDirectorySize(installDirectory);
        long savedBytes = Math.Max(0, beforeBytes - afterBytes);
        return new StorageActionResult
        {
            Success = true,
            ItemCount = 1,
            BytesChanged = savedBytes,
            Message = "Compressed install: " + game.Name + " (saved " + FormatSize(savedBytes) + ")",
            Paths = new List<string> { installDirectory }
        };
    }

    public StorageActionResult PruneExpandedLibraryIfNeeded(IReadOnlyList<GameInfo> games)
    {
        long limitBytes = AppSettings.ExpandedLibrarySizeLimitGb <= 0
            ? 0
            : AppSettings.ExpandedLibrarySizeLimitGb * BytesPerGb;
        if (limitBytes <= 0)
        {
            return new StorageActionResult
            {
                Success = true,
                Message = "Expanded library pruning is disabled."
            };
        }

        List<InstallRecord> installs = BuildInstallRecords(games, includeSizes: true);
        long totalBytes = installs.Sum(record => record.SizeBytes);
        if (totalBytes <= limitBytes)
        {
            return new StorageActionResult
            {
                Success = true,
                Message = "Expanded library is within limit: " + FormatSize(totalBytes) + " / " + FormatSize(limitBytes)
            };
        }

        HashSet<string> restorableKeys = BuildStoredArchiveRecords()
            .Where(record => !record.IsMedia)
            .Select(record => record.NameInfo.Key)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var deletedPaths = new List<string>();
        long deletedBytes = 0;

        foreach (InstallRecord install in installs
            .Where(record => restorableKeys.Contains(record.NameInfo.Key))
            .OrderBy(record => record.ModifiedUtc)
            .ThenBy(record => record.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (totalBytes <= limitBytes)
            {
                break;
            }

            try
            {
                DeleteExpandedInstall(install.Game);
                deletedPaths.Add(install.Path);
                deletedBytes += install.SizeBytes;
                totalBytes -= install.SizeBytes;
            }
            catch
            {
                // A locked or protected install should not stop cleanup of other restorable installs.
            }
        }

        bool withinLimit = totalBytes <= limitBytes;
        return new StorageActionResult
        {
            Success = withinLimit,
            ItemCount = deletedPaths.Count,
            BytesChanged = deletedBytes,
            Paths = deletedPaths,
            Message = withinLimit
                ? "Pruned " + deletedPaths.Count + " install(s), freed " + FormatSize(deletedBytes) + "."
                : "Expanded library is still over limit; no more restorable installs could be pruned."
        };
    }

    public string BuildExpandedLibrarySummary(IReadOnlyList<GameInfo> games)
    {
        List<InstallRecord> installs = BuildInstallRecords(games, includeSizes: true);
        long totalBytes = installs.Sum(record => record.SizeBytes);
        long limitBytes = AppSettings.ExpandedLibrarySizeLimitGb <= 0
            ? 0
            : AppSettings.ExpandedLibrarySizeLimitGb * BytesPerGb;
        string limitText = limitBytes <= 0 ? "no limit" : FormatSize(limitBytes);
        int restorableCount = CountRestorableInstalls(installs);
        return "Expanded installs: " + FormatSize(totalBytes) + " / " + limitText +
            " (" + restorableCount + " restorable)";
    }

    public IReadOnlyList<StorageFindingItem> DetectFindings(IReadOnlyList<GameInfo> games)
    {
        var findings = new List<StorageFindingItem>();
        List<StoredFileRecord> archives = BuildStoredArchiveRecords();
        List<InstallRecord> installs = BuildInstallRecords(games, includeSizes: false);
        List<StoredFileRecord> videos = BuildVideoRecords();

        AddVersionFindings(findings, archives.Where(record => !record.IsMedia).ToList(), "Stored archive");
        AddVersionFindings(findings, installs, "Expanded install");
        AddVideoSidecarFindings(findings, archives, installs, videos);

        return findings
            .OrderBy(finding => finding.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(finding => finding.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string FormatSize(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        double value = Math.Max(0, bytes);
        int index = 0;

        while (value >= 1024 && index < suffixes.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return value.ToString(index == 0 ? "0" : "0.0") + " " + suffixes[index];
    }

    private ArchiveImportResult RestoreStoredMedia(string storedPath)
    {
        Directory.CreateDirectory(AppSettings.VideoLibraryDirectory);
        string destinationPath = GetUniqueFilePath(AppSettings.VideoLibraryDirectory, Path.GetFileName(storedPath));
        File.Copy(storedPath, destinationPath, overwrite: false);

        return new ArchiveImportResult
        {
            Success = true,
            Message = "Restored media to Videos",
            InstallDirectory = AppSettings.VideoLibraryDirectory,
            MediaPath = destinationPath,
            MediaPaths = new List<string> { destinationPath }
        };
    }

    private static string BuildDeleteExpandedInstallMessage(GameInfo game, GameSaveTransferResult saveBackupResult)
    {
        string message = "Deleted expanded install: " + game.Name;
        if (saveBackupResult?.HasFiles == true)
        {
            message += " (backed up " + saveBackupResult.FileCount + " save file(s))";
        }

        return message;
    }

    private static List<string> BuildDeleteExpandedInstallPaths(
        string installDirectory,
        GameSaveTransferResult saveBackupResult)
    {
        var paths = new List<string> { installDirectory };
        if (!string.IsNullOrWhiteSpace(saveBackupResult?.BackupDirectory))
        {
            paths.Add(saveBackupResult.BackupDirectory);
        }

        return paths;
    }

    private List<StoredFileRecord> BuildStoredArchiveRecords()
    {
        if (string.IsNullOrWhiteSpace(AppSettings.ArchiveStorageDirectory) ||
            !Directory.Exists(AppSettings.ArchiveStorageDirectory))
        {
            return new List<StoredFileRecord>();
        }

        return archiveImportService
            .GetArchivePaths(AppSettings.ArchiveStorageDirectory)
            .Select(path => new StoredFileRecord(
                path,
                ArchiveNameInfo.Parse(Path.GetFileNameWithoutExtension(path)),
                new FileInfo(path).Length,
                archiveImportService.IsMediaPath(path)))
            .ToList();
    }

    private List<StoredFileRecord> BuildVideoRecords()
    {
        if (string.IsNullOrWhiteSpace(AppSettings.VideoLibraryDirectory) ||
            !Directory.Exists(AppSettings.VideoLibraryDirectory))
        {
            return new List<StoredFileRecord>();
        }

        HashSet<string> mediaExtensions = ToExtensionSet(AppSettings.MediaExtensions);

        return Directory
            .EnumerateFiles(AppSettings.VideoLibraryDirectory, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => mediaExtensions.Contains(Path.GetExtension(path)))
            .Select(path => new StoredFileRecord(
                path,
                ArchiveNameInfo.Parse(Path.GetFileNameWithoutExtension(path)),
                new FileInfo(path).Length,
                isMedia: true))
            .ToList();
    }

    private List<InstallRecord> BuildInstallRecords(IReadOnlyList<GameInfo> games, bool includeSizes)
    {
        return (games ?? Array.Empty<GameInfo>())
            .Where(game => game != null && !game.IsVideo && IsSafeExpandedInstall(game.InstallDirectory))
            .GroupBy(game => NormalizePathKey(game.InstallDirectory), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(game => new InstallRecord(
                game,
                ArchiveNameInfo.Parse(string.IsNullOrWhiteSpace(game.Name)
                    ? new DirectoryInfo(game.InstallDirectory).Name
                    : game.Name),
                includeSizes ? GetDirectorySize(game.InstallDirectory) : 0,
                GetDirectoryModifiedUtc(game.InstallDirectory)))
            .ToList();
    }

    private int CountRestorableInstalls(IEnumerable<InstallRecord> installs)
    {
        HashSet<string> restorableKeys = BuildStoredArchiveRecords()
            .Where(record => !record.IsMedia)
            .Select(record => record.NameInfo.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return installs.Count(record => restorableKeys.Contains(record.NameInfo.Key));
    }

    private static void AddVersionFindings<T>(
        List<StorageFindingItem> findings,
        IReadOnlyList<T> records,
        string sourceType)
        where T : IStorageRecord
    {
        foreach (var group in records
            .Where(record => !string.IsNullOrWhiteSpace(record.NameInfo.Key))
            .GroupBy(record => record.NameInfo.Key, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1))
        {
            T latest = group
                .OrderByDescending(record => record.NameInfo, ArchiveNameInfoComparer.Instance)
                .First();

            foreach (T record in group.Where(record => record.NameInfo.HasVersion &&
                ArchiveNameInfoComparer.Instance.Compare(record.NameInfo, latest.NameInfo) < 0))
            {
                findings.Add(new StorageFindingItem(
                    "Old version",
                    record.Name,
                    sourceType + " older than " + latest.Name,
                    record.Path));
            }

            foreach (var duplicateGroup in group
                .GroupBy(record => record.NameInfo.VersionSignature, StringComparer.OrdinalIgnoreCase)
                .Where(duplicateGroup => duplicateGroup.Count() > 1))
            {
                foreach (T record in duplicateGroup.Skip(1))
                {
                    findings.Add(new StorageFindingItem(
                        "Duplicate version",
                        record.Name,
                        sourceType + " duplicates " + duplicateGroup.First().Name,
                        record.Path));
                }
            }
        }
    }

    private static void AddVideoSidecarFindings(
        List<StorageFindingItem> findings,
        IReadOnlyList<StoredFileRecord> storedFiles,
        IReadOnlyList<InstallRecord> installs,
        IReadOnlyList<StoredFileRecord> videos)
    {
        HashSet<string> gameKeys = installs
            .Select(record => record.NameInfo.Key)
            .Concat(storedFiles.Where(record => !record.IsMedia).Select(record => record.NameInfo.Key))
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (StoredFileRecord video in videos.Concat(storedFiles.Where(record => record.IsMedia)))
        {
            if (gameKeys.Contains(video.NameInfo.Key) || LooksLikeVideoSidecar(video.Name))
            {
                findings.Add(new StorageFindingItem(
                    "Video sidecar",
                    video.Name,
                    "Media file appears to belong beside a game/archive.",
                    video.Path));
            }
        }

        foreach (StoredFileRecord archive in storedFiles.Where(record => !record.IsMedia && LooksLikePatchOrSidecar(record.Name)))
        {
            findings.Add(new StorageFindingItem(
                "Patch or extra",
                archive.Name,
                "Archive name looks like an append/patch/extra package.",
                archive.Path));
        }
    }

    private static bool LooksLikeVideoSidecar(string value)
    {
        return Regex.IsMatch(value ?? string.Empty, "(movie|video|animation|scene|bonus)", RegexOptions.IgnoreCase);
    }

    private static bool LooksLikePatchOrSidecar(string value)
    {
        return Regex.IsMatch(value ?? string.Empty, "(append|patch|update|extra|bonus|movie|video)", RegexOptions.IgnoreCase);
    }

    private string ValidateExpandedInstall(GameInfo game)
    {
        if (game == null || game.IsVideo)
        {
            throw new InvalidOperationException("Only expanded game installs can be maintained.");
        }

        if (!IsSafeExpandedInstall(game.InstallDirectory))
        {
            throw new InvalidOperationException("Install directory is outside the library root or cannot be maintained.");
        }

        return game.InstallDirectory;
    }

    private static bool IsSafeExpandedInstall(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return false;
        }

        string root = NormalizePathKey(AppSettings.GamesDirectory);
        string target = NormalizePathKey(path);
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        return !string.Equals(root, target, StringComparison.OrdinalIgnoreCase) &&
            target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static long GetDirectorySize(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return 0;
        }

        long total = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = 0
        };

        foreach (string file in Directory.EnumerateFiles(directory, "*", options))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return total;
    }

    private static DateTime GetDirectoryModifiedUtc(string directory)
    {
        try
        {
            return Directory.GetLastWriteTimeUtc(directory);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static void ClearReadOnlyAttributes(string directory)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = 0
        };

        foreach (string path in Directory.EnumerateFileSystemEntries(directory, "*", options))
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                }
            }
            catch
            {
            }
        }
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

    private static string NormalizePathKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private interface IStorageRecord
    {
        string Name { get; }

        string Path { get; }

        ArchiveNameInfo NameInfo { get; }
    }

    private sealed class StoredFileRecord : IStorageRecord
    {
        public StoredFileRecord(string path, ArchiveNameInfo nameInfo, long sizeBytes, bool isMedia)
        {
            Path = path;
            Name = System.IO.Path.GetFileName(path);
            NameInfo = nameInfo;
            SizeBytes = sizeBytes;
            IsMedia = isMedia;
        }

        public string Name { get; }

        public string Path { get; }

        public ArchiveNameInfo NameInfo { get; }

        public long SizeBytes { get; }

        public bool IsMedia { get; }
    }

    private sealed class InstallRecord : IStorageRecord
    {
        public InstallRecord(GameInfo game, ArchiveNameInfo nameInfo, long sizeBytes, DateTime modifiedUtc)
        {
            Game = game;
            Path = game.InstallDirectory;
            Name = game.Name;
            NameInfo = nameInfo;
            SizeBytes = sizeBytes;
            ModifiedUtc = modifiedUtc;
        }

        public GameInfo Game { get; }

        public string Name { get; }

        public string Path { get; }

        public ArchiveNameInfo NameInfo { get; }

        public long SizeBytes { get; }

        public DateTime ModifiedUtc { get; }
    }

    private sealed class ArchiveNameInfo
    {
        public ArchiveNameInfo(string identity, string productCode, IReadOnlyList<int> versionParts, bool hasVersion)
        {
            Identity = identity ?? string.Empty;
            ProductCode = productCode ?? string.Empty;
            VersionParts = versionParts ?? Array.Empty<int>();
            HasVersion = hasVersion;
        }

        public string Identity { get; }

        public string ProductCode { get; }

        public IReadOnlyList<int> VersionParts { get; }

        public bool HasVersion { get; }

        public string Key => !string.IsNullOrWhiteSpace(ProductCode) ? ProductCode : Identity;

        public string VersionSignature => HasVersion
            ? string.Join(".", VersionParts)
            : "unversioned";

        public static ArchiveNameInfo Parse(string name)
        {
            string safeName = name ?? string.Empty;
            string productCode = ExtractProductCode(safeName) ?? string.Empty;
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
                return new ArchiveNameInfo(NormalizeIdentity(safeName), productCode, Array.Empty<int>(), hasVersion: false);
            }

            Match match = matches[matches.Count - 1];
            int[] versionParts = Regex
                .Matches(match.Groups["version"].Value, @"\d+")
                .Select(part => int.Parse(part.Value))
                .ToArray();
            string identity = NormalizeIdentity(safeName.Remove(match.Index, match.Length));
            return new ArchiveNameInfo(identity, productCode, versionParts, hasVersion: true);
        }

        private static string NormalizeIdentity(string value)
        {
            string normalized = Regex.Replace(value ?? string.Empty, "[\\[\\]\\u3010\\u3011\\uFF08\\uFF09(){}]", " ");
            normalized = Regex.Replace(normalized, @"[\s._-]+", " ").Trim();
            return normalized;
        }

    }

    private sealed class ArchiveNameInfoComparer : IComparer<ArchiveNameInfo>
    {
        public static readonly ArchiveNameInfoComparer Instance = new();

        public int Compare(ArchiveNameInfo left, ArchiveNameInfo right)
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

            int maxParts = Math.Max(left.VersionParts.Count, right.VersionParts.Count);
            for (int i = 0; i < maxParts; i++)
            {
                int leftPart = i < left.VersionParts.Count ? left.VersionParts[i] : 0;
                int rightPart = i < right.VersionParts.Count ? right.VersionParts[i] : 0;
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
