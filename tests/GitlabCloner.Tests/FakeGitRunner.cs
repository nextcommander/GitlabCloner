using GitlabCloner.Cli.Git;

namespace GitlabCloner.Tests;

/// <summary>
/// Fake runner used to test <see cref="RepositorySynchronizer"/> decisions without
/// invoking real git.
/// </summary>
public sealed class FakeGitRunner : IGitRunner
{
    private readonly List<(Func<IReadOnlyList<string>, bool> Match, GitResult Result)> _rules = [];

    /// <summary>Executed commands, joined with spaces.</summary>
    public List<string> Commands { get; } = [];

    private readonly List<(string Prefix, Action Effect)> _sideEffects = [];

    public GitResult Fallback { get; set; } = new(0, "", "");

    /// <summary>
    /// Simulates a command's file-system side effect — for example git clone having
    /// created the .git directory even though checkout failed.
    /// </summary>
    public FakeGitRunner Does(string commandPrefix, Action effect)
    {
        _sideEffects.Add((commandPrefix, effect));
        return this;
    }

    public FakeGitRunner When(Func<IReadOnlyList<string>, bool> match, GitResult result)
    {
        _rules.Add((match, result));
        return this;
    }

    public FakeGitRunner When(string commandPrefix, GitResult result) =>
        When(args => string.Join(' ', args).StartsWith(commandPrefix, StringComparison.Ordinal), result);

    public FakeGitRunner WhenOutputs(string commandPrefix, string standardOutput) =>
        When(commandPrefix, new GitResult(0, standardOutput, ""));

    public FakeGitRunner WhenFails(string commandPrefix, string standardError = "error") =>
        When(commandPrefix, new GitResult(1, "", standardError));

    public bool Ran(string commandPrefix) =>
        Commands.Any(c => c.StartsWith(commandPrefix, StringComparison.Ordinal));

    public Task<GitResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        bool authenticated,
        CancellationToken cancellationToken)
    {
        var command = string.Join(' ', arguments);
        Commands.Add(command);

        foreach (var (prefix, effect) in _sideEffects)
        {
            if (command.StartsWith(prefix, StringComparison.Ordinal))
            {
                effect();
            }
        }

        foreach (var (match, result) in _rules)
        {
            if (match(arguments))
            {
                return Task.FromResult(result);
            }
        }

        return Task.FromResult(Fallback);
    }
}
