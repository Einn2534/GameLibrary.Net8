using Newtonsoft.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GameLibrary.Net8;

public static class GameSaveBackupService
{
    private const string FilesDirectoryName = "files";
    private const string ManifestFileName = "manifest.json";

    private static readonly HashSet<string> SaveDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "save",
        "saves",
        "savedata",
        "save_data",
        "save-data",
        "save data",
        "savefiles",
        "save_files",
        "save-files",
        "savedgames",
        "saved_games",
        "saved-games",
        "saved games",
        "userdata",
        "user_data",
        "user-data",
        "user data",
        "profile",
        "profiles"
    };

    private static readonly HashSet<string> SaveFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sav",
        ".save",
        ".rpgsave",
        ".rvdata",
        ".rvdata2",
        ".rxdata",
        ".lsd",
        ".sol"
    };

    private static readonly HashSet<string> SaveFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "persistent",
        "global",
        "config.rpgsave",
        "global.rpgsave"
    };

    private static readonly EnumerationOptions RecursiveEnumerationOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    public static GameSaveTransferResult BackupSaves(string installDirectory)
    {
        if (string.IsNullOrWhiteSpace(installDirectory) || !Directory.Exists(installDirectory))
        {
            return new GameSaveTransferResult();
        }

        string installName = new DirectoryInfo(installDirectory).Name;
        List<string> saveDirectories = FindSaveDirectories(installDirectory);
        List<string> saveFiles = FindSaveFiles(installDirectory, saveDirectories);
        if (saveDirectories.Count == 0 && saveFiles.Count == 0)
        {
            return new GameSaveTransferResult();
        }

        string backupDirectory = GetBackupDirectory(installName);
        string tempDirectory = backupDirectory + ".tmp-" + Guid.NewGuid().ToString("N");
        string filesDirectory = Path.Combine(tempDirectory, FilesDirectoryName);
        Directory.CreateDirectory(filesDirectory);

        var relativePaths = new List<string>();
        var copiedPathKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int fileCount = 0;
        long bytesCopied = 0;

        try
        {
            foreach (string saveDirectory in saveDirectories)
            {
                CopyDirectoryForBackup(
                    installDirectory,
                    saveDirectory,
                    filesDirectory,
                    copiedPathKeys,
                    relativePaths,
                    ref fileCount,
                    ref bytesCopied);
            }

            foreach (string saveFile in saveFiles)
            {
                CopyFileForBackup(
                    installDirectory,
                    saveFile,
                    filesDirectory,
                    copiedPathKeys,
                    relativePaths,
                    ref fileCount,
                    ref bytesCopied);
            }

            if (fileCount == 0)
            {
                return new GameSaveTransferResult();
            }

            WriteManifest(tempDirectory, new SaveBackupManifest
            {
                InstallDirectory = installDirectory,
                InstallDirectoryName = installName,
                IdentityKey = BuildIdentityKey(installName),
                BackedUpUtc = DateTime.UtcNow,
                RelativePaths = relativePaths
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            });

            Directory.CreateDirectory(AppSettings.SaveBackupDirectory);
            if (Directory.Exists(backupDirectory))
            {
                ClearReadOnlyAttributes(backupDirectory);
                Directory.Delete(backupDirectory, recursive: true);
            }

            Directory.Move(tempDirectory, backupDirectory);

            return new GameSaveTransferResult
            {
                FileCount = fileCount,
                BytesCopied = bytesCopied,
                BackupDirectory = backupDirectory
            };
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                try
                {
                    ClearReadOnlyAttributes(tempDirectory);
                    Directory.Delete(tempDirectory, recursive: true);
                }
                catch
                {
                }
            }
        }
    }

    public static GameSaveTransferResult RestoreSaves(string installDirectory)
    {
        if (string.IsNullOrWhiteSpace(installDirectory) || !Directory.Exists(installDirectory))
        {
            return new GameSaveTransferResult();
        }

        string backupDirectory = FindBestBackupDirectory(installDirectory);
        if (string.IsNullOrWhiteSpace(backupDirectory))
        {
            return new GameSaveTransferResult();
        }

        string filesDirectory = Path.Combine(backupDirectory, FilesDirectoryName);
        if (!Directory.Exists(filesDirectory))
        {
            return new GameSaveTransferResult();
        }

        int fileCount = 0;
        long bytesCopied = 0;

        foreach (string directory in Directory.EnumerateDirectories(filesDirectory, "*", RecursiveEnumerationOptions))
        {
            string relativePath = Path.GetRelativePath(filesDirectory, directory);
            if (IsSafeRelativePath(relativePath))
            {
                Directory.CreateDirectory(Path.Combine(installDirectory, relativePath));
            }
        }

        foreach (string file in Directory.EnumerateFiles(filesDirectory, "*", RecursiveEnumerationOptions))
        {
            string relativePath = Path.GetRelativePath(filesDirectory, file);
            if (!IsSafeRelativePath(relativePath))
            {
                continue;
            }

            string destinationPath = Path.Combine(installDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));
            ClearReadOnlyAttribute(destinationPath);
            File.Copy(file, destinationPath, overwrite: true);
            fileCount++;
            bytesCopied += new FileInfo(file).Length;
        }

        return new GameSaveTransferResult
        {
            FileCount = fileCount,
            BytesCopied = bytesCopied,
            BackupDirectory = backupDirectory
        };
    }

    private static List<string> FindSaveDirectories(string installDirectory)
    {
        var results = new List<string>();
        foreach (string directory in Directory
            .EnumerateDirectories(installDirectory, "*", RecursiveEnumerationOptions)
            .Where(IsSaveDirectory)
            .OrderBy(path => path.Length))
        {
            if (results.Any(existing => IsPathInsideDirectory(directory, existing)))
            {
                continue;
            }

            results.Add(directory);
        }

        return results;
    }

    private static List<string> FindSaveFiles(string installDirectory, IReadOnlyList<string> saveDirectories)
    {
        return Directory
            .EnumerateFiles(installDirectory, "*", RecursiveEnumerationOptions)
            .Where(IsSaveFile)
            .Where(file => !saveDirectories.Any(directory => IsPathInsideDirectory(file, directory)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsSaveDirectory(string directory)
    {
        string name = new DirectoryInfo(directory).Name;
        return SaveDirectoryNames.Contains(NormalizeSaveName(name));
    }

    private static bool IsSaveFile(string file)
    {
        string fileName = Path.GetFileName(file);
        string extension = Path.GetExtension(file);
        return SaveFileExtensions.Contains(extension) ||
            SaveFileNames.Contains(fileName) ||
            Regex.IsMatch(fileName, @"^(save|slot|file)\d{1,3}\.(dat|bin)$", RegexOptions.IgnoreCase);
    }

    private static void CopyDirectoryForBackup(
        string sourceRoot,
        string sourceDirectory,
        string destinationRoot,
        HashSet<string> copiedPathKeys,
        List<string> relativePaths,
        ref int fileCount,
        ref long bytesCopied)
    {
        string relativeDirectory = Path.GetRelativePath(sourceRoot, sourceDirectory);
        if (!IsSafeRelativePath(relativeDirectory))
        {
            return;
        }

        Directory.CreateDirectory(Path.Combine(destinationRoot, relativeDirectory));

        foreach (string directory in Directory.EnumerateDirectories(sourceDirectory, "*", RecursiveEnumerationOptions))
        {
            string relativePath = Path.GetRelativePath(sourceRoot, directory);
            if (IsSafeRelativePath(relativePath))
            {
                Directory.CreateDirectory(Path.Combine(destinationRoot, relativePath));
            }
        }

        foreach (string file in Directory.EnumerateFiles(sourceDirectory, "*", RecursiveEnumerationOptions))
        {
            CopyFileForBackup(
                sourceRoot,
                file,
                destinationRoot,
                copiedPathKeys,
                relativePaths,
                ref fileCount,
                ref bytesCopied);
        }
    }

    private static void CopyFileForBackup(
        string sourceRoot,
        string sourceFile,
        string destinationRoot,
        HashSet<string> copiedPathKeys,
        List<string> relativePaths,
        ref int fileCount,
        ref long bytesCopied)
    {
        string relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
        if (!IsSafeRelativePath(relativePath))
        {
            return;
        }

        string pathKey = NormalizePathKey(relativePath);
        if (!copiedPathKeys.Add(pathKey))
        {
            return;
        }

        string destinationPath = Path.Combine(destinationRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));
        File.Copy(sourceFile, destinationPath, overwrite: true);
        ClearReadOnlyAttribute(destinationPath);

        fileCount++;
        bytesCopied += new FileInfo(sourceFile).Length;
        relativePaths.Add(relativePath);
    }

    private static string FindBestBackupDirectory(string installDirectory)
    {
        if (string.IsNullOrWhiteSpace(AppSettings.SaveBackupDirectory) ||
            !Directory.Exists(AppSettings.SaveBackupDirectory))
        {
            return null;
        }

        string installName = new DirectoryInfo(installDirectory).Name;
        string exactBackupDirectory = GetBackupDirectory(installName);
        if (Directory.Exists(exactBackupDirectory))
        {
            return exactBackupDirectory;
        }

        string identityKey = BuildIdentityKey(installName);
        return Directory
            .EnumerateDirectories(AppSettings.SaveBackupDirectory, "*", SearchOption.TopDirectoryOnly)
            .Select(directory => new
            {
                Directory = directory,
                Manifest = ReadManifest(directory)
            })
            .Where(item => item.Manifest != null &&
                string.Equals(item.Manifest.IdentityKey, identityKey, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Manifest.BackedUpUtc)
            .Select(item => item.Directory)
            .FirstOrDefault();
    }

    private static void WriteManifest(string backupDirectory, SaveBackupManifest manifest)
    {
        string manifestPath = Path.Combine(backupDirectory, ManifestFileName);
        File.WriteAllText(manifestPath, JsonConvert.SerializeObject(manifest, Formatting.Indented));
    }

    private static SaveBackupManifest ReadManifest(string backupDirectory)
    {
        string manifestPath = Path.Combine(backupDirectory, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            return JsonConvert.DeserializeObject<SaveBackupManifest>(File.ReadAllText(manifestPath));
        }
        catch
        {
            return null;
        }
    }

    private static string GetBackupDirectory(string installName)
    {
        return Path.Combine(AppSettings.SaveBackupDirectory, BuildBackupDirectoryName(installName));
    }

    private static string BuildBackupDirectoryName(string installName)
    {
        string safeName = string.Join("_", (installName ?? string.Empty).Split(Path.GetInvalidFileNameChars()));
        safeName = Regex.Replace(safeName, @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = "game";
        }

        if (safeName.Length > 80)
        {
            safeName = safeName[..80].Trim();
        }

        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(installName ?? string.Empty)))
            .Substring(0, 8)
            .ToLowerInvariant();
        return safeName + "_" + hash;
    }

    private static string BuildIdentityKey(string installName)
    {
        string value = Regex.Replace(
            installName ?? string.Empty,
            @"(?ix)
              (?<![A-Za-z0-9])
              (version|ver\.?|v)
              \s*
              \d+(?:[._-]\d+)*
              (?![A-Za-z0-9])",
            " ");
        value = Regex.Replace(value, "[\\[\\]\\u3010\\u3011\\uFF08\\uFF09(){}]", " ");
        value = Regex.Replace(value, @"[\s._-]+", " ").Trim();
        return string.IsNullOrWhiteSpace(value) ? (installName ?? string.Empty).Trim() : value;
    }

    private static string NormalizeSaveName(string value)
    {
        return Regex.Replace(value ?? string.Empty, @"[\s._-]+", " ").Trim();
    }

    private static bool IsPathInsideDirectory(string path, string directory)
    {
        string target = NormalizePathKey(path);
        string parent = NormalizePathKey(directory);
        return target.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeRelativePath(string relativePath)
    {
        return !string.IsNullOrWhiteSpace(relativePath) &&
            !Path.IsPathRooted(relativePath) &&
            !relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment == "..");
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

    private static void ClearReadOnlyAttributes(string directory)
    {
        foreach (string path in Directory.EnumerateFileSystemEntries(directory, "*", RecursiveEnumerationOptions))
        {
            ClearReadOnlyAttribute(path);
        }
    }

    private static void ClearReadOnlyAttribute(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
        {
            return;
        }

        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }

    private sealed class SaveBackupManifest
    {
        public string InstallDirectory { get; set; }

        public string InstallDirectoryName { get; set; }

        public string IdentityKey { get; set; }

        public DateTime BackedUpUtc { get; set; }

        public List<string> RelativePaths { get; set; } = new();
    }
}

public class GameSaveTransferResult
{
    public int FileCount { get; set; }

    public long BytesCopied { get; set; }

    public string BackupDirectory { get; set; }

    public bool HasFiles => FileCount > 0;
}
