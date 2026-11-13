namespace GitlabCloner.Cli.Sync;

/// <summary>
/// Converts a GitLab "group/subgroup/project" path into a safe local directory path
/// that preserves the same hierarchy.
/// </summary>
public static class PathMapper
{
    private static readonly char[] InvalidCharacters = Path.GetInvalidFileNameChars();

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string ToLocalPath(string rootDirectory, string pathWithNamespace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        var segments = (pathWithNamespace ?? "")
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Select(SanitizeSegment)
            .ToArray();

        if (segments.Length == 0)
        {
            throw new ArgumentException(
                $"Project path is empty or invalid: '{pathWithNamespace}'.", nameof(pathWithNamespace));
        }

        return Path.GetFullPath(Path.Combine([Path.GetFullPath(rootDirectory), .. segments]));
    }

    /// <summary>Makes a single path segment safe for the Windows file system.</summary>
    public static string SanitizeSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            return "_";
        }

        var buffer = segment.Select(c => InvalidCharacters.Contains(c) ? '_' : c).ToArray();
        var sanitized = new string(buffer);

        // Windows does not accept folder names ending in a dot or a space.
        sanitized = sanitized.TrimEnd('.', ' ');

        if (sanitized.Length == 0 || sanitized is "." or "..")
        {
            return "_";
        }

        var withoutExtension = Path.GetFileNameWithoutExtension(sanitized);
        return ReservedNames.Contains(withoutExtension) ? sanitized + "_" : sanitized;
    }
}
