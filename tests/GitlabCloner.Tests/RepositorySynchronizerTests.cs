using GitlabCloner.Cli.Configuration;
using GitlabCloner.Cli.Git;
using GitlabCloner.Cli.GitLab;
using GitlabCloner.Cli.Infrastructure;
using GitlabCloner.Cli.Sync;
using Xunit;

namespace GitlabCloner.Tests;

public class RepositorySynchronizerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gitlab-cloner-test-" + Guid.NewGuid().ToString("N"));

    private const string RepoUrl = "https://gitlab.local/group/project.git";

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private SyncOptions Options(bool dryRun = false) => new()
    {
        GitLabUrl = "https://gitlab.local",
        Token = "test-token",
        TargetDirectory = _root,
        DryRun = dryRun,
    };

    private static GitLabProject Project(bool emptyRepo = false) => new()
    {
        Id = 1,
        Name = "project",
        PathWithNamespace = "group/project",
        HttpUrlToRepo = RepoUrl,
        DefaultBranch = "main",
        EmptyRepo = emptyRepo,
    };

    private RepositorySynchronizer Create(FakeGitRunner git, bool dryRun = false) =>
        new(git, Options(dryRun), NullLog.Instance);

    private string LocalPathOfProject() => Path.Combine(Path.GetFullPath(_root), "group", "project");

    private void CreateExistingClone()
    {
        Directory.CreateDirectory(Path.Combine(LocalPathOfProject(), ".git"));
    }

    [Fact]
    public async Task Empty_repository_is_skipped()
    {
        var git = new FakeGitRunner();

        var outcome = await Create(git).SyncAsync(Project(emptyRepo: true), CancellationToken.None);

        Assert.Equal(RepositoryStatus.Skipped, outcome.Status);
        Assert.Empty(git.Commands);
    }

    [Fact]
    public async Task DryRun_runs_no_git_commands()
    {
        var git = new FakeGitRunner();

        var outcome = await Create(git, dryRun: true).SyncAsync(Project(), CancellationToken.None);

        Assert.Equal(RepositoryStatus.Skipped, outcome.Status);
        Assert.Empty(git.Commands);
        Assert.Contains("would clone", outcome.Detail);
    }

    [Fact]
    public async Task Missing_repository_is_cloned_and_local_branches_are_created_for_all_branches()
    {
        var git = new FakeGitRunner()
            // Real git abbreviates refs/remotes/origin/HEAD to a bare "origin".
            .WhenOutputs("for-each-ref --format=%(refname:short) refs/remotes/origin",
                "origin\norigin/main\norigin/develop\n")
            .WhenOutputs("for-each-ref --format=%(refname:short) refs/heads", "main\n");

        var outcome = await Create(git).SyncAsync(Project(), CancellationToken.None);

        Assert.Equal(RepositoryStatus.Cloned, outcome.Status);
        Assert.True(git.Ran("clone --no-single-branch --origin origin " + RepoUrl));
        Assert.True(git.Ran("branch --track develop origin/develop"));
        Assert.False(git.Ran("branch --track main origin/main"));
        Assert.Empty(outcome.WarningList);
    }

    [Fact]
    public async Task Existing_repository_is_fetched_and_branches_fast_forwarded()
    {
        CreateExistingClone();

        var git = new FakeGitRunner()
            .WhenOutputs("remote get-url origin", RepoUrl)
            .WhenOutputs("for-each-ref --format=%(refname:short) refs/remotes/origin",
                "origin\norigin/main\norigin/feature/x\n")
            .WhenOutputs("for-each-ref --format=%(refname:short) refs/heads", "main\n")
            .WhenOutputs("rev-parse --abbrev-ref HEAD", "main\n")
            .WhenOutputs("status --porcelain", "")
            .WhenOutputs("rev-parse main origin/main", "aaaaaaa\nbbbbbbb\n");

        var outcome = await Create(git).SyncAsync(Project(), CancellationToken.None);

        Assert.Equal(RepositoryStatus.Updated, outcome.Status);
        Assert.True(git.Ran("fetch --prune --prune-tags --tags origin"));
        Assert.True(git.Ran("merge --ff-only origin/main"));
        Assert.True(git.Ran("branch --track feature/x origin/feature/x"));
        Assert.Empty(outcome.WarningList);
    }

    [Fact]
    public async Task Diverged_branch_is_left_untouched()
    {
        CreateExistingClone();

        var git = new FakeGitRunner()
            .WhenOutputs("remote get-url origin", RepoUrl)
            .WhenOutputs("for-each-ref --format=%(refname:short) refs/remotes/origin", "origin/main\n")
            .WhenOutputs("for-each-ref --format=%(refname:short) refs/heads", "main\n")
            .WhenOutputs("rev-parse --abbrev-ref HEAD", "main\n")
            .WhenOutputs("status --porcelain", "")
            .WhenOutputs("rev-parse main origin/main", "aaaaaaa\nbbbbbbb\n")
            .WhenFails("merge-base --is-ancestor main origin/main");

        var outcome = await Create(git).SyncAsync(Project(), CancellationToken.None);

        Assert.Equal(RepositoryStatus.UpToDate, outcome.Status);
        Assert.False(git.Ran("merge --ff-only"));
        Assert.Contains(outcome.WarningList, w => w.Contains("diverged"));
    }

    [Fact]
    public async Task Checked_out_branch_is_not_updated_when_working_tree_is_dirty()
    {
        CreateExistingClone();

        var git = new FakeGitRunner()
            .WhenOutputs("remote get-url origin", RepoUrl)
            .WhenOutputs("for-each-ref --format=%(refname:short) refs/remotes/origin", "origin/main\n")
            .WhenOutputs("for-each-ref --format=%(refname:short) refs/heads", "main\n")
            .WhenOutputs("rev-parse --abbrev-ref HEAD", "main\n")
            .WhenOutputs("status --porcelain", " M src/Program.cs\n")
            .WhenOutputs("rev-parse main origin/main", "aaaaaaa\nbbbbbbb\n");

        var outcome = await Create(git).SyncAsync(Project(), CancellationToken.None);

        Assert.Equal(RepositoryStatus.UpToDate, outcome.Status);
        Assert.False(git.Ran("merge --ff-only"));
        Assert.Contains(outcome.WarningList, w => w.Contains("dirty"));
    }

    [Fact]
    public async Task Fails_when_target_directory_is_not_empty_and_not_a_git_repository()
    {
        var localPath = LocalPathOfProject();
        Directory.CreateDirectory(localPath);
        File.WriteAllText(Path.Combine(localPath, "unexpected.txt"), "x");

        var outcome = await Create(new FakeGitRunner()).SyncAsync(Project(), CancellationToken.None);

        Assert.Equal(RepositoryStatus.Failed, outcome.Status);
    }

    [Fact]
    public async Task Clone_failure_is_reported()
    {
        var git = new FakeGitRunner().WhenFails("clone", "fatal: Authentication failed");

        var outcome = await Create(git).SyncAsync(Project(), CancellationToken.None);

        Assert.Equal(RepositoryStatus.Failed, outcome.Status);
        Assert.Contains("Authentication failed", outcome.Detail);
    }
}
