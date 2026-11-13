# GitlabCloner

Downloads every project reachable with a Personal Access Token from an on-prem
GitLab server, **preserving the group hierarchy exactly** and **including all
branches**. Running it again updates the existing local copies.

```
GitLab                              Local
platform/backend/order-service  →   D:\repos\platform\backend\order-service
platform/frontend/web           →   D:\repos\platform\frontend\web
hr/payroll                      →   D:\repos\hr\payroll
```

## Requirements

- .NET 10 SDK
- `git` installed and on the PATH (`git --version` must work)
- A GitLab Personal Access Token with the `read_api` and `read_repository` scopes

## Build

```powershell
dotnet build
```

To distribute as a single file:

```powershell
dotnet publish src/GitlabCloner.Cli -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -o publish
```

## Usage

Passing the token via an environment variable is the safest option — it does not
end up in your command history:

```powershell
$env:GITLAB_TOKEN = "glpat-xxxxxxxxxxxxxxxxxxxx"
dotnet run --project src/GitlabCloner.Cli -- `
    --gitlab-url https://gitlab.example.local `
    --target-directory D:\repos
```

To see what would be done first:

```powershell
dotnet run --project src/GitlabCloner.Cli -- --gitlab-url https://gitlab.example.local --dry-run
```

To download only a specific group (and its subgroups):

```powershell
dotnet run --project src/GitlabCloner.Cli -- --gitlab-url https://gitlab.example.local --group platform/backend
```

### Options

| Option | Default | Description |
|---|---|---|
| `--gitlab-url <url>` | — | GitLab address (can also be set via `GITLAB_URL`) |
| `--token <token>` | — | Personal Access Token (`GITLAB_TOKEN` is recommended) |
| `--target-directory <path>` | `repos` | Root directory to clone repositories into |
| `--group <group/path>` | all | Only this group and its subgroups |
| `--parallelism <n>` | `4` | Number of repositories processed concurrently (1-64) |
| `--git-timeout-minutes <n>` | `60` | Timeout for a single git command |
| `--log-file <path>` | none | Also write all log entries to this text file |
| `--include-archived` | off | Also download archived projects |
| `--dry-run` | off | Download nothing, just print the plan |
| `--verbose` | off | Also log the executed git commands |

With no arguments the help screen is shown (also when the exe is double-clicked).
If all settings live in `appsettings.json`, pass at least one option to run.

The log file is **appended to** and each run is separated by a header, so history is
preserved for scheduled tasks. The token is never written to the log.

```powershell
dotnet run --project src/GitlabCloner.Cli -- `
    --gitlab-url https://gitlab.example.local `
    --target-directory D:\repos `
    --log-file D:\repos\_log\gitlab-cloner.log
```

Settings are read in the order `appsettings.json` → environment variables → command
line; later sources override earlier ones. Environment variable names: `GITLAB_URL`,
`GITLAB_TOKEN`, and `GITLABCLONER_<SETTINGNAME>` for the rest
(e.g. `GITLABCLONER_PARALLELISM`).

### Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | At least one repository failed (listed in the summary) |
| 2 | Invalid argument or missing setting |
| 3 | `git` not found |
| 4 | GitLab API error (token, address, network) |
| 130 | Cancelled with Ctrl+C |

## Behavior details

**Branches.** Clones are made with `--no-single-branch`, then a local tracking branch
is created for every remote branch — `git branch` lists all of them. On update, only
branches that can be **fast-forwarded** are touched:

| Situation | Action |
|---|---|
| No local branch | Tracking branch is created |
| Checked out, fast-forwardable, clean tree | `git merge --ff-only` |
| Not checked out, fast-forwardable | `git branch --force` |
| Diverged (local commits exist) | **Left untouched**, warning logged |
| Dirty working tree | Active branch left untouched, warning logged |

A local branch whose remote counterpart was deleted is **not deleted**; a "does not
exist on the remote" warning is logged. Your local work is never overwritten.

**Windows path restrictions.** Every git call runs with `core.longpaths=true`, so
cloning works even when deep group hierarchies exceed the 260-character MAX_PATH limit.

Some repositories may contain paths Windows cannot represent at all — folder names
ending in a space or a dot (`docs/Ipa & Apk /...`), reserved device names such as
`aux`/`con`, file names containing `:`. In that case git completes the clone but
aborts checkout and prints `Clone succeeded, but checkout failed`. So **the git data
is downloaded completely** (all commits, all branches, including that file's
content); only that file cannot be written to disk. The tool does not treat failures
confined to the checkout stage as errors, whatever their cause:

- All writable files are written to disk with `git restore --source=HEAD :/`
- The repository is marked with the `gitlab-cloner.incompleteWorktree` setting
- The summary lists these under a separate "incomplete working tree" section; exit code stays 0
- On later syncs these repositories are advanced with `update-ref` and their files refreshed

You can still get at the file's content:

```powershell
git -c core.longpaths=true show "HEAD:docs/Ipa & Apk /Build APK.docx" > file.docx
```

**Token security.** The token is not written to `.git/config` nor embedded in the
remote URL. It is passed to every git call as a per-command `http.extraHeader` and
never appears in logs. Consequently, if you update repositories manually with
`git pull`, you will be prompted for credentials — this is expected.

**Renamed projects.** If a project/group is moved in GitLab, it is cloned again under
the new path; the old folder stays where it is and must be deleted manually.

**Skipped.** Empty repositories (no commits), projects without an HTTP clone URL and,
unless `--include-archived` is given, archived projects are skipped.

## Project structure

```
src/GitlabCloner.Cli/
  Configuration/SyncOptions.cs        settings loading and validation
  Infrastructure/ConsoleLog.cs        concurrency-safe console log
  Infrastructure/FileLog.cs           append-only file log
  GitLab/GitLabClient.cs              paginated project listing + retry
  GitLab/LinkHeaderParser.cs          Link header parsing
  Git/GitRunner.cs                    git.exe process wrapper
  Git/CheckoutDiagnostics.cs          checkout-only failure detection
  Sync/PathMapper.cs                  namespace → safe Windows path
  Sync/RepositorySynchronizer.cs      single repository: clone / sync
  Sync/SyncOrchestrator.cs            parallel execution + summary report
tests/GitlabCloner.Tests/             unit tests (fake git + fake HTTP)
```

The CLI project has **no external NuGet dependencies** — `dotnet build` works fine
on an air-gapped network too.

## Tests

```powershell
dotnet test
```
