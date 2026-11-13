using System.Text.Json.Serialization;

namespace GitlabCloner.Cli.GitLab;

/// <summary>The fields needed from the GitLab /projects response.</summary>
public sealed class GitLabProject
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    /// <summary>Full path including the group hierarchy, e.g. "platform/backend/order-service".</summary>
    [JsonPropertyName("path_with_namespace")]
    public string PathWithNamespace { get; init; } = "";

    [JsonPropertyName("http_url_to_repo")]
    public string? HttpUrlToRepo { get; init; }

    [JsonPropertyName("default_branch")]
    public string? DefaultBranch { get; init; }

    [JsonPropertyName("archived")]
    public bool Archived { get; init; }

    [JsonPropertyName("empty_repo")]
    public bool EmptyRepo { get; init; }

    public override string ToString() => PathWithNamespace;
}
