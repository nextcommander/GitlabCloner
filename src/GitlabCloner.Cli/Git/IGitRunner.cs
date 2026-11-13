namespace GitlabCloner.Cli.Git;

public interface IGitRunner
{
    /// <param name="workingDirectory">Directory git runs in. For a clone, the target's parent directory.</param>
    /// <param name="arguments">Arguments passed to git (excluding the word "git").</param>
    /// <param name="authenticated">When true, the GitLab token is added as a per-command HTTP header.</param>
    Task<GitResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        bool authenticated,
        CancellationToken cancellationToken);
}
