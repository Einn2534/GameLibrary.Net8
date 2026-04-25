using System.Diagnostics;

namespace GameLibrary.Net8;

public class ArchiveImportService
{
    private readonly string sevenZipPath;
    private readonly IReadOnlyList<string> passwords;
    private readonly IReadOnlyList<string> archiveExtensions;

    public ArchiveImportService(string sevenZipPath, IEnumerable<string> passwords, IEnumerable<string> archiveExtensions)
    {
        this.sevenZipPath = sevenZipPath ?? throw new ArgumentNullException(nameof(sevenZipPath));
        this.passwords = (passwords ?? Enumerable.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        this.archiveExtensions = (archiveExtensions ?? Enumerable.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(NormalizeExtension)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> GetArchivePaths(string sourceDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
        {
            return Array.Empty<string>();
        }

        var results = Directory
            .EnumerateFiles(sourceDirectory, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => archiveExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return results;
    }

    public ArchiveImportResult ImportArchive(string archivePath, string libraryDirectory)
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
        string installDirectory = Path.Combine(libraryDirectory, archiveName);

        if (Directory.Exists(installDirectory))
        {
            return new ArchiveImportResult
            {
                Success = true,
                Skipped = true,
                Message = UiText.Get("Archive.StatusSkippedAlreadyExists"),
                InstallDirectory = installDirectory
            };
        }

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

            string normalizedPath = NormalizeExtractedContent(tempRoot, archiveName);
            CopyDirectory(normalizedPath, installDirectory);

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
        process.WaitForExit();
        return process.ExitCode;
    }

    private static string NormalizeExtractedContent(string tempRoot, string archiveName)
    {
        string[] files = Directory.GetFiles(tempRoot);
        string[] directories = Directory.GetDirectories(tempRoot);

        if (files.Length == 0 && directories.Length == 1)
        {
            return directories[0];
        }

        string normalizedDirectory = Path.Combine(Path.GetDirectoryName(tempRoot), archiveName + "_normalized");
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

    private static string NormalizeExtension(string value)
    {
        return value.StartsWith(".") ? value : "." + value;
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
}
