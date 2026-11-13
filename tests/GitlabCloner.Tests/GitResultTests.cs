using GitlabCloner.Cli.Git;
using Xunit;

namespace GitlabCloner.Tests;

public class GitResultTests
{
    [Fact]
    public void Progress_output_does_not_drown_the_real_error()
    {
        // git writes checkout progress to stderr, overwriting the same line with a carriage return.
        var progress = string.Join("", Enumerable.Range(1, 200)
            .Select(i => $"\rUpdating files:  {i / 2}% ({i * 11}/2338)"));

        var result = new GitResult(128,
            "",
            $"Cloning into 'C:\\repos\\mobile\\enterpriseapp'...\n{progress}\n" +
            "error: unable to create file src/Assets/nul.png: Invalid argument\n" +
            "fatal: unable to checkout working tree");

        var text = result.FailureText();

        Assert.Contains("unable to create file", text);
        Assert.Contains("unable to checkout working tree", text);
        Assert.DoesNotContain("Updating files", text);
    }

    [Fact]
    public void Without_diagnostic_lines_the_tail_of_the_output_is_used()
    {
        var result = new GitResult(1, "", "first line\nsecond line\nlast line");

        Assert.Contains("last line", result.FailureText());
    }

    [Fact]
    public void Without_output_the_exit_code_is_reported()
    {
        var text = new GitResult(137, "", "").FailureText();

        Assert.Contains("137", text);
    }

    [Fact]
    public void Trailing_error_is_kept_in_very_long_text()
    {
        var noise = string.Join("\n", Enumerable.Range(0, 50)
            .Select(i => $"remote: sending object {i}, this line is padded with filler text to make it long"));

        var result = new GitResult(128, "", noise + "\nfatal: the real error is here");

        var text = result.FailureText();

        Assert.Contains("the real error is here", text);
        Assert.True(text.Length <= 601, $"text too long: {text.Length}");
    }

    [Fact]
    public void Stdout_is_used_when_stderr_is_empty()
    {
        var result = new GitResult(1, "fatal: error reported on stdout", "");

        Assert.Contains("error reported on stdout", result.FailureText());
    }

    [Fact]
    public void OutputLines_splits_lines()
    {
        var result = new GitResult(0, "origin/main\r\norigin/develop\r\n", "");

        Assert.Equal(["origin/main", "origin/develop"], result.OutputLines());
    }
}
