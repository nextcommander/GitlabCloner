using GitlabCloner.Cli.Configuration;
using GitlabCloner.Cli.Git;
using GitlabCloner.Cli.GitLab;
using GitlabCloner.Cli.Infrastructure;

namespace GitlabCloner.Cli.Sync;

/// <summary>
/// Clones a single GitLab project locally or updates its existing copy.
/// All file-system decisions live here; git calls go through <see cref="IGitRunner"/>.
/// </summary>
public sealed class RepositorySynchronizer(IGitRunner git, SyncOptions options, ILog log)
{
    private const string RemoteName = "origin";

    /// <summary>Marker set on repositories whose working tree is incomplete due to Windows restrictions.</summary>
    private const string IncompleteWorktreeFlag = "gitlab-cloner.incompleteWorktree";

    public async Task<RepositoryOutcome> SyncAsync(GitLabProject project, CancellationToken cancellationToken)
    {
        var localPath = PathMapper.ToLocalPath(options.ResolvedTargetDirectory(), project.PathWithNamespace);

        if (string.IsNullOrWhiteSpace(project.HttpUrlToRepo))
        {
            return new RepositoryOutcome(project.PathWithNamespace, localPath, RepositoryStatus.Skipped,
                "No HTTP clone URL.");
        }

        if (project.EmptyRepo)
        {
            return new RepositoryOutcome(project.PathWithNamespace, localPath, RepositoryStatus.Skipped,
                "Repository is empty (no commits).");
        }

        var alreadyCloned = Directory.Exists(Path.Combine(localPath, ".git"));

        if (options.DryRun)
        {
            return new RepositoryOutcome(project.PathWithNamespace, localPath, RepositoryStatus.Skipped,
                alreadyCloned ? "dry-run: would update." : "dry-run: would clone.");
        }

        try
        {
            return alreadyCloned
                ? await UpdateAsync(project, localPath, cancellationToken)
                : await CloneAsync(project, localPath, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (GitNotAvailableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new RepositoryOutcome(project.PathWithNamespace, localPath, RepositoryStatus.Failed, ex.Message);
        }
    }

    private async Task<RepositoryOutcome> CloneAsync(
        GitLabProject project, string localPath, CancellationToken cancellationToken)
    {
        if (Directory.Exists(localPath) && Directory.EnumerateFileSystemEntries(localPath).Any())
        {
            return new RepositoryOutcome(project.PathWithNamespace, localPath, RepositoryStatus.Failed,
                "Target directory is not empty but is not a git repository. Clean it up manually.");
        }

        var parent = Path.GetDirectoryName(localPath)
                     ?? throw new InvalidOperationException($"Could not determine parent directory: {localPath}");
        Directory.CreateDirectory(parent);

        log.Info($"Cloning: {project.PathWithNamespace}");

        var clone = await git.RunAsync(parent,
            ["clone", "--no-single-branch", "--origin", RemoteName, project.HttpUrlToRepo!, localPath],
            authenticated: true, cancellationToken);

        var repositoryExists = Directory.Exists(Path.Combine(localPath, ".git"));
        var checkoutOnlyFailure = !clone.Success &&
                                  repositoryExists &&
                                  CheckoutDiagnostics.IsCheckoutOnlyFailure(clone);

        if (!clone.Success && !checkoutOnlyFailure)
        {
            return new RepositoryOutcome(project.PathWithNamespace, localPath, RepositoryStatus.Failed,
                "Clone failed: " + clone.FailureText());
        }

        var (created, warnings) = await CreateMissingLocalBranchesAsync(localPath, cancellationToken);

        // The clone succeeded but checkout stopped halfway: the git data is complete,
        // only the working tree could not be written. Recover the files that can be written.
        if (checkoutOnlyFailure)
        {
            var invalidPaths = CheckoutDiagnostics.InvalidPaths(clone);
            await RecoverIncompleteWorktreeAsync(localPath, invalidPaths, warnings, cancellationToken);

            if (invalidPaths.Count == 0)
            {
                // git did not report the path by name; pass on the raw error so the cause is visible.
                warnings.Add("Working tree could not be completed: " + clone.FailureText());
            }
        }

        var incomplete = checkoutOnlyFailure;

        return new RepositoryOutcome(project.PathWithNamespace, localPath, RepositoryStatus.Cloned,
            $"{created + 1} branches ready locally." + (incomplete ? " Working tree incomplete." : ""),
            warnings, incomplete);
    }

    /// <summary>
    /// Recovers a repository whose checkout stopped halfway: writes every file that can
    /// be written to disk and marks the repository so that later syncs behave correctly.
    /// </summary>
    private async Task RecoverIncompleteWorktreeAsync(
        string localPath,
        IReadOnlyList<string> invalidPaths,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        // Still fails on the invalid path, but writes the remaining files to disk.
        await git.RunAsync(localPath, ["restore", "--source=HEAD", ":/"], authenticated: false, cancellationToken);

        await git.RunAsync(localPath, ["config", IncompleteWorktreeFlag, "true"],
            authenticated: false, cancellationToken);

        foreach (var path in invalidPaths)
        {
            warnings.Add($"'{path}' cannot be created on Windows; the file was not written to disk " +
                         "(git history and all branches were downloaded completely).");
        }
    }

    private async Task<bool> HasIncompleteWorktreeFlagAsync(string localPath, CancellationToken cancellationToken)
    {
        var result = await git.RunAsync(localPath, ["config", "--get", IncompleteWorktreeFlag],
            authenticated: false, cancellationToken);

        return result.Success && result.StandardOutput.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<RepositoryOutcome> UpdateAsync(
        GitLabProject project, string localPath, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();

        await EnsureRemoteUrlAsync(localPath, project, warnings, cancellationToken);

        log.Info($"Updating: {project.PathWithNamespace}");

        var fetch = await git.RunAsync(localPath,
            ["fetch", "--prune", "--prune-tags", "--tags", RemoteName],
            authenticated: true, cancellationToken);

        if (!fetch.Success)
        {
            return new RepositoryOutcome(project.PathWithNamespace, localPath, RepositoryStatus.Failed,
                "fetch failed: " + fetch.FailureText(), warnings);
        }

        var alreadyIncomplete = await HasIncompleteWorktreeFlagAsync(localPath, cancellationToken);
        var update = await UpdateBranchesAsync(localPath, warnings, alreadyIncomplete, cancellationToken);

        var incomplete = alreadyIncomplete || update.InvalidPaths.Count > 0;

        if (update.InvalidPaths.Count > 0)
        {
            // An invalid path appeared for the first time in this sync: mark and recover.
            await RecoverIncompleteWorktreeAsync(localPath, update.InvalidPaths, warnings, cancellationToken);
        }
        else if (alreadyIncomplete)
        {
            if (update.Changed > 0)
            {
                // Refs moved forward; refresh the files that can be written.
                await git.RunAsync(localPath, ["restore", "--source=HEAD", ":/"],
                    authenticated: false, cancellationToken);
            }

            warnings.Add("Working tree incomplete: some paths cannot be created on Windows " +
                         "(git history and all branches are up to date).");
        }

        var detail = update.Changed switch
        {
            > 0 => $"{update.Changed} branches updated.",
            // No changes may mean genuinely up to date, or a protected (diverged / dirty)
            // branch; distinguish the two.
            _ when warnings.Count > 0 => "No changes applied; some branches are protected.",
            _ => "Already up to date.",
        };

        return new RepositoryOutcome(project.PathWithNamespace, localPath,
            update.Changed > 0 ? RepositoryStatus.Updated : RepositoryStatus.UpToDate,
            detail,
            warnings,
            incomplete);
    }

    private sealed record BranchUpdateResult(int Changed, IReadOnlyList<string> InvalidPaths);

    /// <summary>Fixes the origin URL if the GitLab address changed (host migration, etc.).</summary>
    private async Task EnsureRemoteUrlAsync(
        string localPath, GitLabProject project, List<string> warnings, CancellationToken cancellationToken)
    {
        var current = await git.RunAsync(localPath, ["remote", "get-url", RemoteName],
            authenticated: false, cancellationToken);

        if (!current.Success)
        {
            var add = await git.RunAsync(localPath, ["remote", "add", RemoteName, project.HttpUrlToRepo!],
                authenticated: false, cancellationToken);

            if (!add.Success)
            {
                warnings.Add("Could not add the origin remote: " + add.FailureText());
            }

            return;
        }

        var existing = current.StandardOutput.Trim();
        if (!string.Equals(existing, project.HttpUrlToRepo, StringComparison.OrdinalIgnoreCase))
        {
            log.Debug($"Updating origin URL: {existing} → {project.HttpUrlToRepo}");
            await git.RunAsync(localPath, ["remote", "set-url", RemoteName, project.HttpUrlToRepo!],
                authenticated: false, cancellationToken);
        }
    }

    /// <summary>After cloning, creates tracking branches for remote branches that have no local counterpart yet.</summary>
    private async Task<(int Created, List<string> Warnings)> CreateMissingLocalBranchesAsync(
        string localPath, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var remoteBranches = await ListRemoteBranchesAsync(localPath, cancellationToken);
        var localBranches = await ListLocalBranchesAsync(localPath, cancellationToken);

        var created = 0;
        foreach (var branch in remoteBranches.Where(b => !localBranches.Contains(b)))
        {
            var result = await git.RunAsync(localPath,
                ["branch", "--track", branch, $"{RemoteName}/{branch}"],
                authenticated: false, cancellationToken);

            if (result.Success)
            {
                created++;
            }
            else
            {
                warnings.Add($"Could not create branch '{branch}': {result.FailureText()}");
            }
        }

        return (created, warnings);
    }

    /// <summary>
    /// After a fetch, aligns local branches with the remote. Only branches that can be
    /// fast-forwarded are touched; diverged ones are left alone with a warning.
    /// </summary>
    private async Task<BranchUpdateResult> UpdateBranchesAsync(
        string localPath, List<string> warnings, bool incompleteWorktree, CancellationToken cancellationToken)
    {
        var remoteBranches = await ListRemoteBranchesAsync(localPath, cancellationToken);
        var localBranches = await ListLocalBranchesAsync(localPath, cancellationToken);
        var currentBranch = await GetCurrentBranchAsync(localPath, cancellationToken);

        // If the working tree is already incomplete, looking "dirty" is structural, not
        // the user's work; applying dirty-tree protection to this repository is pointless.
        var workingTreeDirty = !incompleteWorktree &&
                               await IsWorkingTreeDirtyAsync(localPath, cancellationToken);

        var changed = 0;
        var invalidPaths = new List<string>();

        foreach (var branch in remoteBranches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remoteRef = $"{RemoteName}/{branch}";

            if (!localBranches.Contains(branch))
            {
                var create = await git.RunAsync(localPath, ["branch", "--track", branch, remoteRef],
                    authenticated: false, cancellationToken);

                if (create.Success)
                {
                    changed++;
                }
                else
                {
                    warnings.Add($"Could not create branch '{branch}': {create.FailureText()}");
                }

                continue;
            }

            if (await IsSameCommitAsync(localPath, branch, remoteRef, cancellationToken))
            {
                continue;
            }

            if (!await CanFastForwardAsync(localPath, branch, remoteRef, cancellationToken))
            {
                warnings.Add($"'{branch}' has diverged (local commits exist); left untouched.");
                continue;
            }

            if (branch == currentBranch)
            {
                if (workingTreeDirty)
                {
                    warnings.Add($"'{branch}' is checked out and the working tree is dirty; not updated.");
                    continue;
                }

                if (incompleteWorktree)
                {
                    // merge touches the working tree and would fail again on the invalid path.
                    // update-ref only advances the ref; files are refreshed afterwards with restore.
                    var advance = await git.RunAsync(localPath,
                        ["update-ref", $"refs/heads/{branch}", remoteRef],
                        authenticated: false, cancellationToken);

                    if (advance.Success)
                    {
                        changed++;
                    }
                    else
                    {
                        warnings.Add($"Could not advance '{branch}': {advance.FailureText()}");
                    }

                    continue;
                }

                var merge = await git.RunAsync(localPath, ["merge", "--ff-only", remoteRef],
                    authenticated: false, cancellationToken);

                if (merge.Success)
                {
                    changed++;
                }
                else if (CheckoutDiagnostics.InvalidPaths(merge) is { Count: > 0 } blockedPaths)
                {
                    // A file added on the remote cannot be represented on Windows: advance the
                    // ref anyway; the caller will mark and recover the repository.
                    invalidPaths.AddRange(blockedPaths);

                    var advance = await git.RunAsync(localPath,
                        ["update-ref", $"refs/heads/{branch}", remoteRef],
                        authenticated: false, cancellationToken);

                    if (advance.Success)
                    {
                        changed++;
                    }
                }
                else
                {
                    warnings.Add($"Could not fast-forward '{branch}': {merge.FailureText()}");
                }

                continue;
            }

            var force = await git.RunAsync(localPath, ["branch", "--force", branch, remoteRef],
                authenticated: false, cancellationToken);

            if (force.Success)
            {
                changed++;
            }
            else
            {
                warnings.Add($"Could not update '{branch}': {force.FailureText()}");
            }
        }

        foreach (var orphan in localBranches.Where(b => !remoteBranches.Contains(b)))
        {
            warnings.Add($"'{orphan}' does not exist on the remote (possibly deleted); kept locally.");
        }

        return new BranchUpdateResult(changed, invalidPaths.Distinct(StringComparer.Ordinal).ToList());
    }

    private async Task<HashSet<string>> ListRemoteBranchesAsync(string localPath, CancellationToken cancellationToken)
    {
        var result = await git.RunAsync(localPath,
            ["for-each-ref", "--format=%(refname:short)", $"refs/remotes/{RemoteName}"],
            authenticated: false, cancellationToken);

        if (!result.Success)
        {
            throw new InvalidOperationException("Could not list remote branches: " + result.FailureText());
        }

        var prefix = RemoteName + "/";
        return result.OutputLines()
            .Where(line => line.StartsWith(prefix, StringComparison.Ordinal))
            .Select(line => line[prefix.Length..])
            .Where(name => name != "HEAD" && name.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
    }

    private async Task<HashSet<string>> ListLocalBranchesAsync(string localPath, CancellationToken cancellationToken)
    {
        var result = await git.RunAsync(localPath,
            ["for-each-ref", "--format=%(refname:short)", "refs/heads"],
            authenticated: false, cancellationToken);

        if (!result.Success)
        {
            throw new InvalidOperationException("Could not list local branches: " + result.FailureText());
        }

        return result.OutputLines().ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Returns null when HEAD is detached.</summary>
    private async Task<string?> GetCurrentBranchAsync(string localPath, CancellationToken cancellationToken)
    {
        var result = await git.RunAsync(localPath, ["rev-parse", "--abbrev-ref", "HEAD"],
            authenticated: false, cancellationToken);

        if (!result.Success)
        {
            return null;
        }

        var branch = result.StandardOutput.Trim();
        return branch is "HEAD" or "" ? null : branch;
    }

    private async Task<bool> IsWorkingTreeDirtyAsync(string localPath, CancellationToken cancellationToken)
    {
        var result = await git.RunAsync(localPath, ["status", "--porcelain"],
            authenticated: false, cancellationToken);

        return result.Success && !string.IsNullOrWhiteSpace(result.StandardOutput);
    }

    private async Task<bool> IsSameCommitAsync(
        string localPath, string branch, string remoteRef, CancellationToken cancellationToken)
    {
        var result = await git.RunAsync(localPath, ["rev-parse", branch, remoteRef],
            authenticated: false, cancellationToken);

        if (!result.Success)
        {
            return false;
        }

        var lines = result.OutputLines();
        return lines.Count == 2 && lines[0] == lines[1];
    }

    /// <summary>Is the local branch an ancestor of the remote branch (i.e. can it be fast-forwarded losslessly)?</summary>
    private async Task<bool> CanFastForwardAsync(
        string localPath, string branch, string remoteRef, CancellationToken cancellationToken)
    {
        var result = await git.RunAsync(localPath, ["merge-base", "--is-ancestor", branch, remoteRef],
            authenticated: false, cancellationToken);

        return result.Success;
    }
}
