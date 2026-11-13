using System.Net;
using System.Text.Json;
using GitlabCloner.Cli.Configuration;
using GitlabCloner.Cli.Infrastructure;

namespace GitlabCloner.Cli.GitLab;

public interface IGitLabClient
{
    IAsyncEnumerable<GitLabProject> GetProjectsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// GitLab REST API v4 client. For pagination it first uses the rel="next" URL from
/// the Link header, falling back to the X-Next-Page header; this supports both
/// keyset (/projects) and offset (/groups/:id/projects) pagination.
/// </summary>
public sealed class GitLabClient(HttpClient http, SyncOptions options, ILog log) : IGitLabClient
{
    private const int PageSize = 100;
    private const int MaxAttempts = 3;

    public async IAsyncEnumerable<GitLabProject> GetProjectsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var next = BuildFirstPageUrl();
        var page = 1;

        while (next is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            log.Debug($"Fetching project list (page {page}): {next}");

            using var response = await SendWithRetryAsync(next, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);

            List<GitLabProject>? projects;
            try
            {
                projects = JsonSerializer.Deserialize<List<GitLabProject>>(payload);
            }
            catch (JsonException ex)
            {
                throw new GitLabException($"Could not parse GitLab response: {ex.Message}");
            }

            if (projects is null || projects.Count == 0)
            {
                yield break;
            }

            foreach (var project in projects)
            {
                yield return project;
            }

            next = ResolveNextUrl(response, next);
            page++;
        }
    }

    private string BuildFirstPageUrl()
    {
        var archived = options.IncludeArchived ? "" : "&archived=false";

        if (string.IsNullOrWhiteSpace(options.Group))
        {
            // On large installations keyset pagination on /projects is more reliable than offset.
            return new Uri(
                http.BaseAddress!,
                $"projects?membership=true&per_page={PageSize}&pagination=keyset&order_by=id&sort=asc" +
                $"&simple=false{archived}").ToString();
        }

        var encodedGroup = Uri.EscapeDataString(options.Group!);
        return new Uri(
            http.BaseAddress!,
            $"groups/{encodedGroup}/projects?include_subgroups=true&per_page={PageSize}&page=1{archived}").ToString();
    }

    private string? ResolveNextUrl(HttpResponseMessage response, string currentUrl)
    {
        if (response.Headers.TryGetValues("Link", out var linkValues))
        {
            var next = LinkHeaderParser.FindRel(string.Join(", ", linkValues), "next");
            if (!string.IsNullOrWhiteSpace(next))
            {
                return next;
            }
        }

        if (!response.Headers.TryGetValues("X-Next-Page", out var nextPageValues))
        {
            return null;
        }

        var nextPage = nextPageValues.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(nextPage) || !int.TryParse(nextPage, out var pageNumber))
        {
            return null;
        }

        return ReplacePageParameter(currentUrl, pageNumber);
    }

    /// <summary>Replaces the page parameter in the URL, or appends it if missing.</summary>
    internal static string ReplacePageParameter(string url, int pageNumber)
    {
        var builder = new UriBuilder(url);
        var parameters = builder.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.StartsWith("page=", StringComparison.OrdinalIgnoreCase))
            .Append($"page={pageNumber}");

        builder.Query = string.Join('&', parameters);
        return builder.Uri.ToString();
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(string url, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage? response = null;
            string? transientReason = null;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("PRIVATE-TOKEN", options.Token);
                response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                transientReason = ex.Message;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                transientReason = "request timed out: " + ex.Message;
            }

            if (response is not null)
            {
                if (response.IsSuccessStatusCode)
                {
                    return response;
                }

                var status = response.StatusCode;
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                response.Dispose();

                if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw new GitLabException(
                        $"GitLab authentication failed ({(int)status}). The token may be invalid or lack permissions.");
                }

                if (status == HttpStatusCode.NotFound)
                {
                    throw new GitLabException(
                        $"GitLab resource not found (404): {url}. Is the group path correct?");
                }

                if (status != HttpStatusCode.TooManyRequests && (int)status < 500)
                {
                    throw new GitLabException($"GitLab request failed ({(int)status}): {Truncate(body)}");
                }

                transientReason = $"HTTP {(int)status}";
            }

            if (attempt >= MaxAttempts)
            {
                throw new GitLabException($"GitLab request failed after {MaxAttempts} attempts: {transientReason}");
            }

            var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
            log.Warn($"GitLab request failed ({transientReason}). Retrying in {delay.TotalSeconds:0} s.");
            await Task.Delay(delay, cancellationToken);
        }
    }

    private static string Truncate(string value) =>
        value.Length <= 300 ? value : value[..300] + "…";
}

public sealed class GitLabException(string message) : Exception(message);
