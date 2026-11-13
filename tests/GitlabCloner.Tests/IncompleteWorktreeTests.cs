using GitlabCloner.Cli.Configuration;
using GitlabCloner.Cli.Git;
using GitlabCloner.Cli.GitLab;
using GitlabCloner.Cli.Infrastructure;
using GitlabCloner.Cli.Sync;
using Xunit;

namespace GitlabCloner.Tests;

/// <summary>
/// Repositories containing paths Windows cannot represent (e.g. a folder name ending
/// in a space). The clone itself succeeds for these, only checkout stops halfway —
/// there is no data loss, so they are not treated as failures.
/// </summary>
public class IncompleteWorktreeTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gitlab-cloner-iw-" + Guid.NewGuid().ToString("N"));

    private const string RepoUrl = "https://gitlab.local/infrastructure/settings.git";

    private const string InvalidPathError =
        "error: invalid path 'docs/Ipa & Apk /Build APK.docx'\n" +
        "fatal: unable to checkout working tree\n" +
        "warning: Clone succeeded, but checkout failed.";

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private static GitLabProject Project() => new()
    {
        Id = 7,
        Name = "acme.infrastructure.settings",
        PathWithNamespace = "infrastructure/settings/acme.infrastructure.settings",
        HttpUrlToRepo = RepoUrl,
        DefaultBranch = "main",
    };

    private RepositorySynchronizer Create(FakeGitRunner git) => new(
        git,
        new SyncOptions { GitLabUrl = "https://gitlab.local", Token = "t", TargetDirectory = _root },
        NullLog.Instance);

    private string LocalPath() => PathMapper.ToLocalPath(Path.GetFullPath(_root), Project().PathWithNamespace);

    private void CreateGitDirectory() => Directory.CreateDirectory(Path.Combine(LocalPath(), ".git"));

    [Fact]
    public async Task Checkout_failing_on_invalid_path_reports_incomplete_tree_not_failure()
    {
        var git = new FakeGitRunner()
            // Real git has already created the .git directory even if checkout fails.
            .Does("clone", CreateGitDirectory)
            .When("clone", new GitResult(128, "", InvalidPathError))
            .WhenOutputs("for-each-ref --format=%(refname:short) refs/remotes/origin", "origin\norigin/main\n")
            .WhenOutputs("for-each-ref --format=%(refname:short) refs/heads", "main\n");

        var outcome = await Create(git).SyncAsync(Project(), CancellationToken.None);

        Assert.Equal(RepositoryStatus.Cloned, outcome.Status);
        Assert.True(outcome.IncompleteWorktree);
        Assert.True(git.Ran("restore --source=HEAD :/"), "writable files should be recovered");
        Assert.True(git.Ran("config gitlab-cloner.incompleteWorktree true"), "repository should be marked");
        Assert.Contains(outcome.WarningList, w => w.Contains("Ipa & Apk"));
    }

    [Fact]
    public async Task Clone_errors_other_than_invalid_path_remain_failures()
    {
        var git = new FakeGitRunner()
            .Does("clone", CreateGitDirectory)
            .When("clone", new GitResult(128, "", "fatal: Authentication failed"));

        var outcome = await Create(git).SyncAsync(Project(), CancellationToken.None);

        Assert.Equal(RepositoryStatus.Failed, outcome.Status);
        Assert.False(outcome.IncompleteWorktree);
        Assert.False(git.Ran("restore"));
    }

    [Fact]
    public async Task Marked_repository_uses_update_ref_instead_of_merge_on_sync()
    {
        CreateGitDirectory();

        var git = new FakeGitRunner()
            .WhenOutputs("config --get gitlab-cloner.incompleteWorktree", "true\n")
            .WhenOutputs("remote get-url origin", RepoUrl)
            .WhenOutputs("for-each-ref --format=%(refname:short) refs/remotes/origin", "origin\norigin/main\n")
            .WhenOutputs("for-each-ref --format=%(refname:short) refs/heads", "main\n")
            .WhenOutputs("rev-parse --abbrev-ref HEAD", "main\n")
            .WhenOutputs("rev-parse main origin/main", "aaaaaaa\nbbbbbbb\n");

        var outcome = await Create(git).SyncAsync(Project(), CancellationToken.None);

        Assert.Equal(RepositoryStatus.Updated, outcome.Status);
        Assert.True(outcome.IncompleteWorktree);
        Assert.True(git.Ran("update-ref refs/heads/main origin/main"), "ref should be advanced directly");
        Assert.False(git.Ran("merge --ff-only"), "merge touches the working tree and would fail");
        Assert.True(git.Ran("restore --source=HEAD :/"), "files should be refreshed");
    }

    [Fact]
    public async Task Marked_repository_skips_dirty_tree_protection()
    {
        CreateGitDirectory();

        var git = new FakeGitRunner()
            .WhenOutputs("config --get gitlab-cloner.incompleteWorktree", "true\n")
            .WhenOutputs("remote get-url origin", RepoUrl)
            .WhenOutputs("for-each-ref --format=%(refname:short) refs/remotes/origin", "origin\norigin/main\n")
            .WhenOutputs("for-each-ref --format=%(refname:short) refs/heads", "main\n")
            .WhenOutputs("rev-parse --abbrev-ref HEAD", "main\n")
            .WhenOutputs("rev-parse main origin/main", "aaaaaaa\nbbbbbbb\n");

        await Create(git).SyncAsync(Project(), CancellationToken.None);

        // In this repository the dirtiness is structural; querying it is pointless.
        Assert.False(git.Ran("status --porcelain"));
    }

    [Fact]
    public async Task Invalid_path_appearing_during_sync_is_marked_and_recovered()
    {
        CreateGitDirectory();

        var git = new FakeGitRunner()
            .WhenFails("config --get gitlab-cloner.incompleteWorktree")
            .WhenOutputs("remote get-url origin", RepoUrl)
            .WhenOutputs("for-each-ref --format=%(refname:short) refs/remotes/origin", "origin\norigin/main\n")
            .WhenOutputs("for-each-ref --format=%(refname:short) refs/heads", "main\n")
            .WhenOutputs("rev-parse --abbrev-ref HEAD", "main\n")
            .WhenOutputs("status --porcelain", "")
            .WhenOutputs("rev-parse main origin/main", "aaaaaaa\nbbbbbbb\n")
            .When("merge --ff-only", new GitResult(1, "", InvalidPathError));

        var outcome = await Create(git).SyncAsync(Project(), CancellationToken.None);

        Assert.Equal(RepositoryStatus.Updated, outcome.Status);
        Assert.True(outcome.IncompleteWorktree);
        Assert.True(git.Ran("update-ref refs/heads/main origin/main"));
        Assert.True(git.Ran("config gitlab-cloner.incompleteWorktree true"));
        Assert.Contains(outcome.WarningList, w => w.Contains("Ipa & Apk"));
    }
}
