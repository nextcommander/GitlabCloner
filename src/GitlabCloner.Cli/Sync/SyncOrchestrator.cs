using System.Collections.Concurrent;
using GitlabCloner.Cli.Configuration;
using GitlabCloner.Cli.GitLab;
using GitlabCloner.Cli.Infrastructure;

namespace GitlabCloner.Cli.Sync;

/// <summary>
/// Processes the project list in parallel and prints a summary report at the end.
/// A failure in one repository does not stop the others.
/// </summary>
public sealed class SyncOrchestrator(
    IGitLabClient gitLab,
    RepositorySynchronizer synchronizer,
    SyncOptions options,
    ILog log)
{
    public async Task<IReadOnlyList<RepositoryOutcome>> RunAsync(CancellationToken cancellationToken)
    {
        log.Info($"GitLab: {options.GitLabUrl}");
        log.Info($"Target directory: {options.ResolvedTargetDirectory()}");

        if (options.Group is not null)
        {
            log.Info($"Group filter: {options.Group} (including subgroups)");
        }

        if (options.DryRun)
        {
            log.Warn("dry-run mode: nothing will be downloaded.");
        }

        var projects = new List<GitLabProject>();
        await foreach (var project in gitLab.GetProjectsAsync(cancellationToken))
        {
            if (!options.IncludeArchived && project.Archived)
            {
                continue;
            }

            projects.Add(project);
        }

        // If the same project shows up on two pages (concurrent changes), process it only once.
        projects = projects
            .GroupBy(p => p.Id)
            .Select(g => g.First())
            .OrderBy(p => p.PathWithNamespace, StringComparer.OrdinalIgnoreCase)
            .ToList();

        log.Info($"Found {projects.Count} projects. Parallelism: {options.Parallelism}");

        if (projects.Count == 0)
        {
            return [];
        }

        Directory.CreateDirectory(options.ResolvedTargetDirectory());

        var outcomes = new ConcurrentBag<RepositoryOutcome>();
        var completed = 0;

        await Parallel.ForEachAsync(
            projects,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = options.Parallelism,
                CancellationToken = cancellationToken,
            },
            async (project, token) =>
            {
                var outcome = await synchronizer.SyncAsync(project, token);
                outcomes.Add(outcome);

                var done = Interlocked.Increment(ref completed);
                LogOutcome(outcome, done, projects.Count);
            });

        return outcomes
            .OrderBy(o => o.ProjectPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void LogOutcome(RepositoryOutcome outcome, int index, int total)
    {
        var progress = $"[{index}/{total}]";
        var message = $"{progress} {outcome.ProjectPath} — {Describe(outcome.Status)}" +
                      (outcome.Detail is null ? "" : $" ({outcome.Detail})");

        if (outcome.Status == RepositoryStatus.Failed)
        {
            log.Error(message);
        }
        else
        {
            log.Info(message);
        }

        foreach (var warning in outcome.WarningList)
        {
            log.Warn($"    {outcome.ProjectPath}: {warning}");
        }
    }

    public void PrintSummary(IReadOnlyList<RepositoryOutcome> outcomes)
    {
        log.Write("");
        log.Write("=== Summary ===");

        foreach (var status in Enum.GetValues<RepositoryStatus>())
        {
            var count = outcomes.Count(o => o.Status == status);
            if (count > 0)
            {
                log.Write($"{Describe(status),-14}: {count}");
            }
        }

        var incomplete = outcomes.Where(o => o.IncompleteWorktree).ToList();
        if (incomplete.Count > 0)
        {
            log.Write("");
            log.Write($"Incomplete working tree ({incomplete.Count} repositories) — git history and all branches");
            log.Write("were downloaded completely, but some file paths cannot be created on Windows:");
            foreach (var outcome in incomplete)
            {
                log.Write($"  - {outcome.ProjectPath}");
            }
        }

        var failures = outcomes.Where(o => o.Status == RepositoryStatus.Failed).ToList();
        if (failures.Count > 0)
        {
            log.Write("");
            log.Write("Failed repositories:");
            foreach (var failure in failures)
            {
                log.Write($"  - {failure.ProjectPath}: {failure.Detail}");
            }
        }

        var warned = outcomes.Where(o => o.WarningList.Count > 0).ToList();
        if (warned.Count > 0)
        {
            log.Write("");
            log.Write($"Repositories with warnings: {warned.Count} (details in the log above)");
        }
    }

    private static string Describe(RepositoryStatus status) => status switch
    {
        RepositoryStatus.Cloned => "cloned",
        RepositoryStatus.Updated => "updated",
        RepositoryStatus.UpToDate => "up to date",
        RepositoryStatus.Skipped => "skipped",
        RepositoryStatus.Failed => "failed",
        _ => status.ToString(),
    };
}
