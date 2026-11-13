using System.Diagnostics;
using System.Text;
using GitlabCloner.Cli.Infrastructure;

namespace GitlabCloner.Cli.Git;

/// <summary>
/// Invokes the system git executable as a child process.
///
/// The token is never written to .git/config: every authenticated call gets a
/// transient "-c http.extraHeader=Authorization: Basic ..." argument instead.
/// That argument is redacted from logs.
/// </summary>
public sealed class GitRunner(string token, TimeSpan timeout, ILog log) : IGitRunner
{
    private readonly string _authorizationHeader =
        "Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("oauth2:" + token));

    public async Task<GitResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        bool authenticated,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // Prevent credential managers from opening a window or blocking on input.
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GCM_INTERACTIVE"] = "never";
        startInfo.Environment["GIT_ASKPASS"] = "";

        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("credential.helper=");

        // Deep group hierarchies + long file names easily exceed Windows' 260-character
        // MAX_PATH limit, making clones fail with "Filename too long". This setting
        // switches git to the long-path APIs; it is ignored on other platforms.
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.longpaths=true");

        if (authenticated)
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("http.extraHeader=" + _authorizationHeader);
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        log.Debug($"git {string.Join(' ', arguments)}  (cwd: {workingDirectory})");

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new GitNotAvailableException(
                $"Could not run git: {ex.Message}. Make sure git is installed and on the PATH.");
        }

        var stdOutTask = process.StandardOutput.ReadToEndAsync(linkedSource.Token);
        var stdErrTask = process.StandardError.ReadToEndAsync(linkedSource.Token);

        try
        {
            await process.WaitForExitAsync(linkedSource.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);

            if (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return new GitResult(
                    -1, "", $"git command exceeded the {timeout.TotalMinutes:0}-minute timeout and was terminated.");
            }

            throw;
        }

        var standardOutput = await stdOutTask;
        var standardError = await stdErrTask;

        return new GitResult(process.ExitCode, standardOutput, standardError);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The process may have exited on its own in the meantime; nothing to do.
        }
    }

    /// <summary>Checks whether git is installed; returns its version text.</summary>
    public static async Task<string?> DetectVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("git", "--version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch
        {
            return null;
        }
    }
}

public sealed class GitNotAvailableException(string message) : Exception(message);
