using System.Text.Json;

namespace GitlabCloner.Cli.Configuration;

/// <summary>
/// All application settings. Precedence (later sources override earlier ones):
/// appsettings.json → environment variables → command-line arguments.
/// </summary>
public sealed class SyncOptions
{
    public string GitLabUrl { get; set; } = "";

    /// <summary>GitLab Personal Access Token. Must never be stored in appsettings.json.</summary>
    public string Token { get; set; } = "";

    /// <summary>Root directory that repositories are cloned into.</summary>
    public string TargetDirectory { get; set; } = "repos";

    /// <summary>When set, only this group and its subgroups are downloaded (e.g. "platform/backend").</summary>
    public string? Group { get; set; }

    public int Parallelism { get; set; } = 4;

    public bool IncludeArchived { get; set; }

    public bool DryRun { get; set; }

    public bool Verbose { get; set; }

    /// <summary>When set, every log entry is also appended to this text file.</summary>
    public string? LogFile { get; set; }

    public int GitTimeoutMinutes { get; set; } = 60;

    /// <summary>Loads settings from appsettings.json + environment variables + the command line.</summary>
    /// <exception cref="OptionsException">Unknown argument or invalid value.</exception>
    public static SyncOptions Load(string[] args, string baseDirectory)
    {
        var options = new SyncOptions();
        ApplyJsonFile(options, Path.Combine(baseDirectory, "appsettings.json"));
        ApplyEnvironment(options);
        ApplyCommandLine(options, args);
        return options;
    }

    private static void ApplyJsonFile(SyncOptions options, string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(
                File.ReadAllText(path),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new OptionsException($"Could not read appsettings.json: {ex.Message}");
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.Null)
            {
                continue;
            }

            var raw = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            };

            if (raw is not null)
            {
                Assign(options, property.Name, raw, $"appsettings.json:{property.Name}");
            }
        }
    }

    private static void ApplyEnvironment(SyncOptions options)
    {
        // GITLAB_TOKEN and GITLAB_URL are common names, so they are supported as well.
        TryAssignFromEnvironment(options, "GITLAB_TOKEN", nameof(Token));
        TryAssignFromEnvironment(options, "GITLAB_URL", nameof(GitLabUrl));

        foreach (var name in KnownKeys)
        {
            TryAssignFromEnvironment(options, "GITLABCLONER_" + name.ToUpperInvariant(), name);
        }
    }

    private static void TryAssignFromEnvironment(SyncOptions options, string variable, string key)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrWhiteSpace(value))
        {
            Assign(options, key, value, variable);
        }
    }

    private static void ApplyCommandLine(SyncOptions options, string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                throw new OptionsException($"Unexpected argument: '{argument}'. Options must start with '--'.");
            }

            var name = argument[2..];
            string? inlineValue = null;

            var separator = name.IndexOf('=');
            if (separator >= 0)
            {
                inlineValue = name[(separator + 1)..];
                name = name[..separator];
            }

            var key = ResolveKey(name)
                      ?? throw new OptionsException($"Unknown option: '--{name}'. See --help for usage.");

            if (IsFlag(key))
            {
                // --dry-run       → true
                // --dry-run=false → false
                Assign(options, key, inlineValue ?? "true", $"--{name}");
                continue;
            }

            if (inlineValue is null)
            {
                if (i + 1 >= args.Length)
                {
                    throw new OptionsException($"'--{name}' expects a value.");
                }

                inlineValue = args[++i];
            }

            Assign(options, key, inlineValue, $"--{name}");
        }
    }

    private static readonly string[] KnownKeys =
    [
        nameof(GitLabUrl), nameof(Token), nameof(TargetDirectory), nameof(Group),
        nameof(Parallelism), nameof(IncludeArchived), nameof(DryRun), nameof(Verbose),
        nameof(GitTimeoutMinutes), nameof(LogFile),
    ];

    /// <summary>Normalizes spellings such as "dry-run", "dryrun" and "DryRun" to a single key.</summary>
    private static string? ResolveKey(string name)
    {
        var normalized = name.Replace("-", "").Replace("_", "");
        return KnownKeys.FirstOrDefault(k => string.Equals(k, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsFlag(string key) =>
        key is nameof(IncludeArchived) or nameof(DryRun) or nameof(Verbose);

    private static void Assign(SyncOptions options, string key, string value, string source)
    {
        var resolved = ResolveKey(key) ?? key;
        switch (resolved)
        {
            case nameof(GitLabUrl):
                options.GitLabUrl = value.Trim();
                break;
            case nameof(Token):
                options.Token = value.Trim();
                break;
            case nameof(TargetDirectory):
                options.TargetDirectory = value.Trim();
                break;
            case nameof(Group):
                options.Group = string.IsNullOrWhiteSpace(value) ? null : value.Trim().Trim('/');
                break;
            case nameof(Parallelism):
                options.Parallelism = ParseInt(value, source, 1, 64);
                break;
            case nameof(GitTimeoutMinutes):
                options.GitTimeoutMinutes = ParseInt(value, source, 1, 24 * 60);
                break;
            case nameof(IncludeArchived):
                options.IncludeArchived = ParseBool(value, source);
                break;
            case nameof(DryRun):
                options.DryRun = ParseBool(value, source);
                break;
            case nameof(Verbose):
                options.Verbose = ParseBool(value, source);
                break;
            case nameof(LogFile):
                options.LogFile = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                break;
            default:
                throw new OptionsException($"Unknown setting: '{key}' ({source}).");
        }
    }

    private static int ParseInt(string value, string source, int min, int max)
    {
        if (!int.TryParse(value, out var parsed))
        {
            throw new OptionsException($"{source} expects a number, got '{value}'.");
        }

        if (parsed < min || parsed > max)
        {
            throw new OptionsException($"{source} must be between {min} and {max}, got '{parsed}'.");
        }

        return parsed;
    }

    private static bool ParseBool(string value, string source) => value.ToLowerInvariant() switch
    {
        "true" or "1" or "yes" => true,
        "false" or "0" or "no" => false,
        _ => throw new OptionsException($"{source} expects true/false, got '{value}'."),
    };

    /// <summary>Reports missing or inconsistent settings. An empty list means valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(GitLabUrl))
        {
            problems.Add("No GitLab address given. Use --gitlab-url or the GITLAB_URL environment variable.");
        }
        else if (!Uri.TryCreate(GitLabUrl, UriKind.Absolute, out var uri) ||
                 (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            problems.Add($"GitLab address is not a valid http(s) URL: '{GitLabUrl}'.");
        }

        if (string.IsNullOrWhiteSpace(Token))
        {
            problems.Add("No token given. The GITLAB_TOKEN environment variable is recommended; --token also works.");
        }

        if (string.IsNullOrWhiteSpace(TargetDirectory))
        {
            problems.Add("Target directory cannot be empty.");
        }

        return problems;
    }

    /// <summary>Normalized base address for API calls: always ends with ".../api/v4/".</summary>
    public Uri ApiBaseAddress()
    {
        var trimmed = GitLabUrl.TrimEnd('/');
        if (!trimmed.EndsWith("/api/v4", StringComparison.OrdinalIgnoreCase))
        {
            trimmed += "/api/v4";
        }

        return new Uri(trimmed + "/");
    }

    public string ResolvedTargetDirectory() => Path.GetFullPath(TargetDirectory);
}

public sealed class OptionsException(string message) : Exception(message);
