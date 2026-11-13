namespace GitlabCloner.Cli.Git;

public sealed record GitResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;

    public IReadOnlyList<string> OutputLines() =>
        StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0)
            .ToList();

    /// <summary>
    /// Error text to report.
    ///
    /// git also writes progress output to stderr ("Updating files: 10% (1/2338)")
    /// and those lines easily drown the real error. So diagnostic lines
    /// (error:/fatal:/warning:/remote:) are filtered first; if there are none, the
    /// tail of the output is used — in git the actual error always comes last.
    /// </summary>
    public string FailureText()
    {
        var lines = DiagnosticLines();
        if (lines.Count == 0)
        {
            return $"git exited with code {ExitCode} and produced no message.";
        }

        var text = string.Join(" | ", lines);
        return text.Length <= MaxLength ? text : "…" + text[^MaxLength..];
    }

    private const int MaxLength = 600;

    private static readonly string[] DiagnosticPrefixes = ["error:", "fatal:", "warning:", "remote:"];

    private IReadOnlyList<string> DiagnosticLines()
    {
        var source = string.IsNullOrWhiteSpace(StandardError) ? StandardOutput : StandardError;

        // Also split on '\r': progress lines overwrite the same line with a carriage return.
        var lines = source
            .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

        var diagnostics = lines
            .Where(line => DiagnosticPrefixes.Any(p => line.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return (diagnostics.Count > 0 ? diagnostics : lines).TakeLast(6).ToList();
    }
}
