namespace GitlabCloner.Cli.Sync;

public enum RepositoryStatus
{
    /// <summary>The repository did not exist locally and was cloned.</summary>
    Cloned,

    /// <summary>The repository existed; at least one branch was updated or added.</summary>
    Updated,

    /// <summary>The repository existed and nothing changed.</summary>
    UpToDate,

    /// <summary>Intentionally skipped (empty repository, dry-run, archived, etc.).</summary>
    Skipped,

    /// <summary>The operation ended with an error.</summary>
    Failed,
}

public sealed record RepositoryOutcome(
    string ProjectPath,
    string LocalPath,
    RepositoryStatus Status,
    string? Detail = null,
    IReadOnlyList<string>? Warnings = null,
    /// <summary>
    /// The git data was downloaded completely, but some files could not be written to
    /// the working tree because Windows cannot represent them. Not an error; reported separately.
    /// </summary>
    bool IncompleteWorktree = false)
{
    public IReadOnlyList<string> WarningList => Warnings ?? [];
}
