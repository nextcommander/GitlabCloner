namespace GitlabCloner.Cli.Infrastructure;

public interface ILog
{
    void Debug(string message);

    void Info(string message);

    void Warn(string message);

    void Error(string message);

    /// <summary>Writes the line as-is, without timestamp or level (used for the summary report).</summary>
    void Write(string line);
}

/// <summary>A log that writes nothing; used in tests.</summary>
public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();

    public void Debug(string message)
    {
    }

    public void Info(string message)
    {
    }

    public void Warn(string message)
    {
    }

    public void Error(string message)
    {
    }

    public void Write(string line)
    {
    }
}

/// <summary>Writes the same entry to multiple targets (console and file).</summary>
public sealed class CompositeLog(params ILog[] targets) : ILog
{
    public void Debug(string message) => Each(target => target.Debug(message));

    public void Info(string message) => Each(target => target.Info(message));

    public void Warn(string message) => Each(target => target.Warn(message));

    public void Error(string message) => Each(target => target.Error(message));

    public void Write(string line) => Each(target => target.Write(line));

    private void Each(Action<ILog> action)
    {
        foreach (var target in targets)
        {
            action(target);
        }
    }
}
