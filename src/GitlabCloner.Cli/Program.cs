using System.Text;
using GitlabCloner.Cli.Configuration;
using GitlabCloner.Cli.Git;
using GitlabCloner.Cli.GitLab;
using GitlabCloner.Cli.Infrastructure;
using GitlabCloner.Cli.Sync;

Console.OutputEncoding = Encoding.UTF8;

// Running without arguments (e.g. double-clicking the exe) shows the help screen.
if (args.Length == 0 || args.Any(a => a is "--help" or "-h" or "-?" or "/?"))
{
    PrintUsage();
    return 0;
}

SyncOptions options;
try
{
    options = SyncOptions.Load(args, AppContext.BaseDirectory);
}
catch (OptionsException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine("For usage: gitlab-cloner --help");
    return 2;
}

var problems = options.Validate();
if (problems.Count > 0)
{
    foreach (var problem in problems)
    {
        Console.Error.WriteLine("Error: " + problem);
    }

    return 2;
}

var consoleLog = new ConsoleLog(options.Verbose);
FileLog? fileLog = null;

if (!string.IsNullOrWhiteSpace(options.LogFile))
{
    try
    {
        fileLog = new FileLog(options.LogFile, options.Verbose);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
    {
        Console.Error.WriteLine($"Error: could not open log file ('{options.LogFile}'): {ex.Message}");
        return 2;
    }
}

using var logFileHandle = fileLog;
ILog log = fileLog is null ? consoleLog : new CompositeLog(consoleLog, fileLog);

if (fileLog is not null)
{
    log.Info($"Log file: {Path.GetFullPath(options.LogFile!)}");
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    log.Warn("Cancellation requested; stopping once running git operations finish…");
    cancellation.Cancel();
};

var gitVersion = await GitRunner.DetectVersionAsync(cancellation.Token);
if (gitVersion is null)
{
    Console.Error.WriteLine("Error: 'git' not found. Make sure Git is installed and on the PATH.");
    return 3;
}

log.Debug($"Found git: {gitVersion}");

using var http = new HttpClient
{
    BaseAddress = options.ApiBaseAddress(),
    Timeout = TimeSpan.FromMinutes(2),
};
http.DefaultRequestHeaders.UserAgent.ParseAdd("gitlab-cloner/1.0");

var gitRunner = new GitRunner(options.Token, TimeSpan.FromMinutes(options.GitTimeoutMinutes), log);
var synchronizer = new RepositorySynchronizer(gitRunner, options, log);
var orchestrator = new SyncOrchestrator(new GitLabClient(http, options, log), synchronizer, options, log);

try
{
    var outcomes = await orchestrator.RunAsync(cancellation.Token);
    orchestrator.PrintSummary(outcomes);

    return outcomes.Any(o => o.Status == RepositoryStatus.Failed) ? 1 : 0;
}
catch (OperationCanceledException)
{
    log.Warn("Operation cancelled by user.");
    return 130;
}
catch (GitLabException ex)
{
    log.Error(ex.Message);
    return 4;
}
catch (GitNotAvailableException ex)
{
    log.Error(ex.Message);
    return 3;
}

static void PrintUsage()
{
    Console.WriteLine("""
        gitlab-cloner — downloads / updates every repository on an on-prem GitLab
        server, preserving the group hierarchy and including all branches.

        Usage:
          gitlab-cloner --gitlab-url https://gitlab.example.local --target-directory D:\repos

        Options:
          --gitlab-url <url>          GitLab address (can also be set via the GITLAB_URL env variable)
          --token <token>             Personal Access Token — the GITLAB_TOKEN env variable is recommended
          --target-directory <path>   Root directory to clone repositories into (default: repos)
          --group <group/path>        Only download this group and its subgroups
          --parallelism <n>           Number of repositories processed concurrently (1-64, default: 4)
          --git-timeout-minutes <n>   Timeout for a single git command (default: 60)
          --log-file <path>           Also write all log entries to this text file
          --include-archived          Also download archived projects
          --dry-run                   Download nothing, just print what would be done
          --verbose                   Detailed logging (including executed git commands)
          --help                      This help

        With no arguments this help is shown. To take all settings from
        appsettings.json, pass at least one option, for example:
          gitlab-cloner --target-directory D:\repos

        The safest way to provide the token is an environment variable:
          PowerShell : $env:GITLAB_TOKEN = "glpat-..."
          cmd        : set GITLAB_TOKEN=glpat-...

        Required token scopes: read_api and read_repository.

        Exit codes: 0 success, 1 at least one repository failed, 2 invalid arguments,
        3 git not found, 4 GitLab API error, 130 cancelled.
        """);
}
