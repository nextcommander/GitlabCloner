using GitlabCloner.Cli.Configuration;
using Xunit;

namespace GitlabCloner.Tests;

public class SyncOptionsTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "gitlab-cloner-opts-" + Guid.NewGuid().ToString("N"));

    public SyncOptionsTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private void WriteAppSettings(string json) =>
        File.WriteAllText(Path.Combine(_directory, "appsettings.json"), json);

    [Fact]
    public void Command_line_overrides_appsettings_value()
    {
        WriteAppSettings("""{ "GitLabUrl": "https://from-file.local", "Parallelism": 2 }""");

        var options = SyncOptions.Load(["--gitlab-url", "https://from-cli.local"], _directory);

        Assert.Equal("https://from-cli.local", options.GitLabUrl);
        Assert.Equal(2, options.Parallelism);
    }

    [Theory]
    [InlineData("--dry-run")]
    [InlineData("--dryrun")]
    [InlineData("--DryRun")]
    public void Flag_spellings_are_equivalent(string argument)
    {
        var options = SyncOptions.Load([argument], _directory);

        Assert.True(options.DryRun);
    }

    [Fact]
    public void Flag_can_be_explicitly_set_to_false()
    {
        var options = SyncOptions.Load(["--dry-run=false"], _directory);

        Assert.False(options.DryRun);
    }

    [Fact]
    public void Value_can_be_given_with_equals_sign()
    {
        var options = SyncOptions.Load(["--parallelism=8"], _directory);

        Assert.Equal(8, options.Parallelism);
    }

    [Theory]
    [InlineData("--log-file")]
    [InlineData("--logfile")]
    public void Log_file_option_is_read(string argument)
    {
        var options = SyncOptions.Load([argument, @"D:\logs\run.log"], _directory);

        Assert.Equal(@"D:\logs\run.log", options.LogFile);
    }

    [Fact]
    public void Log_file_is_empty_by_default()
    {
        Assert.Null(SyncOptions.Load([], _directory).LogFile);
    }

    [Fact]
    public void Unknown_option_throws()
    {
        Assert.Throws<OptionsException>(() => SyncOptions.Load(["--no-such-option", "x"], _directory));
    }

    [Fact]
    public void Option_without_value_throws()
    {
        Assert.Throws<OptionsException>(() => SyncOptions.Load(["--gitlab-url"], _directory));
    }

    [Fact]
    public void Out_of_range_parallelism_throws()
    {
        Assert.Throws<OptionsException>(() => SyncOptions.Load(["--parallelism", "0"], _directory));
    }

    [Theory]
    [InlineData("https://gitlab.local", "https://gitlab.local/api/v4/")]
    [InlineData("https://gitlab.local/", "https://gitlab.local/api/v4/")]
    [InlineData("https://gitlab.local/api/v4", "https://gitlab.local/api/v4/")]
    public void Api_address_is_normalized(string input, string expected)
    {
        var options = new SyncOptions { GitLabUrl = input };

        Assert.Equal(expected, options.ApiBaseAddress().ToString());
    }

    [Fact]
    public void Missing_token_and_url_are_reported_by_validation()
    {
        var problems = new SyncOptions().Validate();

        Assert.Equal(2, problems.Count);
    }

    [Fact]
    public void Invalid_url_is_reported_by_validation()
    {
        var problems = new SyncOptions { GitLabUrl = "ftp://gitlab.local", Token = "t" }.Validate();

        Assert.Single(problems);
    }
}
