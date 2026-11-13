using System.Text;

namespace GitlabCloner.Cli.Infrastructure;

/// <summary>
/// Writes log entries to a text file. The file is appended to so that run history
/// is preserved for scheduled tasks; each run is separated by a header line.
/// Unlike the console, the date is included and no colors are used.
/// </summary>
public sealed class FileLog : ILog, IDisposable
{
    private readonly StreamWriter _writer;
    private readonly Lock _gate = new();
    private readonly bool _verbose;

    public FileLog(string path, bool verbose)
    {
        _verbose = verbose;

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _writer = new StreamWriter(fullPath, append: true, Encoding.UTF8) { AutoFlush = true };
        _writer.WriteLine();
        _writer.WriteLine($"===== gitlab-cloner run — {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
    }

    public void Debug(string message)
    {
        if (_verbose)
        {
            WriteLine("DBG", message);
        }
    }

    public void Info(string message) => WriteLine("INF", message);

    public void Warn(string message) => WriteLine("WRN", message);

    public void Error(string message) => WriteLine("ERR", message);

    public void Write(string line)
    {
        lock (_gate)
        {
            _writer.WriteLine(line);
        }
    }

    private void WriteLine(string level, string message)
    {
        lock (_gate)
        {
            _writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer.Dispose();
        }
    }
}
