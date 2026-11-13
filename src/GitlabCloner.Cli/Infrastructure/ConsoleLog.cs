namespace GitlabCloner.Cli.Infrastructure;

/// <summary>
/// Simple console log that serializes writes so lines coming from repository tasks
/// running in parallel do not interleave.
/// </summary>
public sealed class ConsoleLog(bool verbose) : ILog
{
    private readonly Lock _gate = new();

    public void Debug(string message)
    {
        if (verbose)
        {
            Write(ConsoleColor.DarkGray, "DBG", message, toError: false);
        }
    }

    public void Info(string message) => Write(null, "INF", message, toError: false);

    public void Warn(string message) => Write(ConsoleColor.Yellow, "WRN", message, toError: false);

    public void Error(string message) => Write(ConsoleColor.Red, "ERR", message, toError: true);

    public void Write(string line)
    {
        lock (_gate)
        {
            Console.Out.WriteLine(line);
        }
    }

    private void Write(ConsoleColor? color, string level, string message, bool toError)
    {
        var writer = toError ? Console.Error : Console.Out;
        var line = $"{DateTime.Now:HH:mm:ss} [{level}] {message}";

        lock (_gate)
        {
            if (color is null)
            {
                writer.WriteLine(line);
                return;
            }

            var previous = Console.ForegroundColor;
            try
            {
                Console.ForegroundColor = color.Value;
                writer.WriteLine(line);
            }
            finally
            {
                Console.ForegroundColor = previous;
            }
        }
    }
}
