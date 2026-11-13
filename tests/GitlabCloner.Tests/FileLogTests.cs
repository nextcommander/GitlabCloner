using GitlabCloner.Cli.Infrastructure;
using Xunit;

namespace GitlabCloner.Tests;

public class FileLogTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "gitlab-cloner-log-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private string LogPath() => Path.Combine(_directory, "sub", "folder", "run.log");

    [Fact]
    public void Creates_missing_folder_and_writes_entry()
    {
        using (var log = new FileLog(LogPath(), verbose: false))
        {
            log.Info("project cloned");
        }

        var content = File.ReadAllText(LogPath());

        Assert.Contains("[INF] project cloned", content);
        Assert.Contains("gitlab-cloner run", content);
    }

    [Fact]
    public void Debug_is_not_written_when_verbose_is_off()
    {
        using (var log = new FileLog(LogPath(), verbose: false))
        {
            log.Debug("git clone ...");
            log.Info("info");
        }

        var content = File.ReadAllText(LogPath());

        Assert.DoesNotContain("git clone", content);
        Assert.Contains("info", content);
    }

    [Fact]
    public void Debug_is_written_when_verbose_is_on()
    {
        using (var log = new FileLog(LogPath(), verbose: true))
        {
            log.Debug("git clone ...");
        }

        Assert.Contains("[DBG] git clone", File.ReadAllText(LogPath()));
    }

    [Fact]
    public void Summary_lines_are_written_without_timestamp()
    {
        using (var log = new FileLog(LogPath(), verbose: false))
        {
            log.Write("=== Summary ===");
        }

        Assert.Contains("\n=== Summary ===", File.ReadAllText(LogPath()));
    }

    [Fact]
    public void Second_run_is_appended_to_the_file()
    {
        using (var log = new FileLog(LogPath(), verbose: false))
        {
            log.Info("first run");
        }

        using (var log = new FileLog(LogPath(), verbose: false))
        {
            log.Info("second run");
        }

        var content = File.ReadAllText(LogPath());

        Assert.Contains("first run", content);
        Assert.Contains("second run", content);
    }

    [Fact]
    public void CompositeLog_writes_to_all_targets()
    {
        var first = new RecordingLog();
        var second = new RecordingLog();

        new CompositeLog(first, second).Warn("warning");

        Assert.Equal(["WRN:warning"], first.Entries);
        Assert.Equal(["WRN:warning"], second.Entries);
    }

    private sealed class RecordingLog : ILog
    {
        public List<string> Entries { get; } = [];

        public void Debug(string message) => Entries.Add("DBG:" + message);

        public void Info(string message) => Entries.Add("INF:" + message);

        public void Warn(string message) => Entries.Add("WRN:" + message);

        public void Error(string message) => Entries.Add("ERR:" + message);

        public void Write(string line) => Entries.Add("RAW:" + line);
    }
}
