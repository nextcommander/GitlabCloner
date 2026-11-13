using GitlabCloner.Cli.Git;
using Xunit;

namespace GitlabCloner.Tests;

public class CheckoutDiagnosticsTests
{
    [Fact]
    public void Extracts_invalid_path_from_real_git_output()
    {
        const string output = """
            Cloning into 'C:\repos\infrastructure\settings\acme.infrastructure.settings'...
            error: invalid path 'docs/Ipa & Apk /Build APK.docx'
            fatal: unable to checkout working tree
            warning: Clone succeeded, but checkout failed.
            """;

        Assert.Equal(["docs/Ipa & Apk /Build APK.docx"], CheckoutDiagnostics.InvalidPaths(output));
    }

    [Fact]
    public void Extracts_multiple_paths_and_removes_duplicates()
    {
        const string output = """
            error: invalid path 'a/one /x.txt'
            error: invalid path 'b/two./y.txt'
            error: invalid path 'a/one /x.txt'
            """;

        Assert.Equal(["a/one /x.txt", "b/two./y.txt"], CheckoutDiagnostics.InvalidPaths(output));
    }

    [Fact]
    public void Also_matches_capitalized_Invalid_path()
    {
        Assert.Single(CheckoutDiagnostics.InvalidPaths("error: Invalid path 'x/y /z.txt'"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fatal: Authentication failed for 'https://gitlab.local/x.git/'")]
    public void Returns_empty_for_unrelated_output(string? output)
    {
        Assert.Empty(CheckoutDiagnostics.InvalidPaths(output));
    }

    [Fact]
    public void Failure_during_checkout_is_treated_as_recoverable()
    {
        var result = new GitResult(128, "",
            "Updating files:  10% (234/2338)\n" +
            "error: unable to create file src/aux.config: Invalid argument\n" +
            "fatal: unable to checkout working tree\n" +
            "warning: Clone succeeded, but checkout failed.");

        Assert.True(CheckoutDiagnostics.IsCheckoutOnlyFailure(result));
        // git did not report the path as 'invalid path'; it must still be recoverable.
        Assert.Empty(CheckoutDiagnostics.InvalidPaths(result));
    }

    [Fact]
    public void Invalid_path_is_recoverable_even_without_marker()
    {
        var result = new GitResult(128, "", "error: invalid path 'docs/Apk /a.docx'");

        Assert.True(CheckoutDiagnostics.IsCheckoutOnlyFailure(result));
    }

    [Theory]
    [InlineData("fatal: Authentication failed for 'https://gitlab.local/x.git/'")]
    [InlineData("fatal: failed to unlink '...': Filename too long")]
    [InlineData("fatal: could not read from remote repository")]
    public void Real_clone_errors_are_not_recoverable(string stderr)
    {
        Assert.False(CheckoutDiagnostics.IsCheckoutOnlyFailure(new GitResult(128, "", stderr)));
    }
}
