using System.Text;

using SharpClaw.ModuleSDK;

namespace SharpClaw.Modules.ModuleDev.Services;

/// <summary>
/// File I/O scoped to the <c>external-modules/</c> subtree.
/// Validates all paths against traversal and extension blocklists.
/// </summary>
internal sealed class ModuleWorkspaceService
{
    private static readonly HashSet<string> AllowedExtensions =
    [
        ".cs",
        ".csproj",
        ".json",
        ".md",
        ".txt",
        ".yaml",
        ".yml"
    ];

    private readonly object _rootGate = new();
    private string? _externalModulesDir;

    public string ExternalPackagesDirectory => _externalModulesDir
        ?? throw new InvalidOperationException(
            "The host has not supplied the external module directory.");

    public void BindExternalModulesDirectory(string externalModulesDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalModulesDirectory);
        if (!Path.IsPathFullyQualified(externalModulesDirectory))
            throw new ArgumentException(
                "The external module directory must be a fully qualified path.",
                nameof(externalModulesDirectory));

        var canonical = Path.GetFullPath(externalModulesDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        lock (_rootGate)
        {
            if (_externalModulesDir is null)
            {
                _externalModulesDir = canonical;
                return;
            }

            if (!string.Equals(_externalModulesDir, canonical, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The host supplied a different external module directory.");
            }
        }
    }

    /// <summary>
    /// Resolves and validates a module workspace root directory.
    /// </summary>
    public string ResolveModuleDir(string SourceId)
    {
        ValidateModuleId(SourceId);

        var root = ExternalPackagesDirectory;
        var moduleDir = Path.GetFullPath(Path.Combine(root, SourceId));
        ModulePathGuard.EnsureContainedIn(moduleDir, root);

        return moduleDir;
    }

    /// <summary>
    /// Resolves a file path inside a module workspace.
    /// Rejects traversal, absolute paths, null bytes, and reserved names.
    /// </summary>
    public string ResolveFilePath(string SourceId, string relativePath)
    {
        ValidateModuleId(SourceId);
        ValidateRelativePath(relativePath);

        var moduleDir = ResolveModuleDir(SourceId);
        var fullPath = Path.GetFullPath(Path.Combine(moduleDir, relativePath));

        if (!fullPath.StartsWith(moduleDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !fullPath.Equals(moduleDir, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Path '{relativePath}' escapes the module workspace.");
        }

        return fullPath;
    }

    /// <summary>
    /// Writes a file to the module workspace. Creates intermediate directories.
    /// </summary>
    public async Task<(string Path, long BytesWritten)> WriteFileAsync(
        string SourceId, string relativePath, string content, CancellationToken ct = default)
    {
        var fullPath = ValidateFileForWrite(SourceId, relativePath, content);

        var dir = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(dir);

        var bytes = Encoding.UTF8.GetBytes(content);
        await File.WriteAllBytesAsync(fullPath, bytes, ct);

        return (fullPath, bytes.Length);
    }

    /// <summary>
    /// Publishes one complete module workspace after every file passes validation.
    /// </summary>
    public async Task WriteFilesAtomicallyAsync(
        string SourceId,
        IReadOnlyDictionary<string, string> files,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
            throw new ArgumentException("At least one file is required.", nameof(files));

        var moduleDir = ResolveModuleDir(SourceId);
        if (Directory.Exists(moduleDir))
            throw new InvalidOperationException(
                $"Module workspace '{SourceId}' already exists.");

        var validated = files.Select(file => new
        {
            file.Key,
            file.Value,
            FinalPath = ValidateFileForWrite(SourceId, file.Key, file.Value),
        }).ToArray();
        ct.ThrowIfCancellationRequested();

        var stagingDir = ModulePathGuard.EnsureContainedIn(
            Path.Combine(
                ExternalPackagesDirectory,
                $".{SourceId}.{Guid.NewGuid():N}.scaffold"),
            ExternalPackagesDirectory);

        try
        {
            Directory.CreateDirectory(stagingDir);
            foreach (var file in validated)
            {
                ct.ThrowIfCancellationRequested();
                var relativePath = Path.GetRelativePath(moduleDir, file.FinalPath);
                var stagingPath = ModulePathGuard.EnsureContainedIn(
                    Path.GetFullPath(Path.Combine(stagingDir, relativePath)),
                    stagingDir);
                Directory.CreateDirectory(Path.GetDirectoryName(stagingPath)!);
                await File.WriteAllBytesAsync(
                    stagingPath,
                    Encoding.UTF8.GetBytes(file.Value),
                    ct);
            }

            ct.ThrowIfCancellationRequested();
            Directory.Move(stagingDir, moduleDir);
        }
        catch (Exception exception)
        {
            try
            {
                if (Directory.Exists(stagingDir))
                    Directory.Delete(stagingDir, recursive: true);
            }
            catch (Exception cleanupException)
            {
                throw new IOException(
                    "The incomplete scaffold staging directory could not be removed.",
                    new AggregateException(exception, cleanupException));
            }

            throw;
        }
    }

    /// <summary>
    /// Validates a file path and content without changing the workspace.
    /// </summary>
    public string ValidateFileForWrite(string SourceId, string relativePath, string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var fullPath = ResolveFilePath(SourceId, relativePath);
        ValidateExtension(fullPath);
        ValidateContent(relativePath, content);
        ModulePathGuard.EnsureContainedIn(fullPath, ExternalPackagesDirectory);

        return fullPath;
    }

    /// <summary>
    /// Reads a file from the module workspace, optionally truncated.
    /// </summary>
    public async Task<string> ReadFileAsync(
        string SourceId, string relativePath, int maxLines = 500, CancellationToken ct = default)
    {
        var fullPath = ResolveFilePath(SourceId, relativePath);
        ModulePathGuard.EnsureContainedIn(fullPath, ExternalPackagesDirectory);

        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"File not found: {relativePath}", fullPath);

        var lines = await File.ReadAllLinesAsync(fullPath, ct);

        if (lines.Length <= maxLines)
            return string.Join(Environment.NewLine, lines);

        var truncated = lines.Take(maxLines);
        return string.Join(Environment.NewLine, truncated) +
               $"{Environment.NewLine}... (truncated, {lines.Length - maxLines} lines omitted)";
    }

    /// <summary>
    /// Lists the file tree of a module workspace, optionally filtered by glob.
    /// </summary>
    public IReadOnlyList<string> ListFiles(string SourceId, string? includePattern = null)
    {
        var moduleDir = ModulePathGuard.EnsureContainedIn(
            ResolveModuleDir(SourceId),
            ExternalPackagesDirectory);

        if (!Directory.Exists(moduleDir))
            return [];

        var pattern = includePattern ?? "*";
        var searchOption = pattern.Contains("**") ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        // Normalize glob: strip leading **/ for Directory.EnumerateFiles
        var filePattern = pattern
            .Replace("**/", "")
            .Replace("**\\", "");

        if (string.IsNullOrWhiteSpace(filePattern) || filePattern == "**")
            filePattern = "*";

        return Directory.EnumerateFiles(moduleDir, filePattern, searchOption)
            .Select(f => Path.GetRelativePath(moduleDir, f).Replace('\\', '/'))
            .Order()
            .ToList();
    }

    // ── Validation ────────────────────────────────────────────────

    private static void ValidateModuleId(string SourceId)
    {
        ArgumentNullException.ThrowIfNull(SourceId);

        if (!System.Text.RegularExpressions.Regex.IsMatch(SourceId, @"^[a-z][a-z0-9_]{0,39}$"))
            throw new ArgumentException(
                $"Invalid module ID '{SourceId}'. Must match ^[a-z][a-z0-9_]{{0,39}}$.", nameof(SourceId));
    }

    private static void ValidateRelativePath(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("Relative path cannot be empty.", nameof(relativePath));

        if (relativePath.Contains('\0'))
            throw new ArgumentException("Path contains null bytes.", nameof(relativePath));

        if (Path.IsPathRooted(relativePath))
            throw new ArgumentException(
                $"Absolute paths are not allowed: '{relativePath}'.", nameof(relativePath));

        if (relativePath.Contains(".."))
            throw new ArgumentException(
                $"Path traversal (..) is not allowed: '{relativePath}'.", nameof(relativePath));

        // Block reserved Windows device names
        var fileName = Path.GetFileNameWithoutExtension(relativePath).ToUpperInvariant();
        ReadOnlySpan<string> reserved = ["CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

        foreach (var name in reserved)
        {
            if (fileName == name)
                throw new ArgumentException(
                    $"Reserved Windows device name: '{relativePath}'.", nameof(relativePath));
        }
    }

    private static void ValidateExtension(string fullPath)
    {
        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext))
            throw new InvalidOperationException(
                $"Cannot write files with extension '{ext}'. Allowed extensions: {string.Join(", ", AllowedExtensions.Order())}.");
    }

    private static void ValidateContent(string relativePath, string content)
    {
        if (!string.Equals(Path.GetFileName(relativePath), "package.json", StringComparison.OrdinalIgnoreCase))
            return;

        var loaded = PackageManifestLoader.Parse(content, relativePath);
        loaded.Runtime.EnsureDotNetEntryAssembly(loaded.Manifest);
    }
}
